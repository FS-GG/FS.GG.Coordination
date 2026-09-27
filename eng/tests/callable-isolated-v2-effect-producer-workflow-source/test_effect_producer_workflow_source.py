"""Future producer workflow must be a selected Git blob, never a run claim."""

import datetime as dt
import hashlib
import os
import pathlib
import socket
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
sys.path.insert(0, str(ENG / "tests/callable-isolated-v2-effect-git-tree-witness"))
from test_effect_git_tree_witness import FakeGit, git_oid, git_tree
import callable_isolated_v2_effect_git_tree_witness as tree_witness
import callable_isolated_v2_effect_producer_workflow_source as workflow

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
RAW = (ENG.parent / ".github/workflows/callable-isolated-v2-effect-release.yml").read_bytes()
FOREIGN_RAW = b"name: foreign-producer\non:\n  workflow_dispatch:\npermissions: {}\n"


def fixture(mode="100644", workflow_path=workflow.WORKFLOW, raw=RAW):
    tree, objects = git_tree({workflow_path: (raw, mode)})
    commit = b"tree " + tree.encode() + b"\nauthor Fake <fake@example.test> 0 +0000\n\nreviewed producer\n"
    revision = git_oid("commit", commit)
    objects[revision] = commit
    source_tree = tree_witness.TreeWitnessResult(revision, tree, "a" * 64,
        tuple(sorted((path, "b" * 64) for path in workflow.SOURCE_PATHS)), 808)
    selection = {"repositoryId": 77, "identityEventId": 808,
        "workflowSha256": hashlib.sha256(raw).hexdigest(),
        "gitReaderPrincipalId": "git-reader", "gitReaderCredentialId": "2" * 64,
        "sourceReaderPrincipalId": "source-reader",
        "sourceReaderCredentialId": "1" * 64}
    return source_tree, FakeGit(objects), selection


class ProducerWorkflowSourceTests(unittest.TestCase):
    def observe(self, change=None, mode="100644", workflow_path=workflow.WORKFLOW):
        source_tree, port, selection = fixture(mode, workflow_path)
        if change:
            change(source_tree, port, selection)
        return workflow.qualify(source_tree, port, selection, NOW)

    def refuses(self, change=None, mode="100644", workflow_path=workflow.WORKFLOW):
        with self.assertRaises(workflow.Refused):
            self.observe(change, mode, workflow_path)

    def test_matching_fake_workflow_blob_stays_closed(self):
        source_tree, port, selection = fixture()
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect", side_effect=AssertionError("post")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = workflow.qualify(source_tree, port, selection, NOW)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual(result.workflow_sha256, selection["workflowSha256"])
        self.assertFalse(hasattr(workflow, "dispatch"))

    def test_missing_foreign_or_symlink_workflow_refuses(self):
        self.refuses(workflow_path=".github/workflows/foreign.yml")
        self.refuses(mode="120000")
        self.refuses(lambda _t, _g, s: s.__setitem__("workflowSha256", "f" * 64))
        self.refuses(lambda t, _g, _s: object.__setattr__(t, "source_tree", "f" * 40))
        self.refuses(lambda _t, g, _s: g.objects.__setitem__(git_oid("blob", RAW), b"foreign"))

    def test_git_read_cannot_replace_foreign_selected_workflow_digest(self):
        source_tree, port, selection = fixture()
        expected = selection["workflowSha256"]
        selection["workflowSha256"] = "f" * 64
        original_read = port.read_commit

        def read_commit(oid):
            selection["workflowSha256"] = expected
            return original_read(oid)

        port.read_commit = read_commit
        with self.assertRaises(workflow.Refused):
            workflow.qualify(source_tree, port, selection, NOW)

    def test_digest_consistent_foreign_workflow_refuses_before_producer_read(self):
        source_tree, port, selection = fixture(raw=FOREIGN_RAW)
        with self.assertRaises(workflow.Refused):
            workflow.qualify(source_tree, port, selection, NOW)

    def test_reader_custody_and_scope_drift_refuse(self):
        self.refuses(lambda _t, _g, s: s.__setitem__("gitReaderPrincipalId", "foreign"))
        self.refuses(lambda _t, _g, s: s.__setitem__("sourceReaderCredentialId", "2" * 64))
        self.refuses(lambda _t, g, _s: g.scopes[1].__setitem__("credentialId", "3" * 64))
        self.refuses(lambda _t, g, _s: g.scopes[0].__setitem__("permissions", ["contents:write"]))

    def test_same_scope_object_mutated_during_workflow_read_refuses(self):
        source_tree, port, selection = fixture()
        shared = dict(port.scopes[0])
        shared["permissions"] = list(shared["permissions"])
        port.scope = lambda: shared
        original_read = port.read_commit
        def drift(oid):
            raw = original_read(oid)
            shared["credentialId"] = "f" * 64
            return raw
        port.read_commit = drift
        with self.assertRaises(workflow.Refused):
            workflow.qualify(source_tree, port, selection, NOW)


if __name__ == "__main__":
    unittest.main()
