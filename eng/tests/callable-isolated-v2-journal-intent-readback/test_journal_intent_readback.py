"""Fake journal controls for a closed v5 one-attempt readback contract."""

import dataclasses
import datetime as dt
import hashlib
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_effect_v5_ports as v5
import callable_isolated_v2_operation_plan_read_adapter as plan
import callable_isolated_v2_journal_intent_readback as intent

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


class FakeReplay:
    def __init__(self, record, scope):
        self.record = record
        self.scope_value = scope
        self.calls = []

    def scope(self):
        return self.scope_value

    def read_committed(self, operation_id):
        self.calls.append(operation_id)
        return self.record


def fixture():
    request = b'{"base":"base","body":"marker","head":"source","title":"test"}'
    verified = plan.VerifiedRequest(500, "1" * 64, "5" * 64,
                                    hashlib.sha256(request).hexdigest(), request)
    selected = {"repository": "FS-GG/coordination-journal",
        "ref": "refs/heads/gs2-09-9-intent", "path": "v2/intent.json",
        "operationId": "5" * 64, "grantSha256": "6" * 64,
        "targetSha256": "7" * 64, "priorGeneration": 3,
        "priorHead": "8" * 40, "committedGeneration": 4,
        "committedHead": "9" * 40,
        "writerPrincipalId": "journal-writer", "writerCredentialId": "a" * 64,
        "readerPrincipalId": "journal-reader", "readerCredentialId": "b" * 64}
    record = v5.JournalRecord("5" * 64, "6" * 64, "7" * 64,
                              verified.request_sha256, 3, "8" * 40,
                              4, "9" * 40, True)
    scope = {"principalId": "journal-reader", "credentialId": "b" * 64,
        "repository": "FS-GG/coordination-journal",
        "ref": "refs/heads/gs2-09-9-intent", "path": "v2/intent.json",
        "permissions": ["read-intent"],
        "expiresAt": "2026-09-25T12:20:00Z"}
    return verified, selected, record, scope


def run(verified=None, selected=None, ack=None, replay=None, scope=None,
        outcome="acknowledged"):
    base_verified, base_selected, base_ack, base_scope = fixture()
    port = FakeReplay(base_ack if replay is None else replay,
                      base_scope if scope is None else scope)
    result = intent.observe(base_verified if verified is None else verified,
        base_selected if selected is None else selected,
        base_ack if ack is None else ack, port, outcome, NOW)
    return result, port


class IntentReadbackTests(unittest.TestCase):
    def test_matching_fake_readback_is_still_closed(self):
        result, port = run()
        self.assertEqual(port.calls, ["5" * 64])
        self.assertEqual(result.state, "consistent-but-unadmitted")
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_lost_or_unacknowledged_cas_refuses_before_reader(self):
        for outcome in ("unknown", "failed", "", "acknowledged "):
            with self.subTest(outcome=outcome):
                verified, selected, ack, scope = fixture()
                port = FakeReplay(ack, scope)
                with self.assertRaises(intent.Refused):
                    intent.observe(verified, selected, ack, port, outcome, NOW)
                self.assertEqual(port.calls, [])
        verified, selected, ack, scope = fixture()
        port = FakeReplay(ack, scope)
        with self.assertRaises(intent.Refused):
            intent.observe(verified, selected, None, port, "acknowledged", NOW)
        self.assertEqual(port.calls, [])

    def test_ack_and_replay_each_bind_every_intent_field(self):
        changes = {"operation_id": "c" * 64, "grant_sha256": "c" * 64,
                   "target_sha256": "c" * 64, "request_sha256": "c" * 64,
                   "prior_generation": 2, "prior_head": "c" * 40,
                   "committed_generation": 5, "committed_head": "c" * 40,
                   "attempt_may_have_started": False}
        for side in ("ack", "replay"):
            for field, value in changes.items():
                with self.subTest(side=side, field=field):
                    verified, selected, record, scope = fixture()
                    changed = dataclasses.replace(record, **{field: value})
                    port = FakeReplay(changed if side == "replay" else record,
                                      scope)
                    with self.assertRaises(intent.Refused):
                        intent.observe(verified, selected,
                                       changed if side == "ack" else record,
                                       port, "acknowledged", NOW)

    def test_foreign_backend_or_shared_reader_refuses(self):
        for field, value in (("repository", "foreign/repo"),
                             ("ref", "refs/heads/foreign"),
                             ("path", "other.json"),
                             ("principalId", "journal-writer"),
                             ("credentialId", "a" * 64),
                             ("permissions", ["read-intent", "write-intent"])):
            with self.subTest(field=field):
                verified, selected, record, scope = fixture()
                scope[field] = value
                with self.assertRaises(intent.Refused):
                    run(verified, selected, record, record, scope)

    def test_invalid_generation_and_selected_identity_refuse(self):
        for field, value in (("committedGeneration", 5),
                             ("committedHead", "8" * 40),
                             ("readerPrincipalId", "journal-writer"),
                             ("readerCredentialId", "a" * 64),
                             ("targetSha256", "c" * 64)):
            with self.subTest(field=field):
                verified, selected, record, scope = fixture()
                selected[field] = value
                with self.assertRaises(intent.Refused):
                    run(verified, selected, record, record, scope)

    def test_mutable_alias_or_reader_exception_refuses_without_secret(self):
        verified, selected, record, scope = fixture()
        class Drift(FakeReplay):
            def read_committed(self, operation_id):
                self.scope_value["credentialId"] = "c" * 64
                selected["targetSha256"] = "c" * 64
                return super().read_committed(operation_id)
        with self.assertRaises(intent.Refused):
            intent.observe(verified, selected, record,
                           Drift(record, scope), "acknowledged", NOW)
        verified, selected, record, scope = fixture()
        class AckDrift(FakeReplay):
            def read_committed(self, operation_id):
                object.__setattr__(record, "grant_sha256", "c" * 64)
                return super().read_committed(operation_id)
        with self.assertRaises(intent.Refused):
            intent.observe(verified, selected, record,
                           AckDrift(record, scope), "acknowledged", NOW)
        verified, selected, record, scope = fixture()
        class Broken(FakeReplay):
            def read_committed(self, operation_id):
                raise RuntimeError("SECRET")
        with self.assertRaisesRegex(intent.Refused,
                                    "journal-read-unavailable") as caught:
            intent.observe(verified, selected, record,
                           Broken(record, scope), "acknowledged", NOW)
        self.assertNotIn("SECRET", str(caught.exception))

    def test_no_token_file_socket_or_journal_writer_access(self):
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("sqlite")):
            result, _ = run()
        self.assertFalse(result.can_dispatch)

    def test_closed_result_fields_cannot_be_set_by_caller(self):
        result, _ = run()
        for field, value in (("authorized", True), ("can_dispatch", True),
                             ("live_effects", 1)):
            with self.subTest(field=field):
                with self.assertRaises((TypeError, ValueError)):
                    dataclasses.replace(result, **{field: value})


if __name__ == "__main__":
    unittest.main()
