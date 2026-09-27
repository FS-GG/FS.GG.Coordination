"""Raw REST fixture boundaries for the source-only S6-A candidate adapter."""

import base64
import copy
import datetime as dt
import hashlib
import importlib.util
import json
import pathlib
import sys
import unittest

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import callable_isolated_v2_runtime_candidate_read_adapter as adapter
import callable_isolated_v2_runtime_candidate_witness as witness
from callable_isolated_v2_workflow_read_adapter import Response

SPEC = importlib.util.spec_from_file_location(
    "s5_candidate_fixture", ENG / "tests/callable-isolated-v2-runtime-candidate-witness"
    / "test_runtime_candidate_witness.py")
s5 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(s5)


class RawReader:
    def __init__(self, scope, pages):
        self.value = scope
        self.pages = pages
        self.calls = []

    def scope(self):
        return copy.deepcopy(self.value)

    def get(self, path):
        self.calls.append(path)
        value = self.pages[path]
        return Response(200, (), json.dumps(value).encode())


class BundleReader:
    def __init__(self, scope, raw):
        self.value, self.raw = scope, raw
        self.calls = []

    def scope(self):
        return copy.deepcopy(self.value)

    def download(self, artifact_id, url):
        self.calls.append((artifact_id, url))
        return self.raw


def fixture():
    selection, workflow, producer, download, _, _ = s5.fixture()
    root = f"repos/{witness.REPOSITORY}"
    blob_sha = hashlib.sha1(b"blob " + str(len(workflow)).encode()
                            + b"\0" + workflow).hexdigest()
    artifact = producer.artifact
    native_artifact = {
        "id": selection.artifact_id, "name": artifact["name"],
        "size_in_bytes": artifact["size"], "digest": artifact["digest"],
        "expired": False, "created_at": artifact["createdAt"],
        "expires_at": artifact["expiresAt"],
        "archive_download_url": artifact["downloadUrl"],
        "workflow_run": {"id": selection.run_id,
                         "repository_id": selection.repository_id,
                         "head_sha": selection.source_sha},
    }
    pages = {
        f"{root}/actions/runs/101/attempts/1": {
            "id": 101, "run_attempt": 1, "head_sha": selection.source_sha,
            "workflow_id": 103, "event": "workflow_dispatch",
            "status": "completed", "conclusion": "success",
            "path": adapter.workflow.WORKFLOW + "@main",
            "repository": {"id": 77, "full_name": witness.REPOSITORY},
            "actor": {"id": 102}, "created_at": producer.run["createdAt"],
            "updated_at": producer.run["completedAt"],
        },
        f"{root}/git/commits/{selection.source_sha}": {
            "sha": selection.source_sha, "tree": {"sha": selection.source_tree}},
        f"{root}/contents/{adapter.workflow.WORKFLOW}?ref={selection.source_sha}": {
            "type": "file", "path": adapter.workflow.WORKFLOW,
            "size": len(workflow), "sha": blob_sha, "encoding": "base64",
            "content": base64.b64encode(workflow).decode(),
        },
        f"{root}/actions/runs/101/artifacts?per_page=100&page=1": {
            "total_count": 1, "artifacts": [copy.deepcopy(native_artifact)]},
        f"{root}/actions/artifacts/104": copy.deepcopy(native_artifact),
    }
    return selection, workflow, RawReader(producer.scopes[0], pages), \
        BundleReader(download.scopes[0], download.bundle)


class NativeCandidateAdapterTests(unittest.TestCase):
    def qualify(self, change=None):
        selection, workflow, reader, bundle = fixture()
        if change:
            change(selection, reader, bundle)
        producer = adapter.NativeCandidateProducerAdapter(reader, selection, s5.NOW)
        downloader = adapter.NativeCandidateDownloadAdapter(bundle, 77, s5.NOW)
        return witness.qualify(selection, workflow, producer, downloader, s5.NOW)

    def test_exact_first_attempt_and_bytes_remain_source_only(self):
        result = self.qualify()
        self.assertEqual((result.run_id, result.run_attempt, result.artifact_id),
                         (101, 1, 104))
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch_effect)
        self.assertEqual(result.live_effects, 0)

    def test_missing_artifact_page_and_extra_artifact_refuse(self):
        key = f"repos/{witness.REPOSITORY}/actions/runs/101/artifacts?per_page=100&page=1"
        for change in (
            lambda s, r, b: r.pages.pop(key),
            lambda s, r, b: r.pages[key].update(total_count=2),
            lambda s, r, b: r.pages[key]["artifacts"].append(
                {"id": 999}),
        ):
            with self.subTest(change=change), self.assertRaises((adapter.Refused, witness.Refused)):
                self.qualify(change)

    def test_failed_rerun_tree_workflow_and_digest_refuse(self):
        root = f"repos/{witness.REPOSITORY}"
        run = f"{root}/actions/runs/101/attempts/1"
        commit = f"{root}/git/commits/{s5.SHA}"
        detail = f"{root}/actions/artifacts/104"
        content = f"{root}/contents/{adapter.workflow.WORKFLOW}?ref={s5.SHA}"
        for change in (
            lambda s, r, b: r.pages[run].update(conclusion="failure"),
            lambda s, r, b: r.pages[run].update(run_attempt=2),
            lambda s, r, b: r.pages[commit]["tree"].update(sha="f" * 40),
            lambda s, r, b: r.pages[content].update(content=base64.b64encode(
                b"foreign workflow").decode()),
            lambda s, r, b: r.pages[detail].update(digest="sha256:" + "f" * 64),
            lambda s, r, b: r.pages[detail]["workflow_run"].update(id=999),
            lambda s, r, b: setattr(b, "raw", b.raw + b"x"),
        ):
            with self.subTest(change=change), self.assertRaises((adapter.Refused, witness.Refused)):
                self.qualify(change)

    def test_expired_and_reused_credential_refuse(self):
        detail = f"repos/{witness.REPOSITORY}/actions/artifacts/104"
        listing = f"repos/{witness.REPOSITORY}/actions/runs/101/artifacts?per_page=100&page=1"
        for change in (
            lambda s, r, b: (r.pages[detail].update(expired=True),
                             r.pages[listing]["artifacts"][0].update(expired=True)),
            lambda s, r, b: b.value.update(credentialId=r.value["credentialId"]),
            lambda s, r, b: b.value.update(principalId=r.value["principalId"]),
        ):
            with self.subTest(change=change), self.assertRaises((adapter.Refused, witness.Refused)):
                self.qualify(change)


if __name__ == "__main__":
    unittest.main()
