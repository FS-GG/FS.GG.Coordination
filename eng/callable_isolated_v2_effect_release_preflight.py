"""Join two injected release observations; protected identities remain absent."""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import copy
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate
import verify_callable_isolated_v2_effect_scaffold as bytes_check

SOURCE_SCHEMA = "fsgg.coordination.callable-isolated-v2-integrated-source/1"
APPROVAL_SCHEMA = "fsgg.coordination.callable-isolated-v2-manifest-approval/2"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-release-preflight/2"
REPOSITORY = "FS-GG/FS.GG.Coordination"


class Refused(ValueError):
    """Fixed refusal without source, approval or credential contents."""


class IntegratedSourcePort(Protocol):
    """Future authenticated source, producer artifact and byte reader."""

    def scope(self) -> dict[str, Any]: ...

    def read_integrated_source(self) -> dict[str, Any]: ...


class ApprovedManifestPort(Protocol):
    """Future independent immutable manifest approval event reader."""

    def scope(self) -> dict[str, Any]: ...

    def read_approval(self, event_id: int) -> dict[str, Any]: ...


@dataclasses.dataclass(frozen=True)
class PreflightResult:
    coordination_revision: str
    source_tree: str
    manifest_sha256: str
    artifact_id: int
    archive_sha256: str
    producer_run_id: int
    producer_run_attempt: int
    producer_actor_id: int
    reviewer_actor_id: int
    approval_event_id: int
    source_record_id: int
    schema: str = RESULT_SCHEMA
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _time(value: Any) -> dt.datetime:
    try:
        return candidate._time(value)
    except candidate.Refused:
        raise Refused("release-time") from None


def _scope(port: object, role: str, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("release-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "repository",
                           "permissions", "expiresAt"}, "release-scope-shape")
    permissions = (["actions:read", "contents:read"] if role == "source"
                   else ["read-release-approval"])
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != REPOSITORY
            or type(value["permissions"]) is not list
            or value["permissions"] != permissions
            or _time(value["expiresAt"]) <= now):
        raise Refused("release-scope-binding")
    return copy.deepcopy(value)


def qualify(source_port: IntegratedSourcePort,
            approval_port: ApprovedManifestPort,
            coordination_revision: str, source_tree: str,
            source_record_id: int,
            producer_run_id: int, producer_run_attempt: int,
            producer_actor_id: int, artifact_id: int,
            reviewer_actor_id: int, approval_event_id: int,
            now: dt.datetime) -> PreflightResult:
    """Require exact candidate and approval agreement; never release authority."""
    if (source_port is None or approval_port is None
            or source_port is approval_port
            or not candidate._hex(coordination_revision, candidate.HEX40)
            or not candidate._hex(source_tree, candidate.HEX40)
            or not _positive(source_record_id)
            or not all(_positive(value) for value in
                       (producer_run_id, producer_run_attempt,
                        producer_actor_id, artifact_id, reviewer_actor_id,
                        approval_event_id))
            or producer_actor_id == reviewer_actor_id
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)):
        raise Refused("release-selection-invalid")
    source_scope = _scope(source_port, "source", now)
    approval_scope = _scope(approval_port, "approval", now)
    if (source_scope["principalId"] == approval_scope["principalId"]
            or source_scope["credentialId"] == approval_scope["credentialId"]):
        raise Refused("release-reader-custody")
    try:
        source = source_port.read_integrated_source()
        source = copy.deepcopy(source)
        approval = approval_port.read_approval(approval_event_id)
    except Exception:
        raise Refused("release-read-unavailable") from None
    source = _exact(source, {"schema", "complete", "principalId",
                    "credentialId", "recordId", "repository",
                    "coordinationRevision",
                    "sourceTree", "producerRunId", "producerRunAttempt",
                    "producerActorId", "artifactId", "archiveSha256",
                    "manifestSha256", "observedAt", "blobs"},
                    "release-source-shape")
    approval = _exact(approval, {"schema", "complete", "principalId",
                    "credentialId", "eventId", "repository",
                    "coordinationRevision",
                    "sourceTree", "producerRunId", "producerRunAttempt",
                    "producerActorId", "artifactId", "reviewerActorId",
                    "sourceRecordId",
                    "manifestSha256", "reviewedAt", "expiresAt"},
                    "release-approval-shape")
    selected = {"coordinationRevision": coordination_revision,
                "sourceTree": source_tree,
                "producerRunId": producer_run_id,
                "producerRunAttempt": producer_run_attempt,
                "producerActorId": producer_actor_id,
                "artifactId": artifact_id}
    if (source["schema"] != SOURCE_SCHEMA or source["complete"] is not True
            or source["repository"] != REPOSITORY
            or source["principalId"] != source_scope["principalId"]
            or source["credentialId"] != source_scope["credentialId"]
            or type(source["recordId"]) is not int
            or source["recordId"] != source_record_id
            or any(type(source[key]) is not int for key in
                   ("producerRunId", "producerRunAttempt",
                    "producerActorId", "artifactId"))
            or any(source[key] != value for key, value in selected.items())
            or not candidate._hex(source["archiveSha256"], candidate.HEX64)
            or not candidate._hex(source["manifestSha256"], candidate.HEX64)):
        raise Refused("release-source-binding")
    if (approval["schema"] != APPROVAL_SCHEMA
            or approval["complete"] is not True
            or approval["repository"] != REPOSITORY
            or approval["principalId"] != approval_scope["principalId"]
            or approval["credentialId"] != approval_scope["credentialId"]
            or not _positive(approval["eventId"])
            or approval["eventId"] != approval_event_id
            or type(approval["sourceRecordId"]) is not int
            or approval["sourceRecordId"] != source_record_id
            or not _positive(approval["reviewerActorId"])
            or approval["reviewerActorId"] != reviewer_actor_id
            or approval["manifestSha256"] != source["manifestSha256"]
            or any(type(approval[key]) is not int for key in
                   ("producerRunId", "producerRunAttempt",
                    "producerActorId", "artifactId"))
            or any(approval[key] != value for key, value in selected.items())):
        raise Refused("release-approval-binding")
    observed = _time(source["observedAt"])
    reviewed = _time(approval["reviewedAt"])
    expires = _time(approval["expiresAt"])
    if (not now - dt.timedelta(minutes=30) <= observed <= reviewed <= now
            or not now < expires <= reviewed + dt.timedelta(minutes=30)):
        raise Refused("release-time-binding")
    blobs = _exact(source["blobs"], {"archive", "workflow", "manifest",
                                      "builderSource", "nativeSource"},
                   "release-blobs-shape")
    if (type(blobs["archive"]) is not bytes
            or hashlib.sha256(blobs["archive"]).hexdigest() !=
               source["archiveSha256"]
            or type(blobs["manifest"]) is not bytes
            or hashlib.sha256(blobs["manifest"]).hexdigest() !=
               source["manifestSha256"]):
        raise Refused("release-source-bytes")
    try:
        checked = bytes_check.verify(blobs["archive"], blobs["workflow"],
            blobs["manifest"], approval["manifestSha256"],
            blobs["builderSource"], blobs["nativeSource"])
    except Exception:
        raise Refused("release-byte-check") from None
    if checked["authorized"] is not False or checked["canDispatch"] is not False:
        raise Refused("release-authority-closed")
    if (_scope(source_port, "source", now) != source_scope
            or _scope(approval_port, "approval", now) != approval_scope):
        raise Refused("release-scope-drift")
    return PreflightResult(coordination_revision, source_tree,
                           source["manifestSha256"], artifact_id,
                           source["archiveSha256"], producer_run_id,
                           producer_run_attempt, producer_actor_id,
                           reviewer_actor_id, approval_event_id,
                           source_record_id=source_record_id)
