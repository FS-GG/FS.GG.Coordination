"""Fake independent source and review selection for exact refusal ZIP bytes."""

import copy
import datetime as dt
import hashlib
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_v5_no_grant_selection as selection

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


class FakePort:
    def __init__(self, scope, record):
        self.scope_value = scope
        self.record = record
        self.calls = []

    def scope(self):
        return self.scope_value

    def read(self, record_id):
        self.calls.append(record_id)
        return copy.deepcopy(self.record)


def fixture():
    blobs = {"entry": (ROOT / builder.ENTRY_SOURCE).read_bytes(),
             "builder": (ENG / "build_callable_isolated_v2_v5_no_grant.py").read_bytes(),
             "verifier": (ENG / "verify_callable_isolated_v2_v5_no_grant.py").read_bytes(),
             "workflow": (ROOT / builder.WORKFLOW).read_bytes(),
             "manifest": (ROOT / builder.MANIFEST).read_bytes()}
    blobs["archive"] = builder._archive(blobs["entry"])
    chosen = {"repositoryId": 100, "revision": "1" * 40,
        "sourceTree": "2" * 40, "runId": 300, "runAttempt": 1,
        "producerActorId": 301, "artifactId": 302,
        "sourceRecordId": 303, "reviewerActorId": 304,
        "reviewEventId": 305}
    digests = {"manifestSha256": sha(blobs["manifest"]),
               "archiveSha256": sha(blobs["archive"]),
               "workflowSha256": sha(blobs["workflow"]),
               "builderSha256": sha(blobs["builder"]),
               "verifierSha256": sha(blobs["verifier"]),
               "entrySha256": sha(blobs["entry"])}
    common = {"repository": selection.REPOSITORY,
              "repositoryId": chosen["repositoryId"],
              "revision": chosen["revision"],
              "sourceTree": chosen["sourceTree"],
              "runId": chosen["runId"],
              "runAttempt": chosen["runAttempt"],
              "producerActorId": chosen["producerActorId"],
              "artifactId": chosen["artifactId"], **digests}
    source = {"schema": selection.SOURCE_SCHEMA, "complete": True,
        "principalId": "source-reader", "credentialId": "a" * 64,
        "recordId": chosen["sourceRecordId"], **common,
        "observedAt": "2026-09-25T11:57:00Z"}
    review = {"schema": selection.REVIEW_SCHEMA, "complete": True,
        "principalId": "review-reader", "credentialId": "b" * 64,
        "eventId": chosen["reviewEventId"],
        "sourceRecordId": chosen["sourceRecordId"],
        "reviewerActorId": chosen["reviewerActorId"], **common,
        "decision": "inspect-only-release",
        "reviewedAt": "2026-09-25T11:58:00Z",
        "expiresAt": "2026-09-25T12:20:00Z"}
    source_scope = {"principalId": "source-reader",
        "credentialId": "a" * 64, "repository": selection.REPOSITORY,
        "repositoryId": 100, "permissions": ["actions:read", "contents:read"],
        "expiresAt": "2026-09-25T12:20:00Z"}
    review_scope = {"principalId": "review-reader",
        "credentialId": "b" * 64, "repository": selection.REPOSITORY,
        "repositoryId": 100, "permissions": ["read-release-approval"],
        "expiresAt": "2026-09-25T12:20:00Z"}
    return chosen, blobs, source, review, source_scope, review_scope


def run(chosen=None, blobs=None, source=None, review=None,
        source_scope=None, review_scope=None):
    c, b, s, r, ss, rs = fixture()
    source_port = FakePort(ss if source_scope is None else source_scope,
                           s if source is None else source)
    review_port = FakePort(rs if review_scope is None else review_scope,
                           r if review is None else review)
    result = selection.qualify(source_port, review_port,
        c if chosen is None else chosen, b if blobs is None else blobs, NOW)
    return result, source_port, review_port


class V5SelectionTests(unittest.TestCase):
    def test_matching_fake_selection_is_closed(self):
        result, source, review = run()
        self.assertEqual(source.calls, [303])
        self.assertEqual(review.calls, [305])
        self.assertEqual(result.archive_sha256, fixture()[2]["archiveSha256"])
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_foreign_source_and_review_coordinates_refuse(self):
        changes = (("revision", "9" * 40), ("sourceTree", "9" * 40),
                   ("runAttempt", 2), ("producerActorId", 306),
                   ("artifactId", 307),
                   ("manifestSha256", "9" * 64),
                   ("archiveSha256", "9" * 64),
                   ("workflowSha256", "9" * 64),
                   ("builderSha256", "9" * 64),
                   ("verifierSha256", "9" * 64),
                   ("entrySha256", "9" * 64))
        for side in ("source", "review"):
            for key, value in changes:
                with self.subTest(side=side, key=key):
                    chosen, blobs, source, review, ss, rs = fixture()
                    (source if side == "source" else review)[key] = value
                    with self.assertRaises(selection.Refused):
                        run(chosen, blobs, source, review, ss, rs)

    def test_bool_cannot_alias_selected_integer_coordinates(self):
        for side in ("source", "review"):
            for key, value in (("runAttempt", True), ("complete", 1)):
                with self.subTest(side=side, key=key):
                    chosen, blobs, source, review, ss, rs = fixture()
                    (source if side == "source" else review)[key] = value
                    with self.assertRaises(selection.Refused):
                        run(chosen, blobs, source, review, ss, rs)

    def test_missing_self_review_stale_or_effect_decision_refuses(self):
        for key, value in (("reviewerActorId", 301),
                           ("eventId", 306),
                           ("sourceRecordId", 999),
                           ("decision", "native-effect"),
                           ("reviewedAt", "2026-09-25T11:00:00Z"),
                           ("expiresAt", "2026-09-25T12:00:00Z")):
            with self.subTest(key=key):
                chosen, blobs, source, review, ss, rs = fixture()
                review[key] = value
                with self.assertRaises(selection.Refused):
                    run(chosen, blobs, source, review, ss, rs)

    def test_changed_selected_bytes_refuse_even_with_fake_approval(self):
        for key in ("archive", "manifest", "builder", "verifier",
                    "entry", "workflow"):
            with self.subTest(key=key):
                chosen, blobs, source, review, ss, rs = fixture()
                blobs[key] += b"FOREIGN"
                with self.assertRaises(selection.Refused):
                    run(chosen, blobs, source, review, ss, rs)

    def test_both_fake_readers_cannot_approve_foreign_verifier_source(self):
        chosen, blobs, source, review, ss, rs = fixture()
        blobs["verifier"] += b"# alternate verifier\n"
        source["verifierSha256"] = sha(blobs["verifier"])
        review["verifierSha256"] = sha(blobs["verifier"])
        with self.assertRaises(selection.Refused):
            run(chosen, blobs, source, review, ss, rs)

    def test_shared_broad_or_drifting_reader_refuses(self):
        for key, value in (("principalId", "source-reader"),
                           ("credentialId", "a" * 64),
                           ("permissions", ["read-release-approval",
                                            "contents:write"])):
            with self.subTest(key=key):
                chosen, blobs, source, review, ss, rs = fixture()
                rs[key] = value
                with self.assertRaises(selection.Refused):
                    run(chosen, blobs, source, review, ss, rs)
        chosen, blobs, source, review, ss, rs = fixture()
        source_port = FakePort(ss, source)
        class Drift(FakePort):
            def read(self, event_id):
                ss["credentialId"] = "c" * 64
                return super().read(event_id)
        with self.assertRaises(selection.Refused):
            selection.qualify(source_port, Drift(rs, review), chosen, blobs, NOW)

    def test_no_token_file_socket_or_journal_access(self):
        chosen, blobs, source, review, ss, rs = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("sqlite")):
            result = selection.qualify(FakePort(ss, source),
                                       FakePort(rs, review), chosen, blobs, NOW)
        self.assertFalse(result.can_dispatch)


if __name__ == "__main__":
    unittest.main()
