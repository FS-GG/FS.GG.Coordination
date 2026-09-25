"""Read-only observation composition for a prospective isolated v2 candidate.

The five observer ports have no protected implementations here. Synthetic
matching observations prove consistency only; no grant, token, journal,
provider or dispatch capability can result from this module.
"""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate

SCHEMA = "fsgg.coordination.callable-isolated-v2-candidate-observation/1"
ROLES = ("source-release", "workflow", "review", "target-scope", "operation-plan")
MAX_BLOB = 4_000_000


class Refused(ValueError):
    """Fixed refusal reason without protected data or exception text."""


class SourceReleaseObserver(Protocol):
    """Future protected reader of source revision, artifact bytes and runtime."""

    def observe_source_release(self) -> dict[str, Any]: ...


class WorkflowObserver(Protocol):
    """Future protected reader of workflow bytes and run actor."""

    def observe_workflow(self) -> dict[str, Any]: ...


class ReviewObserver(Protocol):
    """Future independent approval and active-membership reader."""

    def observe_review(self) -> dict[str, Any]: ...


class TargetScopeObserver(Protocol):
    """Future selected-repository and App-scope read-only reader."""

    def observe_target_scope(self) -> dict[str, Any]: ...


class OperationPlanObserver(Protocol):
    """Future independently sealed operation-intent reader."""

    def observe_operation_plan(self) -> dict[str, Any]: ...


@dataclasses.dataclass(frozen=True)
class ObservedCandidate:
    payload_sha256: str
    observations_sha256: str
    producer_run_id: int
    artifact_id: int
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _blob(value: Any, digest: Any) -> bool:
    return (type(value) is bytes and 0 < len(value) <= MAX_BLOB
            and candidate._hex(digest, candidate.HEX64)
            and hashlib.sha256(value).hexdigest() == digest)


def _observe(port: object, method: str, role: str, payload_sha256: str,
             now: dt.datetime) -> tuple[dict[str, Any], dict[str, Any]]:
    try:
        value = getattr(port, method)()
    except Exception:
        raise Refused("observer-unavailable") from None
    value = _exact(value, {"envelope", "blobs"}, "observer-result-shape")
    envelope = _exact(value["envelope"], {"schema", "role", "complete",
                                           "principalId", "credentialId", "recordId",
                                           "candidateSha256", "observedAt", "facts"},
                      "observer-envelope-shape")
    if (envelope["schema"] != SCHEMA or envelope["role"] != role
            or envelope["complete"] is not True
            or type(envelope["principalId"]) is not str
            or not envelope["principalId"]
            or not candidate._hex(envelope["credentialId"], candidate.HEX64)
            or not _positive(envelope["recordId"])
            or envelope["candidateSha256"] != payload_sha256):
        raise Refused("observer-envelope-binding")
    try:
        observed_at = candidate._time(envelope["observedAt"])
    except candidate.Refused:
        raise Refused("observer-time") from None
    if (type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)
            or not now - dt.timedelta(minutes=30) <= observed_at <= now):
        raise Refused("observer-time")
    if type(value["blobs"]) is not dict or type(envelope["facts"]) is not dict:
        raise Refused("observer-result-shape")
    try:
        snapshot = copy.deepcopy(value)
    except Exception:
        raise Refused("observer-snapshot-unavailable") from None
    return snapshot["envelope"], snapshot["blobs"]


def verify_observed_candidate(
        raw: bytes, payload_sha256: str,
        source: SourceReleaseObserver, workflow: WorkflowObserver,
        review: ReviewObserver, target: TargetScopeObserver,
        plan: OperationPlanObserver, now: dt.datetime) -> ObservedCandidate:
    """Compare five read-only observations with exact candidate bytes.

    A protected implementation must authenticate each observer's API,
    credential, source object and record identity. Distinct local objects and
    matching dictionaries do not establish independent custody.
    """
    ports = (source, workflow, review, target, plan)
    if any(port is None for port in ports) or len({id(port) for port in ports}) != 5:
        raise Refused("observer-port-custody")
    observations = tuple(_observe(port, method, role, payload_sha256, now)
                         for port, method, role in zip(ports, (
                             "observe_source_release", "observe_workflow",
                             "observe_review", "observe_target_scope",
                             "observe_operation_plan"), ROLES))
    envelopes = [item[0] for item in observations]
    if (len({item["principalId"] for item in envelopes}) != 5
            or len({item["credentialId"] for item in envelopes}) != 5):
        raise Refused("observer-principal-custody")

    source_facts = _exact(envelopes[0]["facts"], {"coordinationRevision",
                        "sourceTree", "operatorSha256", "controlsSha256",
                        "effectArchiveSha256", "runtime", "producerRunId",
                        "artifactId", "producerActorId"}, "observer-source-shape")
    source_blobs = _exact(observations[0][1], {"operator", "controls", "archive"},
                          "observer-source-blobs")
    if (not all(_positive(source_facts[key]) for key in
                ("producerRunId", "artifactId", "producerActorId"))
            or not _blob(source_blobs["operator"], source_facts["operatorSha256"])
            or not _blob(source_blobs["controls"], source_facts["controlsSha256"])
            or not _blob(source_blobs["archive"], source_facts["effectArchiveSha256"])):
        raise Refused("observer-source-bytes")

    workflow_facts = _exact(envelopes[1]["facts"], {"workflowRevision", "workflowPath",
                            "workflowSha256", "runId", "runAttempt", "dispatchActorId"},
                            "observer-workflow-shape")
    workflow_blobs = _exact(observations[1][1], {"workflow"}, "observer-workflow-blobs")
    if (not _blob(workflow_blobs["workflow"], workflow_facts["workflowSha256"])
            or any(not _positive(workflow_facts[key]) for key in
                   ("runId", "runAttempt", "dispatchActorId"))):
        raise Refused("observer-workflow-bytes")

    review_facts = _exact(envelopes[2]["facts"], {"repository", "runId", "runAttempt",
                          "environmentId", "dispatchActorId", "reviewerId",
                          "reviewEventId", "membership", "purpose", "reviewedAt",
                          "expiresAt"}, "observer-review-shape")
    if observations[2][1] != {}:
        raise Refused("observer-review-blobs")
    if (review_facts["runId"] != workflow_facts["runId"]
            or review_facts["runAttempt"] != workflow_facts["runAttempt"]
            or review_facts["dispatchActorId"] != workflow_facts["dispatchActorId"]
            or review_facts["reviewerId"] == source_facts["producerActorId"]
            or source_facts["producerRunId"] == workflow_facts["runId"]):
        raise Refused("observer-review-binding")
    try:
        if candidate._time(review_facts["reviewedAt"]) > candidate._time(
                envelopes[2]["observedAt"]):
            raise Refused("observer-review-time")
    except candidate.Refused:
        raise Refused("observer-review-time") from None

    target_facts = _exact(envelopes[3]["facts"], {"target", "credential"},
                          "observer-target-shape")
    credential = _exact(target_facts["credential"], {"kind", "installationId",
                        "repositoryIds", "permissions", "expiresAt"},
                        "observer-credential-shape")
    selected = target_facts["target"]
    if (observations[3][1] != {} or type(selected) is not dict
            or credential["kind"] != "github-app-installation"
            or credential["installationId"] != selected.get("installationId")
            or type(credential["repositoryIds"]) is not list
            or credential["repositoryIds"] != [selected.get("repositoryId")]
            or credential["permissions"] != {"metadata": "read", "contents": "read",
                                                 "pull_requests": "write"}):
        raise Refused("observer-credential-selection")
    try:
        if candidate._time(credential["expiresAt"]) < candidate._time(
                review_facts["expiresAt"]):
            raise Refused("observer-credential-expiry")
    except candidate.Refused:
        raise Refused("observer-credential-expiry") from None

    plan_facts = _exact(envelopes[4]["facts"], {"operation"}, "observer-plan-shape")
    if observations[4][1] != {}:
        raise Refused("observer-plan-blobs")
    expected = {
        "schema": candidate.SCHEMA, "state": "prepared-not-authorized",
        "source": {key: source_facts[key] for key in
                   ("coordinationRevision", "sourceTree", "operatorSha256",
                    "controlsSha256", "effectArchiveSha256", "producerRunId",
                    "artifactId", "producerActorId")}
                  | {key: workflow_facts[key] for key in
                     ("workflowRevision", "workflowPath", "workflowSha256")},
        "runtime": source_facts["runtime"], "review": review_facts,
        "target": selected, "operation": plan_facts["operation"],
    }
    try:
        checked = candidate.verify_candidate(raw, payload_sha256, expected, now)
        evidence = json.dumps(envelopes, sort_keys=True, separators=(",", ":"),
                              ensure_ascii=True, allow_nan=False).encode("ascii")
    except candidate.Refused as error:
        raise Refused(str(error)) from None
    except (TypeError, ValueError, UnicodeError):
        raise Refused("observer-evidence-invalid") from None
    if checked.authorized or checked.can_dispatch or checked.live_effects:
        raise Refused("observer-candidate-authority")
    return ObservedCandidate(checked.payload_sha256,
                             hashlib.sha256(evidence).hexdigest(),
                             source_facts["producerRunId"], source_facts["artifactId"])
