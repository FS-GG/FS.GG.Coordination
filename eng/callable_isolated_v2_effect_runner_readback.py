"""Join injected installed-refusal and audit observations without authority."""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import json
import copy
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_effect_approval_identity as approval
import callable_isolated_v2_effect_release_preflight as release

PROBE_SCHEMA = "fsgg.coordination.callable-isolated-v2-installed-refusal/2"
AUDIT_SCHEMA = "fsgg.coordination.callable-isolated-v2-no-effect-audit/2"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-runner-readback/6"
REFUSAL_SCHEMA = "fsgg.coordination.callable-isolated-v2-effect-scaffold-refusal/1"
IDENTITY = {"runId", "runAttempt", "coordinationRevision", "sourceTree",
            "artifactId", "manifestSha256", "archiveSha256", "installPath",
            "repositoryId", "identityEventId", "sourceRecordId",
            "approvalEventId"}
SELECTION = {"runId", "runAttempt", "runnerActorId", "auditActorId",
             "auditEventId", "repositoryId", "identityEventId",
             "approvalEventId",
             "imageDigest", "attestationDigest", "interpreterSha256",
             "runtimeClosureSha256", "installPath"}
COUNTS = {"tokenReads", "journalReads", "journalWrites", "casWrites",
          "providerPosts", "cleanupAttempts"}
OBJECT = {"device", "inode", "size", "sha256"}


class Refused(ValueError):
    """Fixed refusal without observation or credential contents."""


class RunnerPort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def read_probe(self, run_id: int, attempt: int) -> dict[str, Any]: ...


class AuditPort(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def read_audit(self, event_id: int) -> dict[str, Any]: ...


@dataclasses.dataclass(frozen=True)
class ReadbackResult:
    coordination_revision: str
    artifact_id: int
    archive_sha256: str
    run_id: int
    audit_event_id: int
    audit_actor_id: int
    approval_event_id: int
    source_record_id: int
    repository_id: int
    identity_event_id: int
    approved_at: str
    expires_at: str
    command_started_at: str
    command_completed_at: str
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
        raise Refused("readback-time") from None


def _scope(port: object, permission: str, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("readback-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "repository",
                           "permissions", "expiresAt"}, "readback-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != release.REPOSITORY
            or type(value["permissions"]) is not list
            or value["permissions"] != [permission]
            or _time(value["expiresAt"]) <= now):
        raise Refused("readback-scope-binding")
    return copy.deepcopy(value)


def _path(value: Any) -> bool:
    return (type(value) is str and value.startswith("/")
            and not value.startswith("//") and "\x00" not in value
            and all(part not in ("", ".", "..") for part in value[1:].split("/"))
            and value.endswith(".pyz"))


def _object(value: Any, archive_sha256: str, archive_size: int) -> dict[str, Any]:
    value = _exact(value, OBJECT, "readback-object-shape")
    if (not _positive(value["device"]) or not _positive(value["inode"])
            or type(value["size"]) is not int or value["size"] != archive_size
            or value["sha256"] != archive_sha256):
        raise Refused("readback-object-binding")
    return value


def qualify(preflight: release.PreflightResult, runner_port: RunnerPort,
            audit_port: AuditPort, selection: dict[str, Any],
            now: dt.datetime,
            approval_witness: approval.ApprovalWitnessResult | None = None
            ) -> ReadbackResult:
    """Compare exact fake observations; never authenticate or dispatch."""
    selection = dict(_exact(selection, SELECTION, "readback-selection-shape"))
    if (type(preflight) is not release.PreflightResult
            or type(approval_witness) is not approval.ApprovalWitnessResult
            or preflight.schema != release.RESULT_SCHEMA
            or approval_witness.schema != approval.RESULT_SCHEMA
            or preflight.authorized is not False
            or preflight.can_dispatch is not False
            or type(preflight.live_effects) is not int
            or preflight.live_effects != 0
            or approval_witness.authorized is not False
            or approval_witness.can_dispatch is not False
            or type(approval_witness.live_effects) is not int
            or approval_witness.live_effects != 0
            or approval_witness.coordination_revision != preflight.coordination_revision
            or approval_witness.source_tree != preflight.source_tree
            or approval_witness.artifact_id != preflight.artifact_id
            or approval_witness.manifest_sha256 != preflight.manifest_sha256
            or approval_witness.source_record_id != preflight.source_record_id
            or approval_witness.producer_run_id != preflight.producer_run_id
            or approval_witness.producer_run_attempt != preflight.producer_run_attempt
            or approval_witness.producer_actor_id != preflight.producer_actor_id
            or approval_witness.reviewer_actor_id != preflight.reviewer_actor_id
            or approval_witness.approval_event_id != preflight.approval_event_id
            or not candidate._hex(approval_witness.approval_event_sha256,
                                  candidate.HEX64)
            or not candidate._hex(approval_witness.bundle_sha256,
                                  candidate.HEX64)
            or not candidate._hex(preflight.coordination_revision, candidate.HEX40)
            or not candidate._hex(preflight.source_tree, candidate.HEX40)
            or not candidate._hex(preflight.manifest_sha256, candidate.HEX64)
            or not candidate._hex(preflight.archive_sha256, candidate.HEX64)
            or not _positive(preflight.artifact_id)
            or not all(_positive(value) for value in
                       (preflight.source_record_id, approval_witness.repository_id,
                        approval_witness.identity_event_id))
            or runner_port is None or audit_port is None
            or runner_port is audit_port
            or not all(_positive(selection[key]) for key in
                       ("runId", "runAttempt", "runnerActorId",
                        "auditActorId", "auditEventId", "repositoryId",
                        "identityEventId", "approvalEventId"))
            or selection["repositoryId"] != approval_witness.repository_id
            or selection["identityEventId"] != approval_witness.identity_event_id
            or selection["approvalEventId"] != approval_witness.approval_event_id
            or selection["runnerActorId"] == approval_witness.reviewer_actor_id
            or selection["auditActorId"] == selection["runnerActorId"]
            or not all(candidate._hex(selection[key], candidate.HEX64) for key in
                       ("imageDigest", "attestationDigest", "interpreterSha256",
                        "runtimeClosureSha256"))
            or not _path(selection["installPath"])
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)):
        raise Refused("readback-selection-invalid")
    approved_at = _time(approval_witness.approved_at)
    expires_at = _time(approval_witness.expires_at)
    if (not now - dt.timedelta(minutes=30) <= approved_at <= now < expires_at
            or expires_at > approved_at + dt.timedelta(minutes=30)):
        raise Refused("readback-approval-time")
    runner_scope = _scope(runner_port, "read-installed-probe", now)
    audit_scope = _scope(audit_port, "read-effect-audit", now)
    if (runner_scope["principalId"] == audit_scope["principalId"]
            or runner_scope["credentialId"] == audit_scope["credentialId"]):
        raise Refused("readback-reader-custody")
    try:
        probe = runner_port.read_probe(selection["runId"], selection["runAttempt"])
        probe = copy.deepcopy(probe)
        audit = audit_port.read_audit(selection["auditEventId"])
    except Exception:
        raise Refused("readback-unavailable") from None
    probe = _exact(probe, IDENTITY | {"schema", "complete", "principalId",
        "credentialId", "repository", "runnerActorId", "imageDigest",
        "attestationDigest", "interpreterSha256", "runtimeClosureSha256",
        "realPath", "symlink", "before", "after", "archiveBytes", "argv",
        "exitCode", "stdout", "stderr", "commandStartedAt",
        "commandCompletedAt", "observedAt"}, "readback-probe-shape")
    audit = _exact(audit, IDENTITY | {"schema", "complete", "principalId",
        "credentialId", "repository", "eventId", "auditActorId",
        "runnerActorId", "object", "counts", "commandStartedAt",
        "commandCompletedAt", "observedAt"},
        "readback-audit-shape")
    shared = {"runId": selection["runId"],
              "runAttempt": selection["runAttempt"],
              "coordinationRevision": preflight.coordination_revision,
              "sourceTree": preflight.source_tree,
              "artifactId": preflight.artifact_id,
              "manifestSha256": preflight.manifest_sha256,
              "archiveSha256": preflight.archive_sha256,
              "installPath": selection["installPath"],
              "repositoryId": selection["repositoryId"],
              "identityEventId": selection["identityEventId"],
              "sourceRecordId": preflight.source_record_id,
              "approvalEventId": selection["approvalEventId"]}
    for record, scope, schema in ((probe, runner_scope, PROBE_SCHEMA),
                                  (audit, audit_scope, AUDIT_SCHEMA)):
        if (record["schema"] != schema or record["complete"] is not True
                or record["principalId"] != scope["principalId"]
                or record["credentialId"] != scope["credentialId"]
                or record["repository"] != release.REPOSITORY
                or any(type(record[key]) is not int for key in
                       ("runId", "runAttempt", "artifactId", "repositoryId",
                        "identityEventId", "sourceRecordId", "approvalEventId"))
                or any(record[key] != value for key, value in shared.items())):
            raise Refused("readback-identity-binding")
    if (type(probe["runnerActorId"]) is not int
            or probe["runnerActorId"] != selection["runnerActorId"]
            or any(probe[key] != selection[key] for key in
                   ("imageDigest", "attestationDigest", "interpreterSha256",
                    "runtimeClosureSha256"))
            or probe["realPath"] != selection["installPath"]
            or probe["symlink"] is not False
            or type(probe["archiveBytes"]) is not bytes
            or hashlib.sha256(probe["archiveBytes"]).hexdigest() !=
               preflight.archive_sha256):
        raise Refused("readback-probe-binding")
    before = _object(probe["before"], preflight.archive_sha256,
                     len(probe["archiveBytes"]))
    after = _object(probe["after"], preflight.archive_sha256,
                    len(probe["archiveBytes"]))
    if before != after:
        raise Refused("readback-object-drift")
    refusal = {"schema": REFUSAL_SCHEMA, "reason": "no-grant",
               "authorized": False, "canDispatch": False, "liveEffects": 0}
    stdout = (json.dumps(refusal, sort_keys=True,
                         separators=(",", ":")) + "\n").encode()
    if (probe["argv"] != ["python3", "-I", "-S",
                          selection["installPath"], "execute-native-pull"]
            or type(probe["exitCode"]) is not int or probe["exitCode"] != 78
            or type(probe["stdout"]) is not bytes or probe["stdout"] != stdout
            or type(probe["stderr"]) is not bytes or probe["stderr"] != b""):
        raise Refused("readback-refusal-binding")
    if (type(audit["eventId"]) is not int
            or audit["eventId"] != selection["auditEventId"]
            or type(audit["auditActorId"]) is not int
            or audit["auditActorId"] != selection["auditActorId"]
            or type(audit["runnerActorId"]) is not int
            or audit["runnerActorId"] != selection["runnerActorId"]
            or audit["auditActorId"] == selection["runnerActorId"]
            or _object(audit["object"], preflight.archive_sha256,
                       len(probe["archiveBytes"])) != before):
        raise Refused("readback-audit-binding")
    counts = _exact(audit["counts"], COUNTS, "readback-counts-shape")
    if any(type(value) is not int or value != 0 for value in counts.values()):
        raise Refused("readback-effect-access")
    if (audit["commandStartedAt"] != probe["commandStartedAt"]
            or audit["commandCompletedAt"] != probe["commandCompletedAt"]):
        raise Refused("readback-command-interval-binding")
    started = _time(probe["commandStartedAt"])
    completed = _time(probe["commandCompletedAt"])
    observed = _time(probe["observedAt"])
    audited = _time(audit["observedAt"])
    if not (now - dt.timedelta(minutes=15) <= approved_at <= started
            <= completed <= observed <= audited <= now < expires_at):
        raise Refused("readback-time-binding")
    if (_scope(runner_port, "read-installed-probe", now) != runner_scope
            or _scope(audit_port, "read-effect-audit", now) != audit_scope):
        raise Refused("readback-scope-drift")
    return ReadbackResult(preflight.coordination_revision,
                          preflight.artifact_id, preflight.archive_sha256,
                          selection["runId"], selection["auditEventId"],
                          selection["auditActorId"], preflight.approval_event_id,
                          preflight.source_record_id, approval_witness.repository_id,
                          approval_witness.identity_event_id,
                          approval_witness.approved_at,
                          approval_witness.expires_at,
                          probe["commandStartedAt"],
                          probe["commandCompletedAt"])
