"""Pure candidate verifier for a future protected inspect-only v2 release.

There is no protected observer implementation, credential, CLI, workflow, or
effect port here. A locally supplied observation can test the contract but
cannot prove protected provenance or turn a result into authorization.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import json
import re
from typing import Any, Protocol

SCHEMA = "fsgg.coordination.callable-isolated-v2-release-packet/1"
OBSERVATION_SCHEMA = "fsgg.coordination.callable-isolated-v2-release-observation/1"
APPROVAL_SCHEMA = "fsgg.coordination.callable-isolated-v2-release-approval/1"
WORKFLOW = ".github/workflows/callable-isolated-v2-release.yml"
ENVIRONMENT = "callable-isolated-v2-release"
ARCHIVE = "fsgg-callable-isolated-v2.pyz"
ARCHIVE_SHA256 = "003f63ac1b0f895642a7000954607b0980f8e9f1dbb058576e66b40ed79cbfb7"
ZIPAPP_MANIFEST_SHA256 = "9cf485b238a458c3234adafb6f3964b7e105507a8d8cfbeb01a1d067b2545fd3"
LOCAL_CLOSURE_SHA256 = "6783d094396af466461f3dfc8e8d0e41dada34b177e1c06dd784806c79979162"
PINNED_SOURCE = {
    "eng/build_callable_isolated_v2_zipapp.py": "2c581bed909f835ae24de9b79ab1e2a1d4b144dd5dcf3b8b3285872c4af6ed6b",
    "eng/callable_isolated_v2_entry.py": "03b0f43fffb265e1895e32ef76239e5076bbd22d4da690383b7479f915ab9adc",
    "eng/callable_isolated_v2_grant.py": "f37feb7dd835f3327fb1aac0987d3397ea19314cdfcf42099a0c490b48220da4",
    "eng/callable_isolated_v2_authority.py": "9a806bb78de25be887db0a0b9499f242d7989d33860e598e23e2ceff80172a26",
    "eng/verify_callable_isolated_v2_install.py": "0a581ccfef5cde4f360635e4ef352a85c648ea72b185e088f125c9d86e048237",
    "eng/callable_isolated_v2_runtime_closure.py": "938b71a2f35e3690e32bf81b788c23f95b2e188bc648835a2053eeffec9d6765",
}
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+\Z")
OCI = re.compile(r"[a-z0-9.-]+\.[a-z]{2,}/[a-z0-9][a-z0-9._/-]*@sha256:[0-9a-f]{64}\Z")
UTC = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\Z")
PERMISSIONS = {"actions": "read", "contents": "read"}


class Refused(ValueError):
    """Fixed refusal code without raw release or identity data."""


class ProtectedReleaseObserver(Protocol):
    """Future read-only source, artifact, runner and run observer; absent now."""

    def read_current(self) -> dict[str, Any]: ...


class ProtectedApprovalObserver(Protocol):
    """Future independent environment and membership observer; absent now."""

    def read_approval(self) -> dict[str, Any]: ...


@dataclasses.dataclass(frozen=True)
class PreparedRelease:
    packet_sha256: str
    source_revision: str
    workflow_sha256: str
    archive_sha256: str
    closure_sha256: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise Refused("release-duplicate-member")
        value[key] = item
    return value


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("release-canonical-binding") from None


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _hex(value: Any, pattern: re.Pattern[str]) -> bool:
    return type(value) is str and pattern.fullmatch(value) is not None


def _nonzero_hex(value: Any, pattern: re.Pattern[str]) -> bool:
    return _hex(value, pattern) and value != "0" * len(value)


def _time(value: Any) -> dt.datetime:
    if type(value) is not str or UTC.fullmatch(value) is None:
        raise Refused("release-approval-time")
    try:
        return dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=dt.timezone.utc)
    except ValueError:
        raise Refused("release-approval-time") from None


def _shape(value: dict[str, Any], now: dt.datetime) -> None:
    _exact(value, {"schema", "state", "source", "workflow", "artifact", "runner",
                   "runtime", "run", "approval", "permissions"}, "release-shape")
    if value["schema"] != SCHEMA or value["state"] != "prepared-not-authorized":
        raise Refused("release-state")
    source = _exact(value["source"], {"repository", "revision", "tree", "protectedMain",
                                       "files", "releaseVerifierSha256"}, "release-source-shape")
    if (source["repository"] != "FS-GG/FS.GG.Coordination"
            or not _nonzero_hex(source["revision"], HEX40)
            or not _nonzero_hex(source["tree"], HEX40)
            or source["protectedMain"] is not True or source["files"] != PINNED_SOURCE
            or not _nonzero_hex(source["releaseVerifierSha256"], HEX64)):
        raise Refused("release-source")
    workflow = _exact(value["workflow"], {"repository", "path", "revision", "sha256"},
                      "release-workflow-shape")
    if (workflow["repository"] != source["repository"] or workflow["path"] != WORKFLOW
            or workflow["revision"] != source["revision"]
            or not _nonzero_hex(workflow["sha256"], HEX64)):
        raise Refused("release-workflow")
    artifact = _exact(value["artifact"], {"name", "archiveSha256", "manifestSha256",
                                           "producerRunId", "artifactId", "sourceRevision",
                                           "downloadReadbackSha256"}, "release-artifact-shape")
    if (artifact["name"] != ARCHIVE or artifact["archiveSha256"] != ARCHIVE_SHA256
            or artifact["manifestSha256"] != ZIPAPP_MANIFEST_SHA256
            or not _positive(artifact["producerRunId"]) or not _positive(artifact["artifactId"])
            or artifact["sourceRevision"] != source["revision"]
            or artifact["downloadReadbackSha256"] != ARCHIVE_SHA256):
        raise Refused("release-artifact")
    runner = _exact(value["runner"], {"image", "platform", "imageAttestationSha256",
                                       "ephemeral", "readOnlyRoot"}, "release-runner-shape")
    if (type(runner["image"]) is not str or OCI.fullmatch(runner["image"]) is None
            or runner["image"].endswith("0" * 64)
            or any(segment in {"", ".", ".."} for segment in
                   runner["image"].split("@", 1)[0].split("/")[1:])
            or runner["platform"] != "linux/amd64"
            or not _nonzero_hex(runner["imageAttestationSha256"], HEX64)
            or runner["ephemeral"] is not True or runner["readOnlyRoot"] is not True):
        raise Refused("release-runner")
    runtime = _exact(value["runtime"], {"interpreterPath", "interpreterSha256",
                                         "interpreterVersion", "flags", "closureManifestSha256",
                                         "stdlibTreeSha256", "mappedFileCount"},
                     "release-runtime-shape")
    path = runtime["interpreterPath"]
    if (type(path) is not str or not path.startswith("/")
            or any(part in {"", ".", ".."} for part in path.split("/")[1:])
            or not _nonzero_hex(runtime["interpreterSha256"], HEX64)
            or type(runtime["interpreterVersion"]) is not str
            or VERSION.fullmatch(runtime["interpreterVersion"]) is None
            or runtime["flags"] != ["-I", "-S"]
            or not _nonzero_hex(runtime["closureManifestSha256"], HEX64)
            or runtime["closureManifestSha256"] == LOCAL_CLOSURE_SHA256
            or not _nonzero_hex(runtime["stdlibTreeSha256"], HEX64)
            or not _positive(runtime["mappedFileCount"])
            or runtime["mappedFileCount"] > 128):
        raise Refused("release-runtime")
    run = _exact(value["run"], {"repository", "workflowPath", "revision", "ref", "event",
                                 "runId", "runAttempt", "environment", "environmentId",
                                 "actorId", "executionTokenPresent"}, "release-run-shape")
    if (run["repository"] != source["repository"] or run["workflowPath"] != WORKFLOW
            or run["revision"] != source["revision"] or run["ref"] != "refs/heads/main"
            or run["event"] != "workflow_dispatch" or run["environment"] != ENVIRONMENT
            or any(not _positive(run[key]) for key in
                   ("runId", "runAttempt", "environmentId", "actorId"))
            or run["executionTokenPresent"] is not False):
        raise Refused("release-run")
    approval = _exact(value["approval"], {"schema", "complete", "approvalId", "runId",
                                           "runAttempt", "environmentId", "reviewerId",
                                           "reviewerMembership", "approvedAt", "expiresAt"},
                      "release-approval-shape")
    if (approval["schema"] != APPROVAL_SCHEMA or approval["complete"] is not True
            or any(not _positive(approval[key]) for key in
                   ("approvalId", "runId", "runAttempt", "environmentId", "reviewerId"))
            or approval["runId"] != run["runId"]
            or approval["runAttempt"] != run["runAttempt"]
            or approval["environmentId"] != run["environmentId"]
            or approval["reviewerId"] == run["actorId"]
            or approval["reviewerMembership"] != "active"):
        raise Refused("release-approval")
    approved, expires = _time(approval["approvedAt"]), _time(approval["expiresAt"])
    if (type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)
            or not approved <= now < expires
            or expires - approved > dt.timedelta(minutes=30)):
        raise Refused("release-approval-time")
    if value["permissions"] != PERMISSIONS:
        raise Refused("release-permissions")


def verify_prepared_packet(raw: bytes, expected_sha256: str,
                           release_observer: ProtectedReleaseObserver,
                           approval_observer: ProtectedApprovalObserver,
                           now: dt.datetime) -> PreparedRelease:
    """Validate a future packet against distinct observation ports, never authorize."""
    if (type(raw) is not bytes or len(raw) > 16_384
            or not _hex(expected_sha256, HEX64)
            or hashlib.sha256(raw).hexdigest() != expected_sha256):
        raise Refused("release-packet-digest")
    if (release_observer is None or approval_observer is None
            or release_observer is approval_observer):
        raise Refused("release-observer-custody")
    try:
        value = json.loads(raw.decode("ascii"), object_pairs_hook=_unique)
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("release-packet-json") from None
    if _canonical(value) != raw:
        raise Refused("release-packet-noncanonical")
    _shape(value, now)
    try:
        release = release_observer.read_current()
        approval = approval_observer.read_approval()
    except Exception:
        raise Refused("release-observation-unavailable") from None
    observed = _exact(release, {"schema", "complete", "source", "workflow", "artifact",
                                "runner", "runtime", "run", "permissions"},
                      "release-observation-shape")
    if (observed["schema"] != OBSERVATION_SCHEMA or observed["complete"] is not True
            or _canonical(approval) != _canonical(value["approval"])
            or any(_canonical(observed[key]) != _canonical(value[key]) for key in
                   ("source", "workflow", "artifact", "runner", "runtime", "run", "permissions"))):
        raise Refused("release-protected-binding")
    return PreparedRelease(expected_sha256, value["source"]["revision"],
                           value["workflow"]["sha256"], value["artifact"]["archiveSha256"],
                           value["runtime"]["closureManifestSha256"])
