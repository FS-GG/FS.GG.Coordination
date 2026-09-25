import base64
import datetime as dt
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[2] / "eng/sandbox_run_binding_contract.py"
SPEC = importlib.util.spec_from_file_location("sandbox_run_binding_contract", SOURCE)
binding_contract = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(binding_contract)


class RunBindingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.key = Path(cls.temporary.name) / "signing-key.pem"
        cls.other_key = Path(cls.temporary.name) / "other-key.pem"
        subprocess.run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt",
                        "rsa_keygen_bits:2048", "-out", str(cls.key)],
                       capture_output=True, check=True)
        subprocess.run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt",
                        "rsa_keygen_bits:2048", "-out", str(cls.other_key)],
                       capture_output=True, check=True)
        cls.public = subprocess.run(
            ["openssl", "pkey", "-in", str(cls.key), "-pubout"],
            capture_output=True, check=True).stdout
        cls.other_public = subprocess.run(
            ["openssl", "pkey", "-in", str(cls.other_key), "-pubout"],
            capture_output=True, check=True).stdout
        cls.pin = binding_contract.spki_sha256(cls.public)

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def setUp(self):
        self.now = dt.datetime(2026, 9, 25, 12, 0, tzinfo=dt.timezone.utc)
        self.token = "fake-selected-sandbox-token-123456789"
        self.expected = {
            "workflowSha": "a" * 40,
            "runId": 12345,
            "runAttempt": 2,
            "candidateSha": "b" * 40,
            "runNonce": f'12345-2-{"b" * 40}',
        }
        self.proof = self.json_bytes({
            "tokenSha256": hashlib.sha256(self.token.encode()).hexdigest(),
            "expiresAt": "2026-09-25T13:00:00Z",
        })
        self.bound = {
            "audience": binding_contract.AUDIENCE,
            "workflowRepository": binding_contract.REPOSITORY,
            "workflowPath": binding_contract.WORKFLOW,
            "environment": binding_contract.ENVIRONMENT,
            "sandboxRepositoryId": binding_contract.SANDBOX_ID,
            "sandboxRepositoryNodeId": binding_contract.SANDBOX_NODE,
            "projectNodeId": binding_contract.PROJECT_NODE,
            "proofSha256": hashlib.sha256(self.proof).hexdigest(),
            "tokenSha256": hashlib.sha256(self.token.encode()).hexdigest(),
            "expiresAt": "2026-09-25T13:00:00Z",
            **self.expected,
        }

    @staticmethod
    def json_bytes(value):
        return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()

    def signed(self, bound=None, key=None):
        bound = self.bound if bound is None else bound
        key = self.key if key is None else key
        signature = subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", str(key),
             "-sigopt", "rsa_padding_mode:pss", "-sigopt", "rsa_pss_saltlen:digest"],
            input=binding_contract.payload(bound), capture_output=True, check=True).stdout
        return self.json_bytes({
            "schema": binding_contract.SCHEMA,
            "binding": bound,
            "signatureBase64": base64.b64encode(signature).decode(),
        })

    def verify(self, envelope=None, proof=None, token=None, public=None, pin=None,
               expected=None):
        return binding_contract.verify(
            self.signed() if envelope is None else envelope,
            self.proof if proof is None else proof,
            self.token if token is None else token,
            self.public if public is None else public,
            self.pin if pin is None else pin,
            self.expected if expected is None else expected, self.now)

    def test_exact_signed_run_and_mint_proof_qualify_offline(self):
        self.assertEqual(self.bound, self.verify())

    def test_changed_candidate_or_nonce_refuses_replay(self):
        for changed in (
            {**self.expected, "candidateSha": "c" * 40,
             "runNonce": f'12345-2-{"c" * 40}'},
            {**self.expected, "runNonce": f'99999-2-{"b" * 40}'},
            {**self.expected, "runId": 99999,
             "runNonce": f'99999-2-{"b" * 40}'},
            {**self.expected, "runAttempt": 3,
             "runNonce": f'12345-3-{"b" * 40}'},
        ):
            with self.subTest(changed=changed):
                with self.assertRaises(binding_contract.Refused):
                    self.verify(expected=changed)

    def test_changed_proof_token_or_target_refuses(self):
        with self.assertRaisesRegex(binding_contract.Refused, "proof-digest"):
            self.verify(proof=self.proof + b" ")
        with self.assertRaisesRegex(binding_contract.Refused, "token-digest"):
            self.verify(token="another-selected-sandbox-token-123456789")
        changed = {**self.bound, "projectNodeId": "PVT_foreign"}
        with self.assertRaisesRegex(binding_contract.Refused, "run-or-target"):
            self.verify(envelope=self.signed(changed))

    def test_unsigned_tampered_and_attacker_selected_key_refuse(self):
        unsigned = self.json_bytes({"schema": binding_contract.SCHEMA,
                                    "binding": self.bound})
        with self.assertRaises(binding_contract.Refused):
            self.verify(envelope=unsigned)
        tampered = json.loads(self.signed())
        tampered["binding"]["expiresAt"] = "2026-09-25T13:01:00Z"
        with self.assertRaises(binding_contract.Refused):
            self.verify(envelope=self.json_bytes(tampered))
        bad_signature = json.loads(self.signed())
        bad_signature["signatureBase64"] = base64.b64encode(b"forged").decode()
        with self.assertRaisesRegex(binding_contract.Refused, "signature"):
            self.verify(envelope=self.json_bytes(bad_signature))
        with self.assertRaisesRegex(binding_contract.Refused, "trust-anchor"):
            self.verify(envelope=self.signed(key=self.other_key),
                        public=self.other_public)

    def test_unknown_or_duplicate_members_refuse(self):
        changed = {**self.bound, "secret": "must-not-be-in-evidence"}
        with self.assertRaisesRegex(binding_contract.Refused, "binding-fields"):
            self.verify(envelope=self.signed(changed))
        duplicate = self.signed().replace(b'"schema":', b'"schema":"duplicate","schema":', 1)
        with self.assertRaisesRegex(binding_contract.Refused, "duplicate-member"):
            self.verify(envelope=duplicate)

    def test_expired_signed_claim_and_invalid_expected_nonce_refuse(self):
        changed = {**self.bound, "expiresAt": "2026-09-25T11:59:59Z"}
        changed_proof = self.json_bytes({
            "tokenSha256": self.bound["tokenSha256"],
            "expiresAt": changed["expiresAt"],
        })
        changed["proofSha256"] = hashlib.sha256(changed_proof).hexdigest()
        with self.assertRaisesRegex(binding_contract.Refused, "expiry"):
            self.verify(envelope=self.signed(changed), proof=changed_proof)
        with self.assertRaisesRegex(binding_contract.Refused, "expected-nonce"):
            self.verify(expected={**self.expected, "runNonce": "arbitrary"})


if __name__ == "__main__":
    unittest.main()
