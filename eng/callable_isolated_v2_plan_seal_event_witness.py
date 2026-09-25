"""Closed, injected read-only witness for one selected immutable seal event.

No protected event store, credential, grant, journal or dispatch is supplied.
"""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import json
import re
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate
from callable_isolated_v2_operation_plan_read_adapter import VerifiedRequest

EVENT_SCHEMA = "fsgg.coordination.callable-isolated-v2-plan-seal-event/1"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-seal-event-witness/1"
SELECTED_KEYS = {"eventId", "eventSha256", "candidateSha256", "sourceTree",
                 "workflowRevision", "runId", "runAttempt", "reviewEventId",
                 "targetRepositoryId", "reviewedAt", "reviewExpiresAt",
                 "sealPrincipalId", "sealCredentialId", "otherPrincipals",
                 "otherCredentialIds"}
EVENT_KEYS = {"schema", "complete", "eventId", "eventType", "recordId",
              "planSha256", "operationId", "requestSha256", "candidateSha256",
              "sourceTree", "workflowRevision", "runId", "runAttempt",
              "reviewEventId", "targetRepositoryId", "producerPrincipalId",
              "producerCredentialId", "eventAt", "expiresAt"}
SCOPE_KEYS = {"principalId", "credentialId", "store", "permissions", "expiresAt"}
UTC = re.compile(r"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\Z")


class Refused(ValueError):
    """Fixed refusal without event or credential contents."""


class SealEventPort(Protocol):
    """Future independent immutable event read; fake implementations only."""

    def scope(self) -> dict[str, Any]: ...

    def read_event(self, event_id: int) -> bytes: ...


@dataclasses.dataclass(frozen=True)
class WitnessResult:
    event_id: int
    event_sha256: str
    plan_sha256: str
    request_sha256: str
    event_at: str
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


def _text(value: Any) -> bool:
    return type(value) is str and bool(value)


def _hex(value: Any, length: int) -> bool:
    return (type(value) is str and len(value) == length
            and re.fullmatch(r"[0-9a-f]+", value) is not None)


def _time(value: Any) -> dt.datetime:
    if type(value) is not str or UTC.fullmatch(value) is None:
        raise Refused("event-time-shape")
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise Refused("event-time-shape") from None


def _scope(port: SealEventPort, now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("event-scope-unavailable") from None
    value = _exact(value, SCOPE_KEYS, "event-scope-shape")
    if (not _text(value["principalId"])
            or not _hex(value["credentialId"], 64)
            or value["store"] != "coordination-protected-plan-seal-events"
            or type(value["permissions"]) is not list
            or value["permissions"] != ["read-seal-event"]
            or _time(value["expiresAt"]) <= now):
        raise Refused("event-scope-binding")
    return copy.deepcopy(value)


def observe(verified: VerifiedRequest, selected: dict[str, Any],
            port: SealEventPort, now: dt.datetime) -> WitnessResult:
    """Compare one selected event's bytes and independent reader scope; stay closed."""
    if (type(verified) is not VerifiedRequest or verified.authorized is not False
            or verified.can_dispatch is not False or verified.live_effects != 0
            or not _positive(verified.plan_record_id)
            or not _hex(verified.plan_sha256, 64)
            or not _hex(verified.operation_id, 64)
            or not _hex(verified.request_sha256, 64)
            or type(verified.canonical_request) is not bytes
            or hashlib.sha256(verified.canonical_request).hexdigest()
               != verified.request_sha256
            or port is None or type(now) is not dt.datetime
            or now.tzinfo is None or now.utcoffset() != dt.timedelta(0)):
        raise Refused("event-input-binding")
    original = _exact(selected, SELECTED_KEYS, "event-selection-shape")
    selected = copy.deepcopy(original)
    if (not all(_positive(selected[key]) for key in
                ("eventId", "runId", "runAttempt", "reviewEventId",
                 "targetRepositoryId"))
            or not all(_hex(selected[key], length) for key, length in
                       (("eventSha256", 64), ("candidateSha256", 64),
                        ("sourceTree", 40), ("workflowRevision", 40),
                        ("sealCredentialId", 64)))
            or not _text(selected["sealPrincipalId"])
            or type(selected["otherPrincipals"]) is not list
            or type(selected["otherCredentialIds"]) is not list
            or len(selected["otherPrincipals"]) != 5
            or len(selected["otherCredentialIds"]) != 5
            or not all(_text(v) for v in selected["otherPrincipals"])
            or not all(_hex(v, 64) for v in selected["otherCredentialIds"])
            or len(set(selected["otherPrincipals"] +
                        [selected["sealPrincipalId"]])) != 6
            or len(set(selected["otherCredentialIds"] +
                        [selected["sealCredentialId"]])) != 6):
        raise Refused("event-selection-binding")
    reviewed = _time(selected["reviewedAt"])
    review_expires = _time(selected["reviewExpiresAt"])
    if not reviewed <= now < review_expires:
        raise Refused("event-review-time")
    before = _scope(port, now)
    if (before["principalId"] in selected["otherPrincipals"] +
            [selected["sealPrincipalId"]]
            or before["credentialId"] in selected["otherCredentialIds"] +
            [selected["sealCredentialId"]]):
        raise Refused("event-reader-custody")
    try:
        raw = port.read_event(selected["eventId"])
    except Exception:
        raise Refused("event-read-unavailable") from None
    if type(raw) is not bytes or not 0 < len(raw) <= 8192:
        raise Refused("event-bytes-invalid")
    try:
        event = json.loads(raw.decode("utf-8"),
                           object_pairs_hook=candidate._unique,
                           parse_constant=candidate._no_constant)
        canonical = json.dumps(event, sort_keys=True, separators=(",", ":"),
                               ensure_ascii=True, allow_nan=False).encode("ascii")
    except (UnicodeError, ValueError, TypeError):
        raise Refused("event-json-invalid") from None
    if raw != canonical or hashlib.sha256(raw).hexdigest() != selected["eventSha256"]:
        raise Refused("event-bytes-binding")
    event = _exact(event, EVENT_KEYS, "event-shape")
    bindings = {"eventId": selected["eventId"],
                "recordId": verified.plan_record_id,
                "planSha256": verified.plan_sha256,
                "operationId": verified.operation_id,
                "requestSha256": verified.request_sha256,
                "candidateSha256": selected["candidateSha256"],
                "sourceTree": selected["sourceTree"],
                "workflowRevision": selected["workflowRevision"],
                "runId": selected["runId"],
                "runAttempt": selected["runAttempt"],
                "reviewEventId": selected["reviewEventId"],
                "targetRepositoryId": selected["targetRepositoryId"],
                "producerPrincipalId": selected["sealPrincipalId"],
                "producerCredentialId": selected["sealCredentialId"]}
    if (event["schema"] != EVENT_SCHEMA or event["complete"] is not True
            or event["eventType"] != "plan-sealed"
            or any(type(event[key]) is not type(value) or event[key] != value
                   for key, value in bindings.items())):
        raise Refused("event-binding")
    at, expires = _time(event["eventAt"]), _time(event["expiresAt"])
    if (not reviewed <= at <= now < expires <= review_expires
            or expires - at > dt.timedelta(minutes=30)):
        raise Refused("event-time-binding")
    if _scope(port, now) != before or original != selected:
        raise Refused("event-selection-or-reader-drift")
    return WitnessResult(event["eventId"], selected["eventSha256"],
                         verified.plan_sha256, verified.request_sha256,
                         event["eventAt"])
