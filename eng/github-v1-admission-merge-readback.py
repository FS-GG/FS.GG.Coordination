#!/usr/bin/env python3
"""Import-only native readback of one pinned GitHub PR merge attempt.

This source has no merge or token path. Its positive result still needs the
typed admission plan, Main-local one-shot dispatch fence, exclusive protected
base writer, and provider adapter before it can settle an effect. An open PR,
read failure, or unbound commit never proves strong absence or permits retry.
"""

from __future__ import annotations

import dataclasses
import hashlib
import importlib.util
import json
import pathlib
import re
import sys
import urllib.parse

SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-job-native-read.py")
SPEC = importlib.util.spec_from_file_location("v1_merge_native_transport", SOURCE)
NATIVE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = NATIVE
SPEC.loader.exec_module(NATIVE)

PREFIX = NATIVE.PREFIX
REPOSITORY_ID = NATIVE.REPOSITORY_ID
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
IDENTIFIER = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}\Z")


class Refused(RuntimeError):
    pass


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


def _sha(value: object) -> bool:
    return isinstance(value, str) and HEX40.fullmatch(value) is not None


@dataclasses.dataclass(frozen=True)
class RegisteredMergePolicy:
    repository_id: int
    merge_actor_id: int
    method: str


@dataclasses.dataclass(frozen=True)
class MergeRequestIdentity:
    operation_id: str
    operation_generation: int
    effect_id: str
    attempt: int
    pr_number: int
    pr_id: int
    pr_node_id: str
    base_sha: str
    head_sha: str
    expected_tree_sha: str
    expected_commit_message: str
    method: str
    canonical_request_bytes: bytes
    request_sha256: str


@dataclasses.dataclass(frozen=True)
class PreSendObservation:
    request_sha256: str
    operation_id: str
    operation_generation: int
    effect_id: str
    attempt: int
    pr_number: int
    base_sha: str
    head_sha: str
    complete_census_sha256: str


@dataclasses.dataclass(frozen=True)
class AppliedReadback:
    merge_commit_sha: str
    tree_sha: str
    readback_sha256: str


@dataclasses.dataclass(frozen=True)
class UnknownReadback:
    reason: str


def _validate(policy: RegisteredMergePolicy, request: MergeRequestIdentity) -> None:
    require(isinstance(policy, RegisteredMergePolicy)
            and type(policy.repository_id) is int and policy.repository_id == REPOSITORY_ID
            and type(policy.merge_actor_id) is int and policy.merge_actor_id > 0
            and policy.method in ("squash", "merge"), "merge-policy")
    require(isinstance(request, MergeRequestIdentity)
            and isinstance(request.operation_id, str)
            and IDENTIFIER.fullmatch(request.operation_id) is not None
            and type(request.operation_generation) is int
            and request.operation_generation > 0
            and isinstance(request.effect_id, str)
            and IDENTIFIER.fullmatch(request.effect_id) is not None
            and type(request.attempt) is int and request.attempt == 1
            and type(request.pr_number) is int and request.pr_number > 0
            and type(request.pr_id) is int and request.pr_id > 0
            and isinstance(request.pr_node_id, str) and 0 < len(request.pr_node_id) <= 200
            and all(_sha(value) for value in
                    (request.base_sha, request.head_sha, request.expected_tree_sha))
            and isinstance(request.expected_commit_message, str)
            and 0 < len(request.expected_commit_message) <= 4096
            and "\x00" not in request.expected_commit_message
            and "\r" not in request.expected_commit_message
            and request.method == policy.method
            and isinstance(request.canonical_request_bytes, bytes)
            and isinstance(request.request_sha256, str)
            and HEX64.fullmatch(request.request_sha256) is not None,
            "merge-request-shape")
    core = {
        "version": 1, "repository_id": REPOSITORY_ID,
        "operation_id": request.operation_id,
        "operation_generation": request.operation_generation,
        "effect_id": request.effect_id, "attempt": request.attempt,
        "pr_number": request.pr_number, "pr_id": request.pr_id,
        "pr_node_id": request.pr_node_id,
        "base_sha": request.base_sha, "head_sha": request.head_sha,
        "expected_tree_sha": request.expected_tree_sha,
        "method": request.method,
    }
    core_bytes = json.dumps(core, sort_keys=True, separators=(",", ":"),
                            ensure_ascii=True).encode("ascii") + b"\n"
    marker = hashlib.sha256(core_bytes).hexdigest()
    require(request.expected_commit_message.endswith(
        f"\n\nFS-GG-V1-Effect: {marker}\n"), "merge-request-marker")
    public = dict(core, expected_commit_message=request.expected_commit_message)
    canonical = json.dumps(public, sort_keys=True, separators=(",", ":"),
                           ensure_ascii=True).encode("ascii") + b"\n"
    require(len(canonical) <= 8192
            and request.canonical_request_bytes == canonical
            and request.request_sha256 == hashlib.sha256(canonical).hexdigest(),
            "merge-request-bytes")


def _recorded(read_json, transcript: list, path: str):
    response = read_json(path)
    require(isinstance(response, NATIVE.NativeResponse) and response.link is None,
            "merge-native-response")
    try:
        encoded = json.dumps(response.body, sort_keys=True, separators=(",", ":")).encode()
    except (TypeError, ValueError) as error:
        raise Refused("merge-native-json") from error
    transcript.append((path, hashlib.sha256(encoded).hexdigest()))
    return response.body


def _digest(transcript: list) -> str:
    return hashlib.sha256(
        json.dumps(transcript, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def _pre_once(read_json, request: MergeRequestIdentity) -> str:
    transcript = []
    pr = _recorded(read_json, transcript, f"{PREFIX}/pulls/{request.pr_number}")
    require(isinstance(pr, dict)
            and type(pr.get("number")) is int and pr["number"] == request.pr_number
            and type(pr.get("id")) is int and pr["id"] == request.pr_id
            and pr.get("node_id") == request.pr_node_id
            and pr.get("state") == "open" and pr.get("draft") is False
            and pr.get("merged") is False
            and isinstance(pr.get("base"), dict)
            and pr["base"].get("ref") == "main"
            and pr["base"].get("sha") == request.base_sha
            and (pr["base"].get("repo") or {}).get("id") == REPOSITORY_ID
            and isinstance(pr.get("head"), dict)
            and pr["head"].get("sha") == request.head_sha
            and (pr["head"].get("repo") or {}).get("id") == REPOSITORY_ID
            and isinstance(pr["head"].get("ref"), str)
            and 0 < len(pr["head"]["ref"]) <= 200
            and pr["head"]["ref"] != "main"
            and not pr["head"]["ref"].startswith("refs/")
            and "\x00" not in pr["head"]["ref"], "merge-pre-pr")
    main = _recorded(read_json, transcript, f"{PREFIX}/branches/main")
    source_ref = urllib.parse.quote(pr["head"]["ref"], safe="")
    source = _recorded(read_json, transcript, f"{PREFIX}/branches/{source_ref}")
    require(isinstance(main, dict) and main.get("name") == "main"
            and main.get("protected") is True
            and (main.get("commit") or {}).get("sha") == request.base_sha
            and isinstance(source, dict)
            and source.get("name") == pr["head"]["ref"]
            and (source.get("commit") or {}).get("sha") == request.head_sha,
            "merge-pre-branches")
    return _digest(transcript)


def capture_pre_send(read_json, policy: RegisteredMergePolicy,
                     request: MergeRequestIdentity) -> PreSendObservation:
    """Pin two complete exact pre-send observations; this sends nothing."""
    _validate(policy, request)
    first = _pre_once(read_json, request)
    second = _pre_once(read_json, request)
    require(first == second, "merge-pre-drift")
    return PreSendObservation(
        request.request_sha256, request.operation_id, request.operation_generation,
        request.effect_id, request.attempt, request.pr_number,
        request.base_sha, request.head_sha, second,
    )


def _post_once(read_json, policy: RegisteredMergePolicy,
               request: MergeRequestIdentity):
    transcript = []
    pr = _recorded(read_json, transcript, f"{PREFIX}/pulls/{request.pr_number}")
    require(isinstance(pr, dict)
            and type(pr.get("number")) is int and pr["number"] == request.pr_number
            and type(pr.get("id")) is int and pr["id"] == request.pr_id
            and pr.get("node_id") == request.pr_node_id,
            "merge-post-pr-identity")
    if pr.get("merged") is not True:
        return UnknownReadback("merge-not-proven")
    require(pr.get("state") == "closed"
            and isinstance(pr.get("merged_at"), str) and bool(pr["merged_at"])
            and type((pr.get("merged_by") or {}).get("id")) is int
            and pr["merged_by"]["id"] == policy.merge_actor_id
            and isinstance(pr.get("base"), dict)
            and pr["base"].get("ref") == "main"
            and (pr["base"].get("repo") or {}).get("id") == REPOSITORY_ID
            and isinstance(pr.get("head"), dict)
            and pr["head"].get("sha") == request.head_sha
            and (pr["head"].get("repo") or {}).get("id") == REPOSITORY_ID
            and _sha(pr.get("merge_commit_sha")), "merge-post-pr")
    merge_sha = pr["merge_commit_sha"]
    commit = _recorded(read_json, transcript, f"{PREFIX}/git/commits/{merge_sha}")
    expected_parents = ([request.base_sha] if request.method == "squash"
                        else [request.base_sha, request.head_sha])
    require(isinstance(commit, dict) and commit.get("sha") == merge_sha
            and (commit.get("tree") or {}).get("sha") == request.expected_tree_sha
            and commit.get("message") == request.expected_commit_message
            and isinstance(commit.get("parents"), list)
            and [parent.get("sha") for parent in commit["parents"]
                 if isinstance(parent, dict)] == expected_parents
            and len(commit["parents"]) == len(expected_parents),
            "merge-post-commit")
    main = _recorded(read_json, transcript, f"{PREFIX}/branches/main")
    require(isinstance(main, dict) and main.get("name") == "main"
            and main.get("protected") is True
            and _sha((main.get("commit") or {}).get("sha")), "merge-post-main")
    main_sha = main["commit"]["sha"]
    comparison = _recorded(
        read_json, transcript, f"{PREFIX}/compare/{merge_sha}...{main_sha}")
    require(isinstance(comparison, dict)
            and comparison.get("status") in ("identical", "ahead")
            and (comparison.get("base_commit") or {}).get("sha") == merge_sha
            and (comparison.get("head_commit") or {}).get("sha") == main_sha
            and (comparison.get("merge_base_commit") or {}).get("sha") == merge_sha,
            "merge-post-ancestry")
    return AppliedReadback(merge_sha, request.expected_tree_sha, _digest(transcript))


def reconcile(read_json, policy: RegisteredMergePolicy,
              request: MergeRequestIdentity, pre_send: PreSendObservation,
              provider_response: object = None) -> AppliedReadback | UnknownReadback:
    """Use independent native readback, never an ambiguous provider response.

    `provider_response` is deliberately ignored. The caller must not dispatch
    another attempt from UnknownReadback; this source has no StronglyAbsent
    result and accepts only attempt one. Any malformed or unavailable native
    read also becomes unknown rather than a retry permit.
    """
    del provider_response
    try:
        _validate(policy, request)
        require(isinstance(pre_send, PreSendObservation)
                and pre_send.request_sha256 == request.request_sha256
                and pre_send.operation_id == request.operation_id
                and pre_send.operation_generation == request.operation_generation
                and pre_send.effect_id == request.effect_id
                and pre_send.attempt == request.attempt
                and pre_send.pr_number == request.pr_number
                and pre_send.base_sha == request.base_sha
                and pre_send.head_sha == request.head_sha
                and isinstance(pre_send.complete_census_sha256, str)
                and HEX64.fullmatch(pre_send.complete_census_sha256) is not None,
                "merge-pre-binding")
        first = _post_once(read_json, policy, request)
        second = _post_once(read_json, policy, request)
        if isinstance(first, AppliedReadback) and first == second:
            return second
        return UnknownReadback("merge-readback-unsettled")
    except (Refused, NATIVE.Refused, OSError, ValueError, TypeError, KeyError):
        return UnknownReadback("merge-readback-unavailable")
