"""Closed v5 one-attempt journal intent readback over an injected fake reader.

This is a source contract, not a journal writer, execution grant or dispatcher.
"""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import re
from typing import Any

import callable_isolated_v2_effect_candidate as candidate
from callable_isolated_v2_effect_v5_ports import JournalRecord, JournalReplayReadPort
from callable_isolated_v2_operation_plan_read_adapter import VerifiedRequest

SCHEMA = "fsgg.coordination.callable-isolated-v2-journal-intent-readback/1"
SELECTED_KEYS = {"repository", "ref", "path", "operationId", "grantSha256",
                 "targetSha256", "priorGeneration", "priorHead",
                 "committedGeneration", "committedHead", "writerPrincipalId",
                 "writerCredentialId", "readerPrincipalId",
                 "readerCredentialId"}
SCOPE_KEYS = {"principalId", "credentialId", "repository", "ref", "path",
              "permissions", "expiresAt"}
UTC = re.compile(r"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\Z")
REPOSITORY = re.compile(r"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
PATH = re.compile(r"\A[A-Za-z0-9][A-Za-z0-9._/-]*\.json\Z")


class Refused(ValueError):
    """Fixed refusal without intent, credential or backend contents."""


@dataclasses.dataclass(frozen=True)
class IntentReadback:
    operation_id: str
    committed_generation: int
    committed_head: str
    state: str = dataclasses.field(init=False, default="consistent-but-unadmitted")
    schema: str = dataclasses.field(init=False, default=SCHEMA)
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)


def _hex(value: Any, length: int) -> bool:
    return (type(value) is str and len(value) == length
            and re.fullmatch(r"[0-9a-f]+", value) is not None
            and any(char != "0" for char in value))


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _time(value: Any) -> dt.datetime:
    if type(value) is not str or UTC.fullmatch(value) is None:
        raise Refused("journal-time-shape")
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise Refused("journal-time-shape") from None


def _scope(port: JournalReplayReadPort, selected: dict[str, Any],
           now: dt.datetime) -> dict[str, Any]:
    try:
        scope = port.scope()
    except Exception:
        raise Refused("journal-scope-unavailable") from None
    if type(scope) is not dict or set(scope) != SCOPE_KEYS:
        raise Refused("journal-scope-shape")
    if (scope["principalId"] != selected["readerPrincipalId"]
            or scope["credentialId"] != selected["readerCredentialId"]
            or scope["repository"] != selected["repository"]
            or scope["ref"] != selected["ref"]
            or scope["path"] != selected["path"]
            or type(scope["permissions"]) is not list
            or scope["permissions"] != ["read-intent"]
            or _time(scope["expiresAt"]) <= now):
        raise Refused("journal-scope-binding")
    return copy.deepcopy(scope)


def _record(record: JournalRecord, selected: dict[str, Any],
            verified: VerifiedRequest) -> dict[str, Any]:
    if type(record) is not JournalRecord:
        raise Refused("journal-record-type")
    values = dataclasses.asdict(record)
    expected = {"operation_id": verified.operation_id,
                "grant_sha256": selected["grantSha256"],
                "target_sha256": selected["targetSha256"],
                "request_sha256": verified.request_sha256,
                "prior_generation": selected["priorGeneration"],
                "prior_head": selected["priorHead"],
                "committed_generation": selected["committedGeneration"],
                "committed_head": selected["committedHead"],
                "attempt_may_have_started": True}
    if any(type(values[key]) is not type(value) or values[key] != value
           for key, value in expected.items()):
        raise Refused("journal-intent-binding")
    return values


def observe(verified: VerifiedRequest, selection: dict[str, Any],
            writer_ack: JournalRecord | None, replay: JournalReplayReadPort,
            cas_outcome: str, now: dt.datetime) -> IntentReadback:
    """Inspect one acknowledged marker and distinct replay read; never dispatch."""
    if cas_outcome != "acknowledged" or type(cas_outcome) is not str:
        raise Refused("journal-cas-ambiguous")
    if (type(verified) is not VerifiedRequest or verified.authorized is not False
            or verified.can_dispatch is not False or verified.live_effects != 0
            or not _positive(verified.plan_record_id)
            or not _hex(verified.plan_sha256, 64)
            or not _hex(verified.operation_id, 64)
            or not _hex(verified.request_sha256, 64)
            or type(verified.canonical_request) is not bytes
            or hashlib.sha256(verified.canonical_request).hexdigest()
               != verified.request_sha256
            or replay is None or type(now) is not dt.datetime
            or now.tzinfo is None or now.utcoffset() != dt.timedelta(0)):
        raise Refused("journal-input-binding")
    if type(selection) is not dict or set(selection) != SELECTED_KEYS:
        raise Refused("journal-selection-shape")
    original = selection
    selected = copy.deepcopy(selection)
    if (type(selected["repository"]) is not str
            or REPOSITORY.fullmatch(selected["repository"]) is None
            or not candidate._branch(selected["ref"])
            or type(selected["path"]) is not str
            or PATH.fullmatch(selected["path"]) is None
            or any(part in ("", ".", "..") for part in
                   selected["path"].split("/"))
            or selected["operationId"] != verified.operation_id
            or not all(_hex(selected[key], 64) for key in
                       ("grantSha256", "targetSha256",
                        "writerCredentialId", "readerCredentialId"))
            or not all(_hex(selected[key], 40) for key in
                       ("priorHead", "committedHead"))
            or type(selected["priorGeneration"]) is not int
            or selected["priorGeneration"] < 0
            or type(selected["committedGeneration"]) is not int
            or selected["committedGeneration"] !=
               selected["priorGeneration"] + 1
            or selected["committedHead"] == selected["priorHead"]
            or type(selected["writerPrincipalId"]) is not str
            or not selected["writerPrincipalId"]
            or type(selected["readerPrincipalId"]) is not str
            or not selected["readerPrincipalId"]
            or selected["writerPrincipalId"] == selected["readerPrincipalId"]
            or selected["writerCredentialId"] == selected["readerCredentialId"]):
        raise Refused("journal-selection-binding")
    ack_values = _record(writer_ack, selected, verified)
    before = _scope(replay, selected, now)
    try:
        readback = replay.read_committed(verified.operation_id)
    except Exception:
        raise Refused("journal-read-unavailable") from None
    replay_values = _record(readback, selected, verified)
    if (replay_values != ack_values or _scope(replay, selected, now) != before
            or original != selected
            or dataclasses.asdict(writer_ack) != ack_values):
        raise Refused("journal-reader-or-intent-drift")
    return IntentReadback(verified.operation_id,
                          selected["committedGeneration"],
                          selected["committedHead"])
