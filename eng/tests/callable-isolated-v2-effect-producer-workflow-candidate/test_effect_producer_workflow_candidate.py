"""Exact disabled producer-workflow candidate and independent mutations."""

import hashlib
import os
import pathlib
import socket
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import verify_callable_isolated_v2_effect_producer_workflow as verifier

WORKFLOW = ROOT / ".github/workflows/callable-isolated-v2-effect-release.yml"


class ProducerWorkflowCandidateTests(unittest.TestCase):
    def test_exact_candidate_remains_disabled_without_effect_ports(self):
        raw = WORKFLOW.read_bytes()
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect", side_effect=AssertionError("post")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = verifier.verify(raw, hashlib.sha256(raw).hexdigest())
        self.assertEqual(result["workflowSha256"], verifier.PINNED_WORKFLOW_SHA256)
        self.assertEqual(result["workflowBlobOid"],
                         verifier.PINNED_WORKFLOW_BLOB_OID)
        self.assertFalse(result["producerEnabled"])
        self.assertFalse(result["authorized"])
        self.assertFalse(result["canDispatch"])
        self.assertEqual(result["liveEffects"], 0)
        self.assertFalse(hasattr(verifier, "dispatch"))

    def test_changed_gate_runner_action_permission_or_artifact_refuses_with_new_digest(self):
        raw = WORKFLOW.read_bytes()
        changes = (
            (b"if: ${{ false }}", b"if: ${{ true }}"),
            (b"run: exit 78", b"run: exit 0"),
            (b"isolated-v2-release-unselected", b"ubuntu-latest"),
            (b"contents: read", b"contents: write"),
            (b"actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
             b"actions/checkout@main"),
            (b"actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
             b"actions/upload-artifact@main"),
            (b"path: dist/fsgg-callable-isolated-v2-effect-scaffold.pyz",
             b"path: dist/**"),
        )
        for before, after in changes:
            with self.subTest(before=before):
                self.assertIn(before, raw)
                changed = raw.replace(before, after, 1)
                with self.assertRaises(verifier.Refused):
                    verifier.verify(changed, hashlib.sha256(changed).hexdigest())
        appended = raw + b"\n  extra-live-job:\n    runs-on: ubuntu-latest\n"
        with self.assertRaises(verifier.Refused):
            verifier.verify(appended, hashlib.sha256(appended).hexdigest())
        with self.assertRaises(verifier.Refused):
            verifier.verify(raw, "f" * 64)


if __name__ == "__main__":
    unittest.main()
