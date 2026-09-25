"""Pure fake-port installed refusal join for the pinned v5 no-grant ZIP."""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any

import callable_isolated_v2_v5_no_grant_selection as candidate

PROBE_SCHEMA = "fsgg.coordination.v5-no-grant-installed-probe/1"
AUDIT_SCHEMA = "fsgg.coordination.v5-no-grant-no-effect-audit/1"
CHOSEN = {"repositoryId", "artifactId", "runId", "runAttempt",
          "runnerActorId", "auditActorId", "auditEventId", "imageDigest",
          "interpreterSha256", "runtimeClosureSha256", "installPath"}
SHARED = {"repository", "repositoryId", "revision", "sourceTree",
          "producerRunId", "reviewEventId", "artifactId", "manifestSha256",
          "archiveSha256", "runId", "runAttempt", "installPath"}
OBJECT = {"device", "inode", "size", "sha256"}
COUNTS = {"tokenReads", "journalReads", "journalWrites", "casWrites",
          "providerPosts", "providerPuts", "cleanupAttempts"}
SCOPE = {"principalId", "credentialId", "repository", "repositoryId",
         "permissions", "expiresAt"}
PROBE = SHARED | {"schema", "complete", "principalId", "credentialId",
                  "runnerActorId", "imageDigest", "interpreterSha256",
                  "runtimeClosureSha256", "realPath", "symlink", "before",
                  "after", "archiveBytes", "argv", "exitCode", "stdout",
                  "stderr", "startedAt", "completedAt", "observedAt"}
AUDIT = SHARED | {"schema", "complete", "principalId", "credentialId",
                  "eventId", "auditActorId", "runnerActorId", "object",
                  "counts", "startedAt", "completedAt", "observedAt"}


class Refused(ValueError):
    """Fixed refusal with no observed bytes or credential contents."""


@dataclasses.dataclass(frozen=True)
class Readback:
    archive_sha256: str
    manifest_sha256: str
    revision: str
    artifact_id: int
    run_id: int
    audit_event_id: int
    source_tree: str
    repository_id: int
    runner_actor_id: int
    audit_actor_id: int
    image_digest: str
    interpreter_sha256: str
    runtime_closure_sha256: str
    install_path: str
    command_started_at: str
    command_completed_at: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _shape(value: Any, keys: set[str]):
    if type(value) is not dict or set(value) != keys:
        raise Refused("v5-installed-shape")


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _path(value: Any) -> bool:
    return (type(value) is str and value.startswith("/")
            and not value.startswith("//") and "\x00" not in value
            and all(part not in ("", ".", "..") for part in value[1:].split("/"))
            and value.endswith("/fsgg-callable-isolated-v2-v5-no-grant.pyz"))


def _scope(value: Any, permission: str, repository_id: int, now: dt.datetime):
    _shape(value, SCOPE)
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != candidate.REPOSITORY
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != repository_id
            or type(value["permissions"]) is not list
            or value["permissions"] != [permission]
            or not now < candidate._time(value["expiresAt"])
                   <= now + dt.timedelta(minutes=30)):
        raise Refused("v5-installed-scope")


def _object(value: Any, archive: bytes, archive_sha: str):
    _shape(value, OBJECT)
    if (not _positive(value["device"]) or not _positive(value["inode"])
            or type(value["size"]) is not int or value["size"] != len(archive)
            or type(value["sha256"]) is not str
            or value["sha256"] != archive_sha):
        raise Refused("v5-installed-object")


def qualify(selection: candidate.Selection, chosen: dict, probe_port,
            audit_port, now: dt.datetime) -> Readback:
    """Compare independent injected observations; never install or dispatch."""
    try:
        selected = copy.deepcopy(chosen)
        _shape(selected, CHOSEN)
        original_selection = selection
        if type(selection) is not candidate.Selection:
            raise Refused("v5-installed-selection")
        selection = copy.deepcopy(selection)
        if (type(selection) is not candidate.Selection
                or selection.authorized is not False
                or selection.can_dispatch is not False
                or type(selection.live_effects) is not int
                or selection.live_effects != 0
                or selection.archive_sha256 != candidate.PINNED_BYTES["archive"]
                or selection.manifest_sha256 != candidate.PINNED_BYTES["manifest"]
                or not all(_positive(getattr(selection, key)) for key in
                           ("repository_id", "artifact_id", "producer_run_id",
                            "producer_run_attempt",
                            "review_event_id", "producer_actor_id",
                            "reviewer_actor_id", "source_record_id"))
                or not candidate._hex(selection.revision, candidate.HEX40)
                or not candidate._hex(selection.source_tree, candidate.HEX40)
                or any(type(getattr(selection, key)) is not str
                       or not getattr(selection, key)
                       for key in ("source_reader_principal",
                                   "review_reader_principal"))
                or not all(candidate._hex(getattr(selection, key),
                                            candidate.HEX64)
                           for key in ("source_credential_id",
                                       "review_credential_id"))
                or selection.source_reader_principal ==
                   selection.review_reader_principal
                or selection.source_credential_id ==
                   selection.review_credential_id
                or not all(_positive(selected[key]) for key in CHOSEN
                           if key not in {"imageDigest", "interpreterSha256",
                                          "runtimeClosureSha256", "installPath"})
                or selected["repositoryId"] != selection.repository_id
                or selected["artifactId"] != selection.artifact_id
                or selected["runnerActorId"] in
                   (selection.producer_actor_id, selection.reviewer_actor_id)
                or selected["auditActorId"] in
                   (selection.producer_actor_id, selection.reviewer_actor_id,
                    selected["runnerActorId"])
                or not all(candidate._hex(selected[key], candidate.HEX64)
                           for key in ("imageDigest", "interpreterSha256",
                                       "runtimeClosureSha256"))
                or not _path(selected["installPath"])
                or probe_port is None or audit_port is None
                or probe_port is audit_port
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("v5-installed-selection")
        reviewed = candidate._time(selection.reviewed_at)
        expires = candidate._time(selection.expires_at)
        if not (now - dt.timedelta(minutes=30) <= reviewed <= now < expires
                <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-installed-review-time")
        probe_scope = copy.deepcopy(probe_port.scope())
        audit_scope = copy.deepcopy(audit_port.scope())
        _scope(probe_scope, "read-installed-probe", selection.repository_id, now)
        _scope(audit_scope, "read-no-effect-audit", selection.repository_id, now)
        if (probe_scope["principalId"] == audit_scope["principalId"]
                or probe_scope["credentialId"] == audit_scope["credentialId"]
                or any(scope["principalId"] in
                       (selection.source_reader_principal,
                        selection.review_reader_principal)
                       or scope["credentialId"] in
                       (selection.source_credential_id,
                        selection.review_credential_id)
                       for scope in (probe_scope, audit_scope))):
            raise Refused("v5-installed-shared-reader")
        probe = copy.deepcopy(probe_port.read(selected["runId"],
                                              selected["runAttempt"]))
        audit = copy.deepcopy(audit_port.read(selected["auditEventId"]))
        _shape(probe, PROBE)
        _shape(audit, AUDIT)
        shared = {"repository": candidate.REPOSITORY,
                  "repositoryId": selection.repository_id,
                  "revision": selection.revision,
                  "sourceTree": selection.source_tree,
                  "producerRunId": selection.producer_run_id,
                  "reviewEventId": selection.review_event_id,
                  "artifactId": selection.artifact_id,
                  "manifestSha256": selection.manifest_sha256,
                  "archiveSha256": selection.archive_sha256,
                  "runId": selected["runId"],
                  "runAttempt": selected["runAttempt"],
                  "installPath": selected["installPath"]}
        for record, scope, schema in ((probe, probe_scope, PROBE_SCHEMA),
                                      (audit, audit_scope, AUDIT_SCHEMA)):
            if (record["schema"] != schema or record["complete"] is not True
                    or record["principalId"] != scope["principalId"]
                    or record["credentialId"] != scope["credentialId"]
                    or any(type(record[key]) is not type(value)
                           or record[key] != value
                           for key, value in shared.items())):
                raise Refused("v5-installed-binding")
        archive = probe["archiveBytes"]
        if (type(archive) is not bytes or not 0 < len(archive) <= 256_000
                or hashlib.sha256(archive).hexdigest() !=
                   selection.archive_sha256
                or type(probe["runnerActorId"]) is not int
                or probe["runnerActorId"] != selected["runnerActorId"]
                or any(probe[key] != selected[key] for key in
                       ("imageDigest", "interpreterSha256",
                        "runtimeClosureSha256"))
                or probe["realPath"] != selected["installPath"]
                or probe["symlink"] is not False):
            raise Refused("v5-installed-probe")
        before = probe["before"]
        after = probe["after"]
        _object(before, archive, selection.archive_sha256)
        _object(after, archive, selection.archive_sha256)
        if before != after:
            raise Refused("v5-installed-drift")
        refusal = {"schema":
            "fsgg.coordination.callable-isolated-v2-v5-vault-refusal/1",
            "reason": "no-grant", "authorized": False,
            "canDispatch": False, "liveEffects": 0}
        stdout = (json.dumps(refusal, sort_keys=True,
                             separators=(",", ":")) + "\n").encode()
        if (probe["argv"] != ["python3", "-I", "-S",
                              selected["installPath"], "execute-native-pull"]
                or type(probe["exitCode"]) is not int
                or probe["exitCode"] != 78
                or type(probe["stdout"]) is not bytes
                or probe["stdout"] != stdout
                or type(probe["stderr"]) is not bytes
                or probe["stderr"] != b""):
            raise Refused("v5-installed-refusal")
        if (type(audit["eventId"]) is not int
                or audit["eventId"] != selected["auditEventId"]
                or type(audit["auditActorId"]) is not int
                or audit["auditActorId"] != selected["auditActorId"]
                or type(audit["runnerActorId"]) is not int
                or audit["runnerActorId"] != selected["runnerActorId"]):
            raise Refused("v5-installed-audit")
        _object(audit["object"], archive, selection.archive_sha256)
        if audit["object"] != before:
            raise Refused("v5-installed-audit-object")
        _shape(audit["counts"], COUNTS)
        if any(type(value) is not int or value != 0
               for value in audit["counts"].values()):
            raise Refused("v5-installed-effect-count")
        if (probe["startedAt"] != audit["startedAt"]
                or probe["completedAt"] != audit["completedAt"]):
            raise Refused("v5-installed-interval")
        start = candidate._time(probe["startedAt"])
        completed = candidate._time(probe["completedAt"])
        observed = candidate._time(probe["observedAt"])
        audited = candidate._time(audit["observedAt"])
        if not (reviewed <= start <= completed <= observed <= audited <= now
                and now - dt.timedelta(minutes=15) <= start
                and audited < expires):
            raise Refused("v5-installed-time")
        if (probe_port.scope() != probe_scope
                or audit_port.scope() != audit_scope
                or chosen != selected
                or original_selection != selection):
            raise Refused("v5-installed-scope-drift")
        return Readback(selection.archive_sha256, selection.manifest_sha256,
                        selection.revision, selection.artifact_id,
                        selected["runId"], selected["auditEventId"],
                        selection.source_tree, selection.repository_id,
                        selected["runnerActorId"], selected["auditActorId"],
                        selected["imageDigest"],
                        selected["interpreterSha256"],
                        selected["runtimeClosureSha256"],
                        selected["installPath"], probe["startedAt"],
                        probe["completedAt"])
    except Refused:
        raise
    except Exception:
        raise Refused("v5-installed-unavailable") from None
