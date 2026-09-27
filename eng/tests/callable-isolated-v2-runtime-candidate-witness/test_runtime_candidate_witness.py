"""Fixture-only producer evidence binds the exact runtime candidate bytes."""

import copy
import datetime as dt
import hashlib
import io
import json
import pathlib
import stat
import sys
import unittest
import zipfile
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import callable_isolated_v2_runtime_candidate_witness as witness
import verify_callable_isolated_v2_runtime_candidate_workflow as workflow_check

NOW = dt.datetime(2026, 9, 27, 12, tzinfo=dt.timezone.utc)
SHA = "a" * 40
TREE = "b" * 40


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def zipped(entries, compression=zipfile.ZIP_STORED):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=compression) as archive:
        for name, raw in entries:
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.compress_type = compression
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(info, raw)
    return output.getvalue()


class Port:
    def __init__(self, scope, run=None, artifact=None, bundle=None):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.run = copy.deepcopy(run)
        self.artifact = copy.deepcopy(artifact)
        self.bundle = bundle
        self.calls = []

    def scope(self):
        return self.scopes.pop(0)

    def read_run(self, run_id, attempt):
        self.calls.append(("run", run_id, attempt))
        return copy.deepcopy(self.run)

    def read_artifact(self, artifact_id):
        self.calls.append(("artifact", artifact_id))
        return copy.deepcopy(self.artifact)

    def download(self, artifact_id, url):
        self.calls.append(("download", artifact_id, url))
        return self.bundle


def fixture():
    workflow = (ROOT / workflow_check.WORKFLOW).read_bytes()
    member_bytes = [(path, ("fixture:" + path).encode())
                    for path in witness.MEMBERS]
    archive = zipped(member_bytes)
    manifest = {
        "schema": witness.ARTIFACT_SCHEMA,
        "sourceRevision": SHA,
        "sourceTree": TREE,
        "builderSha256": "c" * 64,
        "retainedOperatorSha256": sha(member_bytes[1][1]),
        "archiveSha256": sha(archive),
        "runtimeRequirements": copy.deepcopy(witness.RUNTIME_REQUIREMENTS),
        "members": [{"path": path, "sha256": sha(raw), "size": len(raw)}
                    for path, raw in member_bytes],
    }
    manifest_bytes = (json.dumps(manifest, sort_keys=True,
                                 separators=(",", ":")) + "\n").encode()
    bundle = zipped([(witness.ARCHIVE_NAME, archive),
                     (witness.MANIFEST_NAME, manifest_bytes)])
    selection = witness.Selection(77, SHA, TREE, 101, 1, 102, 103, 104,
                                  sha(workflow))
    producer_scope = {"principalId": "producer-reader",
        "credentialId": "1" * 64, "repository": witness.REPOSITORY,
        "repositoryId": 77, "permissions": ["actions:read", "metadata:read"],
        "expiresAt": "2026-09-27T12:20:00Z"}
    download_scope = {"principalId": "download-reader",
        "credentialId": "2" * 64, "repository": witness.REPOSITORY,
        "repositoryId": 77, "permissions": ["actions:read"],
        "expiresAt": "2026-09-27T12:20:00Z"}
    common = {"repository": witness.REPOSITORY, "repositoryId": 77,
        "runId": 101, "runAttempt": 1, "headSha": SHA, "headTree": TREE,
        "workflowId": 103}
    run = {"schema": witness.RUN_SCHEMA, "complete": True,
        "principalId": "producer-reader", "credentialId": "1" * 64,
        **common, "event": "workflow_dispatch", "status": "completed",
        "conclusion": "success", "actorId": 102,
        "workflowPath": workflow_check.WORKFLOW,
        "workflowSha256": sha(workflow), "artifactIds": [104],
        "createdAt": "2026-09-27T11:55:00Z",
        "completedAt": "2026-09-27T11:57:00Z"}
    url = (f"{witness.API}/repos/{witness.REPOSITORY}/actions/"
           "artifacts/104/zip")
    artifact = {"schema": witness.RECORD_SCHEMA, "complete": True,
        "principalId": "producer-reader", "credentialId": "1" * 64,
        **common, "artifactId": 104,
        "name": f"callable-isolated-v2-runtime-candidate-{SHA}",
        "expired": False, "size": len(bundle), "digest": "sha256:" + sha(bundle),
        "downloadUrl": url, "createdAt": "2026-09-27T11:56:00Z",
        "expiresAt": "2026-09-28T11:57:00Z"}
    return (selection, workflow, Port(producer_scope, run, artifact),
            Port(download_scope, bundle=bundle), archive, manifest_bytes)


class RuntimeCandidateWitnessTests(unittest.TestCase):
    def observe(self, change=None):
        values = list(fixture())
        if change:
            change(values)
        return witness.qualify(*values[:4], NOW)

    def test_successful_first_attempt_binds_all_candidate_bytes(self):
        selection, workflow, producer, download, archive, manifest = fixture()
        result = witness.qualify(selection, workflow, producer, download, NOW)
        self.assertEqual((result.repository, result.source_sha,
                          result.source_tree, result.run_id,
                          result.run_attempt, result.artifact_id),
                         (witness.REPOSITORY, SHA, TREE, 101, 1, 104))
        self.assertEqual(result.workflow_sha256, sha(workflow))
        self.assertEqual(result.archive_sha256, sha(archive))
        self.assertEqual(result.manifest_sha256, sha(manifest))
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch_effect)
        self.assertEqual(result.live_effects, 0)

    def test_wrong_source_workflow_or_attempt_refuses(self):
        changes = (
            lambda v: object.__setattr__(v[0], "source_sha", "f" * 40),
            lambda v: object.__setattr__(v[0], "source_tree", "f" * 40),
            lambda v: object.__setattr__(v[0], "run_attempt", 2),
            lambda v: v[2].run.update(runAttempt=2),
            lambda v: v.__setitem__(1, v[1] + b"\n"),
            lambda v: v[2].run.update(conclusion="failure"),
        )
        for change in changes:
            with self.subTest(change=change):
                with self.assertRaises(witness.Refused):
                    self.observe(change)

    def test_swapped_archive_or_incomplete_upload_refuses(self):
        def swap_archive(values):
            foreign = zipped([("foreign.py", b"foreign")])
            values[3].bundle = zipped([(witness.ARCHIVE_NAME, foreign),
                                       (witness.MANIFEST_NAME, values[5])])
            values[2].artifact["size"] = len(values[3].bundle)
            values[2].artifact["digest"] = "sha256:" + sha(values[3].bundle)

        def incomplete(values):
            values[3].bundle = zipped([(witness.ARCHIVE_NAME, values[4])])
            values[2].artifact["size"] = len(values[3].bundle)
            values[2].artifact["digest"] = "sha256:" + sha(values[3].bundle)

        for change in (swap_archive, incomplete):
            with self.subTest(change=change):
                with self.assertRaises(witness.Refused):
                    self.observe(change)

    def test_compressed_oversized_inner_archive_refuses(self):
        def compressed_inner(values):
            foreign = zipped([(witness.MEMBERS[0], b"x" * 3_000_000)],
                             zipfile.ZIP_DEFLATED)
            values[3].bundle = zipped([(witness.ARCHIVE_NAME, foreign),
                                       (witness.MANIFEST_NAME, values[5])])
            values[2].artifact["size"] = len(values[3].bundle)
            values[2].artifact["digest"] = "sha256:" + sha(values[3].bundle)

        with self.assertRaises(witness.Refused):
            self.observe(compressed_inner)

    def test_outer_zip_expansion_refuses_before_member_read(self):
        expanded = zipped([
            (witness.ARCHIVE_NAME, b"x" * 3_000_000),
            (witness.MANIFEST_NAME, b"{}\n"),
        ], zipfile.ZIP_DEFLATED)
        reads = []
        original = zipfile.ZipFile.read

        def tracked(instance, *args, **kwargs):
            reads.append(args)
            return original(instance, *args, **kwargs)

        with mock.patch.object(zipfile.ZipFile, "read", tracked):
            with self.assertRaises(witness.Refused):
                witness._read_bundle(expanded)
        self.assertEqual(reads, [])

    def test_mismatched_or_noncanonical_manifest_refuses(self):
        def replace_manifest(values, transform):
            parsed = json.loads(values[5])
            transform(parsed)
            changed = (json.dumps(parsed, sort_keys=True,
                                  separators=(",", ":")) + "\n").encode()
            values[3].bundle = zipped([(witness.ARCHIVE_NAME, values[4]),
                                       (witness.MANIFEST_NAME, changed)])
            values[2].artifact["size"] = len(values[3].bundle)
            values[2].artifact["digest"] = "sha256:" + sha(values[3].bundle)

        changes = (
            lambda v: replace_manifest(v, lambda m: m.update(sourceRevision="f" * 40)),
            lambda v: replace_manifest(v, lambda m: m.update(archiveSha256="f" * 64)),
            lambda v: replace_manifest(v, lambda m: m["members"][0].update(size=999)),
        )
        for change in changes:
            with self.subTest(change=change):
                with self.assertRaises(witness.Refused):
                    self.observe(change)

        def noncanonical(values):
            changed = values[5][:-1] + b" \n"
            values[3].bundle = zipped([(witness.ARCHIVE_NAME, values[4]),
                                       (witness.MANIFEST_NAME, changed)])
            values[2].artifact["size"] = len(values[3].bundle)
            values[2].artifact["digest"] = "sha256:" + sha(values[3].bundle)
        with self.assertRaises(witness.Refused):
            self.observe(noncanonical)

    def test_wrong_artifact_identity_and_incomplete_records_refuse(self):
        for change in (
            lambda v: v[2].artifact.update(artifactId=999),
            lambda v: v[2].artifact.update(name="foreign"),
            lambda v: v[2].artifact.update(complete=False),
            lambda v: v[2].run.update(artifactIds=[104, 999]),
        ):
            with self.subTest(change=change):
                with self.assertRaises(witness.Refused):
                    self.observe(change)

    def test_artifact_outside_run_interval_refuses(self):
        for created_at in ("2026-09-27T11:54:59Z",
                           "2026-09-27T11:57:01Z"):
            with self.subTest(created_at=created_at):
                with self.assertRaises(witness.Refused):
                    self.observe(lambda values: values[2].artifact.update(
                        createdAt=created_at))


if __name__ == "__main__":
    unittest.main()
