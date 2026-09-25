"""Injected protected plan reader with no store, token, journal or dispatch."""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_candidate_observers as observers
import callable_isolated_v2_effect_candidate as candidate

PLAN_SCHEMA = "fsgg.coordination.callable-isolated-v2-operation-plan/1"
SEAL_SCHEMA = "fsgg.coordination.callable-isolated-v2-plan-seal/1"
REQUEST_SCHEMA = "fsgg.coordination.callable-isolated-v2-verified-request/1"
TITLE = "V2-CALL-01.4b synthetic delivery v2"


class Refused(ValueError):
    """Fixed refusal without plan, provider or credential contents."""


@dataclasses.dataclass(frozen=True)
class VerifiedRequest:
    """Closed request bytes from the same canonical plan and seal read."""

    plan_record_id: int
    plan_sha256: str
    operation_id: str
    request_sha256: str
    canonical_request: bytes
    schema: str = dataclasses.field(init=False, default=REQUEST_SCHEMA)
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)


class PlanReadPort(Protocol):
    """Future protected immutable plan store; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def read(self, record_id: int) -> bytes: ...


class ProtectedPlanSeal(Protocol):
    """Future independent plan seal observation; none supplied."""

    def read_seal(self, record_id: int, plan_sha256: str) -> dict[str, Any]: ...


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
        raise Refused("plan-time") from None


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("plan-json-invalid") from None


def _envelope(value: Any, role: str, digest: str,
              now: dt.datetime) -> dict[str, Any]:
    value = _exact(value, {"schema", "role", "complete", "principalId",
             "credentialId", "recordId", "candidateSha256", "observedAt",
             "facts"}, "plan-observation-shape")
    if (value["schema"] != observers.SCHEMA or value["role"] != role
            or value["complete"] is not True
            or type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or not _positive(value["recordId"])
            or value["candidateSha256"] != digest
            or type(value["facts"]) is not dict):
        raise Refused("plan-observation-binding")
    observed = _time(value["observedAt"])
    if not now - dt.timedelta(minutes=30) <= observed <= now:
        raise Refused("plan-observation-time")
    return value


def _scope(port: PlanReadPort, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("plan-reader-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "store",
                           "permissions", "expiresAt"}, "plan-reader-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["store"] != "coordination-protected-plan"
            or value["permissions"] != ["read-plan"]
            or type(value["permissions"]) is not list
            or _time(value["expiresAt"]) <= now):
        raise Refused("plan-reader-binding")
    return copy.deepcopy(value)


class OperationPlanReadAdapter:
    """Read one canonical plan and separate seal; return #587 plan facts only."""

    def __init__(self, port: PlanReadPort, seal: ProtectedPlanSeal,
                 source: dict[str, Any], workflow: dict[str, Any],
                 review: dict[str, Any], target: dict[str, Any],
                 record_id: int, candidate_sha256: str, now: dt.datetime):
        if (port is None or seal is None or port is seal
                or not _positive(record_id)
                or not candidate._hex(candidate_sha256, candidate.HEX64)
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("plan-selection-invalid")
        selected = tuple(_envelope(item, role, candidate_sha256, now)
                         for item, role in zip((source, workflow, review, target),
                                               observers.ROLES[:4]))
        if (len({item["principalId"] for item in selected}) != 4
                or len({item["credentialId"] for item in selected}) != 4):
            raise Refused("plan-observer-custody")
        self.port = port
        self.seal = seal
        self.selected = copy.deepcopy(selected)
        self.record_id = record_id
        self.candidate_sha256 = candidate_sha256
        self.now = now

    def observe_operation_plan(self) -> dict[str, Any]:
        observation, _ = self.observe_with_verified_request()
        return observation

    def observe_with_verified_request(self) -> tuple[dict[str, Any], VerifiedRequest]:
        """Return the existing candidate observation and sealed request once."""
        source, workflow, review, target = self.selected
        source_facts = source["facts"]
        workflow_facts = workflow["facts"]
        review_facts = review["facts"]
        target_facts = target["facts"]
        if (not candidate._hex(source_facts.get("sourceTree"), candidate.HEX40)
                or not candidate._hex(workflow_facts.get("workflowRevision"),
                                      candidate.HEX40)
                or not all(_positive(workflow_facts.get(key)) for key in
                           ("runId", "runAttempt", "dispatchActorId"))
                or not all(_positive(review_facts.get(key)) for key in
                           ("runId", "runAttempt", "reviewEventId", "reviewerId"))
                or review_facts["runId"] != workflow_facts["runId"]
                or review_facts["runAttempt"] != workflow_facts["runAttempt"]
                or review_facts.get("dispatchActorId") !=
                   workflow_facts["dispatchActorId"]
                or review_facts["reviewerId"] == workflow_facts["dispatchActorId"]
                or type(target_facts.get("target")) is not dict):
            raise Refused("plan-selection-binding")
        selected_target = _exact(target_facts["target"],
            {"repository", "repositoryId", "nodeId", "installationId",
             "sourceRef", "sourceSha", "baseRef", "baseSha",
             "prestateSha256"}, "plan-target-shape")
        if (not _positive(selected_target["repositoryId"])
                or not _positive(selected_target["installationId"])
                or type(selected_target["nodeId"]) is not str
                or not selected_target["nodeId"]
                or type(selected_target.get("repository")) is not str
                or candidate.REPOSITORY.fullmatch(selected_target["repository"]) is None
                or selected_target["repository"] == "FS-GG/.github"
                or not candidate._branch(selected_target.get("sourceRef"))
                or not candidate._branch(selected_target.get("baseRef"))
                or selected_target["sourceRef"] == selected_target["baseRef"]
                or not candidate._hex(selected_target["sourceSha"], candidate.HEX40)
                or not candidate._hex(selected_target["baseSha"], candidate.HEX40)
                or not candidate._hex(selected_target["prestateSha256"],
                                      candidate.HEX64)):
            raise Refused("plan-target-binding")
        scope_before = _scope(self.port, self.now)
        if (scope_before["principalId"] in
                {item["principalId"] for item in self.selected}
                or scope_before["credentialId"] in
                {item["credentialId"] for item in self.selected}):
            raise Refused("plan-reader-custody")
        try:
            raw = self.port.read(self.record_id)
        except Exception:
            raise Refused("plan-read-unavailable") from None
        if type(raw) is not bytes or not 0 < len(raw) <= 16_384:
            raise Refused("plan-bytes-invalid")
        try:
            plan = json.loads(raw.decode("utf-8"),
                              object_pairs_hook=candidate._unique,
                              parse_constant=candidate._no_constant)
        except (UnicodeError, ValueError):
            raise Refused("plan-json-invalid") from None
        if raw != _canonical(plan):
            raise Refused("plan-noncanonical")
        plan = _exact(plan, {"schema", "state", "candidateSha256",
                    "sourceTree", "workflowRevision", "runId", "runAttempt",
                    "reviewEventId", "target", "operation", "request"},
                    "plan-shape")
        operation = _exact(plan["operation"], {"identity", "id", "method",
                    "path", "requestSha256", "maxProviderWrites"},
                    "plan-operation-shape")
        request = _exact(plan["request"], {"title", "head", "base", "body"},
                         "plan-request-shape")
        expected_core = {"operation_identity": candidate.IDENTITY,
                         "write_attempts": 1,
                         "repository_id": selected_target["repositoryId"],
                         "repository": selected_target["repository"],
                         "source_ref": selected_target["sourceRef"],
                         "source_sha": selected_target.get("sourceSha"),
                         "base_ref": selected_target["baseRef"],
                         "base_sha": selected_target.get("baseSha")}
        marker = hashlib.sha256(_canonical({"effect": "create-pull",
                                            "intent": expected_core})).hexdigest()
        expected_request = {"title": TITLE,
                            "head": selected_target["sourceRef"].removeprefix("refs/heads/"),
                            "base": selected_target["baseRef"].removeprefix("refs/heads/"),
                            "body": f"FS-GG-Effect: {marker}"}
        request_sha = hashlib.sha256(_canonical(request)).hexdigest()
        if (plan["schema"] != PLAN_SCHEMA
                or plan["state"] != "prepared-not-authorized"
                or plan["candidateSha256"] != self.candidate_sha256
                or plan["sourceTree"] != source_facts["sourceTree"]
                or plan["workflowRevision"] != workflow_facts["workflowRevision"]
                or plan["runId"] != workflow_facts["runId"]
                or plan["runAttempt"] != workflow_facts["runAttempt"]
                or plan["reviewEventId"] != review_facts["reviewEventId"]
                or plan["target"] != selected_target
                or request != expected_request
                or operation["identity"] != candidate.IDENTITY
                or not candidate._hex(operation["id"], candidate.HEX64)
                or operation["method"] != "POST"
                or operation["path"] !=
                   f"repos/{selected_target['repository']}/pulls"
                or operation["requestSha256"] != request_sha
                or type(operation["maxProviderWrites"]) is not int
                or operation["maxProviderWrites"] != 1):
            raise Refused("plan-intent-binding")
        scope_after = _scope(self.port, self.now)
        if scope_after != scope_before:
            raise Refused("plan-reader-drift")
        digest = hashlib.sha256(raw).hexdigest()
        try:
            attested = self.seal.read_seal(self.record_id, digest)
        except Exception:
            raise Refused("plan-seal-unavailable") from None
        attested = _exact(attested, {"schema", "complete", "principalId",
                    "credentialId", "recordId", "planSha256", "candidateSha256",
                    "sourceTree", "workflowRevision", "runId", "runAttempt",
                    "reviewEventId", "targetRepositoryId", "operationId",
                    "sealedAt", "expiresAt"}, "plan-seal-shape")
        if (attested["schema"] != SEAL_SCHEMA or attested["complete"] is not True
                or type(attested["principalId"]) is not str
                or not attested["principalId"]
                or attested["principalId"] in
                   {scope_after["principalId"]} |
                   {item["principalId"] for item in self.selected}
                or not candidate._hex(attested["credentialId"], candidate.HEX64)
                or attested["credentialId"] in
                   {scope_after["credentialId"]} |
                   {item["credentialId"] for item in self.selected}
                or attested["recordId"] != self.record_id
                or type(attested["recordId"]) is not int
                or attested["planSha256"] != digest
                or attested["candidateSha256"] != self.candidate_sha256
                or attested["sourceTree"] != source_facts["sourceTree"]
                or attested["workflowRevision"] != workflow_facts["workflowRevision"]
                or attested["runId"] != workflow_facts["runId"]
                or attested["runAttempt"] != workflow_facts["runAttempt"]
                or attested["reviewEventId"] != review_facts["reviewEventId"]
                or attested["targetRepositoryId"] != selected_target["repositoryId"]
                or attested["operationId"] != operation["id"]):
            raise Refused("plan-seal-binding")
        sealed_at = _time(attested["sealedAt"])
        expires = _time(attested["expiresAt"])
        reviewed = _time(review_facts.get("reviewedAt"))
        review_expires = _time(review_facts.get("expiresAt"))
        if (not reviewed <= sealed_at <= self.now < expires
                or expires > review_expires
                or expires - sealed_at > dt.timedelta(minutes=30)):
            raise Refused("plan-seal-time")
        observation = {"envelope": {
            "schema": observers.SCHEMA, "role": "operation-plan", "complete": True,
            "principalId": scope_after["principalId"],
            "credentialId": scope_after["credentialId"],
            "recordId": self.record_id,
            "candidateSha256": self.candidate_sha256,
            "observedAt": self.now.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "facts": {"operation": operation}}, "blobs": {}}
        verified = VerifiedRequest(self.record_id, digest, operation["id"],
                                   request_sha, _canonical(request))
        return observation, verified
