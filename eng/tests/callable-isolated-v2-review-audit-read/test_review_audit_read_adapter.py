"""Fake organization audit and independent joint-seal controls."""

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_review_audit_read_adapter as audit
import callable_isolated_v2_review_read_adapter as review

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
APPROVED = dt.datetime(2026, 9, 25, 11, 55, tzinfo=dt.timezone.utc)
MILLIS = int(APPROVED.timestamp() * 1000)
DOC = "auditDocument_123"


def fixture():
    scope = {"principalId": "audit-reader", "credentialId": "a" * 64,
             "organization": "FS-GG", "permissions": ["audit-log:read"],
             "expiresAt": "2026-09-25T12:20:00Z"}
    event = {"_document_id": DOC, "action": audit.AUDIT_ACTION,
             "org": "FS-GG", "org_id": 88,
             "repo": "FS-GG/.github", "repo_id": 123,
             "actor": "reviewer", "actor_id": 203,
             "workflow_run_id": 101,
             "created_at": MILLIS, "@timestamp": MILLIS}
    joint = {"schema": audit.JOINT_SCHEMA, "complete": True,
             "principalId": "joint-sealer", "credentialId": "b" * 64,
             "recordId": 600, "documentId": DOC,
             "organizationId": 88, "repositoryId": 123,
             "runId": 101, "runAttempt": 1, "environmentId": 500,
             "reviewerId": 203, "createdAtMs": MILLIS,
             "sealedAt": "2026-09-25T11:56:00Z",
             "expiresAt": "2026-09-25T12:20:00Z"}
    return scope, event, joint


class FakeAuditLog:
    def __init__(self, scope, raw):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.raw = raw
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read_event(self, document_id):
        self.reads.append(document_id)
        return self.raw


class FakeJoint:
    def __init__(self, value):
        self.value = copy.deepcopy(value)
        self.calls = []

    def read_joint(self, record_id, audit_sha256, approvals_sha256):
        self.calls.append((record_id, audit_sha256, approvals_sha256))
        result = copy.deepcopy(self.value)
        result.setdefault("auditSha256", audit_sha256)
        result.setdefault("approvalsSha256", approvals_sha256)
        return result


class AuditReadTests(unittest.TestCase):
    def observe(self, scope=None, event=None, joint=None, raw=None,
                run_id=101, run_attempt=1, environment_id=500,
                reviewer_id=203, approvals_sha256="c" * 64):
        selected_scope, selected_event, selected_joint = fixture()
        encoded = (json.dumps(selected_event if event is None else event).encode()
                   if raw is None else raw)
        log = FakeAuditLog(selected_scope if scope is None else scope, encoded)
        sealer = FakeJoint(selected_joint if joint is None else joint)
        adapter = audit.ReviewAuditReadAdapter(log, sealer, DOC, 600, 88,
                                               123, "reviewer", NOW)
        return adapter.read_review_event(run_id, run_attempt,
            environment_id, reviewer_id, approvals_sha256), log, sealer

    def refuses(self, **changes):
        with self.assertRaises(audit.Refused):
            self.observe(**changes)

    def test_matching_fake_is_read_only_and_satisfies_review_port(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            value, log, joint = self.observe()
        self.assertEqual(log.reads, [DOC])
        self.assertEqual(joint.calls[0][0], 600)
        self.assertEqual(value["schema"], review.AUDIT_SCHEMA)
        self.assertEqual(value["eventId"], 600)
        self.assertEqual(value["approvedAt"], "2026-09-25T11:55:00Z")
        self.assertEqual(value["approvalsSha256"], "c" * 64)
        self.assertNotIn("authorized", value)
        self.assertFalse(hasattr(audit, "dispatch"))

    def test_wrong_provider_document_actor_repo_run_or_time_refuses(self):
        for key, changed in (
            ("_document_id", "foreignDocument_123"),
            ("action", "workflows.reject_workflow_job"),
            ("org_id", 89), ("repo_id", 124),
            ("actor", "foreign"), ("actor_id", 204),
            ("workflow_run_id", 102),
            ("created_at", MILLIS - 31 * 60 * 1000),
            ("@timestamp", MILLIS + 1)):
            with self.subTest(key=key):
                event = fixture()[1]
                event[key] = changed
                self.refuses(event=event)
        self.refuses(raw=b'{"actor_id":203,"actor_id":203}')

    def test_missing_or_foreign_joint_mapping_refuses(self):
        for key, changed in (
            ("recordId", 601), ("documentId", "foreignDocument_123"),
            ("auditSha256", "d" * 64),
            ("approvalsSha256", "d" * 64),
            ("repositoryId", 124), ("runId", 102),
            ("runAttempt", 2), ("environmentId", 501),
            ("runAttempt", True),
            ("reviewerId", 204), ("createdAtMs", MILLIS + 1),
            ("principalId", "audit-reader"),
            ("credentialId", "a" * 64),
            ("sealedAt", "2026-09-25T11:50:00Z"),
            ("expiresAt", "2026-09-25T11:59:00Z")):
            with self.subTest(key=key):
                joint = fixture()[2]
                joint[key] = changed
                self.refuses(joint=joint)
        joint = fixture()[2]
        del joint["environmentId"]
        self.refuses(joint=joint)

    def test_replayed_selection_broad_scope_and_drift_refuse(self):
        for changes in ({"run_id": 102}, {"run_attempt": 2},
                        {"environment_id": 501}, {"reviewer_id": 204}):
            with self.subTest(changes=changes):
                self.refuses(**changes)
        joint = fixture()[2]
        joint["approvalsSha256"] = "c" * 64
        self.refuses(joint=joint, approvals_sha256="d" * 64)
        for key, changed in (
            ("organization", "foreign"),
            ("permissions", ["audit-log:read", "audit-log:write"]),
            ("expiresAt", "2026-09-25T11:00:00Z")):
            scope = fixture()[0]
            scope[key] = changed
            self.refuses(scope=scope)
        scope, event, joint = fixture()
        log = FakeAuditLog(scope, json.dumps(event).encode())
        log.scopes[1]["credentialId"] = "d" * 64
        with self.assertRaisesRegex(audit.Refused, "audit-reader-drift"):
            audit.ReviewAuditReadAdapter(log, FakeJoint(joint), DOC, 600,
                88, 123, "reviewer", NOW).read_review_event(101, 1, 500, 203,
                                                               "c" * 64)

    def test_secret_exception_is_sanitized(self):
        scope, event, joint = fixture()
        class Broken(FakeAuditLog):
            def read_event(self, document_id):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        adapter = audit.ReviewAuditReadAdapter(
            Broken(scope, json.dumps(event).encode()), FakeJoint(joint),
            DOC, 600, 88, 123, "reviewer", NOW)
        with self.assertRaisesRegex(audit.Refused, "audit-log-unavailable") as caught:
            adapter.read_review_event(101, 1, 500, 203, "c" * 64)
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))


if __name__ == "__main__":
    unittest.main()
