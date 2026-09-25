#!/usr/bin/env python3
"""Import-only GitHub merge Applied proof for the V1 provider adapter.

This module has no GitHub write, credential, retry, or StronglyAbsent path.
The caller must load the original pre-send record from a trusted durable
journal. A future Main-local adapter may map ``AppliedEvidence`` to #529's
ProviderAppliedReadback and ``verify_applied`` to its VerifyApplied callback.
That adapter, the protected issuer, and the exclusive base writer are not
installed by this source.
"""

from __future__ import annotations

import dataclasses
import hashlib
import importlib.util
import json
import pathlib
import re
import sys
from typing import Callable

SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-merge-readback.py")
SPEC = importlib.util.spec_from_file_location("v1_merge_readback_for_provider", SOURCE)
MERGE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MERGE
SPEC.loader.exec_module(MERGE)

HEX64 = re.compile(r"[0-9a-f]{64}\Z")
REQUEST_KEYS = frozenset({
    "version", "repository_id", "operation_id", "operation_generation",
    "effect_id", "attempt", "pr_number", "pr_id", "pr_node_id",
    "base_sha", "head_sha", "expected_tree_sha", "expected_commit_message",
    "method",
})
PRE_SEND_KEYS = frozenset(field.name for field in
                          dataclasses.fields(MERGE.PreSendObservation))
EVIDENCE_KEYS = frozenset({
    "version", "operation_id", "operation_generation", "effect_id",
    "attempt", "request_sha256", "pre_send_sha256", "merge_commit_sha",
    "tree_sha", "readback_sha256",
})


class Refused(RuntimeError):
    pass


@dataclasses.dataclass(frozen=True)
class ProviderRequestIdentity:
    operation_id: str
    operation_generation: int
    effect_id: str
    attempt: int
    request_sha256: str
    canonical_request_bytes: bytes


@dataclasses.dataclass(frozen=True)
class AppliedEvidence:
    identity: ProviderRequestIdentity
    response_sha256: str
    evidence_bytes: bytes


@dataclasses.dataclass(frozen=True)
class Unknown:
    reason: str


def _canonical(value: dict) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii") + b"\n"


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise Refused("duplicate-json-key")
        value[key] = item
    return value


def _parse(data: bytes, keys: frozenset[str], reason: str) -> dict:
    if type(data) is not bytes or len(data) > 16384:
        raise Refused(reason)
    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=_unique_object)
    except (UnicodeError, ValueError, TypeError) as error:
        raise Refused(reason) from error
    if type(value) is not dict or value.keys() != keys or _canonical(value) != data:
        raise Refused(reason)
    return value


def encode_pre_send(value: MERGE.PreSendObservation) -> bytes:
    """Canonical public bytes to persist before the one-shot provider send."""
    if type(value) is not MERGE.PreSendObservation:
        raise Refused("pre-send-type")
    return _canonical(dataclasses.asdict(value))


def _request(identity: ProviderRequestIdentity) -> MERGE.MergeRequestIdentity:
    if (type(identity) is not ProviderRequestIdentity
            or type(identity.canonical_request_bytes) is not bytes
            or type(identity.request_sha256) is not str
            or HEX64.fullmatch(identity.request_sha256) is None
            or hashlib.sha256(identity.canonical_request_bytes).hexdigest()
               != identity.request_sha256):
        raise Refused("provider-request-identity")
    value = _parse(identity.canonical_request_bytes, REQUEST_KEYS,
                   "provider-request-canonical")
    if (value["operation_id"] != identity.operation_id
            or value["operation_generation"] != identity.operation_generation
            or value["effect_id"] != identity.effect_id
            or value["attempt"] != identity.attempt):
        raise Refused("provider-request-binding")
    return MERGE.MergeRequestIdentity(
        identity.operation_id, identity.operation_generation,
        identity.effect_id, identity.attempt, value["pr_number"],
        value["pr_id"], value["pr_node_id"], value["base_sha"],
        value["head_sha"], value["expected_tree_sha"],
        value["expected_commit_message"], value["method"],
        identity.canonical_request_bytes, identity.request_sha256,
    )


def _pre_send(data: bytes) -> MERGE.PreSendObservation:
    value = _parse(data, PRE_SEND_KEYS, "pre-send-canonical")
    return MERGE.PreSendObservation(**value)


def _proof(read_json, policy: MERGE.RegisteredMergePolicy,
           identity: ProviderRequestIdentity,
           load_pre_send: Callable[[ProviderRequestIdentity], bytes]):
    request = _request(identity)
    stored_bytes = load_pre_send(identity)
    pre_send = _pre_send(stored_bytes)
    result = MERGE.reconcile(read_json, policy, request, pre_send)
    if type(result) is not MERGE.AppliedReadback:
        return None
    response = _canonical({
        "merge_commit_sha": result.merge_commit_sha,
        "tree_sha": result.tree_sha,
    })
    envelope = _canonical({
        "version": 1,
        "operation_id": identity.operation_id,
        "operation_generation": identity.operation_generation,
        "effect_id": identity.effect_id,
        "attempt": identity.attempt,
        "request_sha256": identity.request_sha256,
        "pre_send_sha256": hashlib.sha256(stored_bytes).hexdigest(),
        "merge_commit_sha": result.merge_commit_sha,
        "tree_sha": result.tree_sha,
        "readback_sha256": result.readback_sha256,
    })
    return hashlib.sha256(response).hexdigest(), envelope


def observe_applied(read_json, policy: MERGE.RegisteredMergePolicy,
                    identity: ProviderRequestIdentity,
                    load_pre_send: Callable[[ProviderRequestIdentity], bytes],
                    provider_response: object = None) -> AppliedEvidence | Unknown:
    """Classify only independent positive GitHub readback as Applied.

    The untrusted send response is deliberately ignored. Unknown never grants
    another attempt; GitHub has no strong exclusion proof for an ambiguous
    merge request here.
    """
    del provider_response
    try:
        result = _proof(read_json, policy, identity, load_pre_send)
        if result is None:
            return Unknown("merge-applied-unproven")
        response_sha256, evidence_bytes = result
        return AppliedEvidence(identity, response_sha256, evidence_bytes)
    except Exception:
        return Unknown("merge-applied-unavailable")


def verify_applied(read_json, policy: MERGE.RegisteredMergePolicy,
                   identity: ProviderRequestIdentity,
                   evidence: AppliedEvidence,
                   load_pre_send: Callable[[ProviderRequestIdentity], bytes]) -> bool:
    """Repeat complete native readback for #529's VerifyApplied callback."""
    try:
        if type(evidence) is not AppliedEvidence or evidence.identity != identity:
            return False
        value = _parse(evidence.evidence_bytes, EVIDENCE_KEYS,
                       "applied-evidence-canonical")
        if value["version"] != 1:
            return False
        fresh = _proof(read_json, policy, identity, load_pre_send)
        return fresh is not None and fresh == (
            evidence.response_sha256, evidence.evidence_bytes)
    except Exception:
        return False
