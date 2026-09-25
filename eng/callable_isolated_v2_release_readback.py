"""Pure proposed readback contract for a future inspect-only v2 installation.

No protected observer is implemented here. Caller-supplied dictionaries, even
when consistent, cannot prove a protected review or activate the held workflow.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_release_provenance as release
import validate_callable_isolated_v2_release_workflow as held

SCHEMA = "fsgg.coordination.callable-isolated-v2-release-selection/1"
CONTROLS_SCHEMA = "fsgg.coordination.callable-isolated-v2-installed-controls/1"
REVIEW_SCHEMA = "fsgg.coordination.callable-isolated-v2-selection-review/1"
DRAFT_HEAD = "55ca33b7b45fd81889ffbccb4fbd4939f352b7d3"
ORIGINAL_HELD_WORKFLOW_SHA256 = "1aac83c07ad0c798bd946dfecc87d16924a94455c05b566d43b44eb70aa445b9"
REFRESHED_HELD_WORKFLOW_SHA256 = "c486fe746ee88536897c277fc1cbcc320813c08d4b876dacb01adb78da759e93"
HELD_WORKFLOW_SHA256S = frozenset({ORIGINAL_HELD_WORKFLOW_SHA256,
                                    REFRESHED_HELD_WORKFLOW_SHA256,
                                    held.WORKFLOW_SHA256})
EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()
NO_GRANT_STDERR_SHA256 = hashlib.sha256(b"inspect-refused:inspect-arguments\n").hexdigest()
UNKNOWN_STDERR_SHA256 = hashlib.sha256(b"inspect-only\n").hexdigest()
SELECTION_KEYS = frozenset({
    "schema", "state", "sourceRevision", "sourceTree", "workflowSha256",
    "archiveSha256", "manifestSha256", "producerRunId", "artifactId",
    "runnerImage", "imageAttestationSha256",
    "interpreterSha256", "closureManifestSha256", "stdlibTreeSha256",
    "packetSha256", "runId", "runAttempt", "environmentId", "actorId", "approvalId",
    "reviewerId",
})
CONTROL_KEYS = frozenset({
    "schema", "complete", "runId", "runAttempt", "sourceRevision", "sourceTree",
    "actorId", "producerRunId", "artifactId", "workflowSha256", "archiveSha256",
    "interpreterSha256",
    "closureManifestSha256", "installedArchivePostSha256",
    "interpreterPostSha256", "closurePostSha256", "noGrant", "unknownCommand",
    "executionTokenPresent", "providerRequestCount", "journalWriteCount",
    "workingDirectoryWriteCount", "observedAt",
})
REVIEW_KEYS = frozenset({"schema", "complete", "selectionSha256", "bindings",
                         "reviewerId", "reviewerMembership", "reviewEventId",
                         "reviewedAt", "repository", "workflowPath",
                         "sourceRevision", "runId", "runAttempt",
                         "environmentId", "actorId", "approvalId"})


class Refused(ValueError):
    """Fixed refusal code without packet, reviewer or runner values."""


class InstalledControlObserver(Protocol):
    """Future independent read-only observer; no adapter is supplied."""

    def read_controls(self) -> dict[str, Any]: ...


class SelectionReviewObserver(Protocol):
    """Future independent protected selection-review reader; no adapter here."""

    def read_review(self) -> dict[str, Any]: ...


@dataclasses.dataclass(frozen=True)
class ReadbackEvidence:
    source_revision: str
    workflow_sha256: str
    archive_sha256: str
    runner_image: str
    closure_sha256: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise Refused("readback-duplicate-member")
        value[key] = item
    return value


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("readback-canonical") from None


def _exact(value: Any, keys: frozenset[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _sha(value: Any) -> bool:
    return (type(value) is str and release.HEX64.fullmatch(value) is not None
            and value != "0" * 64)


def _selection(raw: bytes, expected_sha256: str) -> dict[str, Any]:
    if (type(raw) is not bytes or len(raw) > 8_192 or not _sha(expected_sha256)
            or hashlib.sha256(raw).hexdigest() != expected_sha256):
        raise Refused("readback-selection-digest")
    try:
        value = json.loads(raw.decode("ascii"), object_pairs_hook=_unique)
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("readback-selection-json") from None
    _exact(value, SELECTION_KEYS, "readback-selection-shape")
    if _canonical(value) != raw:
        raise Refused("readback-selection-noncanonical")
    if (value["schema"] != SCHEMA or value["state"] != "selected-not-authorized"
            or type(value["sourceRevision"]) is not str
            or release.HEX40.fullmatch(value["sourceRevision"]) is None
            or value["sourceRevision"] in {"0" * 40, DRAFT_HEAD}
            or type(value["sourceTree"]) is not str
            or release.HEX40.fullmatch(value["sourceTree"]) is None
            or value["sourceTree"] == "0" * 40
            or not _sha(value["workflowSha256"])
            or value["workflowSha256"] in HELD_WORKFLOW_SHA256S
            or value["archiveSha256"] != release.ARCHIVE_SHA256
            or value["manifestSha256"] != release.ZIPAPP_MANIFEST_SHA256
            or type(value["runnerImage"]) is not str
            or release.OCI.fullmatch(value["runnerImage"]) is None
            or "unselected" in value["runnerImage"]
            or value["runnerImage"].endswith("0" * 64)
            or any(segment in {"", ".", ".."} for segment in
                   value["runnerImage"].split("@", 1)[0].split("/")[1:])
            or any(not _sha(value[key]) for key in
                   ("imageAttestationSha256", "interpreterSha256",
                    "closureManifestSha256", "stdlibTreeSha256", "packetSha256"))
            or value["closureManifestSha256"] == release.LOCAL_CLOSURE_SHA256
            or any(type(value[key]) is not int or value[key] <= 0 for key in
                   ("producerRunId", "artifactId", "runId", "runAttempt",
                    "environmentId", "actorId", "approvalId", "reviewerId"))
            or value["actorId"] == value["reviewerId"]):
        raise Refused("readback-selection-unselected")
    return value


def _refusal(value: Any, argv: list[str], stderr_sha256: str) -> None:
    _exact(value, frozenset({"argv", "exitCode", "stdoutSha256", "stderrSha256"}),
           "readback-control-shape")
    if (value["argv"] != argv or type(value["exitCode"]) is not int
            or value["exitCode"] != 2 or value["stdoutSha256"] != EMPTY_SHA256
            or value["stderrSha256"] != stderr_sha256):
        raise Refused("readback-control-refusal")


def _controls(value: Any, selected: dict[str, Any], packet: dict[str, Any],
              now: dt.datetime) -> dt.datetime:
    _exact(value, CONTROL_KEYS, "readback-controls-shape")
    if (value["schema"] != CONTROLS_SCHEMA or value["complete"] is not True
            or any(type(value[key]) is not type(selected[other])
                   or value[key] != selected[other] for key, other in
                   (("runId", "runId"), ("runAttempt", "runAttempt"),
                    ("sourceRevision", "sourceRevision"),
                    ("sourceTree", "sourceTree"),
                    ("actorId", "actorId"),
                    ("producerRunId", "producerRunId"),
                    ("artifactId", "artifactId"),
                    ("workflowSha256", "workflowSha256"),
                    ("archiveSha256", "archiveSha256"),
                    ("interpreterSha256", "interpreterSha256"),
                    ("closureManifestSha256", "closureManifestSha256"),
                    ("installedArchivePostSha256", "archiveSha256"),
                    ("interpreterPostSha256", "interpreterSha256"),
                    ("closurePostSha256", "closureManifestSha256")))
            or value["executionTokenPresent"] is not False
            or any(type(value[key]) is not int or value[key] != 0 for key in
                   ("providerRequestCount", "journalWriteCount",
                    "workingDirectoryWriteCount"))):
        raise Refused("readback-controls-incomplete")
    _refusal(value["noGrant"], ["inspect-grant"], NO_GRANT_STDERR_SHA256)
    _refusal(value["unknownCommand"], ["execute-native-pull"], UNKNOWN_STDERR_SHA256)
    try:
        observed = release._time(value["observedAt"])
        approved = release._time(packet["approval"]["approvedAt"])
        expires = release._time(packet["approval"]["expiresAt"])
    except release.Refused:
        raise Refused("readback-controls-time") from None
    if not approved <= observed <= now < expires:
        raise Refused("readback-controls-time")
    return observed


def _review(value: Any, selection_raw: bytes, selection_sha256: str,
            selected: dict[str, Any], packet: dict[str, Any],
            controls_observed_at: dt.datetime, now: dt.datetime) -> None:
    _exact(value, REVIEW_KEYS, "readback-selection-review-shape")
    if (value["schema"] != REVIEW_SCHEMA or value["complete"] is not True
            or value["selectionSha256"] != selection_sha256
            or type(value["bindings"]) is not dict
            or _canonical(value["bindings"]) != selection_raw
            or type(value["repository"]) is not str
            or value["repository"] != packet["source"]["repository"]
            or type(value["workflowPath"]) is not str
            or value["workflowPath"] != packet["workflow"]["path"]
            or any(type(value[key]) is not type(selected[key])
                   or value[key] != selected[key] for key in
                   ("sourceRevision", "runId", "runAttempt",
                    "environmentId", "actorId", "approvalId"))
            or type(value["reviewerId"]) is not int
            or value["reviewerId"] != selected["reviewerId"]
            or value["reviewerId"] == selected["actorId"]
            or value["reviewerMembership"] != "active"
            or type(value["reviewEventId"]) is not int
            or value["reviewEventId"] <= 0
            or value["reviewEventId"] == selected["approvalId"]):
        raise Refused("readback-selection-review-binding")
    try:
        reviewed = release._time(value["reviewedAt"])
        approved = release._time(packet["approval"]["approvedAt"])
        expires = release._time(packet["approval"]["expiresAt"])
    except release.Refused:
        raise Refused("readback-selection-review-time") from None
    if not approved <= controls_observed_at < reviewed <= now < expires:
        raise Refused("readback-selection-review-time")


def verify_selected_readback(selection_raw: bytes, selection_sha256: str,
                             packet_raw: bytes,
                             release_observer: release.ProtectedReleaseObserver,
                             approval_observer: release.ProtectedApprovalObserver,
                             controls_observer: InstalledControlObserver,
                             now: dt.datetime,
                             selection_review_observer: SelectionReviewObserver | None = None
                             ) -> ReadbackEvidence:
    """Require exact selected pins and four distinct observations; never authorize."""
    selected = _selection(selection_raw, selection_sha256)
    if (release_observer is None or approval_observer is None or controls_observer is None
            or len({id(release_observer), id(approval_observer), id(controls_observer)}) != 3):
        raise Refused("readback-observer-custody")
    if (selection_review_observer is None
            or id(selection_review_observer) in
            {id(release_observer), id(approval_observer), id(controls_observer)}):
        raise Refused("readback-selection-review-custody")
    try:
        prepared = release.verify_prepared_packet(
            packet_raw, selected["packetSha256"], release_observer, approval_observer, now)
    except release.Refused as error:
        raise Refused(f"readback-packet-{error}") from None
    packet = json.loads(packet_raw.decode("ascii"))  # already validated as canonical and unique
    bindings = {
        "sourceRevision": packet["source"]["revision"],
        "sourceTree": packet["source"]["tree"],
        "workflowSha256": packet["workflow"]["sha256"],
        "archiveSha256": packet["artifact"]["archiveSha256"],
        "manifestSha256": packet["artifact"]["manifestSha256"],
        "producerRunId": packet["artifact"]["producerRunId"],
        "artifactId": packet["artifact"]["artifactId"],
        "runnerImage": packet["runner"]["image"],
        "imageAttestationSha256": packet["runner"]["imageAttestationSha256"],
        "interpreterSha256": packet["runtime"]["interpreterSha256"],
        "closureManifestSha256": packet["runtime"]["closureManifestSha256"],
        "stdlibTreeSha256": packet["runtime"]["stdlibTreeSha256"],
        "runId": packet["run"]["runId"],
        "runAttempt": packet["run"]["runAttempt"],
        "environmentId": packet["run"]["environmentId"],
        "actorId": packet["run"]["actorId"],
        "approvalId": packet["approval"]["approvalId"],
        "reviewerId": packet["approval"]["reviewerId"],
    }
    if any(selected[key] != value for key, value in bindings.items()):
        raise Refused("readback-selected-binding")
    if prepared.authorized or prepared.can_dispatch or prepared.live_effects:
        raise Refused("readback-packet-authority")
    try:
        controls = controls_observer.read_controls()
    except Exception:
        raise Refused("readback-controls-unavailable") from None
    controls_observed_at = _controls(controls, selected, packet, now)
    try:
        review = selection_review_observer.read_review()
    except Exception:
        raise Refused("readback-selection-review-unavailable") from None
    _review(review, selection_raw, selection_sha256, selected, packet,
            controls_observed_at, now)
    return ReadbackEvidence(selected["sourceRevision"], selected["workflowSha256"],
                            selected["archiveSha256"], selected["runnerImage"],
                            selected["closureManifestSha256"])
