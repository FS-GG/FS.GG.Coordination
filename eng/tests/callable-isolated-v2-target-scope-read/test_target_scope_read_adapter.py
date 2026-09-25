"""Independent fake reads for the held selected-target/App-scope port."""

import copy
import datetime as dt
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_target_scope_read_adapter as target
import callable_isolated_v2_workflow_read_adapter as workflow

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
REPO = "FS-GG/v2-synthetic"
API = "https://api.github.com"
PATHS = (f"repos/{REPO}", "installation/repositories?per_page=100",
         f"repos/{REPO}/git/ref/heads/source",
         f"repos/{REPO}/git/ref/heads/base")


def fixture():
    selected = {"repository": REPO, "repositoryId": 111,
        "nodeId": "R_111", "installationId": 222,
        "sourceRef": "refs/heads/source", "sourceSha": "a" * 40,
        "baseRef": "refs/heads/base", "baseSha": "b" * 40,
        "prestateSha256": "c" * 64}
    scope = {"principalId": "target-reader", "credentialId": "d" * 64,
        "installationId": 222, "repositoryIds": [111],
        "permissions": {"metadata": "read", "contents": "read"},
        "expiresAt": "2026-09-25T12:30:00Z"}
    repo = {"id": 111, "node_id": "R_111", "full_name": REPO,
            "name": "v2-synthetic", "url": f"{API}/repos/{REPO}",
            "archived": False, "disabled": False}
    accessible = {"total_count": 1, "repositories": [copy.deepcopy(repo)]}
    def ref(name, sha):
        path = f"repos/{REPO}/git/ref/heads/{name}"
        return {"ref": f"refs/heads/{name}", "url": f"{API}/{path}",
                "object": {"type": "commit", "sha": sha,
                           "url": f"{API}/repos/{REPO}/git/commits/{sha}"}}
    reads = [repo, accessible, ref("source", "a" * 40),
             ref("base", "b" * 40)]
    attestation = {"schema": target.SCHEMA, "complete": True,
        "principalId": "scope-attestor", "credentialId": "e" * 64,
        "recordId": 333, "prestateSha256": "c" * 64,
        "credential": {"kind": "github-app-installation",
                       "installationId": 222, "repositoryIds": [111],
                       "permissions": copy.deepcopy(target.PERMISSIONS),
                       "expiresAt": "2026-09-25T12:45:00Z"}}
    return selected, scope, reads, attestation


class FakeTransport:
    def __init__(self, scope, reads):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.reads = copy.deepcopy(reads)
        self.paths = []

    def scope(self):
        return self.scopes.pop(0)

    def get(self, path):
        self.paths.append(path)
        if path != PATHS[len(self.paths) - 1]:
            raise AssertionError("wrong path")
        value = self.reads.pop(0)
        if isinstance(value, workflow.Response):
            return value
        return workflow.Response(200, (), json.dumps(value).encode())


class FakeAttestor:
    def __init__(self, attestation):
        self.attestation = copy.deepcopy(attestation)
        self.calls = []

    def read_target(self, target_sha256, response_sha256):
        self.calls.append((target_sha256, response_sha256))
        result = copy.deepcopy(self.attestation)
        result.setdefault("targetSha256", target_sha256)
        result.setdefault("responseSha256", list(response_sha256))
        return result


class TargetScopeTests(unittest.TestCase):
    def observe(self, selected=None, scope=None, reads=None, attestation=None):
        wanted, reader_scope, provider_reads, attested = fixture()
        transport = FakeTransport(reader_scope if scope is None else scope,
                                  provider_reads if reads is None else reads)
        independent = FakeAttestor(attested if attestation is None else attestation)
        adapter = target.TargetScopeReadAdapter(transport, independent,
            wanted if selected is None else selected, "f" * 64, NOW)
        return adapter.observe_target_scope(), transport, independent

    def refuses(self, **changes):
        with self.assertRaises(target.Refused):
            self.observe(**changes)

    def test_matching_fake_is_read_only(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result, transport, independent = self.observe()
        self.assertEqual(transport.paths, list(PATHS))
        self.assertEqual(len(independent.calls[0][1]), 4)
        self.assertEqual(result["envelope"]["facts"]["target"]["repositoryId"], 111)
        self.assertEqual(result["envelope"]["facts"]["credential"]["repositoryIds"], [111])
        self.assertEqual(result["blobs"], {})
        self.assertNotIn("authorized", result)
        self.assertFalse(hasattr(target, "dispatch"))

    def test_wrong_repository_list_and_ref_refuse(self):
        for index, location, key, change in (
            (0, None, "id", 112), (0, None, "node_id", "R_foreign"),
            (0, None, "url", API + "/repos/FS-GG/foreign"),
            (1, None, "total_count", 2),
            (1, "repositories", "id", 112),
            (2, None, "ref", "refs/heads/foreign"),
            (2, "object", "sha", "b" * 40),
            (3, None, "url", API + "/repos/FS-GG/foreign/git/ref/heads/base")):
            with self.subTest(index=index, key=key):
                reads = fixture()[2]
                obj = reads[index]
                if location == "repositories":
                    obj = obj["repositories"][0]
                elif location:
                    obj = obj[location]
                obj[key] = change
                self.refuses(reads=reads)
        reads = fixture()[2]
        reads[1]["repositories"].append(copy.deepcopy(reads[1]["repositories"][0]))
        self.refuses(reads=reads)

    def test_broad_or_foreign_reader_scope_refuses(self):
        for key, changed in (
            ("installationId", 223), ("repositoryIds", [111, 112]),
            ("permissions", {"metadata": "read", "contents": "read",
                             "pull_requests": "write"}),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            with self.subTest(key=key):
                scope = fixture()[1]
                scope[key] = changed
                self.refuses(scope=scope)

    def test_foreign_or_unsealed_attestation_refuses(self):
        for key, changed in (
            ("recordId", 0), ("principalId", "target-reader"),
            ("credentialId", "d" * 64), ("targetSha256", "a" * 64),
            ("responseSha256", ["a" * 64] * 4),
            ("prestateSha256", "a" * 64), ("complete", False)):
            with self.subTest(key=key):
                attestation = fixture()[3]
                attestation[key] = changed
                self.refuses(attestation=attestation)
        for key, changed in (
            ("installationId", 223), ("repositoryIds", [111, 112]),
            ("permissions", {"metadata": "read", "contents": "read",
                             "pull_requests": "admin"}),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            attestation = fixture()[3]
            attestation["credential"][key] = changed
            self.refuses(attestation=attestation)

    def test_selection_redirect_drift_and_sanitized_exception_refuse(self):
        for key, changed in (
            ("repository", "FS-GG/.github"),
            ("repositoryId", True), ("sourceRef", "refs/heads/../foreign"),
            ("sourceSha", "0" * 40)):
            selected = fixture()[0]
            selected[key] = changed
            self.refuses(selected=selected)
        reads = fixture()[2]
        reads[0] = workflow.Response(302, (("Location", API + "/foreign"),), b"{}")
        self.refuses(reads=reads)
        reads = fixture()[2]
        reads[0] = workflow.Response(200, (), b'{"id":111,"id":111}')
        self.refuses(reads=reads)
        wanted, scope, reads, attested = fixture()
        transport = FakeTransport(scope, reads)
        transport.scopes[1]["credentialId"] = "a" * 64
        with self.assertRaisesRegex(target.Refused, "target-reader-scope-drift"):
            target.TargetScopeReadAdapter(transport, FakeAttestor(attested),
                wanted, "f" * 64, NOW).observe_target_scope()
        class Broken(FakeTransport):
            def get(self, path):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaisesRegex(target.Refused, "target-read-unavailable") as caught:
            target.TargetScopeReadAdapter(Broken(scope, reads), FakeAttestor(attested),
                wanted, "f" * 64, NOW).observe_target_scope()
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))

    def test_reused_target_reader_scope_mutated_at_final_read_refuses(self):
        wanted, scope, reads, attested = fixture()
        transport = FakeTransport(scope, reads)
        shared = copy.deepcopy(scope)
        calls = [0]
        def reused_scope():
            calls[0] += 1
            if calls[0] == 2:
                shared["credentialId"] = "a" * 64
            return shared
        transport.scope = reused_scope
        with self.assertRaises(target.Refused):
            target.TargetScopeReadAdapter(transport, FakeAttestor(attested),
                wanted, "f" * 64, NOW).observe_target_scope()


if __name__ == "__main__":
    unittest.main()
