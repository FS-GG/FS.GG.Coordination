"""Fake provider artifact and workflow observations remain non-authoritative."""

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
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_installed_refusal as installed
import callable_isolated_v2_v5_artifact_workflow_witness as witness

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


class Port:
    def __init__(self, scope, record):
        self.scope_data = scope
        self.record = record
        self.calls = []

    def scope(self):
        return self.scope_data

    def read(self, *args):
        self.calls.append(args)
        return copy.deepcopy(self.record)


def fixture():
    entry = (ROOT / builder.ENTRY_SOURCE).read_bytes()
    workflow = (ROOT / builder.WORKFLOW).read_bytes()
    archive = builder._archive(entry)
    selection = candidate.Selection(
        candidate.PINNED_BYTES["archive"], candidate.PINNED_BYTES["manifest"],
        "1" * 40, "2" * 40, 300, 1, 305, 100, 302, 301, 304, 303,
        "2026-09-25T11:57:00Z", "2026-09-25T12:10:00Z",
        "source-reader", "a" * 64, "review-reader", "b" * 64)
    readback = installed.Readback(selection.archive_sha256,
        selection.manifest_sha256, selection.revision, selection.artifact_id,
        700, 702, selection.source_tree, selection.repository_id, 701, 703,
        "3" * 64, "4" * 64, "5" * 64,
        "/opt/fsgg/v5/fsgg-callable-isolated-v2-v5-no-grant.pyz",
        "2026-09-25T11:58:00Z", "2026-09-25T11:58:01Z")
    artifact_scope = {"principalId": "artifact-reader",
        "credentialId": "6" * 64, "repository": candidate.REPOSITORY,
        "repositoryId": 100, "permissions": ["actions:read"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    workflow_scope = {"principalId": "workflow-reader",
        "credentialId": "7" * 64, "repository": candidate.REPOSITORY,
        "repositoryId": 100, "permissions": ["contents:read"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    common = {"repository": candidate.REPOSITORY, "repositoryId": 100,
        "revision": selection.revision, "sourceTree": selection.source_tree}
    artifact = {"schema": witness.ARTIFACT_SCHEMA, "complete": True,
        "principalId": "artifact-reader", "credentialId": "6" * 64,
        **common, "producerRunId": 300, "producerRunAttempt": 1,
        "producerActorId": 301, "artifactId": 302,
        "artifactName": builder.ARCHIVE_NAME,
        "manifestSha256": selection.manifest_sha256,
        "archiveSha256": selection.archive_sha256,
        "archiveSize": len(archive), "archiveBytes": archive,
        "immutable": True, "createdAt": "2026-09-25T11:56:00Z",
        "expiresAt": "2026-09-25T12:20:00Z"}
    workflow_record = {"schema": witness.WORKFLOW_SCHEMA,
        "complete": True, "principalId": "workflow-reader",
        "credentialId": "7" * 64, **common,
        "workflowPath": builder.WORKFLOW,
        "workflowSha256": hashlib.sha256(workflow).hexdigest(),
        "workflowBytes": workflow, "immutable": True,
        "observedAt": "2026-09-25T11:59:00Z"}
    return (selection, readback, Port(artifact_scope, artifact),
            Port(workflow_scope, workflow_record))


class ArtifactWorkflowWitnessTests(unittest.TestCase):
    def observe(self, change=None):
        selection, readback, artifact, workflow = fixture()
        if change:
            change(selection, readback, artifact, workflow)
        return witness.qualify(selection, readback, artifact, workflow, NOW)

    def test_matching_fake_observations_stay_closed(self):
        selection, readback, artifact, workflow = fixture()
        result = witness.qualify(selection, readback, artifact, workflow, NOW)
        self.assertEqual(artifact.calls, [(302,)])
        self.assertEqual(workflow.calls, [(selection.revision, builder.WORKFLOW)])
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_foreign_producer_or_artifact_refuses(self):
        for key, value in (("producerRunAttempt", 2),
                           ("producerActorId", 999),
                           ("artifactId", 999),
                           ("archiveSha256", "9" * 64),
                           ("archiveBytes", b"foreign"),
                           ("immutable", False)):
            with self.subTest(key=key):
                with self.assertRaises(witness.Refused):
                    self.observe(lambda _s, _r, a, _w:
                                 a.record.update({key: value}))

    def test_foreign_workflow_source_or_reader_refuses(self):
        for key, value in (("sourceTree", "8" * 40),
                           ("workflowSha256", "8" * 64),
                           ("workflowBytes", b"enabled"),
                           ("immutable", False)):
            with self.subTest(key=key):
                with self.assertRaises(witness.Refused):
                    self.observe(lambda _s, _r, _a, w:
                                 w.record.update({key: value}))
        with self.assertRaises(witness.Refused):
            self.observe(lambda s, _r, a, _w:
                         a.scope_data.update(principalId=s.source_reader_principal))

    def test_wrong_installed_artifact_or_expired_observation_refuses(self):
        with self.assertRaises(witness.Refused):
            self.observe(lambda _s, r, _a, _w:
                         object.__setattr__(r, "artifact_id", 999))
        with self.assertRaises(witness.Refused):
            self.observe(lambda _s, _r, a, _w:
                         a.record.update(expiresAt="2026-09-25T11:59:00Z"))

    def test_no_file_token_socket_or_journal_access(self):
        selection, readback, artifact, workflow = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = witness.qualify(selection, readback, artifact, workflow,
                                     NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
