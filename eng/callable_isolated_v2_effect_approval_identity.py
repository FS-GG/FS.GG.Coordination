"""Join injected reviewer membership and immutable release approval bytes."""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import json
import re
import copy
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_effect_producer_artifact as producer
import callable_isolated_v2_effect_release_preflight as release
import verify_callable_isolated_v2_effect_producer_workflow as closed_workflow

IDENTITY_SCHEMA = "fsgg.coordination.callable-isolated-v2-effect-reviewer-identity/1"
EVENT_SCHEMA = "fsgg.coordination.callable-isolated-v2-effect-approval-event/2"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-effect-approval-witness/4"
LOGIN = re.compile(r"[A-Za-z0-9-]{1,39}\Z")


class Refused(ValueError):
    """Fixed refusal without review event or credential contents."""


class ReviewerIdentityPort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def read_reviewer(self, actor_id: int) -> dict[str, Any]: ...


class ApprovalEventPort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def read_approval_event(self, event_id: int) -> bytes: ...


@dataclasses.dataclass(frozen=True)
class ApprovalWitnessResult:
    coordination_revision: str
    source_tree: str
    producer_run_id: int
    producer_run_attempt: int
    artifact_id: int
    reviewer_actor_id: int
    approval_event_id: int
    approval_event_sha256: str
    manifest_sha256: str
    bundle_sha256: str
    source_record_id: int
    producer_actor_id: int
    repository_id: int
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
        raise Refused("approval-time") from None


def _scope(port: object, repository_id: int, permissions: list[str],
           now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("approval-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "repository",
                           "repositoryId", "permissions", "expiresAt"},
                   "approval-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != release.REPOSITORY
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != repository_id
            or type(value["permissions"]) is not list
            or value["permissions"] != permissions
            or _time(value["expiresAt"]) <= now):
        raise Refused("approval-scope-binding")
    return copy.deepcopy(value)


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise Refused("approval-event-duplicate")
        result[key] = value
    return result


def _no_constant(_value: str) -> None:
    raise Refused("approval-event-nonfinite")


def _event(raw: bytes, selected_digest: str) -> dict[str, Any]:
    if (type(raw) is not bytes or not 0 < len(raw) <= 16_384
            or hashlib.sha256(raw).hexdigest() != selected_digest):
        raise Refused("approval-event-digest")
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique,
                           parse_constant=_no_constant)
        canonical = (json.dumps(value, sort_keys=True, separators=(",", ":"),
                                ensure_ascii=True, allow_nan=False) + "\n").encode()
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("approval-event-json") from None
    if raw != canonical:
        raise Refused("approval-event-noncanonical")
    return _exact(value, {"schema", "immutable", "repository", "repositoryId",
        "coordinationRevision", "sourceTree", "producerRunId",
        "producerRunAttempt", "producerActorId", "artifactId",
        "bundleSha256", "workflowSha256", "workflowBlobOid",
        "manifestSha256", "reviewerActorId", "identityRecordId",
        "approvalEventId", "sourceRecordId", "decision", "approvedAt", "expiresAt"},
        "approval-event-shape")


def qualify(preflight: release.PreflightResult,
            produced: producer.ProducerWitnessResult,
            identity_port: ReviewerIdentityPort,
            event_port: ApprovalEventPort,
            selection: dict[str, Any], now: dt.datetime) -> ApprovalWitnessResult:
    """Compare two independent fake readers; never approve an operation."""
    selection = _exact(selection, {"repositoryId", "organizationId",
        "reviewerLogin", "identityRecordId", "approvalEventSha256"},
        "approval-selection-shape")
    if (type(preflight) is not release.PreflightResult
            or type(produced) is not producer.ProducerWitnessResult
            or preflight.schema != release.RESULT_SCHEMA
            or produced.schema != producer.RESULT_SCHEMA
            or preflight.authorized is not False
            or preflight.can_dispatch is not False
            or type(preflight.live_effects) is not int
            or preflight.live_effects != 0
            or produced.authorized is not False
            or produced.can_dispatch is not False
            or type(produced.live_effects) is not int
            or produced.live_effects != 0
            or identity_port is None or event_port is None
            or identity_port is event_port
            or not all(_positive(selection[key]) for key in
                       ("repositoryId", "organizationId", "identityRecordId"))
            or type(selection["reviewerLogin"]) is not str
            or LOGIN.fullmatch(selection["reviewerLogin"]) is None
            or not candidate._hex(selection["approvalEventSha256"],
                                  candidate.HEX64)
            or not _positive(preflight.reviewer_actor_id)
            or not _positive(preflight.approval_event_id)
            or not _positive(preflight.source_record_id)
            or any(not _positive(value) for value in
                   (preflight.producer_run_id,
                    preflight.producer_run_attempt,
                    preflight.producer_actor_id, preflight.artifact_id,
                    produced.producer_run_id,
                    produced.producer_run_attempt, produced.artifact_id,
                    produced.producer_actor_id, produced.source_record_id))
            or preflight.reviewer_actor_id == preflight.producer_actor_id
            or produced.coordination_revision != preflight.coordination_revision
            or produced.source_tree != preflight.source_tree
            or produced.producer_actor_id != preflight.producer_actor_id
            or produced.source_record_id != preflight.source_record_id
            or type(produced.repository_id) is not int
            or produced.repository_id != selection["repositoryId"]
            or (produced.producer_run_id, produced.producer_run_attempt,
                produced.artifact_id) !=
               (preflight.producer_run_id, preflight.producer_run_attempt,
                preflight.artifact_id)
            or produced.archive_sha256 != preflight.archive_sha256
            or not candidate._hex(preflight.manifest_sha256, candidate.HEX64)
            or not candidate._hex(produced.bundle_sha256, candidate.HEX64)
            or produced.workflow_sha256 !=
               closed_workflow.PINNED_WORKFLOW_SHA256
            or produced.workflow_blob_oid !=
               closed_workflow.PINNED_WORKFLOW_BLOB_OID
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)):
        raise Refused("approval-selection-invalid")
    identity_scope = _scope(identity_port, selection["repositoryId"],
                            ["members:read", "metadata:read"], now)
    event_scope = _scope(event_port, selection["repositoryId"],
                         ["read-release-approval"], now)
    if (identity_scope["principalId"] == event_scope["principalId"]
            or identity_scope["credentialId"] == event_scope["credentialId"]):
        raise Refused("approval-reader-custody")
    try:
        identity = identity_port.read_reviewer(preflight.reviewer_actor_id)
        raw = event_port.read_approval_event(preflight.approval_event_id)
    except Exception:
        raise Refused("approval-read-unavailable") from None
    identity = _exact(identity, {"schema", "complete", "principalId",
        "credentialId", "recordId", "repository", "repositoryId",
        "organization", "organizationId", "reviewerActorId", "login",
        "active", "role", "observedAt", "expiresAt"},
        "approval-identity-shape")
    if (identity["schema"] != IDENTITY_SCHEMA
            or identity["complete"] is not True
            or identity["principalId"] != identity_scope["principalId"]
            or identity["credentialId"] != identity_scope["credentialId"]
            or type(identity["recordId"]) is not int
            or identity["recordId"] != selection["identityRecordId"]
            or identity["repository"] != release.REPOSITORY
            or type(identity["repositoryId"]) is not int
            or identity["repositoryId"] != selection["repositoryId"]
            or identity["organization"] != "FS-GG"
            or type(identity["organizationId"]) is not int
            or identity["organizationId"] != selection["organizationId"]
            or type(identity["reviewerActorId"]) is not int
            or identity["reviewerActorId"] != preflight.reviewer_actor_id
            or identity["login"] != selection["reviewerLogin"]
            or identity["active"] is not True
            or identity["role"] != "release-reviewer"):
        raise Refused("approval-identity-binding")
    identity_observed = _time(identity["observedAt"])
    identity_expires = _time(identity["expiresAt"])
    artifact_created = _time(produced.artifact_created_at)
    if (not now - dt.timedelta(minutes=30) <= artifact_created <=
            identity_observed <= now < identity_expires):
        raise Refused("approval-identity-time")
    event = _event(raw, selection["approvalEventSha256"])
    expected = {"coordinationRevision": preflight.coordination_revision,
        "sourceTree": preflight.source_tree,
        "producerRunId": preflight.producer_run_id,
        "producerRunAttempt": preflight.producer_run_attempt,
        "producerActorId": preflight.producer_actor_id,
        "artifactId": preflight.artifact_id,
        "bundleSha256": produced.bundle_sha256,
        "workflowSha256": produced.workflow_sha256,
        "workflowBlobOid": produced.workflow_blob_oid,
        "manifestSha256": preflight.manifest_sha256,
        "reviewerActorId": preflight.reviewer_actor_id,
        "identityRecordId": selection["identityRecordId"],
        "approvalEventId": preflight.approval_event_id,
        "sourceRecordId": preflight.source_record_id,
        "repositoryId": selection["repositoryId"]}
    if (event["schema"] != EVENT_SCHEMA or event["immutable"] is not True
            or event["repository"] != release.REPOSITORY
            or event["decision"] != "approved"
            or any(type(event[key]) is not int for key in
                   ("repositoryId", "producerRunId", "producerRunAttempt",
                    "producerActorId", "artifactId", "reviewerActorId",
                    "identityRecordId", "approvalEventId"))
            or type(event["sourceRecordId"]) is not int
            or any(event[key] != value for key, value in expected.items())):
        raise Refused("approval-event-binding")
    approved = _time(event["approvedAt"])
    expires = _time(event["expiresAt"])
    if (not identity_observed <= approved <= now < expires
            or expires > approved + dt.timedelta(minutes=30)):
        raise Refused("approval-event-time")
    if (_scope(identity_port, selection["repositoryId"],
               ["members:read", "metadata:read"], now) != identity_scope
            or _scope(event_port, selection["repositoryId"],
                      ["read-release-approval"], now) != event_scope):
        raise Refused("approval-scope-drift")
    return ApprovalWitnessResult(preflight.coordination_revision,
        preflight.source_tree, preflight.producer_run_id,
        preflight.producer_run_attempt, preflight.artifact_id,
        preflight.reviewer_actor_id, preflight.approval_event_id,
        selection["approvalEventSha256"], preflight.manifest_sha256,
        produced.bundle_sha256,
        source_record_id=preflight.source_record_id,
        producer_actor_id=preflight.producer_actor_id,
        repository_id=selection["repositoryId"])
