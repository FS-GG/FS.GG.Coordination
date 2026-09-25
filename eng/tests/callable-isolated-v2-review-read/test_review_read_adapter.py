"""Independent fake-provider controls for the held review/audit observer."""

import copy
import datetime as dt
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_candidate_observers as observers
import callable_isolated_v2_review_read_adapter as review
import callable_isolated_v2_workflow_read_adapter as workflow

NOW = dt.datetime(2026, 9, 25, 12, 0, tzinfo=dt.timezone.utc)
APPROVALS_PATH = "repos/FS-GG/.github/actions/runs/101/approvals"
MEMBERSHIP_PATH = "orgs/FS-GG/memberships/reviewer"
ROOT = "https://api.github.com"


def fixture():
    workflow_observation = {
        "schema": observers.SCHEMA, "role": "workflow", "complete": True,
        "principalId": "workflow-reader", "credentialId": "a" * 64,
        "recordId": 101, "candidateSha256": "d" * 64,
        "observedAt": "2026-09-25T11:59:00Z",
        "facts": {"workflowRevision": "e" * 40,
                  "workflowPath": ".github/workflows/callable-isolated-v2-execute.yml",
                  "workflowSha256": "f" * 64, "runId": 101,
                  "runAttempt": 1, "dispatchActorId": 202},
    }
    scope = {"principalId": "review-reader", "credentialId": "b" * 64,
             "repository": "FS-GG/.github", "repositoryId": 123,
             "organization": "FS-GG",
             "permissions": {"actions": "read", "members": "read", "metadata": "read"},
             "expiresAt": "2026-09-25T12:20:00Z"}
    approvals = [{"state": "approved", "user": {
        "id": 203, "login": "reviewer", "url": ROOT + "/users/reviewer"},
        "environments": [{"id": 500, "name": "callable-isolated-v2",
                          "url": ROOT + "/repos/FS-GG/.github/environments/callable-isolated-v2"}]}]
    membership = {"state": "active", "role": "member",
                  "url": ROOT + "/orgs/FS-GG/memberships/reviewer",
                  "organization_url": ROOT + "/orgs/FS-GG",
                  "user": {"id": 203, "login": "reviewer",
                           "url": ROOT + "/users/reviewer"},
                  "organization": {"id": 88, "login": "FS-GG",
                                   "url": ROOT + "/orgs/FS-GG"}}
    audit = {"schema": review.AUDIT_SCHEMA, "complete": True,
             "source": "protected-review-event", "principalId": "audit-reader",
             "credentialId": "c" * 64, "eventId": 600,
             "runId": 101, "runAttempt": 1, "environmentId": 500,
             "reviewerId": 203, "approvedAt": "2026-09-25T11:55:00Z"}
    return workflow_observation, scope, approvals, membership, audit


class FakeTransport:
    def __init__(self, scope, approvals, membership):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.events = [(APPROVALS_PATH, approvals), (MEMBERSHIP_PATH, membership)]
        self.paths = []

    def scope(self):
        return self.scopes.pop(0)

    def get(self, path):
        self.paths.append(path)
        expected, value = self.events.pop(0)
        if path != expected:
            raise AssertionError("wrong GET")
        if isinstance(value, workflow.Response):
            return value
        return workflow.Response(200, (), json.dumps(value).encode())


class FakeAudit:
    def __init__(self, value):
        self.value = copy.deepcopy(value)
        self.calls = []

    def read_review_event(self, run_id, run_attempt, environment_id,
                          reviewer_id, approvals_sha256):
        self.calls.append((run_id, run_attempt, environment_id,
                           reviewer_id, approvals_sha256))
        result = copy.deepcopy(self.value)
        result.setdefault("approvalsSha256", approvals_sha256)
        return result


class ReviewReadTests(unittest.TestCase):
    def read(self, workflow_observation=None, scope=None, approvals=None,
             membership=None, audit=None, now=NOW):
        selected_workflow, selected_scope, selected_approvals, selected_membership, selected_audit = fixture()
        transport = FakeTransport(selected_scope if scope is None else scope,
                                  selected_approvals if approvals is None else approvals,
                                  selected_membership if membership is None else membership)
        audit_port = FakeAudit(selected_audit if audit is None else audit)
        adapter = review.ReviewReadAdapter(
            transport, audit_port,
            selected_workflow if workflow_observation is None else workflow_observation,
            123, 88, 203, "reviewer", 500, "d" * 64,
            "2026-09-25T12:20:00Z", now)
        return adapter.observe_review(), transport, audit_port

    def assert_refused(self, **kwargs):
        with self.assertRaises(review.Refused):
            self.read(**kwargs)

    def test_matching_fake_review_remains_read_only(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            value, transport, audit = self.read()
        self.assertEqual(transport.paths, [APPROVALS_PATH, MEMBERSHIP_PATH])
        self.assertEqual(value["envelope"]["recordId"], 600)
        self.assertEqual(value["envelope"]["facts"]["reviewedAt"],
                         "2026-09-25T11:55:00Z")
        self.assertEqual(value["blobs"], {})
        self.assertEqual(audit.calls[0][:4], (101, 1, 500, 203))
        self.assertNotIn("authorized", value)
        self.assertFalse(hasattr(review, "dispatch"))

    def test_wrong_approval_actor_environment_or_list_refuse(self):
        for field, key, changed in (
            ("user", "id", 204), ("user", "login", "foreign"),
            ("user", "url", ROOT + "/users/foreign"),
            ("environment", "id", 501),
            ("environment", "name", "foreign"),
            ("environment", "url", ROOT + "/foreign")):
            with self.subTest(field=field, key=key):
                approvals = fixture()[2]
                item = approvals[0]["user"] if field == "user" else approvals[0]["environments"][0]
                item[key] = changed
                self.assert_refused(approvals=approvals)
        for state in ("rejected", "pending", None):
            approvals = fixture()[2]
            approvals[0]["state"] = state
            self.assert_refused(approvals=approvals)
        self.assert_refused(approvals=[])
        approvals = fixture()[2]
        approvals.append(copy.deepcopy(approvals[0]))
        self.assert_refused(approvals=approvals)

    def test_missing_or_foreign_membership_refuses(self):
        for field, key, changed in (
            ("root", "state", "pending"), ("root", "role", "outsider"),
            ("root", "role", []),
            ("root", "url", ROOT + "/orgs/foreign/memberships/reviewer"),
            ("user", "id", 204), ("user", "login", "foreign"),
            ("organization", "id", 89),
            ("organization", "login", "foreign")):
            with self.subTest(field=field, key=key):
                membership = fixture()[3]
                destination = membership if field == "root" else membership[field]
                destination[key] = changed
                self.assert_refused(membership=membership)
        self.assert_refused(membership={})

    def test_missing_foreign_replayed_or_stale_audit_refuses(self):
        for key, changed in (
            ("complete", False), ("eventId", 0), ("runId", 102),
            ("runAttempt", 2), ("environmentId", 501),
            ("reviewerId", 204), ("approvedAt", "2026-09-25T10:00:00Z"),
            ("approvedAt", "2026-09-25T12:01:00Z"),
            ("principalId", "review-reader"),
            ("credentialId", "b" * 64),
            ("approvalsSha256", "e" * 64)):
            with self.subTest(key=key):
                audit = fixture()[4]
                audit[key] = changed
                self.assert_refused(audit=audit)
        audit = fixture()[4]
        del audit["eventId"]
        self.assert_refused(audit=audit)
        self.assert_refused(now=NOW + dt.timedelta(minutes=31))

    def test_foreign_workflow_broad_scope_redirect_and_exception_refuse(self):
        for key, changed in (("candidateSha256", "e" * 64),
                             ("role", "source-release"),
                             ("recordId", 102)):
            observed = fixture()[0]
            observed[key] = changed
            self.assert_refused(workflow_observation=observed)
        for key, changed in (("repository", "FS-GG/foreign"),
                             ("permissions", {"actions": "write",
                                              "members": "read", "metadata": "read"}),
                             ("expiresAt", "2026-09-25T11:00:00Z")):
            scope = fixture()[1]
            scope[key] = changed
            self.assert_refused(scope=scope)
        selected_workflow, scope, approvals, membership, audit = fixture()
        transport = FakeTransport(scope, approvals, membership)
        transport.events[0] = (APPROVALS_PATH,
                               workflow.Response(302, (("Location", ROOT + "/foreign"),), b"[]"))
        adapter = review.ReviewReadAdapter(transport, FakeAudit(audit),
            selected_workflow, 123, 88, 203, "reviewer", 500, "d" * 64,
            "2026-09-25T12:20:00Z", NOW)
        with self.assertRaises(review.Refused):
            adapter.observe_review()
        transport = FakeTransport(scope, approvals, membership)
        transport.scopes[1]["credentialId"] = "f" * 64
        adapter = review.ReviewReadAdapter(transport, FakeAudit(audit),
            selected_workflow, 123, 88, 203, "reviewer", 500, "d" * 64,
            "2026-09-25T12:20:00Z", NOW)
        with self.assertRaisesRegex(review.Refused, "review-scope-drift"):
            adapter.observe_review()
        class Broken(FakeTransport):
            def get(self, path):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        adapter = review.ReviewReadAdapter(Broken(scope, approvals, membership),
            FakeAudit(audit), selected_workflow, 123, 88, 203, "reviewer", 500,
            "d" * 64, "2026-09-25T12:20:00Z", NOW)
        with self.assertRaisesRegex(review.Refused, "review-read-unavailable") as error:
            adapter.observe_review()
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(error.exception))

    def test_reused_review_reader_scope_mutated_at_final_read_refuses(self):
        selected_workflow, scope, approvals, membership, audit = fixture()
        transport = FakeTransport(scope, approvals, membership)
        shared = copy.deepcopy(scope)
        reads = [0]
        def reused_scope():
            reads[0] += 1
            if reads[0] == 2:
                shared["credentialId"] = "f" * 64
            return shared
        transport.scope = reused_scope
        reader = review.ReviewReadAdapter(transport, FakeAudit(audit),
            selected_workflow, 123, 88, 203, "reviewer", 500, "d" * 64,
            "2026-09-25T12:20:00Z", NOW)
        with self.assertRaises(review.Refused):
            reader.observe_review()


if __name__ == "__main__":
    unittest.main()
