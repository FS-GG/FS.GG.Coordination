"""Independent negative controls for the disabled inspect-only release proposal."""

from __future__ import annotations

import hashlib
import os
import pathlib
import socket
import sys
import unittest
from unittest.mock import patch

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import validate_callable_isolated_v2_release_workflow as guard  # noqa: E402


def inputs() -> tuple[bytes, dict[str, bytes]]:
    workflow = (ROOT / guard.WORKFLOW_PATH).read_bytes()
    source = {name: (ROOT / name).read_bytes() for name in guard.SOURCE_HASHES}
    return workflow, source


class HeldWorkflowTests(unittest.TestCase):
    def test_exact_template_and_stack_are_non_dispatchable_without_io(self):
        raw, source = inputs()
        self.assertEqual(guard.WORKFLOW_SHA256, hashlib.sha256(raw).hexdigest())
        with patch("builtins.open", side_effect=AssertionError("file or journal IO")), \
             patch.object(os, "getenv", side_effect=AssertionError("token read")), \
             patch.object(socket, "socket", side_effect=AssertionError("provider")):
            evidence = guard.verify_held_template(raw, guard.WORKFLOW_SHA256, source)
        self.assertEqual(len(guard.SOURCE_HASHES), evidence.source_count)
        self.assertFalse(evidence.authorized)
        self.assertFalse(evidence.can_dispatch)
        self.assertEqual(0, evidence.live_effects)

    def test_independent_semantic_mutations_refuse_even_before_new_digest_pin(self):
        raw, _ = inputs()
        body = raw.decode("utf-8")
        mutations = [
            ("if: ${{ false }}", "if: ${{ true }}"),
            ("permissions: {}", "permissions: {contents: write}"),
            ("isolated-v2-release-unselected", "ubuntu-latest"),
            (guard.IMAGE, "ghcr.io/fs-gg/isolated-v2-release:latest"),
            ("environment: callable-isolated-v2-release", "environment: unprotected"),
            ("RELEASE_STATE: unselected", "RELEASE_STATE: selected"),
            ("CANDIDATE_PARENT_HEAD: 5ffa942bb089ba3f376c13432730c9976aa72895",
             "CANDIDATE_PARENT_HEAD: " + "1" * 40),
            ("exit 78", "exit 0"),
            ("EXPECTED_APPROVED_PACKET_SHA256: '" + guard.ZERO64 + "'",
             "EXPECTED_APPROVED_PACKET_SHA256: '" + "1" * 64 + "'"),
            ("EXPECTED_PROTECTED_CLOSURE_SHA256: '" + guard.ZERO64 + "'",
             "EXPECTED_PROTECTED_CLOSURE_SHA256: '6783d094396af466461f3dfc8e8d0e41dada34b177e1c06dd784806c79979162'"),
            ("ARCHIVE_SHA256: " + guard.EMBEDDED["ARCHIVE_SHA256"],
             "ARCHIVE_SHA256: " + "0" * 64),
            ("DISALLOWED_LOCAL_CLOSURE_SHA256: 6783d094396af466461f3dfc8e8d0e41dada34b177e1c06dd784806c79979162",
             "DISALLOWED_LOCAL_CLOSURE_SHA256: " + "0" * 64),
        ]
        for old, new in mutations:
            with self.subTest(old=old):
                changed = body.replace(old, new, 1)
                self.assertNotEqual(body, changed)
                with self.assertRaises(guard.Refused):
                    guard._held_boundary(changed)
                with self.assertRaisesRegex(guard.Refused, "workflow-digest"):
                    guard.verify_held_template(changed.encode(),
                                               hashlib.sha256(changed.encode()).hexdigest(),
                                               inputs()[1])

    def test_added_token_action_or_effect_route_refuses(self):
        raw, _ = inputs()
        body = raw.decode("utf-8")
        for addition in ("      GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}\n",
                         "      - uses: actions/checkout@main\n",
                         "      - run: curl https://api.github.com/repos/FS-GG/x/pulls/\n",
                         "  another-job:\n    runs-on: ubuntu-latest\n    steps:\n      - run: exit 0\n",
                         "      - name: Another step\n        run: |\n          exit 0\n"):
            with self.subTest(addition=addition):
                with self.assertRaises(guard.Refused):
                    guard._held_boundary(body + addition)

    def test_missing_foreign_or_changed_source_and_wrong_digest_refuse(self):
        raw, source = inputs()
        with self.assertRaisesRegex(guard.Refused, "workflow-digest"):
            guard.verify_held_template(raw, "0" * 64, source)
        missing = dict(source)
        missing.pop("eng/callable_isolated_v2_grant.py")
        with self.assertRaisesRegex(guard.Refused, "workflow-source-set"):
            guard.verify_held_template(raw, guard.WORKFLOW_SHA256, missing)
        changed = dict(source)
        changed["eng/callable_isolated_v2_grant.py"] += b"\n# changed"
        with self.assertRaisesRegex(guard.Refused, "workflow-source-digest"):
            guard.verify_held_template(raw, guard.WORKFLOW_SHA256, changed)
        extra = dict(source)
        extra["eng/foreign-effect.py"] = b"effect"
        with self.assertRaisesRegex(guard.Refused, "workflow-source-set"):
            guard.verify_held_template(raw, guard.WORKFLOW_SHA256, extra)


if __name__ == "__main__":
    unittest.main()
