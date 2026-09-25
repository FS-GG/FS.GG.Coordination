#!/usr/bin/env python3
"""Import-only public proof join for a future Main-host OperatingV1 issuer.

The sealed bytes are a public lookup envelope, not an admission decision. This
joins two complete native censuses to signed job OIDC and exact envelope bytes.
It does not validate the typed admission plan, consume a nonce, access a key,
mint a token, run CAS, or invoke a provider effect.
"""

from __future__ import annotations

import dataclasses
import hashlib
import importlib.util
import json
import pathlib
import re
import sys


def _sibling(name: str, module_name: str):
    spec = importlib.util.spec_from_file_location(module_name, pathlib.Path(__file__).with_name(name))
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


JOB = _sibling("github-v1-admission-job-native-read.py", "v1_preflight_job")
TARGET = _sibling("github-v1-admission-pr-target-read.py", "v1_preflight_target")
OIDC = _sibling("github-v1-admission-oidc-proof.py", "v1_preflight_oidc")
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
HEX32 = re.compile(r"[0-9a-f]{32}\Z")
PLAN_KEYS = {
    "version", "kind", "repository_id", "pr_number", "pr_id", "pr_node_id",
    "base_sha", "head_sha", "run_id", "check_run_id", "nonce",
    "typed_admission_sha256",
}


class Refused(RuntimeError):
    pass


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "public-plan-duplicate")
        result[key] = value
    return result


def _decode_plan(sealed: bytes) -> dict:
    require(isinstance(sealed, bytes) and 0 < len(sealed) <= 65536,
            "public-plan-size")
    try:
        plan = json.loads(
            sealed, object_pairs_hook=_unique_pairs,
            parse_constant=lambda _: (_ for _ in ()).throw(Refused("public-plan-constant")),
        )
    except (UnicodeError, ValueError, TypeError) as error:
        raise Refused("public-plan-json") from error
    require(isinstance(plan, dict) and set(plan) == PLAN_KEYS
            and sealed == json.dumps(plan, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=True).encode("ascii") + b"\n",
            "public-plan-canonical")
    require(type(plan["version"]) is int and plan["version"] == 1
            and plan["kind"] == "github_pr_merge"
            and type(plan["repository_id"]) is int
            and plan["repository_id"] == JOB.REPOSITORY_ID
            and all(type(plan[key]) is int and plan[key] > 0
                    for key in ("pr_number", "pr_id", "run_id", "check_run_id"))
            and isinstance(plan["pr_node_id"], str) and bool(plan["pr_node_id"])
            and all(isinstance(plan[key], str) and HEX40.fullmatch(plan[key])
                    for key in ("base_sha", "head_sha"))
            and isinstance(plan["nonce"], str) and HEX32.fullmatch(plan["nonce"])
            and isinstance(plan["typed_admission_sha256"], str)
            and HEX64.fullmatch(plan["typed_admission_sha256"]),
            "public-plan-shape")
    return plan


@dataclasses.dataclass(frozen=True)
class PublicPreflight:
    sealed_plan_sha256: str
    typed_admission_sha256: str
    nonce: str
    token_id: str
    run_id: int
    check_run_id: int
    pr_number: int
    pr_id: int
    base_sha: str
    head_sha: str
    job_census_sha256: str
    target_census_sha256: str
    signed_payload_sha256: str


def verify_public_preflight(
    sealed: bytes,
    typed_admission: bytes,
    compact_oidc: str,
    jwks_bytes: bytes,
    job_policy: JOB.InstalledPolicy,
    registered: OIDC.RegisteredServicePolicy,
    read_json,
    now: int,
) -> PublicPreflight:
    """Join public proofs; Main must independently validate typed admission.

    Both policy arguments must come from protected Main configuration. The
    native reader and JWKS fetch must be Main-controlled; caller data supplies
    only sealed bytes, signed OIDC and lookup hints inside the sealed envelope.
    """
    plan = _decode_plan(sealed)
    require(isinstance(typed_admission, bytes)
            and 0 < len(typed_admission) <= 65536
            and hashlib.sha256(typed_admission).hexdigest() ==
            plan["typed_admission_sha256"], "public-plan-typed-bytes")
    require(isinstance(job_policy, JOB.InstalledPolicy)
            and isinstance(registered, OIDC.RegisteredServicePolicy)
            and job_policy.workflow_path == registered.workflow_path
            and job_policy.workflow_sha == registered.workflow_sha
            and job_policy.actor_id == registered.actor_id
            and job_policy.environment_name == registered.environment_name
            and job_policy.environment_node_id == registered.environment_node_id,
            "public-plan-installed-policy")
    # Read both inventories in each pass. Sequential independent two-read
    # calls could accept a job that changed before the target census began.
    first_job = JOB.collect_once(read_json, job_policy,
                                 plan["run_id"], plan["check_run_id"])
    first_target = TARGET.collect_once(read_json, plan["pr_number"])
    job = JOB.collect_once(read_json, job_policy,
                           plan["run_id"], plan["check_run_id"])
    target = TARGET.collect_once(read_json, plan["pr_number"])
    require(first_job == job and first_target == target,
            "public-plan-two-read-drift")
    require(all(target[key] == plan[key] for key in
                ("repository_id", "pr_number", "pr_id", "pr_node_id",
                 "base_sha", "head_sha")), "public-plan-target-drift")
    native = OIDC.NativeJobIdentity(**{
        key: job[key] for key in
        ("workflow_path", "run_head", "workflow_sha", "run_id", "run_attempt",
         "actor_id", "check_run_id", "environment_name", "environment_node_id")
    })
    signed = OIDC.verify_signed_job(
        compact_oidc, jwks_bytes, native, registered,
        OIDC.plan_audience(sealed, plan["nonce"]), now,
    )
    return PublicPreflight(
        sealed_plan_sha256=hashlib.sha256(sealed).hexdigest(),
        typed_admission_sha256=plan["typed_admission_sha256"],
        nonce=plan["nonce"], token_id=signed.token_id,
        run_id=job["run_id"], check_run_id=job["check_run_id"],
        pr_number=target["pr_number"], pr_id=target["pr_id"],
        base_sha=target["base_sha"], head_sha=target["head_sha"],
        job_census_sha256=job["complete_census_sha256"],
        target_census_sha256=target["complete_census_sha256"],
        signed_payload_sha256=signed.payload_sha256,
    )
