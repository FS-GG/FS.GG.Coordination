"""Token-free fake public-handle custody controls; no secret bytes."""

import dataclasses
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
sys.path.insert(0, str(ENG / "tests/callable-isolated-v2-provider-credential-metadata"))
sys.path.insert(0, str(ENG / "tests/callable-isolated-v2-native-request-custody"))
from test_provider_credential_metadata import fixture as credential_fixture
from test_native_request_custody import selected_target, NOW
import callable_isolated_v2_provider_credential_metadata as credential
import callable_isolated_v2_token_custody_identity as token_identity


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


class FakeIdentityPort:
    def __init__(self, raw, scope):
        self.raw = raw
        self.scope_value = scope
        self.calls = []

    def scope(self):
        return self.scope_value

    def read_public_binding(self, event_id):
        self.calls.append(event_id)
        return self.raw


def fixture():
    spec, native, effective = credential_fixture()
    bound = credential.qualify(spec, native, effective, selected_target(),
        "b" * 64, "a" * 64, 900, "scope-observer", "c" * 64, NOW)
    target_sha = sha(canonical(selected_target()))
    record = {"schema": token_identity.BINDING_SCHEMA,
        "complete": True, "eventId": 800,
        "publicHandleId": "7" * 64,
        "publicCredentialId": "b" * 64,
        "appId": 900, "installationId": 301, "repositoryId": 300,
        "targetSha256": target_sha, "operationId": native.operation_id,
        "requestSha256": native.request_sha256,
        "runId": 200, "runAttempt": 1, "issuerActorId": 205,
        "readerPrincipalId": "token-custody-reader",
        "readerCredentialId": "d" * 64,
        "issuedAt": "2026-09-25T11:57:00Z",
        "observedAt": "2026-09-25T11:59:00Z",
        "expiresAt": "2026-09-25T12:20:00Z"}
    selected = {"eventId": 800, "eventSha256": sha(canonical(record)),
        "publicHandleId": "7" * 64,
        "readerPrincipalId": "token-custody-reader",
        "readerCredentialId": "d" * 64}
    scope = {"principalId": "token-custody-reader",
        "credentialId": "d" * 64,
        "store": "coordination-execution-token-custody",
        "permissions": ["read-public-token-binding"],
        "expiresAt": "2026-09-25T12:20:00Z"}
    return bound, native, selected, record, scope


def run(bound=None, native=None, selected=None, record=None, scope=None,
        raw=None):
    base_bound, base_native, base_selected, base_record, base_scope = fixture()
    selection = base_selected if selected is None else selected
    port = FakeIdentityPort(canonical(base_record if record is None else record)
                            if raw is None else raw,
                            base_scope if scope is None else scope)
    result = token_identity.observe(
        base_bound if bound is None else bound,
        base_native if native is None else native,
        selected_target(), selection, port, NOW)
    return result, port


class TokenCustodyIdentityTests(unittest.TestCase):
    def test_matching_public_binding_remains_closed(self):
        result, port = run()
        self.assertEqual(port.calls, [800])
        self.assertEqual(result.public_handle_id, "7" * 64)
        self.assertEqual(result.public_credential_id, "b" * 64)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_foreign_binding_refuses_even_with_recomputed_digest(self):
        changes = (("eventId", 801), ("publicHandleId", "8" * 64),
                   ("publicCredentialId", "9" * 64), ("appId", 901),
                   ("installationId", 302), ("repositoryId", 301),
                   ("targetSha256", "9" * 64),
                   ("operationId", "9" * 64),
                   ("requestSha256", "9" * 64),
                   ("runId", 201), ("runAttempt", 2),
                   ("issuerActorId", 206))
        for key, value in changes:
            with self.subTest(key=key):
                bound, native, selected, record, scope = fixture()
                record[key] = value
                selected["eventSha256"] = sha(canonical(record))
                with self.assertRaises(token_identity.Refused):
                    run(bound, native, selected, record, scope)

    def test_selected_digest_scope_or_shared_reader_refuses(self):
        bound, native, selected, record, scope = fixture()
        selected["eventSha256"] = "9" * 64
        with self.assertRaises(token_identity.Refused):
            run(bound, native, selected, record, scope)
        for key, value in (("principalId", "scope-observer"),
                           ("credentialId", "c" * 64),
                           ("permissions", ["read-public-token-binding",
                                            "read-token-bytes"]),
                           ("store", "foreign")):
            with self.subTest(key=key):
                bound, native, selected, record, scope = fixture()
                scope[key] = value
                with self.assertRaises(token_identity.Refused):
                    run(bound, native, selected, record, scope)

    def test_stale_binding_or_expired_handle_refuses(self):
        for key, value in (("observedAt", "2026-09-25T11:00:00Z"),
                           ("issuedAt", "2026-09-25T12:01:00Z"),
                           ("expiresAt", "2026-09-25T12:10:00Z")):
            with self.subTest(key=key):
                bound, native, selected, record, scope = fixture()
                record[key] = value
                selected["eventSha256"] = sha(canonical(record))
                with self.assertRaises(token_identity.Refused):
                    run(bound, native, selected, record, scope)
        bound, native, selected, record, scope = fixture()
        with self.assertRaises(token_identity.Refused):
            run(bound, dataclasses.replace(native,
                grant_expires_at="2026-09-25T11:59:00Z"),
                selected, record, scope)

    def test_duplicate_noncanonical_or_extra_record_refuses(self):
        bound, native, selected, record, scope = fixture()
        raw = canonical(record)
        for candidate in (b"{}", raw + b" ",
                          raw.replace(b'"eventId":800',
                                      b'"eventId":800,"eventId":800')):
            with self.subTest(raw=candidate[:20]):
                with self.assertRaises(token_identity.Refused):
                    run(bound, native, selected, record, scope, candidate)
        record["token"] = "SECRET"
        selected["eventSha256"] = sha(canonical(record))
        with self.assertRaises(token_identity.Refused):
            run(bound, native, selected, record, scope)

    def test_mutable_scope_or_selection_refuses(self):
        bound, native, selected, record, scope = fixture()
        class Drift(FakeIdentityPort):
            def read_public_binding(self, event_id):
                self.scope_value["credentialId"] = "e" * 64
                selected["publicHandleId"] = "8" * 64
                return super().read_public_binding(event_id)
        with self.assertRaises(token_identity.Refused):
            token_identity.observe(bound, native, selected_target(), selected,
                Drift(canonical(record), scope), NOW)

    def test_mutated_metadata_or_native_after_callback_refuses(self):
        for field, changed in (("metadata", "d" * 64),
                               ("native", 301)):
            with self.subTest(field=field):
                bound, native, selected, record, scope = fixture()
                class Drift(FakeIdentityPort):
                    def read_public_binding(self, event_id):
                        if field == "metadata":
                            object.__setattr__(bound, "reader_credential_id",
                                               changed)
                        else:
                            object.__setattr__(native, "repository_id", changed)
                        return super().read_public_binding(event_id)
                with self.assertRaises(token_identity.Refused):
                    token_identity.observe(bound, native, selected_target(),
                        selected, Drift(canonical(record), scope), NOW)

    def test_no_secret_file_socket_sqlite_or_token_read(self):
        bound, native, selected, record, scope = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("sqlite")):
            result, _ = run(bound, native, selected, record, scope)
        self.assertFalse(result.can_dispatch)


if __name__ == "__main__":
    unittest.main()
