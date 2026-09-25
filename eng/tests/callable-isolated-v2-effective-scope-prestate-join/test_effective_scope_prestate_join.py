"""Independent fake metadata/prestate join controls; no token is present."""

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_effective_scope_prestate_join as joined
import callable_isolated_v2_app_credential_record_adapter as app
import callable_isolated_v2_target_prestate_read_adapter as prestate

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


def fixture():
    selected = {"repository": "FS-GG/v2-synthetic", "repositoryId": 111,
        "nodeId": "R_111", "installationId": 222,
        "sourceRef": "refs/heads/source", "sourceSha": "a" * 40,
        "baseRef": "refs/heads/base", "baseSha": "b" * 40,
        "prestateSha256": "c" * 64}
    target_sha = hashlib.sha256(canonical(selected)).hexdigest()
    prestate_observation = {"schema": prestate.SCHEMA, "complete": True,
        "principalId": "prestate-reader", "credentialId": "1" * 64,
        "witnessPrincipalId": "prestate-witness",
        "witnessCredentialId": "9" * 64,
        "witnessObservedAt": "2026-09-25T11:58:00Z",
        "witnessExpiresAt": "2026-09-25T12:15:00Z",
        "recordId": 333, "runId": 101, "runAttempt": 1,
        "target": copy.deepcopy(selected),
        "transcriptSha256": "d" * 64,
        "observedAt": "2026-09-25T11:59:00Z"}
    scope = {"principalId": "effective-reader", "credentialId": "2" * 64,
        "store": "coordination-effective-app-scope",
        "permissions": ["read-effective-metadata"],
        "expiresAt": "2026-09-25T12:20:00Z"}
    record = {"schema": joined.SCHEMA, "complete": True,
        "recordId": 401, "credentialId": "6" * 64,
        "mintSha256": "7" * 64, "appId": 700,
        "installationId": 222, "repositoryIds": [111],
        "permissions": copy.deepcopy(app.PERMISSIONS),
        "targetSha256": target_sha,
        "responseSha256": [str(index) * 64 for index in range(3, 7)],
        "prestateSha256": "c" * 64,
        "prestateRecordId": 333, "prestateTranscriptSha256": "d" * 64,
        "runId": 101, "runAttempt": 1,
        "issuedAt": "2026-09-25T11:55:00Z",
        "expiresAt": "2026-09-25T12:45:00Z",
        "observedAt": "2026-09-25T12:00:00Z"}
    return selected, prestate_observation, scope, record


class FakePrestate:
    def __init__(self, value):
        self.value = copy.deepcopy(value)
        self.calls = 0

    def observe_prestate(self):
        self.calls += 1
        return copy.deepcopy(self.value)


class FakeMetadata:
    def __init__(self, scope, raw):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.raw = raw
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read_effective_metadata(self, credential_id):
        self.reads.append(credential_id)
        return self.raw


class JoinTests(unittest.TestCase):
    def observe(self, selected=None, prestate_value=None, scope=None,
                record=None, raw=None, credential_id="6" * 64,
                mint_sha256="7" * 64):
        target, prestate_proof, reader_scope, metadata = fixture()
        port = FakeMetadata(reader_scope if scope is None else scope,
            canonical(metadata if record is None else record) if raw is None else raw)
        independent = FakePrestate(prestate_proof if prestate_value is None
                                   else prestate_value)
        adapter = joined.EffectiveScopePrestateJoin(port, independent,
            target if selected is None else selected, 700, 101, 1, NOW)
        return adapter.read_effective_scope(credential_id, mint_sha256), port, independent

    def refuses(self, **changes):
        with self.assertRaises(joined.Refused):
            self.observe(**changes)

    def test_matching_fake_is_closed_and_joins_record_ids(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result, port, prestate_port = self.observe()
        self.assertEqual(port.reads, ["6" * 64])
        self.assertEqual(prestate_port.calls, 1)
        self.assertEqual(result["schema"], app.WITNESS_SCHEMA)
        self.assertEqual(result["prestateSha256"], "c" * 64)
        self.assertEqual(result["repositoryIds"], [111])
        self.assertNotIn("token", repr(result).lower())
        self.assertFalse(hasattr(joined, "dispatch"))

    def test_foreign_or_replayed_prestate_refuses(self):
        for key, changed in (
            ("complete", False), ("recordId", 0),
            ("runId", 102), ("runAttempt", 2),
            ("transcriptSha256", "8" * 64),
            ("principalId", "effective-reader"),
            ("credentialId", "2" * 64),
            ("witnessPrincipalId", "effective-reader"),
            ("witnessPrincipalId", "prestate-reader"),
            ("witnessCredentialId", "2" * 64),
            ("witnessCredentialId", "1" * 64),
            ("witnessObservedAt", "2026-09-25T11:00:00Z"),
            ("witnessObservedAt", "2026-09-25T12:01:00Z"),
            ("witnessExpiresAt", "2026-09-25T11:59:00Z"),
            ("observedAt", "2026-09-25T11:50:00Z")):
            with self.subTest(key=key):
                proof = fixture()[1]
                proof[key] = changed
                self.refuses(prestate_value=proof)
        proof = fixture()[1]
        proof["target"]["repositoryId"] = 112
        self.refuses(prestate_value=proof)
        for key in ("witnessPrincipalId", "witnessCredentialId",
                    "witnessObservedAt", "witnessExpiresAt"):
            with self.subTest(missing=key):
                proof = fixture()[1]
                del proof[key]
                self.refuses(prestate_value=proof)

    def test_metadata_read_cannot_replace_earlier_prestate_transcript(self):
        selected, proof, scope, record = fixture()
        proof["transcriptSha256"] = "8" * 64
        metadata = FakeMetadata(scope, canonical(record))
        prestate_port = FakePrestate(proof)
        shared_proof = copy.deepcopy(proof)
        original_metadata_read = metadata.read_effective_metadata

        def observe_prestate():
            return shared_proof

        def read_metadata(credential_id):
            shared_proof["transcriptSha256"] = "d" * 64
            return original_metadata_read(credential_id)

        prestate_port.observe_prestate = observe_prestate
        metadata.read_effective_metadata = read_metadata
        with self.assertRaises(joined.Refused):
            joined.EffectiveScopePrestateJoin(
                metadata, prestate_port, selected, 700, 101, 1, NOW
            ).read_effective_scope("6" * 64, "7" * 64)

    def test_broad_foreign_or_stale_effective_record_refuses(self):
        for key, changed in (
            ("complete", False), ("recordId", 0),
            ("credentialId", "8" * 64), ("mintSha256", "8" * 64),
            ("appId", 701), ("installationId", 223),
            ("repositoryIds", [111, 112]),
            ("permissions", {"metadata": "read", "contents": "write",
                             "pull_requests": "write"}),
            ("targetSha256", "8" * 64),
            ("prestateSha256", "8" * 64),
            ("prestateRecordId", 334),
            ("prestateTranscriptSha256", "8" * 64),
            ("runId", 102), ("runAttempt", 2),
            ("issuedAt", "2026-09-25T12:00:00Z"),
            ("observedAt", "2026-09-25T11:58:00Z"),
            ("expiresAt", "2026-09-25T11:59:00Z")):
            with self.subTest(key=key):
                record = fixture()[3]
                record[key] = changed
                self.refuses(record=record)
        record = fixture()[3]
        record["token"] = "SYNTHETIC_SECRET_SENTINEL"
        self.refuses(record=record)

    def test_noncanonical_broad_scope_and_wrong_credential_refuse(self):
        self.refuses(raw=json.dumps(fixture()[3]).encode())
        self.refuses(raw=b'{"credentialId":"a","credentialId":"b"}')
        self.refuses(credential_id="8" * 64)
        self.refuses(mint_sha256="8" * 64)
        for key, changed in (
            ("store", "foreign"),
            ("permissions", ["read-effective-metadata", "write-effective"]),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            scope = fixture()[2]
            scope[key] = changed
            self.refuses(scope=scope)

    def test_scope_drift_and_exception_refuse(self):
        selected, proof, scope, record = fixture()
        port = FakeMetadata(scope, canonical(record))
        port.scopes[1]["credentialId"] = "8" * 64
        with self.assertRaisesRegex(joined.Refused, "effective-reader-drift"):
            joined.EffectiveScopePrestateJoin(
                port, FakePrestate(proof), selected, 700, 101, 1, NOW
            ).read_effective_scope("6" * 64, "7" * 64)
        class Broken(FakeMetadata):
            def read_effective_metadata(self, credential_id):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaisesRegex(joined.Refused, "effective-record-unavailable") as caught:
            joined.EffectiveScopePrestateJoin(
                Broken(scope, canonical(record)), FakePrestate(proof),
                selected, 700, 101, 1, NOW
            ).read_effective_scope("6" * 64, "7" * 64)
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))

    def test_reused_scope_mutated_at_final_read_refuses(self):
        selected, proof, scope, record = fixture()
        port = FakeMetadata(scope, canonical(record))
        shared = copy.deepcopy(scope)
        reads = [0]
        def reused_scope():
            reads[0] += 1
            if reads[0] == 2:
                shared["credentialId"] = "8" * 64
            return shared
        port.scope = reused_scope
        with self.assertRaises(joined.Refused):
            joined.EffectiveScopePrestateJoin(
                port, FakePrestate(proof), selected, 700, 101, 1, NOW
            ).read_effective_scope("6" * 64, "7" * 64)

    def test_boolean_repository_id_alias_refuses(self):
        selected, proof, _, record = fixture()
        selected["repositoryId"] = 1
        proof["target"] = copy.deepcopy(selected)
        record["repositoryIds"] = [True]
        record["targetSha256"] = hashlib.sha256(canonical(selected)).hexdigest()
        self.refuses(selected=selected, prestate_value=proof, record=record)

        proof["target"]["repositoryId"] = True
        record["repositoryIds"] = [1]
        self.refuses(selected=selected, prestate_value=proof, record=record)


if __name__ == "__main__":
    unittest.main()
