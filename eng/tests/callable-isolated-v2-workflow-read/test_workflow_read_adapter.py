"""Independent fake REST controls for the closed workflow/run observer."""

import base64
import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_workflow_read_adapter as adapter

NOW = dt.datetime(2026, 9, 25, 12, 0, tzinfo=dt.timezone.utc)
REV = "a" * 40
WORKFLOW = b"name: synthetic-native-v2\non: workflow_dispatch\n"
SHA256 = hashlib.sha256(WORKFLOW).hexdigest()
BLOB_SHA = hashlib.sha1(b"blob " + str(len(WORKFLOW)).encode() + b"\0" + WORKFLOW).hexdigest()
RUN_PATH = "repos/FS-GG/.github/actions/runs/101/attempts/1"
CONTENT_PATH = ("repos/FS-GG/.github/contents/" + candidate.WORKFLOW_PATH +
                "?ref=" + REV)
ROOT = "https://api.github.com/repos/FS-GG/.github"


def fixture():
    scope = {"principalId": "protected-reader-synthetic", "credentialId": "b" * 64,
             "repository": "FS-GG/.github", "repositoryId": 123,
             "permissions": {"actions": "read", "contents": "read", "metadata": "read"},
             "expiresAt": "2026-09-25T12:20:00Z"}
    repo = {"id": 123, "full_name": "FS-GG/.github"}
    run = {"id": 101, "run_attempt": 1, "event": "workflow_dispatch",
           "head_branch": "main", "head_sha": REV,
           "path": candidate.WORKFLOW_PATH + "@main",
           "url": ROOT + "/actions/runs/101", "workflow_id": 301,
           "workflow_url": ROOT + "/actions/workflows/301",
           "actor": {"id": 201}, "triggering_actor": {"id": 201},
           "repository": copy.deepcopy(repo), "head_repository": copy.deepcopy(repo)}
    contents = {"type": "file", "encoding": "base64",
                "path": candidate.WORKFLOW_PATH,
                "name": "callable-isolated-v2-execute.yml", "size": len(WORKFLOW),
                "content": base64.b64encode(WORKFLOW).decode(), "sha": BLOB_SHA,
                "url": ROOT + "/contents/" + candidate.WORKFLOW_PATH,
                "git_url": ROOT + "/git/blobs/" + BLOB_SHA}
    return scope, run, contents


class FakeTransport:
    def __init__(self, scope, run, contents):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.events = [(RUN_PATH, run), (CONTENT_PATH, contents)]
        self.requests = []

    def scope(self):
        return self.scopes.pop(0)

    def get(self, path):
        self.requests.append(path)
        expected, value = self.events.pop(0)
        if path != expected:
            raise AssertionError("foreign request")
        if isinstance(value, adapter.Response):
            return value
        return adapter.Response(200, (), json.dumps(value).encode())


class WorkflowReadTests(unittest.TestCase):
    def read(self, scope=None, run=None, contents=None):
        normal_scope, normal_run, normal_contents = fixture()
        transport = FakeTransport(normal_scope if scope is None else scope,
                                  normal_run if run is None else run,
                                  normal_contents if contents is None else contents)
        observer = adapter.WorkflowRunReadAdapter(
            transport, 123, 101, 1, REV, SHA256, "c" * 64, NOW)
        return observer.observe_workflow(), transport

    def assert_refused(self, scope=None, run=None, contents=None):
        with self.assertRaises(adapter.Refused):
            self.read(scope, run, contents)

    def test_matching_fake_read_stays_observation_only(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            value, transport = self.read()
        self.assertEqual(transport.requests, [RUN_PATH, CONTENT_PATH])
        self.assertEqual(value["envelope"]["facts"], {
            "workflowRevision": REV, "workflowPath": candidate.WORKFLOW_PATH,
            "workflowSha256": SHA256, "runId": 101, "runAttempt": 1,
            "dispatchActorId": 201})
        self.assertEqual(value["blobs"], {"workflow": WORKFLOW})
        self.assertNotIn("authorized", value)
        self.assertFalse(hasattr(adapter, "dispatch"))

    def test_wrong_run_actor_repository_and_workflow_refuse(self):
        for key, changed in (
            ("id", 102), ("run_attempt", 2), ("event", "push"),
            ("head_branch", "feature"), ("head_sha", "d" * 40),
            ("path", candidate.WORKFLOW_PATH + "@feature"),
            ("url", ROOT + "/actions/runs/102"),
            ("workflow_url", ROOT + "/actions/workflows/302"),
            ("workflow_id", True)):
            with self.subTest(key=key):
                run = fixture()[1]
                run[key] = changed
                self.assert_refused(run=run)
        for field, key, changed in (
            ("actor", "id", 202), ("triggering_actor", "id", 202),
            ("repository", "id", 124),
            ("head_repository", "full_name", "FS-GG/foreign")):
            with self.subTest(field=field, key=key):
                run = fixture()[1]
                run[field][key] = changed
                self.assert_refused(run=run)

    def test_swapped_or_malformed_workflow_bytes_refuse(self):
        for key, changed in (
            ("content", base64.b64encode(b"foreign").decode()),
            ("content", "not-valid-base64*"),
            ("size", True), ("size", len(WORKFLOW) + 1),
            ("sha", "d" * 40), ("type", "dir"), ("encoding", "none"),
            ("path", ".github/workflows/foreign.yml"),
            ("url", ROOT + "/contents/foreign.yml"),
            ("git_url", ROOT + "/git/blobs/" + "d" * 40)):
            with self.subTest(key=key):
                contents = fixture()[2]
                contents[key] = changed
                self.assert_refused(contents=contents)

    def test_broad_foreign_expired_or_drifting_scope_refuse(self):
        for key, changed in (
            ("repository", "FS-GG/foreign"), ("repositoryId", 124),
            ("permissions", {"actions": "write", "contents": "read",
                             "metadata": "read"}),
            ("expiresAt", "2026-09-25T11:59:59Z")):
            with self.subTest(key=key):
                scope = fixture()[0]
                scope[key] = changed
                self.assert_refused(scope=scope)
        scope, run, contents = fixture()
        transport = FakeTransport(scope, run, contents)
        transport.scopes[1]["credentialId"] = "d" * 64
        reader = adapter.WorkflowRunReadAdapter(
            transport, 123, 101, 1, REV, SHA256, "c" * 64, NOW)
        with self.assertRaisesRegex(adapter.Refused, "workflow-scope-drift"):
            reader.observe_workflow()

    def test_redirect_duplicate_and_secret_exception_refuse(self):
        scope, run, contents = fixture()
        transport = FakeTransport(scope, run, contents)
        transport.events[0] = (RUN_PATH, adapter.Response(302, (("Location", "https://evil.invalid"),), b"{}"))
        reader = adapter.WorkflowRunReadAdapter(
            transport, 123, 101, 1, REV, SHA256, "c" * 64, NOW)
        with self.assertRaises(adapter.Refused):
            reader.observe_workflow()
        transport = FakeTransport(scope, run, contents)
        raw = json.dumps(run).encode()[:-1] + b',"id":101}'
        transport.events[0] = (RUN_PATH, adapter.Response(200, (), raw))
        reader = adapter.WorkflowRunReadAdapter(
            transport, 123, 101, 1, REV, SHA256, "c" * 64, NOW)
        with self.assertRaisesRegex(adapter.Refused, "workflow-json-duplicate"):
            reader.observe_workflow()
        class Broken(FakeTransport):
            def get(self, path):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        reader = adapter.WorkflowRunReadAdapter(
            Broken(scope, run, contents), 123, 101, 1, REV, SHA256, "c" * 64, NOW)
        with self.assertRaisesRegex(adapter.Refused, "workflow-read-unavailable") as error:
            reader.observe_workflow()
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(error.exception))


if __name__ == "__main__":
    unittest.main()
