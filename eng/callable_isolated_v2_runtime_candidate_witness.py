"""Bind a hosted source-only runtime candidate to exact immutable bytes."""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import io
import json
import re
import zipfile
from typing import Any, Protocol

import verify_callable_isolated_v2_runtime_candidate_workflow as workflow_check

REPOSITORY = "FS-GG/FS.GG.Coordination"
API = "https://api.github.com"
ARTIFACT_SCHEMA = "fsgg.gs2-09-9-v5-runtime-candidate/1"
RUN_SCHEMA = "fsgg.coordination.callable-isolated-v2-runtime-candidate-run/1"
RECORD_SCHEMA = "fsgg.coordination.callable-isolated-v2-runtime-candidate-artifact/1"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-runtime-candidate-witness/1"
ARCHIVE_NAME = "callable-isolated-v2-runtime.pyz"
MANIFEST_NAME = "callable-isolated-v2-runtime-manifest.json"
MEMBERS = (
    "__main__.py",
    "callable_isolated_v2_retained_operator.py",
    "callable_isolated_v2_runtime/__init__.py",
    "callable_isolated_v2_runtime/adapters.py",
    "callable_isolated_v2_runtime/contracts.py",
    "callable_isolated_v2_runtime/coordinator.py",
    "callable_isolated_v2_runtime/grant.py",
)
RUNTIME_REQUIREMENTS = {
    "pythonMin": "3.11", "sqlite": True, "opensslEd25519Pkeyutl": True,
}
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
MAX_BUNDLE = 2_000_000
MAX_ARCHIVE = 1_000_000


class Refused(ValueError):
    """Fixed refusal without returning source, artifact or credential bytes."""


class ProducerPort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def read_run(self, run_id: int, attempt: int) -> dict[str, Any]: ...
    def read_artifact(self, artifact_id: int) -> dict[str, Any]: ...


class DownloadPort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def download(self, artifact_id: int, url: str) -> bytes: ...


@dataclasses.dataclass(frozen=True)
class Selection:
    repository_id: int
    source_sha: str
    source_tree: str
    run_id: int
    run_attempt: int
    actor_id: int
    workflow_id: int
    artifact_id: int
    workflow_sha256: str


@dataclasses.dataclass(frozen=True)
class CandidateWitness:
    repository: str
    repository_id: int
    source_sha: str
    source_tree: str
    workflow_sha256: str
    run_id: int
    run_attempt: int
    artifact_id: int
    bundle_sha256: str
    archive_sha256: str
    manifest_sha256: str
    schema: str = RESULT_SCHEMA
    authorized: bool = False
    can_dispatch_effect: bool = False
    live_effects: int = 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _time(value: Any) -> dt.datetime:
    if type(value) is not str or not value.endswith("Z"):
        raise Refused("runtime-candidate-time")
    try:
        parsed = dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        raise Refused("runtime-candidate-time") from None
    if parsed.utcoffset() != dt.timedelta(0):
        raise Refused("runtime-candidate-time")
    return parsed


def _scope(port: object, repository_id: int, permissions: list[str],
           now: dt.datetime) -> dict[str, Any]:
    try:
        value = copy.deepcopy(port.scope())
    except Exception:
        raise Refused("runtime-candidate-scope-unavailable") from None
    _exact(value, {"principalId", "credentialId", "repository",
                   "repositoryId", "permissions", "expiresAt"},
           "runtime-candidate-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or type(value["credentialId"]) is not str
            or HEX64.fullmatch(value["credentialId"]) is None
            or value["repository"] != REPOSITORY
            or value["repositoryId"] != repository_id
            or value["permissions"] != permissions
            or _time(value["expiresAt"]) <= now):
        raise Refused("runtime-candidate-scope-binding")
    return value


def _read_bundle(raw: bytes) -> tuple[bytes, bytes]:
    if type(raw) is not bytes or not 0 < len(raw) <= MAX_BUNDLE:
        raise Refused("runtime-candidate-bundle-size")
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as bundle:
            infos = bundle.infolist()
            names = [item.filename for item in infos]
            if (len(names) != 2 or set(names) != {ARCHIVE_NAME, MANIFEST_NAME}
                    or any(item.is_dir() or item.flag_bits & 0x1 for item in infos)
                    or any(((item.external_attr >> 16) & 0o170000)
                           not in (0, 0o100000) for item in infos)):
                raise Refused("runtime-candidate-bundle-members")
            archive = bundle.read(ARCHIVE_NAME)
            manifest = bundle.read(MANIFEST_NAME)
    except Refused:
        raise
    except Exception:
        raise Refused("runtime-candidate-bundle-invalid") from None
    if not 0 < len(archive) <= MAX_ARCHIVE or not 0 < len(manifest) <= 128_000:
        raise Refused("runtime-candidate-bundle-member-size")
    return archive, manifest


def _verify_manifest(raw: bytes, archive: bytes, selection: Selection) -> str:
    try:
        value = json.loads(raw)
    except (UnicodeError, json.JSONDecodeError):
        raise Refused("runtime-candidate-manifest-json") from None
    keys = {"schema", "sourceRevision", "sourceTree", "builderSha256",
            "retainedOperatorSha256", "archiveSha256", "runtimeRequirements",
            "members"}
    _exact(value, keys, "runtime-candidate-manifest-shape")
    canonical = (json.dumps(value, sort_keys=True, separators=(",", ":"),
                            ensure_ascii=True, allow_nan=False) + "\n").encode("ascii")
    if canonical != raw:
        raise Refused("runtime-candidate-manifest-canonical")
    if (value["schema"] != ARTIFACT_SCHEMA
            or value["sourceRevision"] != selection.source_sha
            or value["sourceTree"] != selection.source_tree
            or type(value["builderSha256"]) is not str
            or HEX64.fullmatch(value["builderSha256"]) is None
            or type(value["retainedOperatorSha256"]) is not str
            or HEX64.fullmatch(value["retainedOperatorSha256"]) is None
            or value["archiveSha256"] != hashlib.sha256(archive).hexdigest()
            or value["runtimeRequirements"] != RUNTIME_REQUIREMENTS
            or type(value["members"]) is not list
            or [item.get("path") if type(item) is dict else None
                for item in value["members"]] != list(MEMBERS)):
        raise Refused("runtime-candidate-manifest-binding")
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as packaged:
            infos = packaged.infolist()
            if ([item.filename for item in infos] != list(MEMBERS)
                    or any(item.is_dir() or item.flag_bits & 0x1 for item in infos)
                    or any(((item.external_attr >> 16) & 0o170000)
                           not in (0, 0o100000) for item in infos)):
                raise Refused("runtime-candidate-archive-members")
            for info, member in zip(infos, value["members"], strict=True):
                _exact(member, {"path", "sha256", "size"},
                       "runtime-candidate-member-shape")
                content = packaged.read(info)
                if (member["path"] != info.filename
                        or type(member["sha256"]) is not str
                        or member["sha256"] != hashlib.sha256(content).hexdigest()
                        or type(member["size"]) is not int
                        or member["size"] != len(content)):
                    raise Refused("runtime-candidate-member-binding")
    except Refused:
        raise
    except Exception:
        raise Refused("runtime-candidate-archive-invalid") from None
    if value["retainedOperatorSha256"] != value["members"][1]["sha256"]:
        raise Refused("runtime-candidate-retained-operator")
    return hashlib.sha256(raw).hexdigest()


def qualify(selection: Selection, workflow_bytes: bytes,
            producer_port: ProducerPort, download_port: DownloadPort,
            now: dt.datetime) -> CandidateWitness:
    """Verify exact injected provider observations without granting effects."""
    if (type(selection) is not Selection
            or not _positive(selection.repository_id)
            or HEX40.fullmatch(selection.source_sha) is None
            or HEX40.fullmatch(selection.source_tree) is None
            or not all(_positive(value) for value in
                       (selection.run_id, selection.actor_id,
                        selection.workflow_id, selection.artifact_id))
            or selection.run_attempt != 1
            or type(now) is not dt.datetime or now.utcoffset() != dt.timedelta(0)
            or producer_port is None or download_port is None
            or producer_port is download_port):
        raise Refused("runtime-candidate-selection")
    try:
        checked_workflow = workflow_check.verify(workflow_bytes,
                                                 selection.workflow_sha256)
    except workflow_check.Refused:
        raise Refused("runtime-candidate-workflow") from None
    producer_scope = _scope(producer_port, selection.repository_id,
                            ["actions:read", "metadata:read"], now)
    download_scope = _scope(download_port, selection.repository_id,
                            ["actions:read"], now)
    if (producer_scope["principalId"] == download_scope["principalId"]
            or producer_scope["credentialId"] == download_scope["credentialId"]):
        raise Refused("runtime-candidate-reader-custody")
    try:
        run = copy.deepcopy(producer_port.read_run(selection.run_id,
                                                   selection.run_attempt))
        artifact = copy.deepcopy(producer_port.read_artifact(
            selection.artifact_id))
    except Exception:
        raise Refused("runtime-candidate-provider-unavailable") from None
    _exact(run, {"schema", "complete", "principalId", "credentialId",
        "repository", "repositoryId", "runId", "runAttempt", "headSha",
        "headTree", "event", "status", "conclusion", "actorId",
        "workflowId", "workflowPath", "workflowSha256", "artifactIds",
        "createdAt", "completedAt"}, "runtime-candidate-run-shape")
    _exact(artifact, {"schema", "complete", "principalId", "credentialId",
        "repository", "repositoryId", "artifactId", "name", "runId",
        "runAttempt", "headSha", "headTree", "workflowId", "expired",
        "size", "digest", "downloadUrl", "createdAt", "expiresAt"},
        "runtime-candidate-artifact-shape")
    common = {"repository": REPOSITORY, "repositoryId": selection.repository_id,
              "runId": selection.run_id, "runAttempt": 1,
              "headSha": selection.source_sha, "headTree": selection.source_tree,
              "workflowId": selection.workflow_id}
    for record, schema in ((run, RUN_SCHEMA), (artifact, RECORD_SCHEMA)):
        if (record["schema"] != schema or record["complete"] is not True
                or record["principalId"] != producer_scope["principalId"]
                or record["credentialId"] != producer_scope["credentialId"]
                or any(record[key] != expected for key, expected in common.items())):
            raise Refused("runtime-candidate-provider-binding")
    if (run["event"] != "workflow_dispatch" or run["status"] != "completed"
            or run["conclusion"] != "success"
            or run["actorId"] != selection.actor_id
            or run["workflowPath"] != workflow_check.WORKFLOW
            or run["workflowSha256"] != checked_workflow["workflowSha256"]
            or run["artifactIds"] != [selection.artifact_id]):
        raise Refused("runtime-candidate-run-binding")
    created, completed = _time(run["createdAt"]), _time(run["completedAt"])
    if not now - dt.timedelta(minutes=30) <= created <= completed <= now:
        raise Refused("runtime-candidate-run-time")
    name = f"callable-isolated-v2-runtime-candidate-{selection.source_sha}"
    url = (f"{API}/repos/{REPOSITORY}/actions/artifacts/"
           f"{selection.artifact_id}/zip")
    if (artifact["artifactId"] != selection.artifact_id
            or artifact["name"] != name or artifact["expired"] is not False
            or type(artifact["size"]) is not int
            or not 0 < artifact["size"] <= MAX_BUNDLE
            or type(artifact["digest"]) is not str
            or not artifact["digest"].startswith("sha256:")
            or HEX64.fullmatch(artifact["digest"][7:]) is None
            or artifact["downloadUrl"] != url):
        raise Refused("runtime-candidate-artifact-binding")
    artifact_created, expires = (_time(artifact["createdAt"]),
                                 _time(artifact["expiresAt"]))
    if not created <= artifact_created <= completed <= now < expires:
        raise Refused("runtime-candidate-artifact-time")
    try:
        bundle = download_port.download(selection.artifact_id, url)
    except Exception:
        raise Refused("runtime-candidate-download-unavailable") from None
    if (type(bundle) is not bytes or len(bundle) != artifact["size"]
            or hashlib.sha256(bundle).hexdigest() != artifact["digest"][7:]):
        raise Refused("runtime-candidate-download-digest")
    archive, manifest = _read_bundle(bundle)
    manifest_sha = _verify_manifest(manifest, archive, selection)
    if (_scope(producer_port, selection.repository_id,
               ["actions:read", "metadata:read"], now) != producer_scope
            or _scope(download_port, selection.repository_id,
                      ["actions:read"], now) != download_scope):
        raise Refused("runtime-candidate-scope-drift")
    return CandidateWitness(REPOSITORY, selection.repository_id,
        selection.source_sha, selection.source_tree,
        checked_workflow["workflowSha256"], selection.run_id, 1,
        selection.artifact_id, hashlib.sha256(bundle).hexdigest(),
        hashlib.sha256(archive).hexdigest(), manifest_sha)
