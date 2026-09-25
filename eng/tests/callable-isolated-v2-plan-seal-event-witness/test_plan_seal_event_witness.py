"""Fake-only negative controls for a closed immutable plan-seal event witness."""

import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_operation_plan_read_adapter as plan
import callable_isolated_v2_plan_seal_event_witness as witness

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


class FakeEventPort:
    def __init__(self, raw, scope):
        self.raw = raw
        self.scope_value = scope
        self.calls = []

    def scope(self):
        return self.scope_value

    def read_event(self, event_id):
        self.calls.append(event_id)
        return self.raw


def fixture():
    request = b'{"base":"base","body":"FS-GG-Effect: marker","head":"source","title":"test"}'
    verified = plan.VerifiedRequest(500, "1" * 64, "5" * 64,
                                    hashlib.sha256(request).hexdigest(), request)
    selected = {
        "eventId": 600, "eventSha256": "", "candidateSha256": "f" * 64,
        "sourceTree": "b" * 40, "workflowRevision": "c" * 40,
        "runId": 200, "runAttempt": 1, "reviewEventId": 204,
        "targetRepositoryId": 300, "reviewedAt": "2026-09-25T11:55:00Z",
        "reviewExpiresAt": "2026-09-25T12:20:00Z",
        "sealPrincipalId": "plan-sealer", "sealCredentialId": "2" * 64,
        "otherPrincipals": ["observer-0", "observer-1", "observer-2",
                            "observer-3", "plan-reader"],
        "otherCredentialIds": ["a" * 64, "b" * 64, "c" * 64,
                               "d" * 64, "1" * 64]}
    event = {
        "schema": witness.EVENT_SCHEMA, "complete": True,
        "eventId": 600, "eventType": "plan-sealed",
        "recordId": 500, "planSha256": "1" * 64,
        "operationId": "5" * 64, "requestSha256": verified.request_sha256,
        "candidateSha256": "f" * 64, "sourceTree": "b" * 40,
        "workflowRevision": "c" * 40, "runId": 200, "runAttempt": 1,
        "reviewEventId": 204, "targetRepositoryId": 300,
        "producerPrincipalId": "plan-sealer",
        "producerCredentialId": "2" * 64,
        "eventAt": "2026-09-25T11:58:00Z",
        "expiresAt": "2026-09-25T12:15:00Z"}
    selected["eventSha256"] = hashlib.sha256(canonical(event)).hexdigest()
    scope = {"principalId": "event-reader", "credentialId": "3" * 64,
             "store": "coordination-protected-plan-seal-events",
             "permissions": ["read-seal-event"],
             "expiresAt": "2026-09-25T12:20:00Z"}
    return verified, selected, event, scope


def run(verified=None, selected=None, event=None, scope=None, raw=None):
    default_verified, default_selected, default_event, default_scope = fixture()
    port = FakeEventPort(raw if raw is not None else canonical(
        default_event if event is None else event),
        default_scope if scope is None else scope)
    result = witness.observe(verified or default_verified,
        default_selected if selected is None else selected, port, NOW)
    return result, port


class SealEventWitnessTests(unittest.TestCase):
    def test_matching_event_remains_closed_and_one_read(self):
        result, port = run()
        self.assertEqual(port.calls, [600])
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual(result.event_id, 600)
        self.assertEqual(result.plan_sha256, "1" * 64)

    def test_wrong_event_or_request_refuses(self):
        for key, value in (("eventId", 601), ("recordId", 501),
                           ("planSha256", "9" * 64),
                           ("operationId", "9" * 64),
                           ("requestSha256", "9" * 64),
                           ("runAttempt", 2), ("reviewEventId", 205),
                           ("targetRepositoryId", 301),
                           ("producerPrincipalId", "foreign")):
            with self.subTest(key=key):
                verified, selected, event, scope = fixture()
                event[key] = value
                selected["eventSha256"] = hashlib.sha256(canonical(event)).hexdigest()
                with self.assertRaises(witness.Refused):
                    run(verified, selected, event, scope)

    def test_selected_event_digest_and_selected_coordinates_refuse(self):
        verified, selected, event, scope = fixture()
        selected["eventSha256"] = "0" * 64
        with self.assertRaises(witness.Refused):
            run(verified, selected, event, scope)
        for key, value in (("candidateSha256", "e" * 64),
                           ("sourceTree", "a" * 40),
                           ("workflowRevision", "a" * 40),
                           ("runId", 201), ("runAttempt", 2),
                           ("reviewEventId", 205),
                           ("targetRepositoryId", 301),
                           ("sealPrincipalId", "foreign-sealer")):
            with self.subTest(key=key):
                verified, selected, event, scope = fixture()
                selected[key] = value
                with self.assertRaises(witness.Refused):
                    run(verified, selected, event, scope)

    def test_missing_duplicate_or_noncanonical_event_refuses(self):
        verified, selected, event, scope = fixture()
        for raw in (b"{}", canonical(event).replace(b'"eventId":600',
                     b'"eventId":600,"eventId":600'),
                    json.dumps(event).encode(), canonical(event) + b" "):
            with self.subTest(raw=raw[:20]), self.assertRaises(witness.Refused):
                run(verified, selected, scope=scope, raw=raw)

    def test_stale_or_replayed_selection_refuses(self):
        for key, value in (("eventAt", "2026-09-25T11:00:00Z"),
                           ("eventAt", "2026-09-25T12:01:00Z"),
                           ("expiresAt", "2026-09-25T12:00:00Z")):
            with self.subTest(key=key):
                verified, selected, event, scope = fixture()
                event[key] = value
                selected["eventSha256"] = hashlib.sha256(canonical(event)).hexdigest()
                with self.assertRaises(witness.Refused):
                    run(verified, selected, event, scope)

    def test_foreign_broad_or_shared_reader_refuses(self):
        for key, value in (("principalId", "plan-sealer"),
                           ("credentialId", "1" * 64),
                           ("permissions", ["read-seal-event", "write-seal"]),
                           ("store", "foreign")):
            with self.subTest(key=key):
                verified, selected, event, scope = fixture()
                scope[key] = value
                with self.assertRaises(witness.Refused):
                    run(verified, selected, event, scope)

    def test_scope_alias_drift_and_selection_mutation_refuse(self):
        verified, selected, event, scope = fixture()
        class DriftPort(FakeEventPort):
            def read_event(self, event_id):
                self.scope_value["credentialId"] = "4" * 64
                selected["eventId"] = 601
                return super().read_event(event_id)
        with self.assertRaises(witness.Refused):
            witness.observe(verified, selected, DriftPort(canonical(event), scope), NOW)

    def test_no_local_effect_or_secret_leak(self):
        verified, selected, event, scope = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("sqlite")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")):
            result, _ = run(verified, selected, event, scope)
        self.assertFalse(result.can_dispatch)
        class Broken(FakeEventPort):
            def read_event(self, event_id):
                raise RuntimeError("SECRET")
        with self.assertRaisesRegex(witness.Refused, "event-read-unavailable") as caught:
            witness.observe(verified, selected, Broken(canonical(event), scope), NOW)
        self.assertNotIn("SECRET", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
