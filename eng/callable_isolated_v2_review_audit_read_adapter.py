"""Injected audit event plus independent joint seal for #591's review port."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import re
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_review_read_adapter as review
import callable_isolated_v2_workflow_read_adapter as workflow

ORGANIZATION = "FS-GG"
REPOSITORY = "FS-GG/.github"
AUDIT_ACTION = "workflows.approve_workflow_job"
JOINT_SCHEMA = "fsgg.coordination.callable-isolated-v2-review-joint-seal/1"
DOCUMENT_ID = re.compile(r"[A-Za-z0-9_-]{12,80}\Z")


class Refused(ValueError):
    """Fixed refusal without audit or credential contents."""


class ProtectedAuditLogPort(Protocol):
    """Future independent organization audit-log reader; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def read_event(self, document_id: str) -> bytes: ...


class ProtectedReviewJointSeal(Protocol):
    """Future immutable mapping for fields absent from GitHub's audit event."""

    def read_joint(self, record_id: int, audit_sha256: str,
                   approvals_sha256: str) -> dict[str, Any]: ...


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
        raise Refused("audit-time-invalid") from None


def _scope(port: ProtectedAuditLogPort, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("audit-reader-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "organization",
                           "permissions", "expiresAt"}, "audit-reader-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["organization"] != ORGANIZATION
            or value["permissions"] != ["audit-log:read"]
            or type(value["permissions"]) is not list
            or _time(value["expiresAt"]) <= now):
        raise Refused("audit-reader-binding")
    return copy.deepcopy(value)


class ReviewAuditReadAdapter:
    """Implement #591 audit Protocol without creating an audit transport."""

    def __init__(self, log: ProtectedAuditLogPort,
                 seal: ProtectedReviewJointSeal,
                 document_id: str, record_id: int,
                 organization_id: int, repository_id: int,
                 reviewer_login: str, now: dt.datetime):
        if (log is None or seal is None or log is seal
                or type(document_id) is not str
                or DOCUMENT_ID.fullmatch(document_id) is None
                or not all(_positive(value) for value in
                           (record_id, organization_id, repository_id))
                or type(reviewer_login) is not str
                or review.LOGIN.fullmatch(reviewer_login) is None
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("audit-selection-invalid")
        self.log = log
        self.seal = seal
        self.document_id = document_id
        self.record_id = record_id
        self.organization_id = organization_id
        self.repository_id = repository_id
        self.reviewer_login = reviewer_login
        self.now = now

    def read_review_event(self, run_id: int, run_attempt: int,
                          environment_id: int, reviewer_id: int,
                          approvals_sha256: str) -> dict[str, Any]:
        if (not all(_positive(value) for value in
                    (run_id, run_attempt, environment_id, reviewer_id))
                or not candidate._hex(approvals_sha256, candidate.HEX64)):
            raise Refused("audit-input-invalid")
        scope_before = _scope(self.log, self.now)
        try:
            raw = self.log.read_event(self.document_id)
        except Exception:
            raise Refused("audit-log-unavailable") from None
        if type(raw) is not bytes or not 0 < len(raw) <= 16_384:
            raise Refused("audit-log-bytes")
        try:
            value = json.loads(raw.decode("utf-8"),
                               object_pairs_hook=workflow._unique,
                               parse_constant=workflow._nonfinite)
        except (UnicodeError, ValueError):
            raise Refused("audit-log-json") from None
        if type(value) is not dict:
            raise Refused("audit-log-shape")
        created = value.get("created_at")
        timestamp = value.get("@timestamp")
        if (value.get("_document_id") != self.document_id
                or value.get("action") != AUDIT_ACTION
                or value.get("org") != ORGANIZATION
                or type(value.get("org_id")) is not int
                or value["org_id"] != self.organization_id
                or value.get("repo") != REPOSITORY
                or type(value.get("repo_id")) is not int
                or value["repo_id"] != self.repository_id
                or value.get("actor") != self.reviewer_login
                or type(value.get("actor_id")) is not int
                or value["actor_id"] != reviewer_id
                or type(value.get("workflow_run_id")) is not int
                or value["workflow_run_id"] != run_id
                or type(created) is not int or created <= 0
                or type(timestamp) is not int or timestamp != created):
            raise Refused("audit-log-binding")
        try:
            approved = dt.datetime.fromtimestamp(created / 1000,
                                                 tz=dt.timezone.utc)
        except (ValueError, OverflowError, OSError):
            raise Refused("audit-time-invalid") from None
        if not self.now - dt.timedelta(minutes=30) <= approved <= self.now:
            raise Refused("audit-time-invalid")
        scope_after = _scope(self.log, self.now)
        if scope_after != scope_before:
            raise Refused("audit-reader-drift")
        digest = hashlib.sha256(raw).hexdigest()
        try:
            joint = self.seal.read_joint(self.record_id, digest,
                                         approvals_sha256)
        except Exception:
            raise Refused("audit-joint-unavailable") from None
        joint = _exact(joint, {"schema", "complete", "principalId",
                    "credentialId", "recordId", "documentId", "auditSha256",
                    "approvalsSha256", "organizationId", "repositoryId",
                    "runId", "runAttempt", "environmentId", "reviewerId",
                    "createdAtMs", "sealedAt", "expiresAt"},
                    "audit-joint-shape")
        if (joint["schema"] != JOINT_SCHEMA or joint["complete"] is not True
                or type(joint["principalId"]) is not str
                or not joint["principalId"]
                or joint["principalId"] == scope_after["principalId"]
                or not candidate._hex(joint["credentialId"], candidate.HEX64)
                or joint["credentialId"] == scope_after["credentialId"]
                or not all(_positive(joint[key]) for key in
                           ("recordId", "organizationId", "repositoryId",
                            "runId", "runAttempt", "environmentId", "reviewerId"))
                or joint["recordId"] != self.record_id
                or joint["documentId"] != self.document_id
                or joint["auditSha256"] != digest
                or joint["approvalsSha256"] != approvals_sha256
                or joint["organizationId"] != self.organization_id
                or joint["repositoryId"] != self.repository_id
                or joint["runId"] != run_id
                or joint["runAttempt"] != run_attempt
                or joint["environmentId"] != environment_id
                or joint["reviewerId"] != reviewer_id
                or type(joint["createdAtMs"]) is not int
                or joint["createdAtMs"] != created):
            raise Refused("audit-joint-binding")
        sealed_at = _time(joint["sealedAt"])
        expires_at = _time(joint["expiresAt"])
        if (not approved <= sealed_at <= self.now < expires_at
                or expires_at - approved > dt.timedelta(minutes=30)):
            raise Refused("audit-joint-time")
        return {"schema": review.AUDIT_SCHEMA, "complete": True,
                "source": "protected-review-event",
                "principalId": scope_after["principalId"],
                "credentialId": scope_after["credentialId"],
                "eventId": self.record_id, "runId": run_id,
                "runAttempt": run_attempt, "environmentId": environment_id,
                "reviewerId": reviewer_id,
                "approvedAt": approved.strftime("%Y-%m-%dT%H:%M:%SZ"),
                "approvalsSha256": approvals_sha256}
