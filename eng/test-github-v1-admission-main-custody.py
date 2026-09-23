#!/usr/bin/env python3
"""Synthetic-key controls for the Main-only v1 admission custody handoff."""

import base64
import datetime as dt
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import unittest


sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-main-custody.py")
spec = importlib.util.spec_from_file_location("v1_admission_main_custody", SOURCE)
custody = importlib.util.module_from_spec(spec)
spec.loader.exec_module(custody)


class MainCustodyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.key = subprocess.run(
            ["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:3072"],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=True).stdout
        cls.public, cls.spki = custody.public_key(cls.key)

    def verify(self, payload, signature, pss):
        public_fd = os.memfd_create("test-public")
        signature_fd = os.memfd_create("test-signature")
        try:
            os.write(public_fd, self.public)
            os.write(signature_fd, signature)
            command = ["openssl", "dgst", "-sha256", "-verify",
                       f"/proc/self/fd/{public_fd}", "-signature",
                       f"/proc/self/fd/{signature_fd}"]
            if pss:
                command += ["-sigopt", "rsa_padding_mode:pss"]
            result = subprocess.run(command, input=payload, stdout=subprocess.PIPE,
                                    stderr=subprocess.DEVNULL,
                                    pass_fds=(public_fd, signature_fd), check=False)
            self.assertEqual(0, result.returncode)
        finally:
            os.close(public_fd)
            os.close(signature_fd)

    @staticmethod
    def native(run_id):
        artifact = {"schema": "fsgg.v1-admission-genesis-protected-authorization/2",
                    "runId": run_id, "conclusion": "success", "genesisIntentSha256": "a" * 64,
                    "operationId": "fleet-v1-admission:fs-gg-production",
                    "repository": "FS-GG/.github", "environment": "fleet-v1-admission-owner",
                    "workflowRevision": "b" * 40,
                    "approvedAt": "2026-09-23T14:00:00Z",
                    "expiresAt": "2026-09-23T15:30:00Z"}
        return {"schema": "fsgg.v1-admission-genesis-native-read/2",
                "runRepositoryId": 1269292704, "runId": run_id,
                "runEvent": "workflow_dispatch",
                "runPath": ".github/workflows/gs2-v1-admission-protected-authorization.yml",
                "runRef": "refs/heads/main", "runHead": "b" * 40,
                "workflowReadRevision": "b" * 40, "runAttempt": 1,
                "runConclusion": "success", "runActorId": 1645484,
                "environmentId": 22582241959, "environmentName": "fleet-v1-admission-owner",
                "environmentBranchPolicy": "custom-main", "environmentWaitMinutes": 5,
                "environmentPreventsSelfReview": False,
                "environmentReviewerIds": [1645484],
                "approvals": [{"reviewerId": 1645484, "state": "approved",
                               "environmentIds": [22582241959]}],
                "artifactBytesBase64": base64.b64encode(custody.canonical(artifact)).decode()}

    @staticmethod
    def timestamp():
        return int(dt.datetime(2026, 9, 23, 14, 1, tzinfo=dt.timezone.utc).timestamp())

    def test_jwt_is_short_lived_and_signed_by_ordinary_key(self):
        token = custody.issue_jwt(42, lambda role: self.key if role == "ordinary" else None,
                                  self.native, now=self.timestamp()).decode("ascii")
        header, claims, signature = token.split(".")
        decoded = json.loads(base64.urlsafe_b64decode(claims + "=" * (-len(claims) % 4)))
        self.assertEqual({"iat": self.timestamp() - 30, "exp": self.timestamp() + 540,
                          "iss": 4882140}, decoded)
        self.verify((header + "." + claims).encode("ascii"),
                    base64.urlsafe_b64decode(signature + "=" * (-len(signature) % 4)), False)
        self.assertEqual(3, len(token.split(".")))

    def test_signing_envelope_matches_trust_and_rejects_wrong_key(self):
        payload = custody.canonical({
            "schema": custody.SIGNATURE_SCHEMA, "intentSha256": "a" * 64,
            "keyId": "test-key", "protectedRunId": 42,
            "authorizedAt": "2026-09-23T14:00:00.0000000+00:00",
            "expiresAt": "2026-09-23T15:30:00.0000000+00:00"})
        trust = custody.canonical({"schema": "fsgg.github-ledger-initial-trust/1",
                                   "authorizer": {"keyId": "test-key",
                                                  "algorithm": "RSA-PSS-SHA256",
                                                  "publicKeySpkiSha256": self.spki}}) + b"\n"
        encoded = custody.sign_envelope(payload, trust, lambda role: self.key,
                                        self.native, now=self.timestamp())
        envelope = json.loads(encoded)
        self.assertEqual(custody.ENVELOPE_SCHEMA, envelope["schema"])
        self.assertEqual(self.public.decode("ascii"), envelope["publicKeyPem"])
        self.assertEqual(384, len(base64.b64decode(envelope["signatureBase64"])))
        self.verify(payload, base64.b64decode(envelope["signatureBase64"]), True)
        self.assertNotIn("PRIVATE KEY", encoded.decode("ascii"))
        with self.assertRaisesRegex(custody.Refused, "authorizer-key-mismatch"):
            wrong = trust.replace(self.spki.encode("ascii"), b"f" * 64)
            custody.sign_envelope(payload, wrong, lambda role: self.key,
                                  self.native, now=self.timestamp())
        with self.assertRaisesRegex(custody.Refused, "signing-payload-binding"):
            custody.sign_envelope(payload + b"\n", trust, lambda role: self.key,
                                  self.native, now=self.timestamp())
        with self.assertRaisesRegex(custody.Refused, "signing-payload-binding"):
            custody.sign_envelope(payload.replace(b"a" * 64, b"?" * 64), trust,
                                  lambda role: self.key, self.native, now=self.timestamp())
        with self.assertRaisesRegex(custody.Refused, "native-approval-expired"):
            custody.issue_jwt(42, lambda role: self.key, self.native,
                              now=self.timestamp() + 7200)

    def test_secret_lookup_uses_exact_attributes_and_never_returns_failure_output(self):
        calls = []
        def run(command, **kwargs):
            calls.append(command)
            return subprocess.CompletedProcess(command, 0, self.key)
        self.assertEqual(self.key, custody.secret("ordinary", run))
        self.assertEqual(self.key, custody.secret("authorizer", run))
        self.assertEqual(["secret-tool", "lookup", "service", "fsgg-ledger-protection",
                          "role", "ordinary", "app-id", "4882140"], calls[0])
        self.assertEqual(["secret-tool", "lookup", "service", "fsgg-ledger-protection",
                          "role", "authorizer"], calls[1])


if __name__ == "__main__":
    unittest.main()
