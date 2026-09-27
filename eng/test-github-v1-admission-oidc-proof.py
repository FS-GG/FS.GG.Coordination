#!/usr/bin/env python3
"""Synthetic-key controls for the import-only OperatingV1 OIDC proof verifier."""

from __future__ import annotations

import base64
import dataclasses
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-oidc-proof.py")
spec = importlib.util.spec_from_file_location("v1_admission_oidc_proof", SOURCE)
proof = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = proof
spec.loader.exec_module(proof)


def b64(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


class OidcProofTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix="fsgg-test-v1-oidc-")
        cls.private_key = pathlib.Path(cls.directory.name) / "synthetic-only.pem"
        generated = subprocess.run(
            ["openssl", "genpkey", "-algorithm", "RSA",
             "-pkeyopt", "rsa_keygen_bits:2048", "-out", str(cls.private_key)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30, check=False,
        )
        if generated.returncode != 0:
            raise RuntimeError("synthetic RSA key generation failed")
        modulus = subprocess.run(
            ["openssl", "rsa", "-in", str(cls.private_key), "-noout", "-modulus"],
            capture_output=True, timeout=10, check=True,
        ).stdout.decode("ascii").strip().removeprefix("Modulus=")
        cls.jwks = json.dumps({
            "keys": [{"kty": "RSA", "kid": "synthetic-only", "alg": "RS256",
                      "use": "sig", "n": b64(bytes.fromhex(modulus)),
                      "e": b64(b"\x01\x00\x01")}]
        }).encode()
        cls.native = proof.NativeJobIdentity(
            workflow_path=".github/workflows/gs2-v1-admission-operating.yml",
            run_head="a" * 40,
            workflow_sha="b" * 40,
            run_id=123456,
            run_attempt=1,
            actor_id=1645484,
            check_run_id=987654,
            environment_name="fleet-v1-admission-runtime",
            environment_node_id="EN_synthetic",
        )
        cls.registered = proof.RegisteredServicePolicy(
            workflow_path=cls.native.workflow_path,
            workflow_sha=cls.native.workflow_sha,
            actor_id=cls.native.actor_id,
            environment_name=cls.native.environment_name,
            environment_node_id=cls.native.environment_node_id,
            subject="synthetic-job",
        )
        cls.now = 1790310000
        cls.plan = b"public sealed admission plan\n"
        cls.nonce = "ab" * 16
        cls.audience = proof.plan_audience(cls.plan, cls.nonce)

    @classmethod
    def tearDownClass(cls):
        cls.directory.cleanup()

    def claims(self):
        n = self.native
        return {
            "iss": proof.ISSUER, "aud": self.audience, "sub": "synthetic-job",
            "jti": "synthetic-token-1", "iat": self.now - 60,
            "nbf": self.now - 60, "exp": self.now + 300,
            "repository": proof.REPOSITORY, "repository_id": proof.REPOSITORY_ID,
            "ref": proof.REF, "sha": n.run_head,
            "workflow_ref": f"{proof.REPOSITORY}/{n.workflow_path}@{proof.REF}",
            "workflow_sha": n.workflow_sha, "run_id": str(n.run_id),
            "run_attempt": str(n.run_attempt), "actor_id": str(n.actor_id),
            "check_run_id": str(n.check_run_id),
            "environment": n.environment_name,
            "environment_node_id": n.environment_node_id,
            "event_name": "workflow_dispatch",
        }

    def token(self, claims=None, header=None):
        header = header or {"alg": "RS256", "typ": "JWT", "kid": "synthetic-only"}
        claims = claims or self.claims()
        signing_input = (
            b64(json.dumps(header, sort_keys=True, separators=(",", ":")).encode())
            + "."
            + b64(json.dumps(claims, sort_keys=True, separators=(",", ":")).encode())
        )
        signature = subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", str(self.private_key)],
            input=signing_input.encode(), capture_output=True, timeout=10, check=True,
        ).stdout
        return signing_input + "." + b64(signature)

    def verify(self, token, audience=None, native=None, registered=None, jwks=None):
        return proof.verify_signed_job(
            token, self.jwks if jwks is None else jwks,
            self.native if native is None else native,
            self.registered if registered is None else registered,
            self.audience if audience is None else audience,
            self.now,
        )

    def test_valid_signed_job_and_plan_nonce_binding(self):
        verified = self.verify(self.token())
        self.assertEqual(verified.token_id, "synthetic-token-1")
        self.assertEqual(verified.audience, self.audience)
        self.assertEqual(verified.payload_sha256,
                         hashlib.sha256(json.dumps(self.claims(), sort_keys=True,
                                                    separators=(",", ":")).encode()).hexdigest())
        with self.assertRaises(proof.Refused):
            self.verify(self.token(), audience=proof.plan_audience(self.plan, "cd" * 16))
        with self.assertRaises(proof.Refused):
            self.verify(self.token(), audience=proof.plan_audience(self.plan + b"x", self.nonce))

    def test_wrong_signature_key_and_duplicate_json_refuse(self):
        token = self.token()
        altered = token.split(".")
        altered[1] = b64(b'{"sub":"changed"}')
        with self.assertRaises(proof.Refused):
            self.verify(".".join(altered))
        with self.assertRaises(proof.Refused):
            self.verify(token, jwks=b'{"keys":[]}')
        duplicate = b'{"keys":[],"keys":[]}'
        with self.assertRaises(proof.Refused):
            self.verify(token, jwks=duplicate)

    def test_signed_foreign_run_environment_and_expiry_refuse(self):
        for field, value in [
            ("run_id", "999"), ("run_attempt", "2"), ("check_run_id", "999"),
            ("actor_id", "999"), ("environment", "genesis"),
            ("environment_node_id", "EN_foreign"), ("workflow_sha", "c" * 40),
            ("repository_id", "1"), ("ref", "refs/heads/feature"),
        ]:
            claims = self.claims()
            claims[field] = value
            with self.subTest(field=field), self.assertRaises(proof.Refused):
                self.verify(self.token(claims))
        claims = self.claims()
        claims["exp"] = self.now - 1
        with self.assertRaises(proof.Refused):
            self.verify(self.token(claims))
        foreign = dataclasses.replace(self.native,
                                      workflow_path=".github/workflows/foreign.yml",
                                      environment_name="foreign-runtime")
        foreign_claims = self.claims()
        foreign_claims["workflow_ref"] = (
            f"{proof.REPOSITORY}/{foreign.workflow_path}@{proof.REF}")
        foreign_claims["environment"] = foreign.environment_name
        with self.assertRaises(proof.Refused):
            self.verify(self.token(foreign_claims), native=foreign)

    def test_malformed_audience_nonce_and_native_shape_refuse(self):
        with self.assertRaises(proof.Refused):
            proof.plan_audience(self.plan, "not-a-nonce")
        with self.assertRaises(proof.Refused):
            self.verify(self.token(), native=proof.NativeJobIdentity(
                self.native.workflow_path, self.native.run_head, self.native.workflow_sha,
                self.native.run_id, 2, self.native.actor_id,
                self.native.check_run_id, self.native.environment_name,
                self.native.environment_node_id))


if __name__ == "__main__":
    unittest.main()
