"""Closed byte join for a proposed v5 native POST, plan, seal and intent.

No provider, token, grant, journal writer or installed entry exists here.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any

import callable_isolated_v2_effect_candidate as candidate
from callable_isolated_v2_effect_v5_ports import NativeCoordinates
from callable_isolated_v2_journal_intent_readback import IntentReadback
from callable_isolated_v2_operation_plan_read_adapter import TITLE, VerifiedRequest
from callable_isolated_v2_plan_seal_event_witness import WitnessResult

SCHEMA = "fsgg.coordination.callable-isolated-v2-native-request-custody/1"
TARGET_KEYS = {"repository", "repositoryId", "nodeId", "installationId",
               "sourceRef", "sourceSha", "baseRef", "baseSha",
               "prestateSha256"}


class Refused(ValueError):
    """Fixed refusal without plan, request or credential contents."""


@dataclasses.dataclass(frozen=True)
class RequestCustody:
    operation_id: str
    request_sha256: str
    seal_event_id: int
    committed_generation: int
    state: str = dataclasses.field(init=False,
                                   default="request-consistent-but-unadmitted")
    schema: str = dataclasses.field(init=False, default=SCHEMA)
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)


def _canonical(value: Any) -> bytes:
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("request-json-invalid") from None


def _hex(value: Any, pattern) -> bool:
    return candidate._hex(value, pattern)


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _time(value: Any) -> dt.datetime:
    try:
        return candidate._time(value)
    except candidate.Refused:
        raise Refused("request-time-invalid") from None


def qualify(verified: VerifiedRequest, seal: WitnessResult,
            intent: IntentReadback, native: NativeCoordinates,
            target: dict[str, Any], selected_event_id: int,
            selected_event_sha256: str,
            selected_intent_generation: int,
            selected_intent_head: str,
            now: dt.datetime) -> RequestCustody:
    """Compare exact held request bytes and one-attempt coordinates; stay closed."""
    if (type(verified) is not VerifiedRequest
            or type(seal) is not WitnessResult
            or type(intent) is not IntentReadback
            or type(native) is not NativeCoordinates
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)
            or not _positive(selected_event_id)
            or not _hex(selected_event_sha256, candidate.HEX64)
            or not _positive(selected_intent_generation)
            or not _hex(selected_intent_head, candidate.HEX40)
            or not all(value.authorized is False
                       and value.can_dispatch is False
                       and type(value.live_effects) is int
                       and value.live_effects == 0
                       for value in (verified, seal, intent))
            or not _positive(verified.plan_record_id)
            or not _hex(verified.plan_sha256, candidate.HEX64)
            or not _hex(verified.operation_id, candidate.HEX64)
            or not _hex(verified.request_sha256, candidate.HEX64)
            or type(verified.canonical_request) is not bytes
            or hashlib.sha256(verified.canonical_request).hexdigest()
               != verified.request_sha256):
        raise Refused("request-input-binding")
    if (seal.event_id != selected_event_id
            or seal.event_sha256 != selected_event_sha256
            or seal.plan_sha256 != verified.plan_sha256
            or seal.request_sha256 != verified.request_sha256
            or _time(seal.event_at) > now):
        raise Refused("request-seal-binding")
    if type(target) is not dict or set(target) != TARGET_KEYS:
        raise Refused("request-target-shape")
    if (type(target["repository"]) is not str
            or candidate.REPOSITORY.fullmatch(target["repository"]) is None
            or target["repository"] == "FS-GG/.github"
            or not _positive(target["repositoryId"])
            or type(target["nodeId"]) is not str or not target["nodeId"]
            or not _positive(target["installationId"])
            or not candidate._branch(target["sourceRef"])
            or not candidate._branch(target["baseRef"])
            or target["sourceRef"] == target["baseRef"]
            or not _hex(target["sourceSha"], candidate.HEX40)
            or not _hex(target["baseSha"], candidate.HEX40)
            or not _hex(target["prestateSha256"], candidate.HEX64)):
        raise Refused("request-target-binding")
    target_sha = hashlib.sha256(_canonical(target)).hexdigest()
    if (intent.operation_id != verified.operation_id
            or intent.request_sha256 != verified.request_sha256
            or intent.target_sha256 != target_sha
            or intent.grant_sha256 != native.grant_sha256
            or intent.repository != native.journal_repository
            or intent.ref != native.journal_ref
            or intent.path != native.journal_path
            or intent.prior_generation != native.journal_prior_generation
            or intent.prior_head != native.journal_prior_head
            or type(intent.committed_generation) is not int
            or intent.committed_generation != intent.prior_generation + 1
            or intent.committed_generation != selected_intent_generation
            or intent.committed_head != selected_intent_head
            or not _hex(intent.prior_head, candidate.HEX40)):
        raise Refused("request-journal-binding")
    expected_target = {
        "repository": native.repository,
        "repositoryId": native.repository_id,
        "nodeId": native.repository_node_id,
        "installationId": native.installation_id,
        "sourceRef": native.source_ref,
        "sourceSha": native.source_sha,
        "baseRef": native.base_ref,
        "baseSha": native.base_sha,
        "prestateSha256": native.prestate_sha256}
    if (expected_target != target
            or native.operation_identity != candidate.IDENTITY
            or native.operation_id != verified.operation_id
            or native.method != "POST"
            or native.path != f"repos/{target['repository']}/pulls"
            or type(native.max_provider_writes) is not int
            or native.max_provider_writes != 1
            or native.request_sha256 != verified.request_sha256
            or type(native.canonical_request) is not bytes
            or native.canonical_request != verified.canonical_request
            or not _hex(native.setup_receipt_sha256, candidate.HEX64)
            or not _hex(native.grant_sha256, candidate.HEX64)
            or not all(_positive(value) for value in
                       (native.run_id, native.run_attempt,
                        native.environment_id, native.dispatch_actor_id,
                        native.reviewer_actor_id, native.approval_event_id,
                        native.issuer_actor_id))
            or len({native.dispatch_actor_id, native.reviewer_actor_id,
                    native.issuer_actor_id}) != 3
            or _time(native.grant_expires_at) <= now
            or _time(native.grant_expires_at) - _time(seal.event_at)
               > dt.timedelta(minutes=30)):
        raise Refused("request-native-binding")
    core = {"operation_identity": candidate.IDENTITY, "write_attempts": 1,
            "repository_id": target["repositoryId"],
            "repository": target["repository"],
            "source_ref": target["sourceRef"],
            "source_sha": target["sourceSha"],
            "base_ref": target["baseRef"],
            "base_sha": target["baseSha"]}
    marker = hashlib.sha256(_canonical({"effect": "create-pull",
                                        "intent": core})).hexdigest()
    body = {"title": TITLE,
            "head": target["sourceRef"].removeprefix("refs/heads/"),
            "base": target["baseRef"].removeprefix("refs/heads/"),
            "body": f"FS-GG-Effect: {marker}"}
    if verified.canonical_request != _canonical(body):
        raise Refused("request-body-binding")
    return RequestCustody(verified.operation_id,
                          verified.request_sha256,
                          seal.event_id,
                          intent.committed_generation)
