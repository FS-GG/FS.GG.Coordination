"""Token-free fake metadata join for the closed provider request."""

import dataclasses
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
sys.path.insert(0, str(ENG / "tests/callable-isolated-v2-provider-request-spec"))
sys.path.insert(0, str(ENG / "tests/callable-isolated-v2-native-request-custody"))
from test_provider_request_spec import matching
from test_native_request_custody import selected_target, NOW
import callable_isolated_v2_app_credential_record_adapter as app
import callable_isolated_v2_provider_request_spec as provider
import callable_isolated_v2_provider_credential_metadata as credential


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


def fixture():
    joined, verified, native = matching()
    spec = provider.prepare(joined, verified, native,
                            "https://api.github.com")
    effective = {"schema": app.WITNESS_SCHEMA, "complete": True,
        "principalId": "scope-observer", "observerCredentialId": "c" * 64,
        "witnessId": 700, "credentialId": "b" * 64,
        "mintSha256": "a" * 64, "appId": 900,
        "installationId": 301, "repositoryIds": [300],
        "permissions": app.PERMISSIONS,
        "targetSha256": hashlib.sha256(canonical(selected_target())).hexdigest(),
        "responseSha256": ["d" * 64, "e" * 64, "f" * 64, "1" * 64],
        "prestateSha256": "4" * 64,
        "expiresAt": "2026-09-25T12:20:00Z",
        "observedAt": "2026-09-25T11:59:00Z"}
    return spec, native, effective


class ProviderCredentialMetadataTests(unittest.TestCase):
    def check(self, spec=None, native=None, effective=None):
        s, n, e = fixture()
        return credential.qualify(s if spec is None else spec,
            n if native is None else native,
            e if effective is None else effective,
            selected_target(), "b" * 64, "a" * 64, 900,
            "scope-observer", "c" * 64, NOW)

    def test_matching_metadata_remains_closed(self):
        result = self.check()
        self.assertEqual(result.credential_id, "b" * 64)
        self.assertEqual(result.repository_id, 300)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_foreign_app_repo_scope_or_target_refuses(self):
        mutations = (("appId", 901), ("installationId", 302),
                     ("repositoryIds", [300, 301]),
                     ("repositoryIds", [True]),
                     ("permissions", {"metadata": "read",
                                      "contents": "read",
                                      "pull_requests": "admin"}),
                     ("targetSha256", "9" * 64),
                     ("prestateSha256", "9" * 64),
                     ("credentialId", "9" * 64),
                     ("mintSha256", "9" * 64))
        for key, value in mutations:
            with self.subTest(key=key, value=value):
                spec, native, effective = fixture()
                effective[key] = value
                with self.assertRaises(credential.Refused):
                    self.check(spec, native, effective)

    def test_reader_identity_and_time_refuse(self):
        for key, value in (("principalId", "foreign-reader"),
                           ("observerCredentialId", "b" * 64),
                           ("witnessId", 0),
                           ("observedAt", "2026-09-25T11:00:00Z"),
                           ("expiresAt", "2026-09-25T12:10:00Z")):
            with self.subTest(key=key):
                spec, native, effective = fixture()
                effective[key] = value
                with self.assertRaises(credential.Refused):
                    self.check(spec, native, effective)

    def test_native_installation_or_request_swap_refuses(self):
        spec, native, effective = fixture()
        for field, value in (("repository_id", 301),
                             ("installation_id", 302),
                             ("grant_expires_at", "2026-09-25T12:25:00Z"),
                             ("path", "repos/FS-GG/foreign/pulls"),
                             ("method", "PUT"),
                             ("max_provider_writes", 2),
                             ("canonical_request", b"{}"),
                             ("operation_identity", "foreign")):
            with self.subTest(field=field), self.assertRaises(credential.Refused):
                self.check(spec, dataclasses.replace(native, **{field: value}),
                           effective)

    def test_extra_or_missing_metadata_refuses(self):
        spec, native, effective = fixture()
        effective["token"] = "SECRET"
        with self.assertRaises(credential.Refused):
            self.check(spec, native, effective)
        del effective["token"]
        del effective["witnessId"]
        with self.assertRaises(credential.Refused):
            self.check(spec, native, effective)

    def test_no_token_file_socket_or_journal_access(self):
        spec, native, effective = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("sqlite")):
            result = self.check(spec, native, effective)
        self.assertFalse(result.can_dispatch)


if __name__ == "__main__":
    unittest.main()
