"""The dedicated runtime producer remains an exact source-only workflow."""

import hashlib
import pathlib
import sys
import unittest

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import verify_callable_isolated_v2_runtime_candidate_workflow as verifier

WORKFLOW = ROOT / verifier.WORKFLOW


class RuntimeCandidateWorkflowTests(unittest.TestCase):
    def test_exact_workflow_uses_frozen_builder_verifier_and_two_outputs(self):
        raw = WORKFLOW.read_bytes()
        result = verifier.verify(raw, hashlib.sha256(raw).hexdigest())
        self.assertEqual(result["workflowSha256"],
                         verifier.PINNED_WORKFLOW_SHA256)
        self.assertTrue(result["sourceOnly"])
        self.assertFalse(result["authorized"])
        self.assertFalse(result["canDispatchEffect"])
        self.assertEqual(result["liveEffects"], 0)

    def test_changed_source_action_permission_or_output_refuses(self):
        raw = WORKFLOW.read_bytes()
        changes = (
            (b"ref: ${{ github.sha }}", b"ref: main"),
            (b"persist-credentials: false", b"persist-credentials: true"),
            (b"contents: read", b"contents: write"),
            (b"actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
             b"actions/checkout@main"),
            (b"actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
             b"actions/upload-artifact@main"),
            (b"eng/build_callable_isolated_v2_runtime.py",
             b"eng/build_foreign.py"),
            (b"eng/verify_callable_isolated_v2_runtime_artifact.py",
             b"eng/verify_foreign.py"),
            (b"callable-isolated-v2-runtime-manifest.json",
             b"foreign.json"),
        )
        for before, after in changes:
            with self.subTest(before=before):
                self.assertIn(before, raw)
                changed = raw.replace(before, after, 1)
                with self.assertRaises(verifier.Refused):
                    verifier.verify(changed, hashlib.sha256(changed).hexdigest())
        with self.assertRaises(verifier.Refused):
            verifier.verify(raw, "f" * 64)


if __name__ == "__main__":
    unittest.main()
