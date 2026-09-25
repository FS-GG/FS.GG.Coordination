"""Independent fake source and approval joins for a closed effect scaffold."""

import copy
import datetime as dt
import hashlib
import os
import pathlib
import socket
import tempfile
import unittest
from unittest import mock
import sys

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_effect_release_preflight as preflight

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
REVISION = "a" * 40
TREE = "b" * 40


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def fixture():
    with tempfile.TemporaryDirectory() as temporary:
        path = pathlib.Path(temporary) / builder.ARCHIVE_NAME
        builder.build(path)
        archive = path.read_bytes()
    manifest = (ROOT / builder.MANIFEST).read_bytes()
    blobs = {"archive": archive,
        "workflow": (ROOT / builder.WORKFLOW).read_bytes(),
        "manifest": manifest,
        "builderSource": (ENG /
            "build_callable_isolated_v2_effect_scaffold.py").read_bytes(),
        "nativeSource": (ROOT / builder.NATIVE_SOURCE).read_bytes()}
    source_scope = {"principalId": "source-reader", "credentialId": "1" * 64,
        "repository": "FS-GG/FS.GG.Coordination",
        "permissions": ["actions:read", "contents:read"],
        "expiresAt": "2026-09-25T12:20:00Z"}
    approval_scope = {"principalId": "approval-reader",
        "credentialId": "2" * 64,
        "repository": "FS-GG/FS.GG.Coordination",
        "permissions": ["read-release-approval"],
        "expiresAt": "2026-09-25T12:20:00Z"}
    source = {"schema": preflight.SOURCE_SCHEMA, "complete": True,
        "principalId": "source-reader", "credentialId": "1" * 64,
        "recordId": 101, "repository": "FS-GG/FS.GG.Coordination",
        "coordinationRevision": REVISION,
        "sourceTree": TREE, "producerRunId": 202,
        "producerRunAttempt": 1, "producerActorId": 303,
        "artifactId": 404, "archiveSha256": sha(archive),
        "manifestSha256": sha(manifest),
        "observedAt": "2026-09-25T11:55:00Z", "blobs": blobs}
    approval = {"schema": preflight.APPROVAL_SCHEMA, "complete": True,
        "principalId": "approval-reader", "credentialId": "2" * 64,
        "eventId": 505, "repository": "FS-GG/FS.GG.Coordination",
        "coordinationRevision": REVISION,
        "sourceTree": TREE, "producerRunId": 202,
        "producerRunAttempt": 1, "producerActorId": 303,
        "artifactId": 404, "reviewerActorId": 606,
        "sourceRecordId": 101,
        "manifestSha256": sha(manifest),
        "reviewedAt": "2026-09-25T11:58:00Z",
        "expiresAt": "2026-09-25T12:15:00Z"}
    return source_scope, approval_scope, source, approval


class FakePort:
    def __init__(self, scope, record):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.record = copy.deepcopy(record)
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read_integrated_source(self):
        self.reads.append("source")
        return copy.deepcopy(self.record)

    def read_approval(self, event_id):
        self.reads.append(event_id)
        return copy.deepcopy(self.record)


class ReleasePreflightTests(unittest.TestCase):
    def observe(self, source_scope=None, approval_scope=None,
                source=None, approval=None, revision=REVISION, tree=TREE,
                producer=303, reviewer=606, run_attempt=1):
        source_claim, approval_claim, source_record, approval_event = fixture()
        left = FakePort(source_claim if source_scope is None else source_scope,
                        source_record if source is None else source)
        right = FakePort(approval_claim if approval_scope is None else approval_scope,
                         approval_event if approval is None else approval)
        result = preflight.qualify(left, right, revision, tree, 101, 202,
            run_attempt, producer, 404, reviewer, 505, NOW)
        return result, left, right

    def refuses(self, **changes):
        with self.assertRaises(preflight.Refused):
            self.observe(**changes)

    def test_matching_observations_stay_closed_without_effect_ports(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect",
                                side_effect=AssertionError("provider")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result, left, right = self.observe()
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual(result.schema, preflight.RESULT_SCHEMA)
        self.assertEqual(result.archive_sha256, fixture()[2]["archiveSha256"])
        self.assertEqual(result.source_record_id, 101)
        self.assertEqual((result.producer_run_id, result.producer_run_attempt,
                          result.producer_actor_id, result.reviewer_actor_id,
                          result.approval_event_id), (202, 1, 303, 606, 505))
        self.assertEqual(left.reads, ["source"])
        self.assertEqual(right.reads, [505])
        self.assertFalse(hasattr(preflight, "dispatch"))

    def test_foreign_selection_review_and_bytes_refuse(self):
        self.refuses(revision="c" * 40)
        self.refuses(tree="c" * 40)
        self.refuses(run_attempt=2)
        self.refuses(producer=606)
        self.refuses(reviewer=303)
        for key, changed in (("producerRunId", 203),
                             ("repository", "FS-GG/foreign"),
                             ("producerRunAttempt", 2),
                             ("sourceTree", "c" * 40),
                             ("manifestSha256", "c" * 64),
                             ("reviewerActorId", 303),
                             ("reviewedAt", "2026-09-25T11:54:00Z"),
                             ("expiresAt", "2026-09-25T11:59:00Z")):
            with self.subTest(key=key):
                event = fixture()[3]
                event[key] = changed
                self.refuses(approval=event)
        record = fixture()[2]
        record["blobs"]["archive"] += b"foreign"
        self.refuses(source=record)
        record = fixture()[2]
        record["blobs"]["builderSource"] += b"foreign"
        self.refuses(source=record)
        record = fixture()[2]
        record["observedAt"] = "2026-09-25T11:00:00Z"
        self.refuses(source=record)
        record = fixture()[2]
        record["producerActorId"] = 606
        self.refuses(source=record)
        record = fixture()[2]
        record["repository"] = "FS-GG/foreign"
        self.refuses(source=record)
        event = fixture()[3]
        event["principalId"] = "source-reader"
        self.refuses(approval=event)

    def test_scope_drift_broad_scope_and_exception_refuse(self):
        broad = fixture()[0]
        broad["permissions"].append("contents:write")
        self.refuses(source_scope=broad)
        shared = fixture()[1]
        shared["credentialId"] = "1" * 64
        self.refuses(approval_scope=shared)
        source_claim, approval_claim, source_record, approval_event = fixture()
        left = FakePort(source_claim, source_record)
        right = FakePort(approval_claim, approval_event)
        right.scopes[1]["credentialId"] = "8" * 64
        with self.assertRaisesRegex(preflight.Refused, "release-scope-drift"):
            preflight.qualify(left, right, REVISION, TREE, 101, 202, 1,
                              303, 404, 606, 505, NOW)
        class Broken(FakePort):
            def read_approval(self, event_id):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaises(preflight.Refused) as caught:
            preflight.qualify(FakePort(source_claim, source_record),
                Broken(approval_claim, approval_event), REVISION, TREE,
                101, 202, 1, 303, 404, 606, 505, NOW)
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))

    def test_reused_source_or_approval_scope_mutated_at_final_read_refuses(self):
        for changed_port in ("source", "approval"):
            with self.subTest(changed_port=changed_port):
                source_claim, approval_claim, source_record, approval_event = fixture()
                left = FakePort(source_claim, source_record)
                right = FakePort(approval_claim, approval_event)
                port = left if changed_port == "source" else right
                shared = copy.deepcopy(port.scopes[0])
                reads = [0]
                def scope():
                    reads[0] += 1
                    if reads[0] == 2:
                        shared["credentialId"] = "8" * 64
                    return shared
                port.scope = scope
                with self.assertRaises(preflight.Refused):
                    preflight.qualify(left, right, REVISION, TREE, 101, 202, 1,
                                      303, 404, 606, 505, NOW)

    def test_swapped_positive_source_record_id_refuses(self):
        source_claim, approval_claim, source_record, approval_event = fixture()
        source_record["recordId"] = 102
        with self.assertRaises(preflight.Refused):
            preflight.qualify(FakePort(source_claim, source_record),
                FakePort(approval_claim, approval_event), REVISION, TREE,
                101, 202, 1, 303, 404, 606, 505, NOW)
        source_claim, approval_claim, source_record, approval_event = fixture()
        approval_event["sourceRecordId"] = 102
        with self.assertRaises(preflight.Refused):
            preflight.qualify(FakePort(source_claim, source_record),
                FakePort(approval_claim, approval_event), REVISION, TREE,
                101, 202, 1, 303, 404, 606, 505, NOW)


if __name__ == "__main__":
    unittest.main()
