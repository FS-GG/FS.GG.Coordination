"""Join injected producer run, artifact record and bundle; no effect authority."""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import io
import copy
import zipfile
from typing import Any, Protocol

import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_effect_git_tree_witness as git_tree
import callable_isolated_v2_effect_producer_workflow_source as workflow_source_check
import callable_isolated_v2_effect_release_preflight as release

RUN_SCHEMA = "fsgg.coordination.callable-isolated-v2-producer-run/1"
ARTIFACT_SCHEMA = "fsgg.coordination.callable-isolated-v2-producer-artifact/1"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-producer-witness/1"
WORKFLOW = ".github/workflows/callable-isolated-v2-effect-release.yml"
API = "https://api.github.com"
MAX_BUNDLE = 2_000_000
SOURCE_PATHS = (set(builder.MEMBERS.values()) |
                {builder.WORKFLOW, builder.MANIFEST,
                 "eng/build_callable_isolated_v2_effect_scaffold.py",
                 builder.NATIVE_SOURCE})


class Refused(ValueError):
    """Fixed refusal without source, artifact or credential contents."""


class ProducerPort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def read_run(self, run_id: int, attempt: int) -> dict[str, Any]: ...
    def read_artifact(self, artifact_id: int) -> dict[str, Any]: ...


class ArtifactBundlePort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def download_bundle(self, artifact_id: int, url: str) -> bytes: ...


@dataclasses.dataclass(frozen=True)
class ProducerWitnessResult:
    coordination_revision: str
    source_tree: str
    producer_run_id: int
    producer_run_attempt: int
    artifact_id: int
    archive_sha256: str
    bundle_sha256: str
    workflow_sha256: str
    workflow_blob_oid: str
    artifact_created_at: str
    schema: str = RESULT_SCHEMA
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _time(value: Any) -> dt.datetime:
    try:
        return candidate._time(value)
    except candidate.Refused:
        raise Refused("producer-time") from None


def _scope(port: object, repository_id: int, permissions: list[str],
           now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("producer-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "repository",
                           "repositoryId", "permissions", "expiresAt"},
                   "producer-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != release.REPOSITORY
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != repository_id
            or type(value["permissions"]) is not list
            or value["permissions"] != permissions
            or _time(value["expiresAt"]) <= now):
        raise Refused("producer-scope-binding")
    return copy.deepcopy(value)


def qualify(preflight: release.PreflightResult,
            source_tree: git_tree.TreeWitnessResult,
            producer_port: ProducerPort, bundle_port: ArtifactBundlePort,
            selection: dict[str, Any], now: dt.datetime,
            workflow_source: workflow_source_check.WorkflowSourceResult | None = None
            ) -> ProducerWitnessResult:
    """Compare exact fake producer objects; never install or dispatch."""
    selection = _exact(selection, {"repositoryId", "identityEventId",
        "producerRunId", "producerRunAttempt", "producerActorId",
        "workflowId", "workflowPath", "workflowSha256", "artifactId"},
        "producer-selection-shape")
    if (type(preflight) is not release.PreflightResult
            or type(source_tree) is not git_tree.TreeWitnessResult
            or type(workflow_source) is not workflow_source_check.WorkflowSourceResult
            or preflight.schema != release.RESULT_SCHEMA
            or source_tree.schema != git_tree.RESULT_SCHEMA
            or workflow_source.schema != workflow_source_check.RESULT_SCHEMA
            or preflight.authorized is not False
            or preflight.can_dispatch is not False
            or type(preflight.live_effects) is not int
            or preflight.live_effects != 0
            or source_tree.authorized is not False
            or source_tree.can_dispatch is not False
            or type(source_tree.live_effects) is not int
            or source_tree.live_effects != 0
            or workflow_source.authorized is not False
            or workflow_source.can_dispatch is not False
            or type(workflow_source.live_effects) is not int
            or workflow_source.live_effects != 0
            or producer_port is None or bundle_port is None
            or producer_port is bundle_port
            or not all(_positive(selection[key]) for key in
                       ("repositoryId", "identityEventId", "producerRunId",
                        "producerRunAttempt", "producerActorId", "workflowId",
                        "artifactId"))
            or selection["workflowPath"] != WORKFLOW
            or not candidate._hex(selection["workflowSha256"], candidate.HEX64)
            or not candidate._hex(preflight.coordination_revision, candidate.HEX40)
            or not candidate._hex(preflight.source_tree, candidate.HEX40)
            or not candidate._hex(preflight.archive_sha256, candidate.HEX64)
            or not _positive(preflight.reviewer_actor_id)
            or not _positive(preflight.approval_event_id)
            or preflight.reviewer_actor_id == selection["producerActorId"]
            or any(not _positive(value) for value in
                   (preflight.producer_run_id, preflight.producer_run_attempt,
                    preflight.producer_actor_id, preflight.artifact_id))
            or (preflight.producer_run_id, preflight.producer_run_attempt,
                preflight.producer_actor_id, preflight.artifact_id) !=
               (selection["producerRunId"], selection["producerRunAttempt"],
                selection["producerActorId"], selection["artifactId"])
            or source_tree.coordination_revision != preflight.coordination_revision
            or source_tree.source_tree != preflight.source_tree
            or source_tree.archive_sha256 != preflight.archive_sha256
            or type(source_tree.source_files) is not tuple
            or len(source_tree.source_files) != len(SOURCE_PATHS)
            or any(type(item) is not tuple or len(item) != 2
                   or type(item[0]) is not str
                   or not candidate._hex(item[1], candidate.HEX64)
                   for item in source_tree.source_files)
            or {item[0] for item in source_tree.source_files} != SOURCE_PATHS
            or tuple(sorted(source_tree.source_files)) != source_tree.source_files
            or type(source_tree.identity_event_id) is not int
            or source_tree.identity_event_id != selection["identityEventId"]
            or workflow_source.coordination_revision !=
               preflight.coordination_revision
            or workflow_source.source_tree != preflight.source_tree
            or type(workflow_source.repository_id) is not int
            or workflow_source.repository_id != selection["repositoryId"]
            or type(workflow_source.identity_event_id) is not int
            or workflow_source.identity_event_id != selection["identityEventId"]
            or workflow_source.workflow_path != WORKFLOW
            or workflow_source.workflow_sha256 != selection["workflowSha256"]
            or not candidate._hex(workflow_source.workflow_blob_oid,
                                  candidate.HEX40)
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)):
        raise Refused("producer-selection-invalid")
    producer_scope = _scope(producer_port, selection["repositoryId"],
                            ["actions:read", "metadata:read"], now)
    bundle_scope = _scope(bundle_port, selection["repositoryId"],
                          ["actions:read"], now)
    if (producer_scope["principalId"] == bundle_scope["principalId"]
            or producer_scope["credentialId"] == bundle_scope["credentialId"]):
        raise Refused("producer-reader-custody")
    try:
        run = producer_port.read_run(selection["producerRunId"],
                                     selection["producerRunAttempt"])
        artifact = producer_port.read_artifact(selection["artifactId"])
    except Exception:
        raise Refused("producer-read-unavailable") from None
    run = _exact(run, {"schema", "complete", "principalId", "credentialId",
        "repository", "repositoryId", "runId", "runAttempt", "headSha",
        "headTree", "headBranch", "event", "status", "conclusion",
        "actorId", "workflowId", "workflowPath", "workflowSha256",
        "artifactIds", "createdAt", "completedAt"}, "producer-run-shape")
    artifact = _exact(artifact, {"schema", "complete", "principalId",
        "credentialId", "repository", "repositoryId", "artifactId", "name",
        "runId", "runAttempt", "headSha", "headTree", "workflowId",
        "expired", "size", "digest", "downloadUrl", "createdAt",
        "expiresAt"}, "producer-artifact-shape")
    for record, schema in ((run, RUN_SCHEMA), (artifact, ARTIFACT_SCHEMA)):
        if (record["schema"] != schema or record["complete"] is not True
                or record["principalId"] != producer_scope["principalId"]
                or record["credentialId"] != producer_scope["credentialId"]
                or record["repository"] != release.REPOSITORY
                or type(record["repositoryId"]) is not int
                or record["repositoryId"] != selection["repositoryId"]
                or type(record["runId"]) is not int
                or record["runId"] != selection["producerRunId"]
                or type(record["runAttempt"]) is not int
                or record["runAttempt"] != selection["producerRunAttempt"]
                or record["headSha"] != preflight.coordination_revision
                or record["headTree"] != preflight.source_tree
                or type(record["workflowId"]) is not int
                or record["workflowId"] != selection["workflowId"]):
            raise Refused("producer-object-binding")
    if (type(run["actorId"]) is not int
            or run["actorId"] != selection["producerActorId"]
            or run["headBranch"] != "main"
            or run["event"] != "workflow_dispatch"
            or run["status"] != "completed"
            or run["conclusion"] != "success"
            or run["workflowPath"] != WORKFLOW
            or run["workflowSha256"] != selection["workflowSha256"]
            or type(run["artifactIds"]) is not list
            or run["artifactIds"] != [selection["artifactId"]]):
        raise Refused("producer-run-binding")
    created = _time(run["createdAt"])
    completed = _time(run["completedAt"])
    if not now - dt.timedelta(minutes=30) <= created <= completed <= now:
        raise Refused("producer-run-time")
    download_url = (f"{API}/repos/{release.REPOSITORY}/actions/artifacts/"
                    f"{selection['artifactId']}/zip")
    if (type(artifact["artifactId"]) is not int
            or artifact["artifactId"] != selection["artifactId"]
            or artifact["name"] != builder.ARCHIVE_NAME
            or artifact["expired"] is not False
            or type(artifact["size"]) is not int
            or not 0 < artifact["size"] <= MAX_BUNDLE
            or type(artifact["digest"]) is not str
            or not artifact["digest"].startswith("sha256:")
            or not candidate._hex(artifact["digest"][7:], candidate.HEX64)
            or artifact["downloadUrl"] != download_url):
        raise Refused("producer-artifact-binding")
    artifact_created = _time(artifact["createdAt"])
    expires = _time(artifact["expiresAt"])
    if not completed <= artifact_created <= now < expires:
        raise Refused("producer-artifact-time")
    try:
        bundle = bundle_port.download_bundle(selection["artifactId"],
                                             download_url)
    except Exception:
        raise Refused("producer-bundle-unavailable") from None
    if (type(bundle) is not bytes or len(bundle) != artifact["size"]
            or hashlib.sha256(bundle).hexdigest() != artifact["digest"][7:]):
        raise Refused("producer-bundle-digest")
    try:
        with zipfile.ZipFile(io.BytesIO(bundle)) as zipped:
            infos = zipped.infolist()
            if (len(infos) != 1 or infos[0].filename != builder.ARCHIVE_NAME
                    or infos[0].is_dir()
                    or ((infos[0].external_attr >> 16) & 0o170000)
                       not in (0, 0o100000)
                    or not 0 < infos[0].file_size <= 256_000):
                raise Refused("producer-bundle-member")
            archive = zipped.read(infos[0])
    except Refused:
        raise
    except Exception:
        raise Refused("producer-bundle-invalid") from None
    if hashlib.sha256(archive).hexdigest() != preflight.archive_sha256:
        raise Refused("producer-archive-binding")
    if (_scope(producer_port, selection["repositoryId"],
               ["actions:read", "metadata:read"], now) != producer_scope
            or _scope(bundle_port, selection["repositoryId"],
                      ["actions:read"], now) != bundle_scope):
        raise Refused("producer-scope-drift")
    return ProducerWitnessResult(preflight.coordination_revision,
        preflight.source_tree, selection["producerRunId"],
        selection["producerRunAttempt"], selection["artifactId"],
        preflight.archive_sha256, hashlib.sha256(bundle).hexdigest(),
        selection["workflowSha256"], workflow_source.workflow_blob_oid,
        artifact["createdAt"])
