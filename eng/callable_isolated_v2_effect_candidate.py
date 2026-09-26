"""Pure, non-authorizing shape check for a future isolated v2 effect artifact.

Inputs are caller-supplied consistency data. No protected observer, artifact
builder, token, journal, provider transport, grant or dispatch entry exists here.
A successful result is deliberately non-dispatchable.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import json
import re
from typing import Any

SCHEMA = "fsgg.coordination.callable-isolated-v2-effect-candidate/1"
IDENTITY = "v2-call-01-4b-isolated-native-v2-provisional"
WORKFLOW_PATH = ".github/workflows/callable-isolated-v2-execute.yml"
INSPECT_ONLY_ARCHIVE = "003f63ac1b0f895642a7000954607b0980f8e9f1dbb058576e66b40ed79cbfb7"
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
REPOSITORY = re.compile(r"FS-GG/[A-Za-z0-9_.-]+\Z")
REF = re.compile(r"refs/heads/[A-Za-z0-9._/-]+\Z")
OCI = re.compile(r"[^\s@]+@sha256:[0-9a-f]{64}\Z")
UTC = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\Z")


class Refused(ValueError):
    """Fixed public refusal reason without candidate contents."""


@dataclasses.dataclass(frozen=True)
class CandidateCheck:
    payload_sha256: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise Refused("candidate-duplicate-member")
        result[key] = value
    return result


def _no_constant(_value: str) -> None:
    raise Refused("candidate-nonfinite")


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _hex(value: Any, pattern: re.Pattern[str]) -> bool:
    return type(value) is str and pattern.fullmatch(value) is not None and any(c != "0" for c in value)


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _branch(value: Any) -> bool:
    return (type(value) is str and REF.fullmatch(value) is not None
            and all(segment not in {"", ".", ".."} for segment in value.split("/")[2:]))


def _time(value: Any) -> dt.datetime:
    if type(value) is not str or UTC.fullmatch(value) is None:
        raise Refused("candidate-review-time")
    try:
        return dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=dt.timezone.utc)
    except ValueError:
        raise Refused("candidate-review-time") from None


def verify_candidate(raw: bytes, payload_sha256: str, expected: dict[str, Any],
                     now: dt.datetime) -> CandidateCheck:
    """Check exact synthetic bindings; never interpret them as authority.

    A future protected verifier must obtain expected facts from distinct
    authenticated source, review, target and credential observers. This pure
    function cannot establish their provenance or allow a provider attempt.
    """
    if (type(raw) is not bytes or len(raw) > 16_384
            or not _hex(payload_sha256, HEX64)
            or hashlib.sha256(raw).hexdigest() != payload_sha256):
        raise Refused("candidate-input")
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique,
                           parse_constant=_no_constant)
        canonical = json.dumps(value, sort_keys=True, separators=(",", ":"),
                               ensure_ascii=True, allow_nan=False).encode("ascii")
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("candidate-json") from None
    if raw != canonical:
        raise Refused("candidate-noncanonical")
    packet = _exact(value, {"schema", "state", "source", "runtime", "review",
                            "target", "operation"}, "candidate-shape")
    if packet["schema"] != SCHEMA or packet["state"] != "prepared-not-authorized":
        raise Refused("candidate-state")

    source = _exact(packet["source"], {"coordinationRevision", "sourceTree",
                                        "operatorSha256", "controlsSha256",
                                        "effectArchiveSha256", "workflowRevision",
                                        "workflowPath", "workflowSha256",
                                        "producerRunId", "artifactId", "producerActorId"},
                    "candidate-source-shape")
    if (not _hex(source["coordinationRevision"], HEX40)
            or not _hex(source["sourceTree"], HEX40)
            or not _hex(source["workflowRevision"], HEX40)
            or source["workflowPath"] != WORKFLOW_PATH
            or any(not _hex(source[key], HEX64) for key in
                   ("operatorSha256", "controlsSha256", "effectArchiveSha256",
                    "workflowSha256"))
            or any(not _positive(source[key]) for key in
                   ("producerRunId", "artifactId", "producerActorId"))):
        raise Refused("candidate-source")
    if source["effectArchiveSha256"] == INSPECT_ONLY_ARCHIVE:
        raise Refused("candidate-source")

    runtime = _exact(packet["runtime"], {"runnerImage", "imageAttestationSha256",
                                          "interpreterSha256", "closureSha256"},
                     "candidate-runtime-shape")
    if (type(runtime["runnerImage"]) is not str
            or OCI.fullmatch(runtime["runnerImage"]) is None
            or runtime["runnerImage"].endswith("0" * 64)
            or any(not _hex(runtime[key], HEX64) for key in
                   ("imageAttestationSha256", "interpreterSha256", "closureSha256"))):
        raise Refused("candidate-runtime")

    review = _exact(packet["review"], {"repository", "runId", "runAttempt",
                                        "environmentId", "dispatchActorId", "reviewerId",
                                        "reviewEventId", "membership", "purpose",
                                        "reviewedAt", "expiresAt"},
                    "candidate-review-shape")
    if (review["repository"] != "FS-GG/.github"
            or review["membership"] != "active"
            or review["purpose"] != "source-only"
            or any(not _positive(review[key]) for key in
                   ("runId", "runAttempt", "environmentId", "dispatchActorId",
                    "reviewerId", "reviewEventId"))
            or review["dispatchActorId"] == review["reviewerId"]
            or source["producerActorId"] == review["reviewerId"]
            or source["producerRunId"] == review["runId"]):
        raise Refused("candidate-review")
    reviewed = _time(review["reviewedAt"])
    expires = _time(review["expiresAt"])
    if (type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)
            or not reviewed <= now < expires
            or expires - reviewed > dt.timedelta(minutes=30)):
        raise Refused("candidate-review-time")

    target = _exact(packet["target"], {"repository", "repositoryId", "nodeId",
                                        "installationId", "sourceRef", "sourceSha",
                                        "baseRef", "baseSha", "prestateSha256"},
                    "candidate-target-shape")
    if (type(target["repository"]) is not str
            or REPOSITORY.fullmatch(target["repository"]) is None
            or target["repository"].split("/", 1)[1] in {".", ".."}
            or any(not _positive(target[key]) for key in
                   ("repositoryId", "installationId"))
            or type(target["nodeId"]) is not str or not target["nodeId"]
            or not _branch(target["sourceRef"]) or not _branch(target["baseRef"])
            or target["sourceRef"] == target["baseRef"]
            or any(not _hex(target[key], HEX40) for key in ("sourceSha", "baseSha"))
            or not _hex(target["prestateSha256"], HEX64)):
        raise Refused("candidate-target")

    operation = _exact(packet["operation"], {"identity", "id", "method", "path",
                                              "requestSha256", "maxProviderWrites"},
                       "candidate-operation-shape")
    if (operation["identity"] != IDENTITY
            or not _hex(operation["id"], HEX64)
            or operation["method"] != "POST"
            or operation["path"] != f"repos/{target['repository']}/pulls"
            or not _hex(operation["requestSha256"], HEX64)
            or type(operation["maxProviderWrites"]) is not int
            or operation["maxProviderWrites"] != 1):
        raise Refused("candidate-operation")
    if type(expected) is not dict or packet != expected:
        raise Refused("candidate-observed-binding")
    return CandidateCheck(payload_sha256)
