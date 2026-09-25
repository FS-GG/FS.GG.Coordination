"""Token-free selected App scope join for a closed v5 provider request.

The metadata is caller-supplied here. No execution token or HTTP port exists.
"""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any

import callable_isolated_v2_app_credential_record_adapter as app
import callable_isolated_v2_effect_candidate as candidate
from callable_isolated_v2_effect_v5_ports import NativeCoordinates
from callable_isolated_v2_provider_request_spec import ORIGIN, RequestSpec

SCHEMA = "fsgg.coordination.callable-isolated-v2-provider-credential-metadata/1"
EFFECTIVE_KEYS = {"schema", "complete", "principalId",
                  "observerCredentialId", "witnessId", "credentialId",
                  "mintSha256", "appId", "installationId", "repositoryIds",
                  "permissions", "targetSha256", "responseSha256",
                  "prestateSha256", "expiresAt", "observedAt"}
TARGET_KEYS = {"repository", "repositoryId", "nodeId", "installationId",
               "sourceRef", "sourceSha", "baseRef", "baseSha",
               "prestateSha256"}


class Refused(ValueError):
    """Fixed refusal without token or metadata contents."""


@dataclasses.dataclass(frozen=True)
class CredentialMetadata:
    credential_id: str
    app_id: int
    installation_id: int
    repository_id: int
    request_sha256: str
    schema: str = dataclasses.field(init=False, default=SCHEMA)
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _time(value: Any) -> dt.datetime:
    try:
        return candidate._time(value)
    except candidate.Refused:
        raise Refused("credential-time") from None


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("credential-target-json") from None


def qualify(spec: RequestSpec, native: NativeCoordinates,
            effective: dict[str, Any], target: dict[str, Any],
            selected_credential_id: str, selected_mint_sha256: str,
            selected_app_id: int, selected_reader_principal: str,
            selected_reader_credential: str,
            now: dt.datetime) -> CredentialMetadata:
    """Compare selected public metadata to the closed request; never read token."""
    if (type(spec) is not RequestSpec or type(native) is not NativeCoordinates
            or spec.authorized is not False
            or spec.can_dispatch is not False
            or type(spec.live_effects) is not int or spec.live_effects != 0
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)
            or not candidate._hex(selected_credential_id, candidate.HEX64)
            or not candidate._hex(selected_mint_sha256, candidate.HEX64)
            or not candidate._hex(selected_reader_credential, candidate.HEX64)
            or not _positive(selected_app_id)
            or type(selected_reader_principal) is not str
            or not selected_reader_principal
            or selected_reader_credential == selected_credential_id):
        raise Refused("credential-input-binding")
    if type(target) is not dict or set(target) != TARGET_KEYS:
        raise Refused("credential-target-shape")
    target = copy.deepcopy(target)
    if (not _positive(target["repositoryId"])
            or not _positive(target["installationId"])
            or type(target["repository"]) is not str
            or type(target["nodeId"]) is not str
            or not target["nodeId"]
            or not candidate._branch(target["sourceRef"])
            or not candidate._branch(target["baseRef"])
            or not candidate._hex(target["sourceSha"], candidate.HEX40)
            or not candidate._hex(target["baseSha"], candidate.HEX40)
            or not candidate._hex(target["prestateSha256"], candidate.HEX64)
            or target != {"repository": native.repository,
                   "repositoryId": native.repository_id,
                   "nodeId": native.repository_node_id,
                   "installationId": native.installation_id,
                   "sourceRef": native.source_ref,
                   "sourceSha": native.source_sha,
                   "baseRef": native.base_ref,
                   "baseSha": native.base_sha,
                   "prestateSha256": native.prestate_sha256}
            or type(native.repository) is not str
            or candidate.REPOSITORY.fullmatch(native.repository) is None
            or native.repository == "FS-GG/.github"
            or not _positive(native.repository_id)
            or not _positive(native.installation_id)
            or native.operation_identity != candidate.IDENTITY
            or native.method != "POST"
            or type(native.max_provider_writes) is not int
            or native.max_provider_writes != 1
            or type(native.canonical_request) is not bytes
            or native.canonical_request != spec.body
            or native.path != f"repos/{native.repository}/pulls"
            or spec.url != f"{ORIGIN}/repos/{native.repository}/pulls"
            or spec.method != "POST"
            or spec.max_sends != 1 or spec.automatic_retries != 0
            or spec.follow_redirects is not False
            or spec.operation_id != native.operation_id
            or spec.request_sha256 != native.request_sha256
            or type(spec.body) is not bytes
            or hashlib.sha256(spec.body).hexdigest() != spec.request_sha256):
        raise Refused("credential-request-binding")
    if type(effective) is not dict or set(effective) != EFFECTIVE_KEYS:
        raise Refused("credential-record-shape")
    record = copy.deepcopy(effective)
    target_sha = hashlib.sha256(_canonical(target)).hexdigest()
    if (record["schema"] != app.WITNESS_SCHEMA
            or record["complete"] is not True
            or record["principalId"] != selected_reader_principal
            or record["observerCredentialId"] != selected_reader_credential
            or not _positive(record["witnessId"])
            or record["credentialId"] != selected_credential_id
            or record["mintSha256"] != selected_mint_sha256
            or type(record["appId"]) is not int
            or record["appId"] != selected_app_id
            or type(record["installationId"]) is not int
            or record["installationId"] != native.installation_id
            or type(record["repositoryIds"]) is not list
            or len(record["repositoryIds"]) != 1
            or type(record["repositoryIds"][0]) is not int
            or record["repositoryIds"] != [native.repository_id]
            or type(record["permissions"]) is not dict
            or record["permissions"] != app.PERMISSIONS
            or record["targetSha256"] != target_sha
            or record["prestateSha256"] != native.prestate_sha256
            or type(record["responseSha256"]) is not list
            or len(record["responseSha256"]) != 4
            or any(not candidate._hex(item, candidate.HEX64)
                   for item in record["responseSha256"])):
        raise Refused("credential-scope-binding")
    observed, expires = _time(record["observedAt"]), _time(record["expiresAt"])
    grant_expires = _time(native.grant_expires_at)
    if (not now - dt.timedelta(minutes=5) <= observed <= now
            < grant_expires <= expires):
        raise Refused("credential-time-binding")
    return CredentialMetadata(selected_credential_id, selected_app_id,
                              native.installation_id, native.repository_id,
                              spec.request_sha256)
