"""Injected read-only review history plus a required separate audit event.

GitHub's review-history response identifies a reviewer and environment but its
published example does not supply an approval event ID or approval time. This
adapter refuses without a separate protected audit observation. No actual
credential, HTTP client, grant, journal or dispatch port is present.
"""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import re
from typing import Any, Protocol

import callable_isolated_v2_candidate_observers as observers
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_workflow_read_adapter as workflow

REPOSITORY = "FS-GG/.github"
ORGANIZATION = "FS-GG"
ENVIRONMENT = "callable-isolated-v2"
API = "https://api.github.com"
LOGIN = re.compile(r"[A-Za-z0-9-]+\Z")
MAX_RESPONSE = 1_000_000
AUDIT_SCHEMA = "fsgg.coordination.callable-isolated-v2-review-audit/1"


class Refused(ValueError):
    """Fixed refusal reason without review or credential contents."""


class ReviewReadTransport(Protocol):
    """Future protected Actions/Members read-only transport; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def get(self, path: str) -> workflow.Response: ...


class ProtectedReviewAudit(Protocol):
    """Future independent event-ID/time reader; none supplied."""

    def read_review_event(self, run_id: int, run_attempt: int,
                          environment_id: int, reviewer_id: int,
                          approvals_sha256: str) -> dict[str, Any]: ...


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _scope(transport: ReviewReadTransport, repository_id: int, now: dt.datetime):
    try:
        value = transport.scope()
    except Exception:
        raise Refused("review-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "repository",
                           "repositoryId", "organization", "permissions",
                           "expiresAt"}, "review-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != REPOSITORY
            or value["repositoryId"] != repository_id
            or type(value["repositoryId"]) is not int
            or value["organization"] != ORGANIZATION
            or value["permissions"] != {"actions": "read", "members": "read",
                                        "metadata": "read"}):
        raise Refused("review-scope-binding")
    try:
        if candidate._time(value["expiresAt"]) <= now:
            raise Refused("review-scope-expired")
    except candidate.Refused:
        raise Refused("review-scope-expired") from None
    return copy.deepcopy(value)


def _read(transport: ReviewReadTransport, path: str) -> tuple[object, bytes]:
    try:
        response = transport.get(path)
    except Exception:
        raise Refused("review-read-unavailable") from None
    if (type(response) is not workflow.Response or type(response.status) is not int
            or response.status != 200 or type(response.headers) is not tuple
            or type(response.body) is not bytes or len(response.body) > MAX_RESPONSE
            or any(type(pair) is not tuple or len(pair) != 2
                   or any(type(item) is not str for item in pair)
                   for pair in response.headers)
            or any(key.lower() in {"location", "link"}
                   for key, _ in response.headers)):
        raise Refused("review-response-invalid")
    try:
        value = json.loads(response.body.decode("utf-8"),
                           object_pairs_hook=workflow._unique,
                           parse_constant=workflow._nonfinite)
    except (UnicodeError, ValueError):
        raise Refused("review-json-invalid") from None
    return value, response.body


class ReviewReadAdapter:
    """Derive #587 review facts only with independent event ID and UTC time."""

    def __init__(self, transport: ReviewReadTransport, audit: ProtectedReviewAudit,
                 workflow_observation: dict[str, Any], repository_id: int,
                 organization_id: int, reviewer_id: int, reviewer_login: str,
                 environment_id: int, candidate_sha256: str,
                 expires_at: str, now: dt.datetime):
        if (transport is None or audit is None or transport is audit
                or not all(_positive(value) for value in
                           (repository_id, organization_id, reviewer_id,
                            environment_id))
                or type(reviewer_login) is not str
                or LOGIN.fullmatch(reviewer_login) is None
                or not candidate._hex(candidate_sha256, candidate.HEX64)
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("review-selection-invalid")
        try:
            expiry = candidate._time(expires_at)
        except candidate.Refused:
            raise Refused("review-selection-invalid") from None
        if not now < expiry <= now + dt.timedelta(minutes=30):
            raise Refused("review-selection-invalid")
        envelope = _exact(workflow_observation, {"schema", "role", "complete",
                "principalId", "credentialId", "recordId", "candidateSha256",
                "observedAt", "facts"}, "review-workflow-shape")
        facts = _exact(envelope["facts"], {"workflowRevision", "workflowPath",
                "workflowSha256", "runId", "runAttempt", "dispatchActorId"},
                "review-workflow-shape")
        if (envelope["schema"] != observers.SCHEMA or envelope["role"] != "workflow"
                or envelope["complete"] is not True
                or envelope["candidateSha256"] != candidate_sha256
                or envelope["recordId"] != facts["runId"]
                or not all(_positive(facts[key]) for key in
                           ("runId", "runAttempt", "dispatchActorId"))
                or reviewer_id == facts["dispatchActorId"]):
            raise Refused("review-workflow-binding")
        self.transport = transport
        self.audit = audit
        self.workflow_input = envelope
        self.workflow = copy.deepcopy(envelope)
        self.facts = self.workflow["facts"]
        self.repository_id = repository_id
        self.organization_id = organization_id
        self.reviewer_id = reviewer_id
        self.reviewer_login = reviewer_login
        self.environment_id = environment_id
        self.candidate_sha256 = candidate_sha256
        self.expires_at = expires_at
        self.now = now

    def observe_review(self) -> dict[str, Any]:
        if self.workflow_input != self.workflow:
            raise Refused("review-workflow-drift")
        scope_before = _scope(self.transport, self.repository_id, self.now)
        if (scope_before["principalId"] == self.workflow["principalId"]
                or scope_before["credentialId"] == self.workflow["credentialId"]):
            raise Refused("review-reader-custody")
        approvals_path = (f"repos/{REPOSITORY}/actions/runs/"
                          f"{self.facts['runId']}/approvals")
        approvals, approvals_raw = _read(self.transport, approvals_path)
        if type(approvals) is not list or len(approvals) != 1:
            raise Refused("review-approvals-incomplete")
        approval = approvals[0]
        if type(approval) is not dict:
            raise Refused("review-approval-shape")
        user = approval.get("user")
        environments = approval.get("environments")
        if (approval.get("state") != "approved"
                or type(user) is not dict
                or type(user.get("id")) is not int
                or user["id"] != self.reviewer_id
                or user.get("login") != self.reviewer_login
                or user.get("url") != f"{API}/users/{self.reviewer_login}"
                or type(environments) is not list or len(environments) != 1
                or type(environments[0]) is not dict
                or type(environments[0].get("id")) is not int
                or environments[0]["id"] != self.environment_id
                or environments[0].get("name") != ENVIRONMENT
                or environments[0].get("url") !=
                f"{API}/repos/{REPOSITORY}/environments/{ENVIRONMENT}"):
            raise Refused("review-approval-binding")
        membership_path = f"orgs/{ORGANIZATION}/memberships/{self.reviewer_login}"
        membership, _ = _read(self.transport, membership_path)
        if type(membership) is not dict:
            raise Refused("review-membership-shape")
        member_user = membership.get("user")
        organization = membership.get("organization")
        if (membership.get("state") != "active"
                or type(membership.get("role")) is not str
                or membership["role"] not in {"member", "admin"}
                or membership.get("url") !=
                f"{API}/orgs/{ORGANIZATION}/memberships/{self.reviewer_login}"
                or membership.get("organization_url") != f"{API}/orgs/{ORGANIZATION}"
                or type(member_user) is not dict
                or type(member_user.get("id")) is not int
                or member_user["id"] != self.reviewer_id
                or member_user.get("login") != self.reviewer_login
                or member_user.get("url") != f"{API}/users/{self.reviewer_login}"
                or type(organization) is not dict
                or type(organization.get("id")) is not int
                or organization["id"] != self.organization_id
                or organization.get("login") != ORGANIZATION
                or organization.get("url") != f"{API}/orgs/{ORGANIZATION}"):
            raise Refused("review-membership-binding")
        digest = hashlib.sha256(approvals_raw).hexdigest()
        try:
            event = self.audit.read_review_event(
                self.facts["runId"], self.facts["runAttempt"],
                self.environment_id, self.reviewer_id, digest)
        except Exception:
            raise Refused("review-audit-unavailable") from None
        event = _exact(event, {"schema", "complete", "source", "principalId",
                "credentialId", "eventId", "runId", "runAttempt",
                "environmentId", "reviewerId", "approvedAt",
                "approvalsSha256"}, "review-audit-shape")
        if (event["schema"] != AUDIT_SCHEMA or event["complete"] is not True
                or event["source"] != "protected-review-event"
                or type(event["principalId"]) is not str or not event["principalId"]
                or not candidate._hex(event["credentialId"], candidate.HEX64)
                or event["principalId"] in
                {scope_before["principalId"], self.workflow["principalId"]}
                or event["credentialId"] in
                {scope_before["credentialId"], self.workflow["credentialId"]}
                or not _positive(event["eventId"])
                or event["runId"] != self.facts["runId"]
                or event["runAttempt"] != self.facts["runAttempt"]
                or event["environmentId"] != self.environment_id
                or event["reviewerId"] != self.reviewer_id
                or event["approvalsSha256"] != digest):
            raise Refused("review-audit-binding")
        try:
            approved = candidate._time(event["approvedAt"])
            expires = candidate._time(self.expires_at)
        except candidate.Refused:
            raise Refused("review-audit-time") from None
        if (not self.now - dt.timedelta(minutes=30) <= approved <= self.now
                or not approved < expires
                or expires - approved > dt.timedelta(minutes=30)):
            raise Refused("review-audit-time")
        if self.workflow_input != self.workflow:
            raise Refused("review-workflow-drift")
        scope_after = _scope(self.transport, self.repository_id, self.now)
        if scope_after != scope_before:
            raise Refused("review-scope-drift")
        return {
            "envelope": {
                "schema": observers.SCHEMA, "role": "review", "complete": True,
                "principalId": scope_after["principalId"],
                "credentialId": scope_after["credentialId"],
                "recordId": event["eventId"],
                "candidateSha256": self.candidate_sha256,
                "observedAt": self.now.strftime("%Y-%m-%dT%H:%M:%SZ"),
                "facts": {"repository": REPOSITORY,
                          "runId": self.facts["runId"],
                          "runAttempt": self.facts["runAttempt"],
                          "environmentId": self.environment_id,
                          "dispatchActorId": self.facts["dispatchActorId"],
                          "reviewerId": self.reviewer_id,
                          "reviewEventId": event["eventId"],
                          "membership": "active", "purpose": "source-only",
                          "reviewedAt": event["approvedAt"],
                          "expiresAt": self.expires_at},
            },
            "blobs": {},
        }
