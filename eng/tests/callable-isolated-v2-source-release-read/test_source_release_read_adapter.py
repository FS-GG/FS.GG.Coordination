"""Fake provider and bundle controls for the held source/release observer."""

import copy
import datetime as dt
import hashlib
import io
import json
import pathlib
import sys
import unittest
import zipfile
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_source_release_read_adapter as source
import callable_isolated_v2_workflow_read_adapter as workflow

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
ROOT = "https://api.github.com/repos/FS-GG/FS.GG.Coordination"
REV = "a" * 40
TREE = "b" * 40
PATHS = (f"repos/{source.REPOSITORY}/git/commits/{REV}",
         f"repos/{source.REPOSITORY}/actions/runs/100/attempts/1",
         f"repos/{source.REPOSITORY}/actions/artifacts/101")


def bundle(members=None):
    values = {source.MEMBERS["operator"]: b"future-operator",
              source.MEMBERS["controls"]: b"future-controls",
              source.MEMBERS["archive"]: b"future-effect-zipapp"}
    if members is not None:
        values = members
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, data in values.items():
            archive.writestr(name, data)
    return output.getvalue()


def fixture():
    raw = bundle()
    scope = {"principalId": "source-reader", "credentialId": "1" * 64,
             "repository": source.REPOSITORY, "repositoryId": 50,
             "permissions": {"actions": "read", "contents": "read",
                             "metadata": "read"},
             "expiresAt": "2026-09-25T12:20:00Z"}
    actor = {"id": 102, "login": "producer"}
    responses = [
        {"sha": REV, "url": ROOT + "/git/commits/" + REV,
         "tree": {"sha": TREE, "url": ROOT + "/git/trees/" + TREE}},
        {"id": 100, "run_attempt": 1, "head_sha": REV,
         "head_branch": "main", "event": "workflow_dispatch",
         "status": "completed", "conclusion": "success",
         "path": ".github/workflows/callable-isolated-v2-effect-release.yml",
         "repository": {"id": 50, "full_name": source.REPOSITORY},
         "head_repository": {"id": 50, "full_name": source.REPOSITORY},
         "actor": actor, "triggering_actor": copy.deepcopy(actor)},
        {"id": 101, "name": "callable-isolated-v2-effect",
         "url": ROOT + "/actions/artifacts/101",
         "archive_download_url": ROOT + "/actions/artifacts/101/zip",
         "expired": False, "size_in_bytes": len(raw),
         "digest": "sha256:" + hashlib.sha256(raw).hexdigest(),
         "workflow_run": {"id": 100, "repository_id": 50,
                          "head_repository_id": 50, "head_sha": REV}},
    ]
    attestation = {"schema": source.SCHEMA, "complete": True,
        "principalId": "release-attestor", "credentialId": "2" * 64,
        "recordId": 103, "revision": REV, "sourceTree": TREE,
        "repositoryId": 50, "runId": 100, "runAttempt": 1,
        "artifactId": 101, "producerActorId": 102,
        "workflowPath": ".github/workflows/callable-isolated-v2-effect-release.yml",
        "runtime": {"runnerImage": "ghcr.io/fs-gg/synthetic@sha256:" + "3" * 64,
                    "imageAttestationSha256": "4" * 64,
                    "interpreterSha256": "5" * 64, "closureSha256": "6" * 64},
        "observedAt": "2026-09-25T11:59:00Z"}
    return scope, responses, raw, attestation


class FakeTransport:
    def __init__(self, scope, responses):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.responses = copy.deepcopy(responses)
        self.paths = []

    def scope(self):
        return self.scopes.pop(0)

    def get(self, path):
        self.paths.append(path)
        if path != PATHS[len(self.paths) - 1]:
            raise AssertionError("wrong path")
        value = self.responses.pop(0)
        if isinstance(value, workflow.Response):
            return value
        return workflow.Response(200, (), json.dumps(value).encode())


class FakeBundle:
    def __init__(self, raw):
        self.raw = raw
        self.calls = []

    def read_bundle(self, artifact_id, download_url):
        self.calls.append((artifact_id, download_url))
        return self.raw


class FakeAttestor:
    def __init__(self, value):
        self.value = copy.deepcopy(value)
        self.calls = []

    def read_release(self, revision, run_id, artifact_id, response_sha256,
                     bundle_sha256, member_sha256):
        self.calls.append((revision, run_id, artifact_id, response_sha256,
                           bundle_sha256, member_sha256))
        result = copy.deepcopy(self.value)
        result.setdefault("responseSha256", list(response_sha256))
        result.setdefault("bundleSha256", bundle_sha256)
        result.setdefault("memberSha256", list(member_sha256))
        return result


class SourceReleaseTests(unittest.TestCase):
    def observe(self, scope=None, responses=None, raw=None, attestation=None):
        selected_scope, selected_responses, selected_raw, selected_attestation = fixture()
        transport = FakeTransport(selected_scope if scope is None else scope,
                                  selected_responses if responses is None else responses)
        bundle_reader = FakeBundle(selected_raw if raw is None else raw)
        attestor = FakeAttestor(selected_attestation if attestation is None
                               else attestation)
        adapter = source.SourceReleaseReadAdapter(transport, bundle_reader,
            attestor, REV, 50, 100, 1, 101, 102,
            ".github/workflows/callable-isolated-v2-effect-release.yml",
            "f" * 64, NOW)
        return adapter.observe_source_release(), transport, bundle_reader, attestor

    def refuses(self, **changes):
        with self.assertRaises(source.Refused):
            self.observe(**changes)

    def test_matching_fake_is_read_only(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result, transport, bundle_reader, attestor = self.observe()
        self.assertEqual(transport.paths, list(PATHS))
        self.assertEqual(bundle_reader.calls,
            [(101, ROOT + "/actions/artifacts/101/zip")])
        self.assertEqual(attestor.calls[0][:3], (REV, 100, 101))
        self.assertEqual(set(result["blobs"]), {"operator", "controls", "archive"})
        self.assertEqual(result["envelope"]["facts"]["sourceTree"], TREE)
        self.assertNotIn("authorized", result)
        self.assertFalse(hasattr(source, "dispatch"))

    def test_wrong_commit_run_actor_or_artifact_refuses(self):
        for index, container, key, value in (
            (0, None, "sha", "c" * 40),
            (0, "tree", "sha", "c" * 40),
            (1, None, "run_attempt", 2),
            (1, None, "conclusion", "failure"),
            (1, None, "repository", []),
            (1, None, "head_sha", "c" * 40),
            (1, "actor", "id", 103),
            (1, "head_repository", "id", 51),
            (2, None, "id", 102),
            (2, None, "archive_download_url", ROOT + "/foreign"),
            (2, "workflow_run", "head_sha", "c" * 40)):
            with self.subTest(index=index, key=key):
                responses = fixture()[1]
                obj = responses[index] if container is None else responses[index][container]
                obj[key] = value
                self.refuses(responses=responses)

    def test_bundle_members_size_and_digest_refuse(self):
        self.refuses(raw=b"not-a-zip")
        missing = bundle({source.MEMBERS["operator"]: b"operator"})
        self.refuses(raw=missing)
        raw = bundle()
        self.refuses(raw=raw + b"changed")
        responses = fixture()[1]
        responses[2]["digest"] = "sha256:" + "a" * 64
        self.refuses(responses=responses)
        responses = fixture()[1]
        del responses[2]["digest"]
        self.refuses(responses=responses)
        responses = fixture()[1]
        responses[2]["size_in_bytes"] += 1
        self.refuses(responses=responses)

    def test_broad_scope_foreign_or_stale_attestation_refuses(self):
        for key, value in (
            ("repositoryId", 51), ("permissions", {"actions": "write",
                                                "contents": "read", "metadata": "read"}),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            scope = fixture()[0]
            scope[key] = value
            self.refuses(scope=scope)
        for key, value in (
            ("recordId", 0), ("sourceTree", "c" * 40),
            ("runAttempt", 2), ("producerActorId", 103),
            ("responseSha256", ["a" * 64] * 3),
            ("bundleSha256", "a" * 64),
            ("principalId", "source-reader"),
            ("observedAt", "2026-09-25T11:00:00Z")):
            attestation = fixture()[3]
            attestation[key] = value
            self.refuses(attestation=attestation)

    def test_redirect_duplicate_scope_drift_and_exception_refuse(self):
        responses = fixture()[1]
        responses[0] = workflow.Response(302, (("Location", ROOT + "/foreign"),), b"{}")
        self.refuses(responses=responses)
        responses = fixture()[1]
        responses[0] = workflow.Response(200, (), b'{"sha":"a","sha":"b"}')
        self.refuses(responses=responses)
        scope, responses, raw, attestation = fixture()
        transport = FakeTransport(scope, responses)
        transport.scopes[1]["credentialId"] = "8" * 64
        with self.assertRaisesRegex(source.Refused, "source-scope-drift"):
            source.SourceReleaseReadAdapter(transport, FakeBundle(raw),
                FakeAttestor(attestation), REV, 50, 100, 1, 101, 102,
                ".github/workflows/callable-isolated-v2-effect-release.yml",
                "f" * 64, NOW).observe_source_release()
        class Broken(FakeTransport):
            def get(self, path):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaisesRegex(source.Refused, "source-read-unavailable") as caught:
            source.SourceReleaseReadAdapter(Broken(scope, responses), FakeBundle(raw),
                FakeAttestor(attestation), REV, 50, 100, 1, 101, 102,
                ".github/workflows/callable-isolated-v2-effect-release.yml",
                "f" * 64, NOW).observe_source_release()
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))

    def test_reused_source_reader_scope_mutated_at_final_read_refuses(self):
        scope, responses, raw, attestation = fixture()
        transport = FakeTransport(scope, responses)
        shared = copy.deepcopy(scope)
        reads = [0]
        def reused_scope():
            reads[0] += 1
            if reads[0] == 2:
                shared["credentialId"] = "8" * 64
            return shared
        transport.scope = reused_scope
        with self.assertRaises(source.Refused):
            source.SourceReleaseReadAdapter(transport, FakeBundle(raw),
                FakeAttestor(attestation), REV, 50, 100, 1, 101, 102,
                ".github/workflows/callable-isolated-v2-effect-release.yml",
                "f" * 64, NOW).observe_source_release()


if __name__ == "__main__":
    unittest.main()
