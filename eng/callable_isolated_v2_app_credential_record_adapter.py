"""Metadata-only execution App credential attestation; no token is handled."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_target_scope_read_adapter as target_reader

SCHEMA = "fsgg.coordination.callable-isolated-v2-app-issue-metadata/1"
WITNESS_SCHEMA = "fsgg.coordination.callable-isolated-v2-app-scope-witness/1"
PERMISSIONS = target_reader.PERMISSIONS
API = "https://api.github.com"


class Refused(ValueError):
    """Fixed refusal without secret, provider or issuer contents."""


class MintMetadataPort(Protocol):
    """Future protected redacted mint-record reader; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def read_mint_metadata(self, record_id: int) -> bytes: ...


class EffectiveAppScopeWitness(Protocol):
    """Future independent effective-scope reader; none supplied."""

    def read_effective_scope(self, credential_id: str,
                             mint_sha256: str) -> dict[str, Any]: ...


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _one_repository(value: Any, selected_id: int) -> bool:
    return (type(value) is list and len(value) == 1
            and _positive(value[0]) and value[0] == selected_id)


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _time(value: Any) -> dt.datetime:
    try:
        return candidate._time(value)
    except candidate.Refused:
        raise Refused("app-record-time") from None


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("app-record-json") from None


def _scope(port: MintMetadataPort, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("app-record-reader-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "store",
                           "permissions", "expiresAt"}, "app-record-reader-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["store"] != "coordination-app-mint-metadata"
            or type(value["permissions"]) is not list
            or value["permissions"] != ["read-mint-metadata"]
            or _time(value["expiresAt"]) <= now):
        raise Refused("app-record-reader-binding")
    return value


class AppCredentialRecordAdapter:
    """Implement #592 attestation Protocol from two injected metadata ports."""

    def __init__(self, mint: MintMetadataPort,
                 witness: EffectiveAppScopeWitness,
                 selected: dict[str, Any], record_id: int, app_id: int,
                 credential_id: str, now: dt.datetime):
        if (mint is None or witness is None or mint is witness
                or not _positive(record_id) or not _positive(app_id)
                or not candidate._hex(credential_id, candidate.HEX64)
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("app-selection-invalid")
        selected = _exact(selected, {"repository", "repositoryId", "nodeId",
                    "installationId", "sourceRef", "sourceSha", "baseRef",
                    "baseSha", "prestateSha256"}, "app-target-shape")
        if (type(selected["repository"]) is not str
                or candidate.REPOSITORY.fullmatch(selected["repository"]) is None
                or selected["repository"] == "FS-GG/.github"
                or not _positive(selected["repositoryId"])
                or not _positive(selected["installationId"])
                or type(selected["nodeId"]) is not str
                or not selected["nodeId"]
                or not candidate._branch(selected["sourceRef"])
                or not candidate._branch(selected["baseRef"])
                or selected["sourceRef"] == selected["baseRef"]
                or not candidate._hex(selected["sourceSha"], candidate.HEX40)
                or not candidate._hex(selected["baseSha"], candidate.HEX40)
                or not candidate._hex(selected["prestateSha256"], candidate.HEX64)):
            raise Refused("app-target-binding")
        self.mint = mint
        self.witness = witness
        self.selected = copy.deepcopy(selected)
        self.record_id = record_id
        self.app_id = app_id
        self.credential_id = credential_id
        self.now = now

    def read_target(self, target_sha256: str,
                    response_sha256: tuple[str, str, str, str]) -> dict[str, Any]:
        selected = self.selected
        if (type(response_sha256) is not tuple or len(response_sha256) != 4
                or any(not candidate._hex(item, candidate.HEX64)
                       for item in response_sha256)
                or target_sha256 != hashlib.sha256(_canonical(selected)).hexdigest()):
            raise Refused("app-target-response-binding")
        scope_before = _scope(self.mint, self.now)
        try:
            raw = self.mint.read_mint_metadata(self.record_id)
        except Exception:
            raise Refused("app-record-unavailable") from None
        if type(raw) is not bytes or not 0 < len(raw) <= 16_384:
            raise Refused("app-record-bytes")
        try:
            record = json.loads(raw.decode("utf-8"),
                                object_pairs_hook=candidate._unique,
                                parse_constant=candidate._no_constant)
        except (UnicodeError, ValueError):
            raise Refused("app-record-json") from None
        if raw != _canonical(record):
            raise Refused("app-record-noncanonical")
        record = _exact(record, {"schema", "state", "recordId", "credentialId",
                    "appId", "installationId", "repositoryIds", "permissions",
                    "issuedAt", "expiresAt", "targetSha256", "responseSha256",
                    "prestateSha256", "request", "redactedResponse"},
                    "app-record-shape")
        request = _exact(record["request"], {"method", "path", "repository_ids",
                         "permissions"}, "app-mint-request-shape")
        redacted = _exact(record["redactedResponse"], {"expires_at",
                          "permissions", "repository_selection", "repositories"},
                          "app-mint-response-shape")
        repository = selected["repository"]
        expected_repo = {"id": selected["repositoryId"],
                         "node_id": selected["nodeId"],
                         "full_name": repository,
                         "url": f"{API}/repos/{repository}"}
        if (record["schema"] != SCHEMA or record["state"] != "issued-metadata-only"
                or not _positive(record["recordId"])
                or record["recordId"] != self.record_id
                or record["credentialId"] != self.credential_id
                or record["appId"] != self.app_id
                or type(record["appId"]) is not int
                or record["installationId"] != selected["installationId"]
                or type(record["installationId"]) is not int
                or not _one_repository(record["repositoryIds"],
                                       selected["repositoryId"])
                or record["permissions"] != PERMISSIONS
                or record["targetSha256"] != target_sha256
                or type(record["responseSha256"]) is not list
                or record["responseSha256"] != list(response_sha256)
                or record["prestateSha256"] != selected["prestateSha256"]
                or not _one_repository(request["repository_ids"],
                                       selected["repositoryId"])
                or type(redacted["repositories"]) is not list
                or len(redacted["repositories"]) != 1
                or type(redacted["repositories"][0]) is not dict
                or not _positive(redacted["repositories"][0].get("id"))
                or request != {"method": "POST",
                               "path": f"app/installations/{selected['installationId']}/access_tokens",
                               "repository_ids": [selected["repositoryId"]],
                               "permissions": PERMISSIONS}
                or redacted != {"expires_at": record["expiresAt"],
                                "permissions": PERMISSIONS,
                                "repository_selection": "selected",
                                "repositories": [expected_repo]}):
            raise Refused("app-record-binding")
        issued = _time(record["issuedAt"])
        expires = _time(record["expiresAt"])
        if (not issued <= self.now < expires
                or expires - issued > dt.timedelta(hours=1)):
            raise Refused("app-record-time")
        scope_after = _scope(self.mint, self.now)
        if scope_after != scope_before:
            raise Refused("app-record-reader-drift")
        mint_sha = hashlib.sha256(raw).hexdigest()
        try:
            effective = self.witness.read_effective_scope(self.credential_id,
                                                           mint_sha)
        except Exception:
            raise Refused("app-scope-unavailable") from None
        effective = _exact(effective, {"schema", "complete", "principalId",
                    "observerCredentialId", "witnessId", "credentialId",
                    "mintSha256", "appId", "installationId", "repositoryIds",
                    "permissions", "targetSha256", "responseSha256",
                    "prestateSha256", "expiresAt", "observedAt"},
                    "app-scope-shape")
        if (effective["schema"] != WITNESS_SCHEMA
                or effective["complete"] is not True
                or type(effective["principalId"]) is not str
                or not effective["principalId"]
                or effective["principalId"] == scope_after["principalId"]
                or not candidate._hex(effective["observerCredentialId"],
                                      candidate.HEX64)
                or effective["observerCredentialId"] == scope_after["credentialId"]
                or not _positive(effective["witnessId"])
                or effective["credentialId"] != self.credential_id
                or effective["mintSha256"] != mint_sha
                or effective["appId"] != self.app_id
                or effective["installationId"] != selected["installationId"]
                or not _one_repository(effective["repositoryIds"],
                                       selected["repositoryId"])
                or effective["permissions"] != PERMISSIONS
                or effective["targetSha256"] != target_sha256
                or type(effective["responseSha256"]) is not list
                or effective["responseSha256"] != list(response_sha256)
                or effective["prestateSha256"] != selected["prestateSha256"]
                or effective["expiresAt"] != record["expiresAt"]):
            raise Refused("app-scope-binding")
        observed = _time(effective["observedAt"])
        if not issued <= observed <= self.now < expires:
            raise Refused("app-scope-time")
        return {"schema": target_reader.SCHEMA, "complete": True,
                "principalId": effective["principalId"],
                "credentialId": effective["observerCredentialId"],
                "recordId": effective["witnessId"],
                "targetSha256": target_sha256,
                "responseSha256": list(response_sha256),
                "prestateSha256": selected["prestateSha256"],
                "credential": {"kind": "github-app-installation",
                               "installationId": selected["installationId"],
                               "repositoryIds": [selected["repositoryId"]],
                               "permissions": PERMISSIONS,
                               "expiresAt": record["expiresAt"]}}
