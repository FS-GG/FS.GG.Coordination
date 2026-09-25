#!/usr/bin/env python3
"""Independent fake GitHub controls for import-only merge readback."""

from __future__ import annotations

import dataclasses
import hashlib
import importlib.util
import json
import pathlib
import sys
import unittest

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).resolve().parents[1] / "eng/github-v1-admission-merge-readback.py"
spec = importlib.util.spec_from_file_location("v1_admission_merge_readback", SOURCE)
readback = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = readback
spec.loader.exec_module(readback)

BASE = "a" * 40
HEAD = "b" * 40
TREE = "c" * 40
MERGE = "d" * 40
P = readback.PREFIX


def request(attempt=1):
    core = {
        "version": 1, "repository_id": readback.REPOSITORY_ID,
        "operation_id": "operation-79", "operation_generation": 2,
        "effect_id": "merge-1", "attempt": attempt,
        "pr_number": 3695, "pr_id": 700, "pr_node_id": "PR_synthetic",
        "base_sha": BASE, "head_sha": HEAD, "expected_tree_sha": TREE,
        "method": "squash",
    }
    marker = hashlib.sha256(json.dumps(core, sort_keys=True,
                                       separators=(",", ":")).encode() + b"\n").hexdigest()
    public = dict(core, expected_commit_message=
                  f"Synthetic squash commit\n\nFS-GG-V1-Effect: {marker}\n")
    canonical = json.dumps(public, sort_keys=True, separators=(",", ":")).encode() + b"\n"
    return readback.MergeRequestIdentity(
        public["operation_id"], public["operation_generation"], public["effect_id"],
        attempt, 3695, 700, "PR_synthetic", BASE, HEAD, TREE,
        public["expected_commit_message"], "squash", canonical,
        hashlib.sha256(canonical).hexdigest(),
    )


class FakeGitHub:
    def __init__(self):
        self.merged = False
        self.base = BASE
        self.head = HEAD
        self.parent = BASE
        self.tree = TREE
        self.actor_id = 77
        self.main = BASE
        self.calls = []
        self.message = request().expected_commit_message

    def read(self, path):
        self.calls.append(path)
        pr = {
            "number": 3695, "id": 700, "node_id": "PR_synthetic",
            "state": "closed" if self.merged else "open", "draft": False,
            "merged": self.merged,
            "merged_at": "2026-09-25T00:00:00Z" if self.merged else None,
            "merged_by": {"id": self.actor_id} if self.merged else None,
            "merge_commit_sha": MERGE if self.merged else None,
            "base": {"ref": "main", "sha": self.base,
                     "repo": {"id": readback.REPOSITORY_ID}},
            "head": {"ref": "routine/test", "sha": self.head,
                     "repo": {"id": readback.REPOSITORY_ID}},
        }
        bodies = {
            f"{P}/pulls/3695": pr,
            f"{P}/branches/main": {"name": "main", "protected": True,
                                    "commit": {"sha": self.main}},
            f"{P}/branches/routine%2Ftest": {"name": "routine/test",
                                              "commit": {"sha": self.head}},
            f"{P}/git/commits/{MERGE}": {
                "sha": MERGE, "tree": {"sha": self.tree},
                "parents": [{"sha": self.parent}],
                "message": self.message,
            },
            f"{P}/compare/{MERGE}...{self.main}": {
                "status": "identical" if self.main == MERGE else "ahead",
                "base_commit": {"sha": MERGE},
                "head_commit": {"sha": self.main},
                "merge_base_commit": {"sha": MERGE},
            },
        }
        if path not in bodies:
            raise AssertionError(path)
        return readback.NATIVE.NativeResponse(bodies[path], None)


class MergeReadbackTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeGitHub()
        self.policy = readback.RegisteredMergePolicy(readback.REPOSITORY_ID, 77, "squash")
        self.request = request()
        self.pre = readback.capture_pre_send(self.fake.read, self.policy, self.request)

    def test_matching_commit_tree_ancestry_is_applied(self):
        self.fake.merged = True
        self.fake.main = MERGE
        result = readback.reconcile(self.fake.read, self.policy, self.request,
                                    self.pre, provider_response={"status": "timeout"})
        self.assertIsInstance(result, readback.AppliedReadback)
        self.assertEqual(result.merge_commit_sha, MERGE)
        self.assertEqual(result.tree_sha, TREE)
        self.assertEqual(len([x for x in self.fake.calls if x.endswith("/pulls/3695")]), 4)

    def test_open_pr_and_ambiguous_success_response_stay_unknown(self):
        result = readback.reconcile(self.fake.read, self.policy, self.request,
                                    self.pre, provider_response={"merged": True, "sha": MERGE})
        self.assertIsInstance(result, readback.UnknownReadback)
        self.assertEqual(result.reason, "merge-readback-unsettled")

    def test_moved_base_or_head_refuses_pre_send_and_post_readback(self):
        for field in ("base", "head"):
            fake = FakeGitHub()
            setattr(fake, field, "9" * 40)
            with self.subTest(field=field), self.assertRaises(readback.Refused):
                readback.capture_pre_send(fake.read, self.policy, self.request)
        self.fake.merged = True
        self.fake.main = MERGE
        self.fake.parent = "9" * 40
        self.assertIsInstance(readback.reconcile(self.fake.read, self.policy,
                                                 self.request, self.pre),
                              readback.UnknownReadback)
        self.fake.parent = BASE
        self.fake.head = "9" * 40
        self.assertIsInstance(readback.reconcile(self.fake.read, self.policy,
                                                 self.request, self.pre),
                              readback.UnknownReadback)

    def test_already_merged_unrelated_commit_or_actor_stays_unknown(self):
        self.fake.merged = True
        self.fake.main = MERGE
        self.fake.tree = "9" * 40
        self.assertIsInstance(readback.reconcile(self.fake.read, self.policy,
                                                 self.request, self.pre),
                              readback.UnknownReadback)
        self.fake.tree = TREE
        self.fake.actor_id = 999
        self.assertIsInstance(readback.reconcile(self.fake.read, self.policy,
                                                 self.request, self.pre),
                              readback.UnknownReadback)
        self.fake.actor_id = 77
        self.fake.message = "Synthetic squash commit\n"
        self.assertIsInstance(readback.reconcile(self.fake.read, self.policy,
                                                 self.request, self.pre),
                              readback.UnknownReadback)

    def test_generic_message_cannot_be_a_request_identity(self):
        generic = dataclasses.replace(self.request,
                                      expected_commit_message="Synthetic squash commit\n")
        with self.assertRaisesRegex(readback.Refused, "merge-request-marker"):
            readback.capture_pre_send(self.fake.read, self.policy, generic)

    def test_no_second_attempt_or_foreign_generation(self):
        second = request(attempt=2)
        with self.assertRaises(readback.Refused):
            readback.capture_pre_send(self.fake.read, self.policy, second)
        self.assertIsInstance(readback.reconcile(self.fake.read, self.policy,
                                                 second, self.pre),
                              readback.UnknownReadback)
        foreign = dataclasses.replace(self.request, operation_generation=3)
        self.assertIsInstance(readback.reconcile(self.fake.read, self.policy,
                                                 foreign, self.pre),
                              readback.UnknownReadback)

    def test_changed_native_transcript_between_post_reads_stays_unknown(self):
        self.fake.merged = True
        self.fake.main = MERGE
        calls = 0

        def changing(path):
            nonlocal calls
            if path == f"{P}/pulls/3695":
                calls += 1
                if calls == 2:
                    self.fake.main = "e" * 40
            return self.fake.read(path)

        result = readback.reconcile(changing, self.policy, self.request, self.pre)
        self.assertIsInstance(result, readback.UnknownReadback)
        self.assertEqual(result.reason, "merge-readback-unsettled")


if __name__ == "__main__":
    unittest.main()
