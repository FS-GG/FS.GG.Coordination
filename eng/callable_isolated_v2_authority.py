"""Source-only protected authority and journal evidence boundary for isolated v2.

There is deliberately no provider, token, issuer, journal, or dispatch adapter.
An injected observer is useful for local contract tests, but its values have no
authority until a separately reviewed protected implementation proves origin.
Every result from this module remains non-dispatchable.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import io
import json
import zipfile
from typing import Any, Protocol

import callable_isolated_v2_grant as grant

OBSERVATION_SCHEMA = "fsgg.coordination.callable-isolated-v2-authority-observation/1"
ISSUER_OBSERVATION_SCHEMA = "fsgg.coordination.callable-isolated-v2-issuer-observation/1"
ISSUE_REQUEST_SCHEMA = "fsgg.coordination.callable-isolated-v2-issue-request/1"
ISSUE_SCHEMA = "fsgg.coordination.callable-isolated-v2-issuance-readback/1"
ARTIFACT_SCHEMA = "fsgg.coordination.callable-isolated-v2-grant-artifact/1"
RESERVATION_SCHEMA = "fsgg.coordination.callable-isolated-v2-attempt-reservation/1"
CAS_SCHEMA = "fsgg.coordination.callable-isolated-v2-journal-cas-readback/1"
GRANT_MEMBER = "grant.json"


class Refused(ValueError):
    """Fixed refusal code without raw protected or credential data."""


class ProtectedAuthorityObserver(Protocol):
    """Future implementation must independently read protected facts.

    Its trust root, role scoping, API pagination, and provenance are outside
    this source-only candidate. No production implementation is supplied.
    """

    def observe_current(self) -> dict[str, Any]: ...


class ProtectedGrantIssuer(Protocol):
    """Future separate issuer custody; this module never calls issue/write."""

    def issue_once(self, request: dict[str, Any]) -> None: ...

    def read_issuance(self) -> dict[str, Any]: ...


class ProtectedReplayObserver(Protocol):
    """Future independent protected journal reader."""

    def read_replay(self, grant_id: str, operation_id: str) -> dict[str, Any]: ...


class ProtectedJournalAdapter(Protocol):
    """Future qualified CAS backend; no implementation or caller is supplied."""

    def compare_and_swap(self, reservation: dict[str, Any]) -> None: ...

    def read_back(self, repository: str, ref: str) -> dict[str, Any]: ...


@dataclasses.dataclass(frozen=True)
class VerifiedCandidate:
    payload_sha256: str
    archive_sha256: str
    grant_id: str
    operation_id: str
    grant: dict[str, Any]
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


@dataclasses.dataclass(frozen=True)
class IssueCandidate:
    request: dict[str, Any]
    request_sha256: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


@dataclasses.dataclass(frozen=True)
class ReservationCandidate:
    request: dict[str, Any]
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


@dataclasses.dataclass(frozen=True)
class ReservationReadback:
    generation: int
    head: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _hex(value: Any, pattern: Any) -> bool:
    return type(value) is str and pattern.fullmatch(value) is not None


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("authority-canonical-binding") from None


def plan_issue_request(observed: dict[str, Any], now: dt.datetime) -> IssueCandidate:
    """Seal complete observed bindings as a candidate; never call an issuer."""
    _exact(observed, {"schema", "complete", "authority", "source", "target",
                      "credential", "operation", "journal"},
           "authority-observation-shape")
    if observed["schema"] != OBSERVATION_SCHEMA or observed["complete"] is not True:
        raise Refused("authority-observation-incomplete")
    provisional = {"schema": grant.SCHEMA, "state": "issued",
                   "grantId": "0" * 64, "nonce": "1" * 64,
                   "operation": observed["operation"], "authority": observed["authority"],
                   "source": observed["source"], "target": observed["target"],
                   "credential": observed["credential"], "journal": observed["journal"]}
    try:
        grant._shape(provisional, now)
        authority = observed["authority"]
        request = {"schema": ISSUE_REQUEST_SCHEMA, "state": "candidate",
                   "runId": authority["runId"], "runAttempt": authority["runAttempt"],
                   "approvalId": authority["approvalId"],
                   "operationId": observed["operation"]["id"],
                   "bindingsSha256": hashlib.sha256(_canonical(observed)).hexdigest()}
        raw = _canonical(request)
    except (grant.Refused, Refused, KeyError, TypeError, ValueError, UnicodeError):
        raise Refused("authority-issue-bindings") from None
    return IssueCandidate(request, hashlib.sha256(raw).hexdigest())


def _read_artifact(archive: bytes, artifact: dict[str, Any]) -> bytes:
    _exact(artifact, {"schema", "archiveSha256", "payloadSha256", "member",
                      "memberCount", "grantId", "runId", "runAttempt"},
           "authority-artifact-shape")
    if (artifact["schema"] != ARTIFACT_SCHEMA
            or type(archive) is not bytes or len(archive) > 32_768
            or not _hex(artifact["archiveSha256"], grant.HEX64)
            or not _hex(artifact["payloadSha256"], grant.HEX64)
            or artifact["archiveSha256"] != hashlib.sha256(archive).hexdigest()
            or artifact["member"] != GRANT_MEMBER
            or type(artifact["memberCount"]) is not int
            or artifact["memberCount"] != 1):
        raise Refused("authority-artifact-binding")
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as package:
            members = package.infolist()
            if (len(members) != 1 or members[0].filename != GRANT_MEMBER
                    or members[0].is_dir() or members[0].file_size > 16_384
                    or members[0].compress_type != zipfile.ZIP_STORED
                    or members[0].external_attr >> 16 & 0o170000 == 0o120000):
                raise Refused("authority-artifact-member")
            raw = package.read(members[0])
    except Refused:
        raise
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile, NotImplementedError):
        raise Refused("authority-artifact-invalid") from None
    if len(raw) > 16_384 or hashlib.sha256(raw).hexdigest() != artifact["payloadSha256"]:
        raise Refused("authority-artifact-payload")
    return raw


def verify_issued_candidate(archive: bytes, observer: ProtectedAuthorityObserver,
                            issuer: ProtectedGrantIssuer, replay_reader: ProtectedReplayObserver,
                            now: dt.datetime) -> VerifiedCandidate:
    """Compare a single artifact with independent observation interfaces.

    The observer itself is intentionally unimplemented. A local fake can prove
    shape checks, but it cannot turn this result into protected authorization.
    """
    if (observer is None or issuer is None or replay_reader is None
            or observer is issuer or observer is replay_reader or issuer is replay_reader):
        raise Refused("authority-port-custody")
    try:
        observed = observer.observe_current()
    except Exception:
        raise Refused("authority-observation-unavailable") from None
    issue_candidate = plan_issue_request(observed, now)
    try:
        issued = issuer.read_issuance()
    except Exception:
        raise Refused("authority-issuance-unavailable") from None
    _exact(issued, {"schema", "complete", "artifact", "issuance"},
           "authority-issuer-observation-shape")
    if issued["schema"] != ISSUER_OBSERVATION_SCHEMA or issued["complete"] is not True:
        raise Refused("authority-issuer-observation-incomplete")
    issuance = _exact(issued["issuance"], {"schema", "state", "grantId", "nonce",
                                               "issuerActorId", "issuanceEventId", "issuedAt",
                                               "approvalId", "runId", "runAttempt",
                                               "archiveSha256", "payloadSha256",
                                               "issueRequestSha256"},
                      "authority-issuance-shape")
    authority = observed["authority"]
    artifact = issued["artifact"]
    if (type(authority) is not dict
            or type(artifact) is not dict
            or issuance["schema"] != ISSUE_SCHEMA or issuance["state"] != "issued"
            or not _hex(issuance["grantId"], grant.HEX64)
            or not _hex(issuance["nonce"], grant.HEX64)
            or not _positive(issuance["issuerActorId"])
            or not _positive(issuance["issuanceEventId"])
            or any(not _positive(issuance[key]) for key in
                   ("approvalId", "runId", "runAttempt"))
            or any(not _positive(artifact.get(key)) for key in ("runId", "runAttempt"))
            or issuance["issuerActorId"] in
               (authority.get("dispatchActorId"), authority.get("reviewerId"))
            or issuance["approvalId"] != authority.get("approvalId")
            or issuance["runId"] != authority.get("runId")
            or issuance["runAttempt"] != authority.get("runAttempt")
            or issuance["issueRequestSha256"] != issue_candidate.request_sha256
            or issuance["grantId"] != artifact.get("grantId")
            or issuance["runId"] != artifact.get("runId")
            or issuance["runAttempt"] != artifact.get("runAttempt")
            or issuance["archiveSha256"] != artifact.get("archiveSha256")
            or issuance["payloadSha256"] != artifact.get("payloadSha256")):
        raise Refused("authority-issuance-binding")
    try:
        issued_at = grant._time(issuance["issuedAt"])
        approved_at = grant._time(authority["approvedAt"])
        expires_at = grant._time(authority["expiresAt"])
    except (KeyError, grant.Refused):
        raise Refused("authority-issuance-time") from None
    if (type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)
            or not approved_at <= issued_at <= now < expires_at):
        raise Refused("authority-issuance-time")
    raw = _read_artifact(archive, artifact)
    expected = {"schema": grant.SCHEMA, "state": "issued",
                "grantId": issuance["grantId"], "nonce": issuance["nonce"],
                "operation": observed["operation"], "authority": authority,
                "source": observed["source"], "target": observed["target"],
                "credential": observed["credential"], "journal": observed["journal"]}
    try:
        replay = replay_reader.read_replay(issuance["grantId"], observed["operation"]["id"])
        parsed = grant.parse_grant(raw, artifact["payloadSha256"], expected, replay, now)
    except grant.Refused as error:
        raise Refused(str(error)) from None
    except Exception:
        raise Refused("authority-replay-unavailable") from None
    return VerifiedCandidate(parsed.payload_sha256, artifact["archiveSha256"],
                             parsed.grant_id, parsed.operation_id, expected)


def plan_attempt_reservation(candidate: VerifiedCandidate) -> ReservationCandidate:
    """Describe one exact-parent marker; this function performs no journal IO."""
    if type(candidate) is not VerifiedCandidate or candidate.can_dispatch or candidate.authorized:
        raise Refused("reservation-candidate")
    value = candidate.grant
    try:
        journal = value["journal"]
        operation = value["operation"]
        authority = value["authority"]
        target = value["target"]
        request = {
            "schema": RESERVATION_SCHEMA,
            "repository": journal["repository"], "ref": journal["ref"],
            "expectedGeneration": journal["generation"],
            "expectedHead": journal["head"],
            "nextGeneration": journal["generation"] + 1,
            "state": "attempt-may-have-started",
            "grantId": candidate.grant_id,
            "grantPayloadSha256": candidate.payload_sha256,
            "operationId": candidate.operation_id,
            "requestSha256": operation["requestSha256"],
            "runId": authority["runId"], "runAttempt": authority["runAttempt"],
            "targetRepositoryId": target["repositoryId"],
            "targetPrestateSha256": target["prestateSha256"],
        }
    except (KeyError, TypeError):
        raise Refused("reservation-candidate") from None
    if (not _positive(request["expectedGeneration"])
            or not _hex(request["expectedHead"], grant.HEX40)
            or not _hex(request["requestSha256"], grant.HEX64)):
        raise Refused("reservation-candidate")
    return ReservationCandidate(request)


def verify_attempt_readback(candidate: ReservationCandidate, cas_result: dict[str, Any],
                            readback: dict[str, Any]) -> ReservationReadback:
    """Check CAS acknowledgement plus a separate full journal readback.

    Even a matched readback is evidence only. It does not expose a dispatch
    capability; a later protected executable must independently qualify CAS.
    """
    if type(candidate) is not ReservationCandidate or candidate.can_dispatch:
        raise Refused("reservation-candidate")
    expected = candidate.request
    keys = {"schema", "complete", "repository", "ref", "expectedGeneration",
            "expectedHead", "generation", "head", "marker"}
    cas = _exact(cas_result, keys | {"outcome"}, "reservation-cas-shape")
    check = _exact(readback, keys, "reservation-readback-shape")
    for item in (cas, check):
        if (item["schema"] != CAS_SCHEMA or item["complete"] is not True
                or item["repository"] != expected["repository"]
                or item["ref"] != expected["ref"]
                or item["expectedGeneration"] != expected["expectedGeneration"]
                or item["expectedHead"] != expected["expectedHead"]
                or item["generation"] != expected["nextGeneration"]
                or type(item["expectedGeneration"]) is not int
                or type(item["generation"]) is not int
                or not _hex(item["head"], grant.HEX40)
                or item["head"] == expected["expectedHead"]
                or _canonical(item["marker"]) != _canonical(expected)):
            raise Refused("reservation-readback-binding")
    if cas["outcome"] != "accepted" or cas["head"] != check["head"]:
        raise Refused("reservation-cas-unknown")
    return ReservationReadback(check["generation"], check["head"])
