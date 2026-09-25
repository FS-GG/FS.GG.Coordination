"""Fake mint-metadata and effective-scope controls; no token is present."""

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_app_credential_record_adapter as app
import callable_isolated_v2_target_scope_read_adapter as target

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
    digest = hashlib.sha256(canonical(selected)).hexdigest()
    response_hashes = tuple(str(index) * 64 for index in range(1, 5))
    scope = {"principalId": "mint-metadata-reader",
             "credentialId": "5" * 64,
             "store": "coordination-app-mint-metadata",
             "permissions": ["read-mint-metadata"],
             "expiresAt": "2026-09-25T12:30:00Z"}
    permissions = copy.deepcopy(app.PERMISSIONS)
    record = {"schema": app.SCHEMA, "state": "issued-metadata-only",
        "recordId": 400, "credentialId": "6" * 64,
        "appId": 700, "installationId": 222,
        "repositoryIds": [111], "permissions": permissions,
        "issuedAt": "2026-09-25T11:55:00Z",
        "expiresAt": "2026-09-25T12:45:00Z",
        "targetSha256": digest, "responseSha256": list(response_hashes),
        "prestateSha256": "c" * 64,
        "request": {"method": "POST",
                    "path": "app/installations/222/access_tokens",
                    "repository_ids": [111], "permissions": permissions},
        "redactedResponse": {"expires_at": "2026-09-25T12:45:00Z",
            "permissions": permissions,
            "repository_selection": "selected",
            "repositories": [{"id": 111, "node_id": "R_111",
                             "full_name": "FS-GG/v2-synthetic",
                             "url": "https://api.github.com/repos/FS-GG/v2-synthetic"}]}}
    witness = {"schema": app.WITNESS_SCHEMA, "complete": True,
        "principalId": "effective-scope-reader",
        "observerCredentialId": "7" * 64,
        "witnessId": 401, "credentialId": "6" * 64,
        "appId": 700, "installationId": 222,
        "repositoryIds": [111], "permissions": permissions,
        "targetSha256": digest, "responseSha256": list(response_hashes),
        "prestateSha256": "c" * 64,
        "expiresAt": "2026-09-25T12:45:00Z",
        "observedAt": "2026-09-25T11:59:00Z"}
    return selected, digest, response_hashes, scope, record, witness


class FakeMint:
    def __init__(self, scope, raw):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.raw = raw
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read_mint_metadata(self, record_id):
        self.reads.append(record_id)
        return self.raw


class FakeWitness:
    def __init__(self, value):
        self.value = copy.deepcopy(value)
        self.calls = []

    def read_effective_scope(self, credential_id, mint_sha256):
        self.calls.append((credential_id, mint_sha256))
        result = copy.deepcopy(self.value)
        result.setdefault("mintSha256", mint_sha256)
        return result


class AppRecordTests(unittest.TestCase):
    def observe(self, selected=None, digest=None, response_hashes=None,
                scope=None, record=None, witness=None, raw=None):
        chosen, target_sha, response_sha, reader_scope, issue, effective = fixture()
        port = FakeMint(reader_scope if scope is None else scope,
                        canonical(issue if record is None else record)
                        if raw is None else raw)
        independent = FakeWitness(effective if witness is None else witness)
        adapter = app.AppCredentialRecordAdapter(
            port, independent, chosen if selected is None else selected,
            400, 700, "6" * 64, NOW)
        return adapter.read_target(target_sha if digest is None else digest,
            response_sha if response_hashes is None else response_hashes), port, independent

    def refuses(self, **changes):
        with self.assertRaises(app.Refused):
            self.observe(**changes)

    def test_matching_fake_is_metadata_only(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            value, mint, witness = self.observe()
        self.assertEqual(mint.reads, [400])
        self.assertEqual(witness.calls[0][0], "6" * 64)
        self.assertEqual(value["schema"], target.SCHEMA)
        self.assertEqual(value["credential"]["repositoryIds"], [111])
        self.assertEqual(value["credential"]["permissions"], app.PERMISSIONS)
        self.assertNotIn("token", repr(value).lower())
        self.assertFalse(hasattr(app, "dispatch"))

    def test_broad_or_foreign_mint_request_response_refuse(self):
        for section, key, changed in (
            ("root", "installationId", 223),
            ("root", "repositoryIds", [111, 112]),
            ("root", "permissions", {"metadata": "read", "contents": "write",
                                      "pull_requests": "write"}),
            ("request", "repository_ids", []),
            ("request", "path", "app/installations/223/access_tokens"),
            ("request", "permissions", {"metadata": "read", "contents": "read",
                                            "pull_requests": "write", "issues": "write"}),
            ("redactedResponse", "repository_selection", "all"),
            ("redactedResponse", "repositories", []),
            ("redactedResponse", "permissions", {"metadata": "read",
                                                     "contents": "read"})):
            with self.subTest(section=section, key=key):
                record = fixture()[4]
                destination = record if section == "root" else record[section]
                destination[key] = changed
                self.refuses(record=record)
        record = fixture()[4]
        record["token"] = "SYNTHETIC_SECRET_SENTINEL"
        self.refuses(record=record)

    def test_foreign_stale_or_self_witness_refuses(self):
        for key, changed in (
            ("complete", False), ("witnessId", 0),
            ("credentialId", "8" * 64), ("mintSha256", "8" * 64),
            ("appId", 701), ("installationId", 223),
            ("repositoryIds", [111, 112]),
            ("permissions", {"metadata": "read", "contents": "write",
                             "pull_requests": "write"}),
            ("responseSha256", ["8" * 64] * 4),
            ("prestateSha256", "8" * 64),
            ("principalId", "mint-metadata-reader"),
            ("observerCredentialId", "5" * 64),
            ("expiresAt", "2026-09-25T11:00:00Z"),
            ("observedAt", "2026-09-25T11:00:00Z")):
            with self.subTest(key=key):
                witness = fixture()[5]
                witness[key] = changed
                self.refuses(witness=witness)

    def test_wrong_target_response_noncanonical_and_scope_refuse(self):
        self.refuses(digest="8" * 64)
        self.refuses(response_hashes=("8" * 64,) * 4)
        selected = fixture()[0]
        selected["sourceRef"] = "refs/heads/../foreign"
        bad_digest = hashlib.sha256(canonical(selected)).hexdigest()
        record = fixture()[4]
        record["targetSha256"] = bad_digest
        witness = fixture()[5]
        witness["targetSha256"] = bad_digest
        self.refuses(selected=selected, digest=bad_digest,
                     record=record, witness=witness)
        self.refuses(raw=json.dumps(fixture()[4]).encode())
        self.refuses(raw=b'{"credentialId":"a","credentialId":"b"}')
        for key, changed in (
            ("store", "foreign"),
            ("permissions", ["read-mint-metadata", "write-mint"]),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            scope = fixture()[3]
            scope[key] = changed
            self.refuses(scope=scope)

    def test_scope_drift_and_secret_exception_refuse(self):
        selected, digest, responses, scope, record, witness = fixture()
        port = FakeMint(scope, canonical(record))
        port.scopes[1]["credentialId"] = "8" * 64
        with self.assertRaisesRegex(app.Refused, "app-record-reader-drift"):
            app.AppCredentialRecordAdapter(port, FakeWitness(witness),
                selected, 400, 700, "6" * 64, NOW).read_target(digest, responses)
        class Broken(FakeMint):
            def read_mint_metadata(self, record_id):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaisesRegex(app.Refused, "app-record-unavailable") as caught:
            app.AppCredentialRecordAdapter(Broken(scope, canonical(record)),
                FakeWitness(witness), selected, 400, 700, "6" * 64, NOW
            ).read_target(digest, responses)
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))

    def test_boolean_repository_id_in_mint_or_effective_scope_refuses(self):
        for location in ("record", "request", "redacted", "effective"):
            for alias in (True, 1.0):
                with self.subTest(location=location, alias=alias):
                    selected, _, responses, _, record, witness = fixture()
                    selected["repositoryId"] = 1
                    digest = hashlib.sha256(canonical(selected)).hexdigest()
                    record["targetSha256"] = digest
                    record["repositoryIds"] = [1]
                    record["request"]["repository_ids"] = [1]
                    record["redactedResponse"]["repositories"][0]["id"] = 1
                    witness["targetSha256"] = digest
                    witness["repositoryIds"] = [1]
                    if location == "record":
                        record["repositoryIds"] = [alias]
                    elif location == "request":
                        record["request"]["repository_ids"] = [alias]
                    elif location == "redacted":
                        record["redactedResponse"]["repositories"][0]["id"] = alias
                    else:
                        witness["repositoryIds"] = [alias]
                    self.refuses(selected=selected, digest=digest,
                                 response_hashes=responses, record=record,
                                 witness=witness)


if __name__ == "__main__":
    unittest.main()
