"""Pure, closed parser for a prospective one-use isolated v2 grant.

This module has no CLI, issuer, token lookup, HTTP client, or effect port.
Passing its checks does not authorize an operation. Protected observations
and an atomic journal reservation are separate future obligations.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import json
import re
from typing import Any

SCHEMA = "fsgg.coordination.callable-isolated-v2-grant/1"
REPLAY_SCHEMA = "fsgg.coordination.callable-isolated-v2-replay-observation/1"
IDENTITY = "v2-call-01-4b-isolated-native-v2-provisional"
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
REF = re.compile(r"refs/heads/[A-Za-z0-9._/-]+\Z")
UTC = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\Z")


class Refused(ValueError):
    """Fixed public refusal code; never include raw grant or provider data."""


@dataclasses.dataclass(frozen=True)
class ParsedGrant:
    payload_sha256: str
    grant_id: str
    operation_id: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise Refused("grant-duplicate-member")
        result[key] = value
    return result


def _no_constant(_value: str) -> None:
    raise Refused("grant-nonfinite")


def _object(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _hex(value: Any, pattern: re.Pattern[str]) -> bool:
    return type(value) is str and pattern.fullmatch(value) is not None


def _time(value: Any) -> dt.datetime:
    if type(value) is not str or UTC.fullmatch(value) is None:
        raise Refused("grant-time-invalid")
    try:
        parsed = dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        raise Refused("grant-time-invalid") from None
    return parsed.replace(tzinfo=dt.timezone.utc)


def _shape(value: Any, now: dt.datetime) -> dict[str, Any]:
    grant = _object(value, {"schema", "state", "grantId", "nonce", "operation",
                            "authority", "source", "target", "credential", "journal"},
                    "grant-shape")
    if grant["schema"] != SCHEMA or grant["state"] != "issued":
        raise Refused("grant-state")
    if not _hex(grant["grantId"], HEX64) or not _hex(grant["nonce"], HEX64):
        raise Refused("grant-identity")

    operation = _object(grant["operation"], {"identity", "id", "method", "path",
                                                  "requestSha256", "maxProviderWrites"},
                        "grant-operation-shape")
    if (operation["identity"] != IDENTITY or not _hex(operation["id"], HEX64)
            or operation["method"] != "POST"
            or type(operation["maxProviderWrites"]) is not int
            or operation["maxProviderWrites"] != 1
            or not _hex(operation["requestSha256"], HEX64)):
        raise Refused("grant-effect")

    authority = _object(grant["authority"], {"repository", "workflowPath", "workflowRevision",
                                                  "workflowSha256", "runId", "runAttempt",
                                                  "environment", "environmentId", "approvalId",
                                                  "approvedAt", "expiresAt", "dispatchActorId",
                                                  "reviewerId", "reviewerMembership"},
                        "grant-authority-shape")
    if (authority["repository"] != "FS-GG/.github"
            or authority["workflowPath"] != ".github/workflows/callable-isolated-v2-execute.yml"
            or authority["environment"] != "callable-isolated-v2"
            or authority["reviewerMembership"] != "active"
            or not _hex(authority["workflowRevision"], HEX40)
            or not _hex(authority["workflowSha256"], HEX64)
            or any(not _positive(authority[key]) for key in
                   ("runId", "runAttempt", "environmentId", "approvalId",
                    "dispatchActorId", "reviewerId"))
            or authority["dispatchActorId"] == authority["reviewerId"]):
        raise Refused("grant-authority")
    approved = _time(authority["approvedAt"])
    expires = _time(authority["expiresAt"])
    if (type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)
            or not approved <= now < expires
            or expires - approved > dt.timedelta(minutes=30)):
        raise Refused("grant-expired")

    source = _object(grant["source"], {"coordinationRevision", "operatorSha256",
                                            "contractSha256", "proposalSha256",
                                            "installedCommandSha256"}, "grant-source-shape")
    if (not _hex(source["coordinationRevision"], HEX40)
            or any(not _hex(source[key], HEX64) for key in
                   ("operatorSha256", "contractSha256", "proposalSha256", "installedCommandSha256"))):
        raise Refused("grant-source")

    target = _object(grant["target"], {"repository", "repositoryId", "nodeId",
                                            "installationId", "sourceRef", "sourceSha",
                                            "baseRef", "baseSha", "prestateSha256"},
                     "grant-target-shape")
    if (type(target["repository"]) is not str or REPOSITORY.fullmatch(target["repository"]) is None
            or not target["repository"].startswith("FS-GG/")
            or target["repository"].split("/", 1)[1] in {".", ".."}
            or not _positive(target["repositoryId"]) or not _positive(target["installationId"])
            or type(target["nodeId"]) is not str or not target["nodeId"]
            or any(type(target[key]) is not str or REF.fullmatch(target[key]) is None
                   or any(segment in {"", ".", ".."} for segment in target[key].split("/")[2:])
                   for key in ("sourceRef", "baseRef"))
            or any(not _hex(target[key], HEX40) for key in ("sourceSha", "baseSha"))
            or not _hex(target["prestateSha256"], HEX64)
            or target["sourceRef"] == target["baseRef"]
            or operation["path"] != f"repos/{target['repository']}/pulls"):
        raise Refused("grant-target")

    credential = _object(grant["credential"], {"kind", "appId", "installationId",
                                                    "repositoryIds", "repositoryFullNames",
                                                    "permissions", "expiresAt"},
                         "grant-credential-shape")
    if (credential["kind"] != "github-app-installation"
            or not _positive(credential["appId"])
            or not _positive(credential["installationId"])
            or credential["installationId"] != target["installationId"]
            or type(credential["repositoryIds"]) is not list
            or len(credential["repositoryIds"]) != 1
            or not _positive(credential["repositoryIds"][0])
            or credential["repositoryIds"] != [target["repositoryId"]]
            or type(credential["repositoryFullNames"]) is not list
            or credential["repositoryFullNames"] != [target["repository"]]
            or credential["permissions"] != {"metadata": "read", "contents": "read",
                                                 "pull_requests": "write"}
            or _time(credential["expiresAt"]) < expires):
        raise Refused("grant-credential")

    journal = _object(grant["journal"], {"repository", "ref", "generation", "head",
                                              "operationId", "state"}, "grant-journal-shape")
    if (journal["repository"] != "FS-GG/.github"
            or type(journal["ref"]) is not str
            or not journal["ref"].startswith("refs/heads/fsgg/v2/journal/operation/")
            or REF.fullmatch(journal["ref"]) is None
            or any(segment in {"", ".", ".."} for segment in journal["ref"].split("/")[2:])
            or not _positive(journal["generation"]) or not _hex(journal["head"], HEX40)
            or journal["operationId"] != operation["id"]
            or journal["state"] != "intent-committed"):
        raise Refused("grant-journal")
    return grant


def parse_grant(raw: bytes, expected_payload_sha256: str, expected: dict[str, Any],
                replay_observation: dict[str, Any], now: dt.datetime) -> ParsedGrant:
    """Validate proposed bytes against independently supplied exact bindings.

    ``expected`` must come from protected source, run, target and credential
    observation. ``replay_observation`` must come from the protected journal.
    This parser does not establish either provenance or reserve an attempt.
    """
    if type(raw) is not bytes or len(raw) > 16_384 or not _hex(expected_payload_sha256, HEX64):
        raise Refused("grant-input")
    payload_sha256 = hashlib.sha256(raw).hexdigest()
    if payload_sha256 != expected_payload_sha256:
        raise Refused("grant-payload-digest")
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique,
                           parse_constant=_no_constant)
        canonical = json.dumps(value, sort_keys=True, separators=(",", ":"),
                               ensure_ascii=True, allow_nan=False).encode("ascii")
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("grant-json") from None
    if canonical != raw:
        raise Refused("grant-noncanonical")
    grant = _shape(value, now)
    if type(expected) is not dict or grant != expected:
        raise Refused("grant-protected-binding")
    replay = _object(replay_observation, {"schema", "complete", "grantId", "operationId",
                                          "journalGeneration", "journalHead", "used"},
                     "grant-replay-shape")
    if (replay["schema"] != REPLAY_SCHEMA or replay["complete"] is not True
            or replay["grantId"] != grant["grantId"]
            or replay["operationId"] != grant["operation"]["id"]
            or not _positive(replay["journalGeneration"])
            or replay["journalGeneration"] != grant["journal"]["generation"]
            or replay["journalHead"] != grant["journal"]["head"]
            or replay["used"] is not False):
        raise Refused("grant-replayed-or-unknown")
    return ParsedGrant(payload_sha256, grant["grantId"], grant["operation"]["id"])
