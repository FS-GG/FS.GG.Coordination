"""Token-free fake public-handle custody observation for closed v5 admission.

It compares declared public identities only. No token bytes or vault client.
"""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate
from callable_isolated_v2_effect_v5_ports import NativeCoordinates
from callable_isolated_v2_provider_credential_metadata import CredentialMetadata

BINDING_SCHEMA = "fsgg.coordination.callable-isolated-v2-public-token-binding/1"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-token-custody-identity/1"
SELECTED_KEYS = {"eventId", "eventSha256", "publicHandleId",
                 "readerPrincipalId", "readerCredentialId"}
RECORD_KEYS = {"schema", "complete", "eventId", "publicHandleId",
               "publicCredentialId", "appId", "installationId",
               "repositoryId", "targetSha256", "operationId",
               "requestSha256", "runId", "runAttempt", "issuerActorId",
               "readerPrincipalId", "readerCredentialId", "issuedAt",
               "observedAt", "expiresAt"}
SCOPE_KEYS = {"principalId", "credentialId", "store", "permissions",
              "expiresAt"}
TARGET_KEYS = {"repository", "repositoryId", "nodeId", "installationId",
               "sourceRef", "sourceSha", "baseRef", "baseSha",
               "prestateSha256"}


class Refused(ValueError):
    """Fixed refusal without token or record contents."""


class PublicBindingPort(Protocol):
    """Future protected token-vault identity reader; fake ports only."""

    def scope(self) -> dict[str, Any]: ...

    def read_public_binding(self, event_id: int) -> bytes: ...


@dataclasses.dataclass(frozen=True)
class TokenCustodyIdentity:
    event_id: int
    public_handle_id: str
    public_credential_id: str
    event_sha256: str
    schema: str = dataclasses.field(init=False, default=RESULT_SCHEMA)
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)


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
        raise Refused("token-identity-time") from None


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (UnicodeError, TypeError, ValueError):
        raise Refused("token-identity-json") from None


def _scope(port: PublicBindingPort, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("token-identity-scope-unavailable") from None
    value = _exact(value, SCOPE_KEYS, "token-identity-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["store"] != "coordination-execution-token-custody"
            or type(value["permissions"]) is not list
            or value["permissions"] != ["read-public-token-binding"]
            or _time(value["expiresAt"]) <= now):
        raise Refused("token-identity-scope-binding")
    return copy.deepcopy(value)


def observe(metadata: CredentialMetadata, native: NativeCoordinates,
            target: dict[str, Any], selected: dict[str, Any],
            port: PublicBindingPort, now: dt.datetime) -> TokenCustodyIdentity:
    """Compare one selected public handle record; never read the token."""
    if (type(metadata) is not CredentialMetadata
            or type(native) is not NativeCoordinates
            or metadata.authorized is not False
            or metadata.can_dispatch is not False
            or type(metadata.live_effects) is not int
            or metadata.live_effects != 0
            or port is None or type(now) is not dt.datetime
            or now.tzinfo is None or now.utcoffset() != dt.timedelta(0)):
        raise Refused("token-identity-input")
    metadata_before = copy.deepcopy(metadata)
    native_before = copy.deepcopy(native)
    selected_original = _exact(selected, SELECTED_KEYS,
                               "token-identity-selection-shape")
    selected = copy.deepcopy(selected_original)
    if (not _positive(selected["eventId"])
            or not all(candidate._hex(selected[key], candidate.HEX64)
                       for key in ("eventSha256", "publicHandleId",
                                   "readerCredentialId"))
            or type(selected["readerPrincipalId"]) is not str
            or not selected["readerPrincipalId"]
            or selected["readerPrincipalId"] == metadata.reader_principal_id
            or selected["readerCredentialId"] == metadata.reader_credential_id
            or selected["readerCredentialId"] == metadata.credential_id
            or not all(candidate._hex(value, candidate.HEX64) for value in
                       (metadata.credential_id, metadata.request_sha256,
                        metadata.reader_credential_id))
            or not _positive(metadata.app_id)
            or not _positive(metadata.installation_id)
            or not _positive(metadata.repository_id)
            or type(metadata.reader_principal_id) is not str
            or not metadata.reader_principal_id):
        raise Refused("token-identity-selection-binding")
    target_original = _exact(target, TARGET_KEYS,
                             "token-identity-target-shape")
    target = copy.deepcopy(target_original)
    if (not _positive(target["repositoryId"])
            or not _positive(target["installationId"])
            or type(target["repository"]) is not str
            or candidate.REPOSITORY.fullmatch(target["repository"]) is None
            or target["repository"] == "FS-GG/.github"
            or type(target["nodeId"]) is not str or not target["nodeId"]
            or not candidate._branch(target["sourceRef"])
            or not candidate._branch(target["baseRef"])
            or target["sourceRef"] == target["baseRef"]
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
            or metadata.repository_id != native.repository_id
            or metadata.installation_id != native.installation_id
            or metadata.request_sha256 != native.request_sha256
            or not all(_positive(value) for value in
                       (native.run_id, native.run_attempt,
                        native.issuer_actor_id, native.dispatch_actor_id,
                        native.reviewer_actor_id))
            or len({native.issuer_actor_id, native.dispatch_actor_id,
                    native.reviewer_actor_id}) != 3):
        raise Refused("token-identity-target-binding")
    scope_before = _scope(port, now)
    if (scope_before["principalId"] != selected["readerPrincipalId"]
            or scope_before["credentialId"] != selected["readerCredentialId"]):
        raise Refused("token-identity-reader-binding")
    try:
        raw = port.read_public_binding(selected["eventId"])
    except Exception:
        raise Refused("token-identity-read-unavailable") from None
    if type(raw) is not bytes or not 0 < len(raw) <= 8192:
        raise Refused("token-identity-bytes")
    try:
        record = json.loads(raw.decode("utf-8"),
                            object_pairs_hook=candidate._unique,
                            parse_constant=candidate._no_constant)
    except (UnicodeError, ValueError):
        raise Refused("token-identity-json") from None
    if (raw != _canonical(record)
            or hashlib.sha256(raw).hexdigest() != selected["eventSha256"]):
        raise Refused("token-identity-digest")
    record = _exact(record, RECORD_KEYS, "token-identity-record-shape")
    expected = {"eventId": selected["eventId"],
                "publicHandleId": selected["publicHandleId"],
                "publicCredentialId": metadata.credential_id,
                "appId": metadata.app_id,
                "installationId": metadata.installation_id,
                "repositoryId": metadata.repository_id,
                "targetSha256": hashlib.sha256(_canonical(target)).hexdigest(),
                "operationId": native.operation_id,
                "requestSha256": metadata.request_sha256,
                "runId": native.run_id,
                "runAttempt": native.run_attempt,
                "issuerActorId": native.issuer_actor_id,
                "readerPrincipalId": scope_before["principalId"],
                "readerCredentialId": scope_before["credentialId"]}
    if (record["schema"] != BINDING_SCHEMA
            or record["complete"] is not True
            or any(type(record[key]) is not type(value)
                   or record[key] != value for key, value in expected.items())):
        raise Refused("token-identity-record-binding")
    issued = _time(record["issuedAt"])
    observed = _time(record["observedAt"])
    expires = _time(record["expiresAt"])
    if (not issued <= observed <= now < expires
            or now - observed > dt.timedelta(minutes=5)
            or expires - issued > dt.timedelta(hours=1)
            or not now < _time(native.grant_expires_at) <= expires):
        raise Refused("token-identity-time-binding")
    if (_scope(port, now) != scope_before
            or selected_original != selected
            or target_original != target
            or metadata != metadata_before
            or native != native_before):
        raise Refused("token-identity-reader-drift")
    return TokenCustodyIdentity(selected["eventId"],
                                selected["publicHandleId"],
                                metadata.credential_id,
                                selected["eventSha256"])
