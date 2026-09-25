"""Fake native GET and witness controls for a held absent prestate."""

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_target_prestate_read_adapter as prestate

NATIVE = prestate.NATIVE
NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
REPO = "FS-GG/v2-synthetic"
PREFIX = "repos/" + REPO


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


def sha(value):
    return hashlib.sha256(value).hexdigest()


def ref(name, oid):
    path = PREFIX + "/git/ref/heads/" + name
    return {"ref": "refs/heads/" + name,
            "url": "https://api.github.com/" +
                   PREFIX + "/git/refs/heads/" + name,
            "object": {"type": "commit", "sha": oid,
                       "url": "https://api.github.com/" + PREFIX +
                              "/git/commits/" + oid}}


def events(pulls=()):
    listed = [{key: copy.deepcopy(value) for key, value in pull.items()
               if key in {"number", "node_id", "state", "draft", "title",
                          "body", "head", "base"}} for pull in pulls]
    one = [
        (PREFIX, {"id": 111, "full_name": REPO}),
        (PREFIX + "/git/ref/heads/source", ref("source", "a" * 40)),
        (PREFIX + "/git/ref/heads/base", ref("base", "b" * 40)),
        (PREFIX + "/pulls?state=open&per_page=100&page=1", listed),
        (PREFIX + "/pulls?state=open&per_page=100&page=2", []),
    ]
    one.extend((PREFIX + "/pulls/" + str(pull["number"]), copy.deepcopy(pull))
               for pull in pulls)
    one.extend([
        (PREFIX, {"id": 111, "full_name": REPO}),
        (PREFIX + "/git/ref/heads/source", ref("source", "a" * 40)),
        (PREFIX + "/git/ref/heads/base", ref("base", "b" * 40)),
    ])
    return one + copy.deepcopy(one)


def fixture():
    selected = {"repository": REPO, "repositoryId": 111,
        "nodeId": "R_111", "installationId": 222,
        "sourceRef": "refs/heads/source", "sourceSha": "a" * 40,
        "baseRef": "refs/heads/base", "baseSha": "b" * 40}
    emitted = events()
    transcript = [{"path": path, "status": 200, "link": "",
                   "bodySha256": sha(json.dumps(value).encode())}
                  for path, value in emitted[:8]]
    transcript_sha = sha(canonical(transcript))
    selected["prestateSha256"] = sha(canonical({
        "schema": prestate.SCHEMA, "repository": REPO,
        "repositoryId": 111, "nodeId": "R_111", "installationId": 222,
        "sourceRef": "refs/heads/source", "sourceSha": "a" * 40,
        "baseRef": "refs/heads/base", "baseSha": "b" * 40,
        "runId": 101, "runAttempt": 1,
        "transcriptSha256": transcript_sha}))
    scope = {"principalId": "prestate-reader", "credentialId": "1" * 64,
             "repository": REPO, "repositoryId": 111,
             "installationId": 222,
             "permissions": {"metadata": "read", "contents": "read",
                             "pull_requests": "read"},
             "expiresAt": "2026-09-25T12:20:00Z"}
    witness = {"schema": prestate.SCHEMA, "complete": True,
               "principalId": "prestate-witness", "credentialId": "2" * 64,
               "recordId": 333, "runId": 101, "runAttempt": 1,
               **copy.deepcopy(selected), "transcriptSha256": transcript_sha,
               "observedAt": "2026-09-25T11:59:00Z",
               "expiresAt": "2026-09-25T12:15:00Z"}
    return selected, scope, emitted, witness


class FakeTransport:
    def __init__(self, scope, emitted):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.emitted = copy.deepcopy(emitted)
        self.paths = []

    def scope(self):
        return self.scopes.pop(0)

    def get(self, path):
        self.paths.append(path)
        expected, value = self.emitted.pop(0)
        if path != expected:
            raise AssertionError("wrong GET")
        if isinstance(value, NATIVE.HttpResponse):
            return value
        return NATIVE.HttpResponse(200, (), json.dumps(value).encode())


class FakeWitness:
    def __init__(self, value):
        self.value = copy.deepcopy(value)
        self.calls = []

    def read_prestate(self, run_id, run_attempt, prestate_sha256,
                      transcript_sha256):
        self.calls.append((run_id, run_attempt, prestate_sha256,
                           transcript_sha256))
        return copy.deepcopy(self.value)


class PrestateTests(unittest.TestCase):
    def observe(self, selected=None, scope=None, emitted=None, witness=None):
        target, reader_scope, reads, proof = fixture()
        transport = FakeTransport(reader_scope if scope is None else scope,
                                  reads if emitted is None else emitted)
        independent = FakeWitness(proof if witness is None else witness)
        adapter = prestate.CompleteTargetPrestateAdapter(
            transport, independent, target if selected is None else selected,
            101, 1, NOW)
        return adapter.observe_prestate(), transport, independent

    def refuses(self, **changes):
        with self.assertRaises(prestate.Refused):
            self.observe(**changes)

    def test_two_matching_absent_censuses_are_get_only(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result, transport, witness = self.observe()
        self.assertEqual(len(transport.paths), 16)
        self.assertEqual(witness.calls[0][:2], (101, 1))
        self.assertEqual(result["target"]["prestateSha256"],
                         fixture()[0]["prestateSha256"])
        self.assertEqual(result["witnessPrincipalId"], "prestate-witness")
        self.assertEqual(result["witnessCredentialId"], "2" * 64)
        self.assertEqual(result["witnessObservedAt"], "2026-09-25T11:59:00Z")
        self.assertEqual(result["witnessExpiresAt"], "2026-09-25T12:15:00Z")
        self.assertNotIn("authorized", result)
        self.assertFalse(hasattr(prestate, "dispatch"))

    def test_foreign_repo_refs_or_changed_second_snapshot_refuse(self):
        for index, key, changed in (
            (0, "id", 112), (1, "ref", "refs/heads/foreign"),
            (2, "url", "https://api.github.com/foreign"),
            (8, "id", 112), (9, "ref", "refs/heads/foreign")):
            with self.subTest(index=index, key=key):
                emitted = fixture()[2]
                emitted[index][1][key] = changed
                self.refuses(emitted=emitted)
        emitted = fixture()[2]
        emitted[12] = (emitted[12][0], [{"number": 1}])
        self.refuses(emitted=emitted)

    def test_matching_pull_or_incomplete_pagination_refuses(self):
        selected = fixture()[0]
        expected = NATIVE.ExpectedPull(NATIVE.OPERATION_IDENTITY, 1, 111,
            REPO, selected["sourceRef"], selected["sourceSha"],
            selected["baseRef"], selected["baseSha"])
        pull = {"number": 1, "node_id": "PR_1", "state": "open",
                "draft": False, "merged": False, "title": NATIVE.PULL_TITLE,
                "body": NATIVE.pull_request_body(expected)["body"],
                "head": {"ref": "source", "sha": "a" * 40,
                         "repo": {"id": 111, "full_name": REPO}},
                "base": {"ref": "base", "sha": "b" * 40,
                         "repo": {"id": 111, "full_name": REPO}}}
        self.refuses(emitted=events((pull,)))
        emitted = fixture()[2]
        emitted[4] = (emitted[4][0], NATIVE.HttpResponse(404, (), b"[]"))
        self.refuses(emitted=emitted)

    def test_wrong_digest_broad_scope_or_witness_refuses(self):
        selected = fixture()[0]
        selected["prestateSha256"] = "8" * 64
        self.refuses(selected=selected)
        for key, changed in (
            ("repositoryId", 112),
            ("permissions", {"metadata": "read", "contents": "read",
                             "pull_requests": "write"}),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            scope = fixture()[1]
            scope[key] = changed
            self.refuses(scope=scope)
        for key, changed in (
            ("complete", False), ("recordId", 0),
            ("runAttempt", 2), ("repositoryId", 112),
            ("sourceSha", "8" * 40),
            ("transcriptSha256", "8" * 64),
            ("principalId", "prestate-reader"),
            ("credentialId", "1" * 64),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            with self.subTest(key=key):
                witness = fixture()[3]
                witness[key] = changed
                self.refuses(witness=witness)

    def test_scope_drift_and_secret_exception_refuse(self):
        selected, scope, emitted, witness = fixture()
        transport = FakeTransport(scope, emitted)
        transport.scopes[1]["credentialId"] = "8" * 64
        with self.assertRaisesRegex(prestate.Refused, "prestate-scope-drift"):
            prestate.CompleteTargetPrestateAdapter(
                transport, FakeWitness(witness), selected, 101, 1, NOW
            ).observe_prestate()
        class Broken(FakeTransport):
            def get(self, path):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaisesRegex(prestate.Refused, "prestate-native-incomplete") as caught:
            prestate.CompleteTargetPrestateAdapter(
                Broken(scope, emitted), FakeWitness(witness),
                selected, 101, 1, NOW).observe_prestate()
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))

    def test_reused_prestate_reader_scope_mutated_at_final_read_refuses(self):
        selected, scope, emitted, witness = fixture()
        transport = FakeTransport(scope, emitted)
        shared = copy.deepcopy(scope)
        reads = [0]
        def reused_scope():
            reads[0] += 1
            if reads[0] == 2:
                shared["credentialId"] = "8" * 64
            return shared
        transport.scope = reused_scope
        with self.assertRaises(prestate.Refused):
            prestate.CompleteTargetPrestateAdapter(
                transport, FakeWitness(witness), selected, 101, 1, NOW
            ).observe_prestate()

    def test_boolean_witness_numeric_id_alias_refuses(self):
        for field in ("installationId", "repositoryId"):
            with self.subTest(field=field):
                selected, scope, emitted, witness = fixture()
                selected[field] = 1
                scope[field] = 1
                witness[field] = True
                if field == "repositoryId":
                    for index in (0, 5, 8, 13):
                        emitted[index][1]["id"] = 1
                    transcript = [{"path": path, "status": 200, "link": "",
                                   "bodySha256": sha(json.dumps(value).encode())}
                                  for path, value in emitted[:8]]
                    witness["transcriptSha256"] = sha(canonical(transcript))
                digest_input = {key: value for key, value in selected.items()
                                if key != "prestateSha256"}
                digest_input.update(schema=prestate.SCHEMA, runId=101,
                                    runAttempt=1,
                                    transcriptSha256=witness["transcriptSha256"])
                selected["prestateSha256"] = sha(canonical(digest_input))
                witness["prestateSha256"] = selected["prestateSha256"]
                self.refuses(selected=selected, scope=scope, emitted=emitted,
                             witness=witness)


if __name__ == "__main__":
    unittest.main()
