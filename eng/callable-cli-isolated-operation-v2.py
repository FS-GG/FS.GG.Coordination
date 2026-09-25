#!/usr/bin/env python3
"""Import-only poststate proof for a prospective isolated-operation revision.

This is a new, inactive operator identity. It has no credential, HTTP, write,
retry, or CLI entrypoint. The original V2-CALL-01.4b operator and accepted
GS2-09.9 evidence remain immutable. A future governed contract and complete
native reader must bind these predicates before any effect is authorized.
"""

from __future__ import annotations

import argparse
import dataclasses
import hashlib
import json
import pathlib
import re
import sys
from typing import Callable

OPERATION_IDENTITY = "v2-call-01-4b-isolated-native-v2-provisional"
HISTORICAL_IDENTITY = "v2-call-01-4b-isolated-native-v1"
HISTORICAL_PREFLIGHT = "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json"
CONTROLS = "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py"
SOURCE = "eng/callable-cli-isolated-operation-v2.py"
ROOT = pathlib.Path(__file__).resolve().parents[1]
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")


class Refused(Exception):
    """A fixed, public refusal reason with no provider exception attached."""


@dataclasses.dataclass(frozen=True)
class ExpectedPull:
    operation_identity: str
    write_attempts: int
    repository_id: int
    repository: str
    source_ref: str
    source_sha: str
    base_ref: str
    base_sha: str


@dataclasses.dataclass(frozen=True)
class PullCensus:
    complete: bool
    repository_id: int
    source_branch_sha: str
    base_branch_sha: str
    pulls: tuple[dict, ...]
    transcript_sha256: str


@dataclasses.dataclass(frozen=True)
class ExactPull:
    number: int
    node_id: str
    census_sha256: str


@dataclasses.dataclass(frozen=True)
class ExpectedProtection:
    operation_identity: str
    write_attempts: int
    repository_id: int
    branch: str
    check_context: str
    check_app_id: int


@dataclasses.dataclass(frozen=True)
class ProtectionReadback:
    complete: bool
    repository_id: int
    branch: str
    protected: bool
    policy: dict
    transcript_sha256: str


@dataclasses.dataclass(frozen=True)
class ExactProtection:
    policy_sha256: str


@dataclasses.dataclass(frozen=True)
class Unknown:
    reason: str


def _shape(expected) -> bool:
    return (expected.operation_identity == OPERATION_IDENTITY
            and type(expected.write_attempts) is int
            and expected.write_attempts == 1
            and type(expected.repository_id) is int
            and expected.repository_id > 0)


def _oid(value: object) -> bool:
    return type(value) is str and HEX40.fullmatch(value) is not None


def _digest(value: object) -> str:
    raw = json.dumps(value, sort_keys=True, separators=(",", ":"),
                     ensure_ascii=True, allow_nan=False).encode("ascii")
    return hashlib.sha256(raw).hexdigest()


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _read_object(path: str) -> tuple[dict, bytes]:
    try:
        raw = pathlib.Path(path).read_bytes()
        value = json.loads(raw)
    except (OSError, UnicodeError, ValueError):
        raise Refused("inspect-input-unavailable") from None
    if type(value) is not dict:
        raise Refused("inspect-input-shape")
    return value, raw


def _sealed(value: dict, key: str) -> bool:
    supplied = value.get(key)
    return (_complete_digest(supplied)
            and supplied == _digest({name: part for name, part in value.items()
                                     if name != key}))


def inspect(contract_path: str, proposal_path: str, preflight_path: str) -> dict:
    """Inspect fixed historical evidence; this cannot authorize an effect."""
    contract, _ = _read_object(contract_path)
    proposal, _ = _read_object(proposal_path)
    preflight, preflight_raw = _read_object(preflight_path)
    if (contract.get("schema") != "fsgg.coordination.callable-isolated-operation-contract/5"
            or contract.get("identity") != OPERATION_IDENTITY
            or contract.get("state") != "prepared-not-authorized"
            or contract.get("authorized") is not False
            or not _sealed(contract, "contractSha256")):
        raise Refused("inspect-contract-invalid")
    source = contract.get("source")
    controls = contract.get("qualificationControls")
    if (type(source) is not dict or source.get("operationSource") != SOURCE
            or not _complete_digest(source.get("operationSourceSha256"))
            or type(controls) is not dict or controls.get("path") != CONTROLS
            or not _complete_digest(controls.get("sha256"))):
        raise Refused("inspect-source-binding")
    try:
        source_sha = _sha((ROOT / SOURCE).read_bytes())
        controls_sha = _sha((ROOT / CONTROLS).read_bytes())
    except OSError:
        raise Refused("inspect-source-unavailable") from None
    if (source_sha != source["operationSourceSha256"]
            or controls_sha != controls["sha256"]):
        raise Refused("inspect-source-drift")
    historical = contract.get("historicalPreflight")
    if (type(historical) is not dict
            or historical.get("path") != HISTORICAL_PREFLIGHT
            or not _complete_digest(historical.get("sha256"))
            or historical.get("operationIdentity") != HISTORICAL_IDENTITY
            or historical.get("authority") != "historical-observation-only"
            or historical["sha256"] != _sha(preflight_raw)
            or preflight.get("schema") != "fsgg.coordination.callable-isolated-operation-preflight/1"
            or preflight.get("operationIdentity") != HISTORICAL_IDENTITY
            or preflight.get("authorized") is not False
            or preflight.get("disposition") != "refused-no-compatible-admitted-target"
            or not _sealed(preflight, "evidenceSha256")):
        raise Refused("inspect-historical-preflight-invalid")
    if (proposal.get("schema") != "fsgg.coordination.callable-isolated-operation-proposal/5"
            or proposal.get("identity") != OPERATION_IDENTITY
            or proposal.get("state") != "prepared-not-authorized"
            or proposal.get("authorized") is not False
            or not _sealed(proposal, "proposalSha256")
            or proposal.get("historicalPreflight") != historical):
        raise Refused("inspect-proposal-invalid")
    binding = proposal.get("contract")
    if (type(binding) is not dict
            or binding.get("sha256") != contract["contractSha256"]
            or binding.get("operationSourceSha256") != source["operationSourceSha256"]):
        raise Refused("inspect-proposal-binding")
    return {
        "schema": "fsgg.coordination.callable-isolated-operation-inspection/2",
        "operationIdentity": OPERATION_IDENTITY,
        "contractSha256": contract["contractSha256"],
        "state": "prepared-not-authorized",
        "authorized": False,
        "disposition": "refused-no-compatible-admitted-target",
        "historicalObservationOnly": True,
        "liveEffects": 0,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--contract", required=True)
    parser.add_argument("--proposal", required=True)
    parser.add_argument("--preflight", required=True)
    parser.add_argument("action", choices=["inspect"])
    args = parser.parse_args(argv)
    try:
        result = inspect(args.contract, args.proposal, args.preflight)
    except Refused as error:
        print(str(error), file=sys.stderr)
        return 2
    except Exception:
        print("inspect-unavailable", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


def _complete_digest(value: object) -> bool:
    return type(value) is str and HEX64.fullmatch(value) is not None


def _two(read: Callable[[], object]):
    """An injected native reader supplies complete evidence; no write occurs."""
    try:
        first = read()
        first_snapshot = _digest(dataclasses.asdict(first))
        second = read()
        second_snapshot = _digest(dataclasses.asdict(second))
        if first_snapshot != second_snapshot:
            return None
        return second
    except Exception:
        # Never attach raw provider exceptions, response bodies, or credentials.
        return None


def classify_pull_after_one_attempt(
        expected: ExpectedPull,
        read: Callable[[], PullCensus],
        provider_response: object = None) -> ExactPull | Unknown:
    """Require one complete, coherent and exact PR poststate after attempt one.

    The ambiguous send response is ignored. A caller must durably prove its
    original one-attempt count and supply a qualified complete native reader.
    """
    del provider_response
    try:
        if (type(expected) is not ExpectedPull or not _shape(expected)
                or type(expected.repository) is not str
                or expected.repository.count("/") != 1
                or type(expected.source_ref) is not str
                or not expected.source_ref.startswith("refs/heads/")
                or type(expected.base_ref) is not str
                or not expected.base_ref.startswith("refs/heads/")
                or expected.source_ref == expected.base_ref
                or not _oid(expected.source_sha) or not _oid(expected.base_sha)):
            return Unknown("pull-request-identity-invalid")
        observed = _two(read)
        if (type(observed) is not PullCensus or observed.complete is not True
                or type(observed.repository_id) is not int
                or observed.repository_id != expected.repository_id
                or observed.source_branch_sha != expected.source_sha
                or observed.base_branch_sha != expected.base_sha
                or type(observed.pulls) is not tuple
                or len(observed.pulls) != 1
                or not _complete_digest(observed.transcript_sha256)):
            return Unknown("pull-request-readback-incomplete")
        pull = observed.pulls[0]
        head = pull.get("head") if type(pull) is dict else None
        base = pull.get("base") if type(pull) is dict else None
        if (type(head) is not dict or type(base) is not dict
                or type(pull.get("number")) is not int or pull["number"] <= 0
                or type(pull.get("node_id")) is not str or not pull["node_id"]
                or pull.get("state") != "open" or pull.get("draft") is not False
                or pull.get("merged") is not False
                or head.get("ref") != expected.source_ref.removeprefix("refs/heads/")
                or head.get("sha") != expected.source_sha
                or (head.get("repo") or {}).get("id") != expected.repository_id
                or (head.get("repo") or {}).get("full_name") != expected.repository
                or base.get("ref") != expected.base_ref.removeprefix("refs/heads/")
                or base.get("sha") != expected.base_sha
                or (base.get("repo") or {}).get("id") != expected.repository_id
                or (base.get("repo") or {}).get("full_name") != expected.repository):
            return Unknown("pull-request-readback-mismatch")
        return ExactPull(pull["number"], pull["node_id"], observed.transcript_sha256)
    except Exception:
        return Unknown("pull-request-readback-unavailable")


def _disabled(value: object) -> bool:
    return value is False or (type(value) is dict and value == {"enabled": False})


def classify_protection_after_one_attempt(
        expected: ExpectedProtection,
        read: Callable[[], ProtectionReadback],
        provider_response: object = None) -> ExactProtection | Unknown:
    """Require full protection readback, including disabled force pushes."""
    del provider_response
    try:
        if (type(expected) is not ExpectedProtection or not _shape(expected)
                or type(expected.branch) is not str or not expected.branch
                or type(expected.check_context) is not str or not expected.check_context
                or type(expected.check_app_id) is not int
                or expected.check_app_id <= 0):
            return Unknown("branch-protection-identity-invalid")
        observed = _two(read)
        if (type(observed) is not ProtectionReadback
                or observed.complete is not True
                or observed.protected is not True
                or type(observed.repository_id) is not int
                or observed.repository_id != expected.repository_id
                or observed.branch != expected.branch
                or type(observed.policy) is not dict
                or not _complete_digest(observed.transcript_sha256)):
            return Unknown("branch-protection-readback-incomplete")
        policy = observed.policy
        checks = policy.get("required_status_checks")
        entries = checks.get("checks") if type(checks) is dict else None
        if (type(checks) is not dict or checks.get("strict") is not True
                or type(entries) is not list or len(entries) != 1
                or type(entries[0]) is not dict
                or entries[0].get("context") != expected.check_context
                or type(entries[0].get("app_id")) is not int
                or entries[0]["app_id"] != expected.check_app_id
                or not _disabled(policy.get("enforce_admins"))
                or policy.get("required_pull_request_reviews") is not None
                or policy.get("restrictions") is not None
                or not _disabled(policy.get("allow_force_pushes"))
                or not _disabled(policy.get("allow_deletions"))):
            return Unknown("branch-protection-readback-mismatch")
        return ExactProtection(_digest({
            "operation_identity": expected.operation_identity,
            "repository_id": expected.repository_id,
            "branch": observed.branch,
            "policy": policy,
            "transcript_sha256": observed.transcript_sha256,
        }))
    except Exception:
        return Unknown("branch-protection-readback-unavailable")


if __name__ == "__main__":
    raise SystemExit(main())
