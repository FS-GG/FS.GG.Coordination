#!/usr/bin/env python3
"""Focused fake-provider controls for the import-only #529 Applied bridge."""

from __future__ import annotations

import dataclasses
import importlib.util
import pathlib
import sys
import unittest

sys.dont_write_bytecode = True
ROOT = pathlib.Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


proof = load("v1_merge_provider_proof",
             ROOT / "eng/github-v1-admission-merge-provider-proof.py")
fixtures = load("v1_merge_provider_fixtures",
                ROOT / "tests/test_v1_admission_merge_readback.py")


class MergeProviderProofTests(unittest.TestCase):
    def setUp(self):
        self.fake = fixtures.FakeGitHub()
        self.read = lambda path: proof.MERGE.NATIVE.NativeResponse(
            self.fake.read(path).body, None)
        self.policy = proof.MERGE.RegisteredMergePolicy(
            proof.MERGE.REPOSITORY_ID, 77, "squash")
        self.request = proof.MERGE.MergeRequestIdentity(
            **dataclasses.asdict(fixtures.request()))
        self.identity = proof.ProviderRequestIdentity(
            self.request.operation_id, self.request.operation_generation,
            self.request.effect_id, self.request.attempt,
            self.request.request_sha256, self.request.canonical_request_bytes)
        self.pre_send = proof.MERGE.capture_pre_send(
            self.read, self.policy, self.request)
        self.stored = proof.encode_pre_send(self.pre_send)
        self.load_stored = lambda _: self.stored

    def merged(self):
        self.fake.merged = True
        self.fake.main = fixtures.MERGE

    def test_applied_requires_durable_pre_send_and_repeated_native_readback(self):
        self.merged()
        observed = proof.observe_applied(
            self.read, self.policy, self.identity, self.load_stored,
            provider_response={"merged": False, "error": "timeout"})
        self.assertIsInstance(observed, proof.AppliedEvidence)
        self.assertEqual(observed.identity, self.identity)
        self.assertEqual(len(observed.response_sha256), 64)
        self.assertTrue(proof.verify_applied(
            self.read, self.policy, self.identity, observed,
            self.load_stored))
        # The original capture and each verification perform two complete PR reads.
        self.assertEqual(sum(path.endswith("/pulls/3695")
                             for path in self.fake.calls), 6)

    def test_open_pr_and_ambiguous_send_response_remain_unknown(self):
        result = proof.observe_applied(
            self.read, self.policy, self.identity, self.load_stored,
            provider_response={"merged": True, "sha": fixtures.MERGE})
        self.assertIsInstance(result, proof.Unknown)
        self.assertFalse(hasattr(result, "retry"))

    def test_wrong_generation_request_digest_or_pre_send_refuses(self):
        self.merged()
        wrong_generation = dataclasses.replace(
            self.identity, operation_generation=self.identity.operation_generation + 1)
        wrong_digest = dataclasses.replace(self.identity, request_sha256="0" * 64)
        for identity in (wrong_generation, wrong_digest):
            with self.subTest(identity=identity):
                self.assertIsInstance(proof.observe_applied(
                    self.read, self.policy, identity, self.load_stored),
                    proof.Unknown)
        foreign = dataclasses.replace(self.pre_send, operation_generation=9)
        self.stored = proof.encode_pre_send(foreign)
        self.assertIsInstance(proof.observe_applied(
            self.read, self.policy, self.identity, self.load_stored),
            proof.Unknown)

    def test_missing_or_changed_durable_pre_send_cannot_verify(self):
        self.merged()
        observed = proof.observe_applied(
            self.read, self.policy, self.identity, self.load_stored)
        self.assertIsInstance(observed, proof.AppliedEvidence)
        original = self.stored
        self.stored = b""
        self.assertFalse(proof.verify_applied(
            self.read, self.policy, self.identity, observed,
            self.load_stored))
        self.stored = original.replace(b'"attempt":1', b'"attempt":2')
        self.assertFalse(proof.verify_applied(
            self.read, self.policy, self.identity, observed,
            self.load_stored))

    def test_changed_native_merge_or_evidence_cannot_verify(self):
        self.merged()
        observed = proof.observe_applied(
            self.read, self.policy, self.identity, self.load_stored)
        self.assertIsInstance(observed, proof.AppliedEvidence)
        self.fake.message = "Unrelated same-tree merge\n"
        self.assertFalse(proof.verify_applied(
            self.read, self.policy, self.identity, observed,
            self.load_stored))
        self.fake.message = self.request.expected_commit_message
        self.fake.parent = "9" * 40
        self.assertFalse(proof.verify_applied(
            self.read, self.policy, self.identity, observed,
            self.load_stored))
        self.fake.parent = fixtures.BASE
        tampered = dataclasses.replace(
            observed, evidence_bytes=observed.evidence_bytes.replace(
                b'"merge_commit_sha":"' + fixtures.MERGE.encode(),
                b'"merge_commit_sha":"' + b"9" * 40))
        self.assertFalse(proof.verify_applied(
            self.read, self.policy, self.identity, tampered,
            self.load_stored))

    def test_no_second_attempt_and_no_exclusion_on_unreadable_provider(self):
        self.merged()
        second = dataclasses.replace(self.identity, attempt=2)
        self.assertIsInstance(proof.observe_applied(
            self.read, self.policy, second, self.load_stored),
            proof.Unknown)

        def unavailable(_):
            raise OSError("synthetic provider failure")

        result = proof.observe_applied(
            unavailable, self.policy, self.identity, self.load_stored)
        self.assertIsInstance(result, proof.Unknown)


if __name__ == "__main__":
    unittest.main()
