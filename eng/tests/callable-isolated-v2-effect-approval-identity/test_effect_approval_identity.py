"""Independent fake reviewer identity and immutable approval-event bytes."""

import copy
import datetime as dt
import hashlib
import json
import os
import pathlib
import socket
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import callable_isolated_v2_effect_release_preflight as release
import callable_isolated_v2_effect_producer_artifact as producer
import callable_isolated_v2_effect_approval_identity as approval
import verify_callable_isolated_v2_effect_producer_workflow as closed_workflow

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
REV = "a" * 40
TREE = "b" * 40
MANIFEST = "c" * 64
ARCHIVE = "d" * 64
BUNDLE = "e" * 64
WORKFLOW = closed_workflow.PINNED_WORKFLOW_SHA256
BLOB = closed_workflow.PINNED_WORKFLOW_BLOB_OID


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


class Port:
    def __init__(self, scope, record):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.record = copy.deepcopy(record)
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read_reviewer(self, actor_id):
        self.reads.append(actor_id)
        return copy.deepcopy(self.record)

    def read_approval_event(self, event_id):
        self.reads.append(event_id)
        return self.record


def fixture():
    preflight = release.PreflightResult(REV, TREE, MANIFEST, 404, ARCHIVE,
        202, 1, 303, 606, 505, source_record_id=101)
    producer_result = producer.ProducerWitnessResult(REV, TREE, 202, 1, 404,
        ARCHIVE, BUNDLE, WORKFLOW, BLOB, "2026-09-25T11:57:00Z", 303, 101, 77, 808)
    identity_scope = {"principalId": "identity-reader", "credentialId": "2" * 64,
        "repository": release.REPOSITORY, "repositoryId": 77,
        "permissions": ["members:read", "metadata:read"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    event_scope = {"principalId": "event-reader", "credentialId": "3" * 64,
        "repository": release.REPOSITORY, "repositoryId": 77,
        "permissions": ["read-release-approval"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    identity = {"schema": approval.IDENTITY_SCHEMA, "complete": True,
        "principalId": "identity-reader", "credentialId": "2" * 64,
        "recordId": 707, "repository": release.REPOSITORY,
        "repositoryId": 77, "organization": "FS-GG", "organizationId": 88,
        "reviewerActorId": 606, "login": "release-reviewer",
        "active": True, "role": "release-reviewer",
        "observedAt": "2026-09-25T11:57:20Z",
        "expiresAt": "2026-09-25T12:10:00Z"}
    event = {"schema": approval.EVENT_SCHEMA, "immutable": True,
        "repository": release.REPOSITORY, "repositoryId": 77,
        "coordinationRevision": REV, "sourceTree": TREE,
        "producerRunId": 202, "producerRunAttempt": 1,
        "producerActorId": 303, "artifactId": 404,
        "bundleSha256": BUNDLE, "workflowSha256": WORKFLOW,
        "workflowBlobOid": BLOB, "manifestSha256": MANIFEST,
        "sourceRecordId": 101, "identityEventId": 808,
        "reviewerActorId": 606, "identityRecordId": 707,
        "approvalEventId": 505, "decision": "approved",
        "approvedAt": "2026-09-25T11:58:00Z",
        "expiresAt": "2026-09-25T12:15:00Z"}
    raw = canonical(event)
    selection = {"repositoryId": 77, "identityEventId": 808,
        "organizationId": 88,
        "reviewerLogin": "release-reviewer", "identityRecordId": 707,
        "approvalEventSha256": hashlib.sha256(raw).hexdigest()}
    return (preflight, producer_result, selection,
        Port(identity_scope, identity), Port(event_scope, raw))


class ApprovalIdentityTests(unittest.TestCase):
    def observe(self, change=None):
        preflight, made, selection, identity, event = fixture()
        if change:
            change(preflight, made, selection, identity, event)
        return approval.qualify(preflight, made, identity, event, selection, NOW)

    def refuses(self, change):
        with self.assertRaises(approval.Refused):
            self.observe(change)

    def test_matching_independent_fake_readers_stay_closed(self):
        preflight, made, selection, identity, event = fixture()
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect", side_effect=AssertionError("post")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = approval.qualify(preflight, made, identity, event,
                                      selection, NOW)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual(result.approval_event_sha256,
                         selection["approvalEventSha256"])
        self.assertEqual(result.source_record_id, 101)
        self.assertEqual(result.producer_actor_id, 303)
        self.assertEqual(result.repository_id, 77)
        self.assertEqual(result.identity_event_id, 808)
        self.assertEqual(identity.reads, [606])
        self.assertEqual(event.reads, [505])

    def test_inactive_foreign_or_self_review_refuses(self):
        self.refuses(lambda _p, _m, _s, i, _e: i.record.__setitem__("active", False))
        self.refuses(lambda _p, _m, _s, i, _e: i.record.__setitem__("reviewerActorId", 607))
        self.refuses(lambda _p, _m, _s, i, _e: i.record.__setitem__("login", "foreign"))
        self.refuses(lambda p, _m, _s, _i, _e: object.__setattr__(p, "reviewer_actor_id", 303))
        self.refuses(lambda _p, _m, _s, i, _e: i.record.__setitem__("observedAt", "2026-09-25T11:59:00Z"))
        self.refuses(lambda p, _m, _s, _i, _e: object.__setattr__(p, "producer_run_attempt", True))

    def test_event_bytes_head_artifact_and_time_refuse(self):
        for key, value in (("coordinationRevision", "f" * 40),
                           ("sourceTree", "f" * 40), ("producerRunAttempt", 2),
                           ("artifactId", 405), ("bundleSha256", "f" * 64),
                           ("workflowBlobOid", "f" * 40),
                           ("manifestSha256", "f" * 64),
                           ("approvalEventId", 506),
                           ("approvedAt", "2026-09-25T11:56:00Z"),
                           ("expiresAt", "2026-09-25T11:59:00Z")):
            with self.subTest(key=key):
                def mutate(_p, _m, selected, _i, event):
                    value_map = json.loads(event.record)
                    value_map[key] = value
                    event.record = canonical(value_map)
                    selected["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
                self.refuses(mutate)
        def duplicate(_p, _m, selected, _i, event):
            event.record = event.record.replace(
                b'"decision":"approved"',
                b'"decision":"approved","decision":"approved"')
            selected["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
        self.refuses(duplicate)
        self.refuses(lambda _p, _m, s, _i, _e: s.__setitem__("approvalEventSha256", "f" * 64))

    def test_reader_custody_scope_and_missing_event_refuse(self):
        self.refuses(lambda _p, _m, _s, _i, e: e.scopes[0].__setitem__("credentialId", "2" * 64))
        self.refuses(lambda _p, _m, _s, _i, e: e.scopes[1].__setitem__("repositoryId", 78))
        self.refuses(lambda _p, _m, _s, i, _e: i.scopes[0].__setitem__("permissions", ["members:write"]))
        self.refuses(lambda _p, _m, _s, _i, e: setattr(e, "record", b""))

    def test_in_place_scope_credential_mutation_was_a_false_green(self):
        preflight, made, selection, identity, event = fixture()
        shared = event.scopes[0]
        event.scopes[1] = shared
        raw = event.record
        def mutate_on_read(event_id):
            event.reads.append(event_id)
            shared["credentialId"] = "4" * 64
            return raw
        event.read_approval_event = mutate_on_read
        with self.assertRaises(approval.Refused):
            approval.qualify(preflight, made, identity, event, selection, NOW)

    def test_coherent_foreign_workflow_approval_refuses(self):
        for field, value in (("workflowSha256", "9" * 64),
                             ("workflowBlobOid", "2" * 40)):
            with self.subTest(field=field):
                preflight, made, selection, identity, event = fixture()
                object.__setattr__(made,
                    "workflow_sha256" if field == "workflowSha256"
                    else "workflow_blob_oid", value)
                claim = json.loads(event.record)
                claim[field] = value
                event.record = canonical(claim)
                selection["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
                with self.assertRaises(approval.Refused):
                    approval.qualify(preflight, made, identity, event,
                                     selection, NOW)

    def test_immutable_event_missing_selected_source_record_refuses(self):
        preflight, made, selection, identity, event = fixture()
        claim = json.loads(event.record)
        claim.pop("sourceRecordId")
        event.record = canonical(claim)
        selection["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
        with self.assertRaises(approval.Refused):
            approval.qualify(preflight, made, identity, event, selection, NOW)

    def test_changed_preflight_actor_or_source_record_cannot_rewrite_review(self):
        for event_key, result_field, foreign in (
                ("producerActorId", "producer_actor_id", 304),
                ("sourceRecordId", "source_record_id", 102)):
            with self.subTest(event_key=event_key):
                preflight, made, selection, identity, event = fixture()
                object.__setattr__(preflight, result_field, foreign)
                claim = json.loads(event.record)
                claim[event_key] = foreign
                event.record = canonical(claim)
                selection["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
                with self.assertRaises(approval.Refused):
                    approval.qualify(preflight, made, identity, event,
                                     selection, NOW)

    def test_coherent_foreign_review_repository_cannot_reuse_producer(self):
        preflight, made, selection, identity, event = fixture()
        selection["repositoryId"] = 78
        for port in (identity, event):
            for scope in port.scopes:
                scope["repositoryId"] = 78
        identity.record["repositoryId"] = 78
        claim = json.loads(event.record)
        claim["repositoryId"] = 78
        event.record = canonical(claim)
        selection["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
        with self.assertRaises(approval.Refused):
            approval.qualify(preflight, made, identity, event, selection, NOW)

    def test_immutable_review_without_repository_identity_event_refuses(self):
        preflight, made, selection, identity, event = fixture()
        claim = json.loads(event.record)
        claim.pop("identityEventId")
        event.record = canonical(claim)
        selection["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
        with self.assertRaises(approval.Refused):
            approval.qualify(preflight, made, identity, event, selection, NOW)
        preflight, made, selection, identity, event = fixture()
        claim = json.loads(event.record)
        claim["identityEventId"] = 809
        event.record = canonical(claim)
        selection["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
        with self.assertRaises(approval.Refused):
            approval.qualify(preflight, made, identity, event, selection, NOW)
        preflight, made, selection, identity, event = fixture()
        claim = json.loads(event.record)
        claim["sourceRecordId"] = 102
        event.record = canonical(claim)
        selection["approvalEventSha256"] = hashlib.sha256(event.record).hexdigest()
        with self.assertRaises(approval.Refused):
            approval.qualify(preflight, made, identity, event, selection, NOW)


if __name__ == "__main__":
    unittest.main()
