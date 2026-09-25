"""Join two injected metadata observations; no execution token is read."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_app_credential_record_adapter as app
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_target_prestate_read_adapter as prestate

SCHEMA = "fsgg.coordination.callable-isolated-v2-effective-scope-metadata/1"
PERMISSIONS = app.PERMISSIONS


class Refused(ValueError):
    """Fixed refusal without credential or provider contents."""


class EffectiveScopeMetadataPort(Protocol):
    """Future protected metadata-only effective-scope reader; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def read_effective_metadata(self, credential_id: str) -> bytes: ...


class SelectedPrestatePort(Protocol):
    """Future independent #602 prestate observer; none supplied."""

    def observe_prestate(self) -> dict[str, Any]: ...


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
        raise Refused("effective-join-time") from None


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("effective-join-json") from None


def _scope(port: EffectiveScopeMetadataPort, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("effective-reader-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "store",
                           "permissions", "expiresAt"}, "effective-reader-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["store"] != "coordination-effective-app-scope"
            or type(value["permissions"]) is not list
            or value["permissions"] != ["read-effective-metadata"]
            or _time(value["expiresAt"]) <= now):
        raise Refused("effective-reader-binding")
    return copy.deepcopy(value)


class EffectiveScopePrestateJoin:
    """Implement #600 witness Protocol from a fresh #602 prestate and metadata."""

    def __init__(self, metadata: EffectiveScopeMetadataPort,
                 selected_prestate: SelectedPrestatePort,
                 selected: dict[str, Any], app_id: int,
                 run_id: int, run_attempt: int, now: dt.datetime):
        if (metadata is None or selected_prestate is None
                or metadata is selected_prestate
                or not all(_positive(value) for value in
                           (app_id, run_id, run_attempt))
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("effective-selection-invalid")
        selected = _exact(selected, {"repository", "repositoryId", "nodeId",
                    "installationId", "sourceRef", "sourceSha", "baseRef",
                    "baseSha", "prestateSha256"}, "effective-target-shape")
        if (type(selected["repository"]) is not str
                or candidate.REPOSITORY.fullmatch(selected["repository"]) is None
                or selected["repository"] == "FS-GG/.github"
                or not _positive(selected["repositoryId"])
                or not _positive(selected["installationId"])
                or type(selected["nodeId"]) is not str or not selected["nodeId"]
                or not candidate._branch(selected["sourceRef"])
                or not candidate._branch(selected["baseRef"])
                or selected["sourceRef"] == selected["baseRef"]
                or any(not candidate._hex(selected[key], candidate.HEX40)
                       for key in ("sourceSha", "baseSha"))
                or not candidate._hex(selected["prestateSha256"],
                                      candidate.HEX64)):
            raise Refused("effective-target-binding")
        self.metadata = metadata
        self.prestate = selected_prestate
        self.selected = copy.deepcopy(selected)
        self.app_id = app_id
        self.run_id = run_id
        self.run_attempt = run_attempt
        self.now = now

    def read_effective_scope(self, credential_id: str,
                             mint_sha256: str) -> dict[str, Any]:
        if (not candidate._hex(credential_id, candidate.HEX64)
                or not candidate._hex(mint_sha256, candidate.HEX64)):
            raise Refused("effective-input-invalid")
        scope_before = _scope(self.metadata, self.now)
        try:
            observed = self.prestate.observe_prestate()
            observed = copy.deepcopy(observed)
        except Exception:
            raise Refused("effective-prestate-unavailable") from None
        observed = _exact(observed, {"schema", "complete", "principalId",
                    "credentialId", "witnessPrincipalId",
                    "witnessCredentialId", "witnessObservedAt",
                    "witnessExpiresAt", "recordId", "runId", "runAttempt",
                    "target", "transcriptSha256", "observedAt"},
                    "effective-prestate-shape")
        if (observed["schema"] != prestate.SCHEMA
                or observed["complete"] is not True
                or type(observed["principalId"]) is not str
                or not observed["principalId"]
                or observed["principalId"] == scope_before["principalId"]
                or not candidate._hex(observed["credentialId"], candidate.HEX64)
                or observed["credentialId"] == scope_before["credentialId"]
                or type(observed["witnessPrincipalId"]) is not str
                or not observed["witnessPrincipalId"]
                or observed["witnessPrincipalId"] in
                   (observed["principalId"], scope_before["principalId"])
                or not candidate._hex(observed["witnessCredentialId"],
                                      candidate.HEX64)
                or observed["witnessCredentialId"] in
                   (observed["credentialId"], scope_before["credentialId"])
                or not all(_positive(observed[key]) for key in
                           ("recordId", "runId", "runAttempt"))
                or observed["runId"] != self.run_id
                or observed["runAttempt"] != self.run_attempt
                or _canonical(observed["target"]) != _canonical(self.selected)
                or not candidate._hex(observed["transcriptSha256"],
                                      candidate.HEX64)):
            raise Refused("effective-prestate-binding")
        prestate_at = _time(observed["observedAt"])
        witness_at = _time(observed["witnessObservedAt"])
        witness_expiry = _time(observed["witnessExpiresAt"])
        if (not self.now - dt.timedelta(minutes=5) <= prestate_at <= self.now
                or not self.now - dt.timedelta(minutes=30) <= witness_at
                       <= prestate_at < witness_expiry
                or witness_expiry <= self.now):
            raise Refused("effective-prestate-time")
        try:
            raw = self.metadata.read_effective_metadata(credential_id)
        except Exception:
            raise Refused("effective-record-unavailable") from None
        if type(raw) is not bytes or not 0 < len(raw) <= 16_384:
            raise Refused("effective-record-bytes")
        try:
            record = json.loads(raw.decode("utf-8"),
                                object_pairs_hook=candidate._unique,
                                parse_constant=candidate._no_constant)
        except (UnicodeError, ValueError):
            raise Refused("effective-join-json") from None
        if raw != _canonical(record):
            raise Refused("effective-record-noncanonical")
        record = _exact(record, {"schema", "complete", "recordId",
                    "credentialId", "mintSha256", "appId", "installationId",
                    "repositoryIds", "permissions", "targetSha256",
                    "responseSha256", "prestateSha256", "prestateRecordId",
                    "prestateTranscriptSha256", "runId", "runAttempt",
                    "issuedAt", "expiresAt", "observedAt"},
                    "effective-record-shape")
        target_sha = hashlib.sha256(_canonical(self.selected)).hexdigest()
        if (record["schema"] != SCHEMA or record["complete"] is not True
                or not _positive(record["recordId"])
                or record["credentialId"] != credential_id
                or record["mintSha256"] != mint_sha256
                or not _positive(record["appId"])
                or record["appId"] != self.app_id
                or not _positive(record["installationId"])
                or record["installationId"] != self.selected["installationId"]
                or type(record["repositoryIds"]) is not list
                or any(not _positive(item) for item in record["repositoryIds"])
                or record["repositoryIds"] != [self.selected["repositoryId"]]
                or record["permissions"] != PERMISSIONS
                or record["targetSha256"] != target_sha
                or type(record["responseSha256"]) is not list
                or len(record["responseSha256"]) != 4
                or any(not candidate._hex(item, candidate.HEX64)
                       for item in record["responseSha256"])
                or record["prestateSha256"] != self.selected["prestateSha256"]
                or not _positive(record["prestateRecordId"])
                or record["prestateRecordId"] != observed["recordId"]
                or record["prestateTranscriptSha256"] !=
                   observed["transcriptSha256"]
                or not _positive(record["runId"])
                or not _positive(record["runAttempt"])
                or record["runId"] != self.run_id
                or record["runAttempt"] != self.run_attempt):
            raise Refused("effective-record-binding")
        issued = _time(record["issuedAt"])
        expires = _time(record["expiresAt"])
        effective_at = _time(record["observedAt"])
        if (not issued <= prestate_at <= effective_at <= self.now < expires
                or effective_at - prestate_at > dt.timedelta(minutes=5)
                or expires - issued > dt.timedelta(hours=1)):
            raise Refused("effective-record-time")
        scope_after = _scope(self.metadata, self.now)
        if scope_after != scope_before:
            raise Refused("effective-reader-drift")
        return {"schema": app.WITNESS_SCHEMA, "complete": True,
                "principalId": scope_after["principalId"],
                "observerCredentialId": scope_after["credentialId"],
                "witnessId": record["recordId"],
                "credentialId": credential_id,
                "mintSha256": mint_sha256,
                "appId": self.app_id,
                "installationId": self.selected["installationId"],
                "repositoryIds": [self.selected["repositoryId"]],
                "permissions": PERMISSIONS,
                "targetSha256": target_sha,
                "responseSha256": record["responseSha256"],
                "prestateSha256": self.selected["prestateSha256"],
                "expiresAt": record["expiresAt"],
                "observedAt": record["observedAt"]}
