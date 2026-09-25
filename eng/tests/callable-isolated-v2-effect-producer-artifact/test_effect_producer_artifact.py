"""Fake producer run and artifact custody cannot release effect authority."""

import copy
import datetime as dt
import hashlib
import io
import os
import pathlib
import socket
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_effect_release_preflight as release
import callable_isolated_v2_effect_git_tree_witness as tree_witness
import callable_isolated_v2_effect_producer_artifact as producer
import callable_isolated_v2_effect_producer_workflow_source as workflow_source
import verify_callable_isolated_v2_effect_producer_workflow as closed_workflow

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
REV = "a" * 40
TREE = "b" * 40
ARCHIVE_SHA = lambda raw: hashlib.sha256(raw).hexdigest()


class Port:
    def __init__(self, scope, run=None, artifact=None, bundle=None):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.run = copy.deepcopy(run)
        self.artifact = copy.deepcopy(artifact)
        self.bundle = bundle
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read_run(self, run_id, attempt):
        self.reads.append(("run", run_id, attempt))
        return copy.deepcopy(self.run)

    def read_artifact(self, artifact_id):
        self.reads.append(("artifact", artifact_id))
        return copy.deepcopy(self.artifact)

    def download_bundle(self, artifact_id, url):
        self.reads.append(("bundle", artifact_id, url))
        return self.bundle


def fixture():
    with tempfile.TemporaryDirectory() as temporary:
        archive_path = pathlib.Path(temporary) / builder.ARCHIVE_NAME
        builder.build(archive_path)
        archive = archive_path.read_bytes()
    container = io.BytesIO()
    with zipfile.ZipFile(container, "w", compression=zipfile.ZIP_STORED) as zipped:
        zipped.writestr(builder.ARCHIVE_NAME, archive)
    bundle = container.getvalue()
    preflight = release.PreflightResult(REV, TREE, "c" * 64, 404,
        ARCHIVE_SHA(archive), 202, 1, 303, 606, 505, 101)
    tree = tree_witness.TreeWitnessResult(REV, TREE,
        ARCHIVE_SHA(archive), tuple(sorted((path, "d" * 64)
            for path in producer.SOURCE_PATHS)), 808)
    workflow = ".github/workflows/callable-isolated-v2-effect-release.yml"
    url = "https://api.github.com/repos/FS-GG/FS.GG.Coordination/actions/artifacts/404/zip"
    selected = {"repositoryId": 77, "identityEventId": 808,
        "producerRunId": 202, "producerRunAttempt": 1,
        "producerActorId": 303, "workflowId": 909,
        "workflowPath": workflow,
        "workflowSha256": closed_workflow.PINNED_WORKFLOW_SHA256,
        "artifactId": 404}
    run_scope = {"principalId": "run-reader", "credentialId": "1" * 64,
        "repository": release.REPOSITORY, "repositoryId": 77,
        "permissions": ["actions:read", "metadata:read"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    bundle_scope = {"principalId": "bundle-reader", "credentialId": "2" * 64,
        "repository": release.REPOSITORY, "repositoryId": 77,
        "permissions": ["actions:read"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    run = {"schema": producer.RUN_SCHEMA, "complete": True,
        "principalId": "run-reader", "credentialId": "1" * 64,
        "repository": release.REPOSITORY, "repositoryId": 77,
        "runId": 202, "runAttempt": 1, "headSha": REV,
        "headTree": TREE, "headBranch": "main", "event": "workflow_dispatch",
        "status": "completed", "conclusion": "success",
        "actorId": 303, "workflowId": 909, "workflowPath": workflow,
        "workflowSha256": closed_workflow.PINNED_WORKFLOW_SHA256,
        "artifactIds": [404],
        "createdAt": "2026-09-25T11:55:00Z",
        "completedAt": "2026-09-25T11:57:00Z"}
    artifact = {"schema": producer.ARTIFACT_SCHEMA, "complete": True,
        "principalId": "run-reader", "credentialId": "1" * 64,
        "repository": release.REPOSITORY, "repositoryId": 77,
        "artifactId": 404, "name": builder.ARCHIVE_NAME,
        "runId": 202, "runAttempt": 1, "headSha": REV,
        "headTree": TREE, "workflowId": 909,
        "expired": False, "size": len(bundle),
        "digest": "sha256:" + ARCHIVE_SHA(bundle), "downloadUrl": url,
        "createdAt": "2026-09-25T11:57:00Z",
        "expiresAt": "2026-09-26T11:57:00Z"}
    return (preflight, tree, selected,
        Port(run_scope, run, artifact), Port(bundle_scope, bundle=bundle))


def source_result(preflight, selected):
    return workflow_source.WorkflowSourceResult(
        preflight.coordination_revision, preflight.source_tree,
        selected["repositoryId"], selected["identityEventId"],
        selected["workflowPath"], selected["workflowSha256"],
        closed_workflow.PINNED_WORKFLOW_BLOB_OID)


class ProducerArtifactTests(unittest.TestCase):
    def test_missing_workflow_source_was_a_false_green(self):
        preflight, tree, selected, run_port, bundle_port = fixture()
        with self.assertRaises(producer.Refused):
            producer.qualify(preflight, tree, run_port, bundle_port,
                             selected, NOW)

    def observe(self, change=None):
        preflight, tree, selected, run_port, bundle_port = fixture()
        if change:
            change(preflight, tree, selected, run_port, bundle_port)
        return producer.qualify(preflight, tree, run_port, bundle_port,
                                selected, NOW,
                                workflow_source=source_result(preflight, selected))

    def refuses(self, change):
        with self.assertRaises(producer.Refused):
            self.observe(change)

    def test_matching_fake_run_artifact_and_bundle_stay_closed(self):
        preflight, tree, selected, run_port, bundle_port = fixture()
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect", side_effect=AssertionError("post")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = producer.qualify(preflight, tree, run_port, bundle_port,
                                      selected, NOW,
                                      workflow_source=source_result(preflight, selected))
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual(result.archive_sha256, preflight.archive_sha256)
        self.assertEqual(result.workflow_blob_oid,
                         closed_workflow.PINNED_WORKFLOW_BLOB_OID)
        self.assertEqual(len(run_port.reads), 2)
        self.assertEqual(len(bundle_port.reads), 1)

    def test_wrong_head_attempt_actor_workflow_and_artifact_refuse(self):
        for key, value in (("headSha", "f" * 40), ("headTree", "f" * 40),
                           ("runAttempt", 2), ("actorId", 606),
                           ("workflowId", 910), ("workflowSha256", "f" * 64),
                           ("status", "in_progress"), ("conclusion", "failure")):
            with self.subTest(key=key):
                self.refuses(lambda _p, _t, _s, r, _b: r.run.__setitem__(key, value))
        for key, value in (("runAttempt", 2), ("headTree", "f" * 40),
                           ("workflowId", 910), ("digest", "sha256:" + "f" * 64),
                           ("downloadUrl", "https://foreign.test/archive"),
                           ("expired", True)):
            with self.subTest(key=key):
                self.refuses(lambda _p, _t, _s, r, _b: r.artifact.__setitem__(key, value))
        self.refuses(lambda _p, _t, _s, _r, b: setattr(b, "bundle", b"foreign"))
        self.refuses(lambda _p, _t, _s, r, _b: r.run.__setitem__("artifactIds", []))

    def test_reader_custody_preflight_and_time_refuse(self):
        self.refuses(lambda _p, _t, _s, _r, b: b.scopes[0].__setitem__("credentialId", "1" * 64))
        self.refuses(lambda _p, _t, _s, _r, b: b.scopes[1].__setitem__("repositoryId", 78))
        self.refuses(lambda p, _t, _s, _r, _b: object.__setattr__(p, "producer_run_attempt", 2))
        self.refuses(lambda _p, t, _s, _r, _b: object.__setattr__(t, "source_tree", "f" * 40))
        self.refuses(lambda _p, t, _s, _r, _b: object.__setattr__(t, "source_files", ()))
        self.refuses(lambda _p, _t, _s, r, _b: r.run.__setitem__("completedAt", "2026-09-25T11:54:00Z"))
        self.refuses(lambda _p, _t, s, _r, _b: s.__setitem__("workflowPath", "../other.yml"))

    def test_reused_reader_scope_mutated_during_bundle_read_refuses(self):
        for changed_port in ("producer", "bundle"):
            with self.subTest(changed_port=changed_port):
                preflight, tree, selected, run_port, bundle_port = fixture()
                port = run_port if changed_port == "producer" else bundle_port
                shared = copy.deepcopy(port.scopes[0])
                port.scopes = [shared, shared]
                original_download = bundle_port.download_bundle
                def drift(artifact_id, url):
                    raw = original_download(artifact_id, url)
                    shared["credentialId"] = "f" * 64
                    return raw
                bundle_port.download_bundle = drift
                with self.assertRaises(producer.Refused):
                    producer.qualify(preflight, tree, run_port, bundle_port,
                                     selected, NOW,
                                     workflow_source=source_result(preflight, selected))

    def test_symlink_mode_artifact_member_was_a_false_green(self):
        preflight, tree, selected, run_port, bundle_port = fixture()
        with zipfile.ZipFile(io.BytesIO(bundle_port.bundle)) as zipped:
            archive = zipped.read(builder.ARCHIVE_NAME)
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as zipped:
            info = zipfile.ZipInfo(builder.ARCHIVE_NAME)
            info.create_system = 3
            info.external_attr = 0o120777 << 16
            zipped.writestr(info, archive)
        bundle_port.bundle = output.getvalue()
        run_port.artifact["size"] = len(bundle_port.bundle)
        run_port.artifact["digest"] = "sha256:" + ARCHIVE_SHA(bundle_port.bundle)
        with self.assertRaises(producer.Refused):
            producer.qualify(preflight, tree, run_port, bundle_port,
                             selected, NOW,
                             workflow_source=source_result(preflight, selected))

    def test_workflow_source_foreign_revision_or_digest_refuses(self):
        preflight, tree, selected, run_port, bundle_port = fixture()
        selected_source = source_result(preflight, selected)
        object.__setattr__(selected_source, "coordination_revision", "f" * 40)
        with self.assertRaises(producer.Refused):
            producer.qualify(preflight, tree, run_port, bundle_port,
                             selected, NOW, workflow_source=selected_source)

    def test_coherent_foreign_workflow_source_blob_refuses(self):
        preflight, tree, selected, run_port, bundle_port = fixture()
        selected_source = source_result(preflight, selected)
        object.__setattr__(selected_source, "workflow_blob_oid", "2" * 40)
        with self.assertRaises(producer.Refused):
            producer.qualify(preflight, tree, run_port, bundle_port,
                             selected, NOW, workflow_source=selected_source)
        preflight, tree, selected, run_port, bundle_port = fixture()
        selected["workflowSha256"] = "9" * 64
        run_port.run["workflowSha256"] = "9" * 64
        selected_source = source_result(preflight, selected)
        with self.assertRaises(producer.Refused):
            producer.qualify(preflight, tree, run_port, bundle_port,
                             selected, NOW, workflow_source=selected_source)
        selected_source = source_result(preflight, selected)
        object.__setattr__(selected_source, "workflow_sha256", "f" * 64)
        with self.assertRaises(producer.Refused):
            producer.qualify(preflight, tree, run_port, bundle_port,
                             selected, NOW, workflow_source=selected_source)


if __name__ == "__main__":
    unittest.main()
