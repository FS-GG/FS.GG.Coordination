#!/usr/bin/env python3
"""Import-only Main target gate from complete native GitHub evidence.

The fixed Main caller supplies installed policy, not request-selected rules.
This reader requires an exclusive writer ruleset, exact required check App IDs,
current head checks and test-merge checks when the test merge has any check or
status evidence, and fresh review history. It refuses a
conversation-resolution requirement until a qualified complete thread reader
exists. Inherited rulesets with repository selectors also refuse until a
qualified selector interpreter proves their exact application. A positive
public result is not typed admission or merge authority.
"""

from __future__ import annotations

import dataclasses
import datetime
import hashlib
import importlib.util
import json
import pathlib
import re
import sys
import urllib.parse

SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-pr-target-read.py")
SPEC = importlib.util.spec_from_file_location("v1_eligibility_target_transport", SOURCE)
TARGET = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = TARGET
SPEC.loader.exec_module(TARGET)
NATIVE = TARGET.NATIVE

PREFIX = NATIVE.PREFIX
REPOSITORY_ID = NATIVE.REPOSITORY_ID
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
CHECK_AGE_SECONDS = 7 * 24 * 60 * 60


class Refused(RuntimeError):
    pass


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


def _sha(value) -> bool:
    return isinstance(value, str) and HEX40.fullmatch(value) is not None


def _digest(value) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True,
                                     separators=(",", ":")).encode()).hexdigest()


def _time(value: object) -> int:
    require(isinstance(value, str) and value.endswith("Z"), "eligibility-time")
    try:
        parsed = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise Refused("eligibility-time") from error
    require(parsed.tzinfo is not None and parsed.utcoffset().total_seconds() == 0,
            "eligibility-time")
    return int(parsed.timestamp())


@dataclasses.dataclass(frozen=True)
class InstalledTargetPolicy:
    repository_id: int
    writer_ruleset_id: int
    writer_app_id: int
    protection_sha256: str
    rules_sha256: str
    required_checks: tuple[tuple[str, int], ...]
    required_reviewer_ids: tuple[int, ...]


def _validate_policy(policy: InstalledTargetPolicy) -> None:
    require(isinstance(policy, InstalledTargetPolicy)
            and type(policy.repository_id) is int and policy.repository_id == REPOSITORY_ID
            and type(policy.writer_ruleset_id) is int and policy.writer_ruleset_id > 0
            and type(policy.writer_app_id) is int and policy.writer_app_id > 0
            and all(isinstance(value, str) and HEX64.fullmatch(value)
                    for value in (policy.protection_sha256, policy.rules_sha256))
            and isinstance(policy.required_checks, tuple)
            and 0 < len(policy.required_checks) <= 32
            and all(isinstance(pair, tuple) and len(pair) == 2
                    and isinstance(pair[0], str) and 0 < len(pair[0]) <= 200
                    and type(pair[1]) is int and pair[1] > 0
                    for pair in policy.required_checks)
            and len({name for name, _ in policy.required_checks}) == len(policy.required_checks)
            and isinstance(policy.required_reviewer_ids, tuple)
            and all(type(value) is int and value > 0 for value in policy.required_reviewer_ids)
            and len(set(policy.required_reviewer_ids)) == len(policy.required_reviewer_ids),
            "eligibility-installed-policy")


def _recorded(read_json, transcript: list, path: str) -> NATIVE.NativeResponse:
    response = read_json(path)
    require(isinstance(response, NATIVE.NativeResponse), "eligibility-native-response")
    transcript.append((path, _digest(response.body), response.link))
    return response


def _body(read_json, transcript: list, path: str):
    response = _recorded(read_json, transcript, path)
    require(response.link is None, "eligibility-unexpected-link")
    return response.body


def _pages(read_json, transcript: list, path: str, key: str | None = None) -> list:
    return NATIVE._pages(lambda value: _recorded(read_json, transcript, value), path, key)


def _rule_applies_main(rule: dict) -> bool:
    conditions = rule.get("conditions")
    # Organization/enterprise repository selectors are not interpreted here.
    # A protected probe and closed selector interpreter must qualify them.
    require(isinstance(conditions, dict)
            and set(conditions) == {"ref_name"}
            and isinstance(conditions["ref_name"], dict)
            and set(conditions["ref_name"]) == {"include", "exclude"}
            and isinstance(conditions["ref_name"]["include"], list)
            and isinstance(conditions["ref_name"]["exclude"], list),
            "eligibility-rule-conditions")
    include = conditions["ref_name"]["include"]
    exclude = conditions["ref_name"]["exclude"]
    require(all(isinstance(value, str) for value in include + exclude)
            and len(include) > 0 and exclude == [], "eligibility-rule-conditions")
    # Ref patterns other than exact refs or ~ALL need a qualified matcher.
    require(all(value == "~ALL" or
                (value.startswith("refs/heads/") and "*" not in value and "?" not in value)
                for value in include), "eligibility-rule-pattern")
    return "~ALL" in include or "refs/heads/main" in include


def _policy_evidence(read_json, transcript: list,
                     policy: InstalledTargetPolicy) -> tuple[dict, list]:
    protection = _body(read_json, transcript, f"{PREFIX}/branches/main/protection")
    require(isinstance(protection, dict)
            and _digest(protection) == policy.protection_sha256
            and (protection.get("enforce_admins") or {}).get("enabled") is True
            and (protection.get("required_conversation_resolution") or {}).get("enabled") is False,
            "eligibility-protection")
    checks = (protection.get("required_status_checks") or {}).get("checks")
    require(isinstance(checks, list)
            and sorted((item.get("context"), item.get("app_id")) for item in checks
                       if isinstance(item, dict)) == sorted(policy.required_checks)
            and len(checks) == len(policy.required_checks),
            "eligibility-required-check-policy")
    reviews = protection.get("required_pull_request_reviews")
    require(reviews is None or
            (isinstance(reviews, dict)
             and type(reviews.get("required_approving_review_count")) is int
             and reviews["required_approving_review_count"] == len(policy.required_reviewer_ids)
             and reviews.get("dismiss_stale_reviews") is True
             and reviews.get("require_code_owner_reviews") is False
             and reviews.get("require_last_push_approval") is False),
            "eligibility-review-policy")
    require((reviews is None) == (len(policy.required_reviewer_ids) == 0),
            "eligibility-review-policy")
    effective = _body(read_json, transcript, f"{PREFIX}/rules/branches/main")
    require(isinstance(effective, list)
            and 0 < len(effective) <= 32
            and all(isinstance(item, dict) for item in effective),
            "eligibility-effective-rules")
    summaries = _pages(read_json, transcript,
                       f"{PREFIX}/rulesets?includes_parents=true&per_page=100")
    require(0 < len(summaries) <= 32
            and all(isinstance(item, dict) and type(item.get("id")) is int
                    for item in summaries)
            and len({item["id"] for item in summaries}) == len(summaries),
            "eligibility-ruleset-census")
    details = []
    for summary in summaries:
        detail = _body(read_json, transcript, f"{PREFIX}/rulesets/{summary['id']}")
        require(isinstance(detail, dict) and detail.get("id") == summary["id"]
                and detail.get("target") == summary.get("target")
                and detail.get("enforcement") == summary.get("enforcement"),
                "eligibility-ruleset-detail")
        details.append(detail)
    rule_state = {"effective": effective, "summaries": summaries, "details": details}
    require(_digest(rule_state) == policy.rules_sha256,
            "eligibility-rule-drift")
    active_main = [detail for detail in details
                   if detail.get("target") == "branch"
                   and detail.get("enforcement") == "active"
                   and _rule_applies_main(detail)]
    writer = [detail for detail in active_main
              if detail.get("id") == policy.writer_ruleset_id]
    require(len(writer) == 1 and writer[0].get("rules") == [{"type": "update"}]
            and writer[0].get("bypass_actors") == [{
                "actor_id": policy.writer_app_id,
                "actor_type": "Integration",
                "bypass_mode": "pull_request",
            }], "eligibility-exclusive-writer")
    require(all(detail.get("bypass_actors") == []
                for detail in active_main if detail["id"] != policy.writer_ruleset_id),
            "eligibility-other-bypass")
    require(any(item.get("type") == "update"
                and item.get("ruleset_id") == policy.writer_ruleset_id
                for item in effective), "eligibility-effective-writer")
    require(all(item.get("type") in ("update",)
                for item in effective), "eligibility-unsupported-effective-rule")
    return protection, active_main


def _check_commit(read_json, transcript: list, commit_sha: str,
                  policy: InstalledTargetPolicy, after: int, now: int,
                  allow_empty: bool = False) -> bool:
    checks = TARGET.complete_check_runs(
        lambda path: _recorded(read_json, transcript, path), commit_sha)
    statuses = _pages(read_json, transcript,
                      f"{PREFIX}/commits/{commit_sha}/statuses?per_page=100")
    require(all(isinstance(item, dict) for item in checks + statuses),
            "eligibility-check-census")
    if allow_empty and not checks and not statuses:
        # GitHub evaluates the head's checks when its synthetic test merge has
        # no check or status evidence. Complete suite/status reads prove empty.
        return False
    for context, app_id in policy.required_checks:
        matching = [item for item in checks if item.get("name") == context]
        require(len(matching) > 0 and all(type(item.get("id")) is int
                                          and item.get("head_sha") == commit_sha
                                          and isinstance(item.get("app"), dict)
                                          for item in matching), "eligibility-check-shape")
        require(all(item.get("status") == "completed"
                    and item.get("completed_at") is not None
                    for item in matching), "eligibility-check-in-progress")
        selected = sorted(matching, key=lambda item: (_time(item["completed_at"]), item["id"]))[-1]
        completed = _time(selected["completed_at"])
        require(selected["app"].get("id") == app_id
                and selected.get("conclusion") == "success"
                and after <= completed <= now
                and now - completed <= CHECK_AGE_SECONDS,
                "eligibility-check-result")
        # If a commit status shares a required check-run name, GitHub requires
        # both. Until its producer identity is independently pinned, refuse.
        require(not any(item.get("context") == context for item in statuses),
                "eligibility-overlapping-status")
    return True


def _reviews(read_json, transcript: list, policy: InstalledTargetPolicy,
             pr_number: int, head_sha: str, author_id: int, after: int, now: int) -> None:
    require(author_id not in policy.required_reviewer_ids,
            "eligibility-self-review")
    reviews = _pages(read_json, transcript,
                     f"{PREFIX}/pulls/{pr_number}/reviews?per_page=100")
    require(all(isinstance(item, dict) and type(item.get("id")) is int
                and isinstance(item.get("user"), dict)
                and type(item["user"].get("id")) is int
                and item.get("state") in
                ("APPROVED", "COMMENTED", "CHANGES_REQUESTED", "DISMISSED")
                and _sha(item.get("commit_id"))
                and isinstance(item.get("submitted_at"), str)
                and _time(item["submitted_at"]) <= now
                for item in reviews), "eligibility-review-history")
    require(len({item["id"] for item in reviews}) == len(reviews),
            "eligibility-review-duplicate")
    latest = {}
    for item in reviews:
        reviewer = item["user"]["id"]
        order = (_time(item["submitted_at"]), item["id"])
        if reviewer not in latest or order > latest[reviewer][0]:
            latest[reviewer] = (order, item)
    require(all(item[1]["state"] != "CHANGES_REQUESTED"
                for item in latest.values()), "eligibility-review-blocking")
    require(all(reviewer in latest
                and latest[reviewer][1]["state"] == "APPROVED"
                and latest[reviewer][1]["commit_id"] == head_sha
                and after <= latest[reviewer][0][0]
                for reviewer in policy.required_reviewer_ids),
            "eligibility-review-approval")


def collect_once(read_json, policy: InstalledTargetPolicy,
                 pr_number: int, now: int) -> dict:
    """One complete read; positive result is public evidence only."""
    _validate_policy(policy)
    require(type(pr_number) is int and pr_number > 0
            and type(now) is int and now > 0, "eligibility-lookup")
    transcript = []
    pr = _body(read_json, transcript, f"{PREFIX}/pulls/{pr_number}")
    main = _body(read_json, transcript, f"{PREFIX}/branches/main")
    require(isinstance(pr, dict) and type(pr.get("number")) is int
            and pr["number"] == pr_number
            and type(pr.get("id")) is int and pr["id"] > 0
            and isinstance(pr.get("node_id"), str) and bool(pr["node_id"])
            and isinstance(pr.get("user"), dict)
            and type(pr["user"].get("id")) is int and pr["user"]["id"] > 0
            and pr.get("state") == "open" and pr.get("draft") is False
            and pr.get("merged") is False
            and isinstance(pr.get("base"), dict)
            and pr["base"].get("ref") == "main"
            and _sha(pr["base"].get("sha"))
            and (pr["base"].get("repo") or {}).get("id") == REPOSITORY_ID
            and isinstance(pr.get("head"), dict)
            and _sha(pr["head"].get("sha"))
            and isinstance(pr["head"].get("ref"), str)
            and 0 < len(pr["head"]["ref"]) <= 200
            and pr["head"]["ref"] != "main"
            and not pr["head"]["ref"].startswith("refs/")
            and (pr["head"].get("repo") or {}).get("id") == REPOSITORY_ID
            and _sha(pr.get("merge_commit_sha"))
            and isinstance(main, dict) and main.get("name") == "main"
            and main.get("protected") is True
            and (main.get("commit") or {}).get("sha") == pr["base"]["sha"],
            "eligibility-pr-target")
    head_sha = pr["head"]["sha"]
    base_sha = pr["base"]["sha"]
    test_merge_sha = pr["merge_commit_sha"]
    source_ref = urllib.parse.quote(pr["head"]["ref"], safe="")
    source = _body(read_json, transcript, f"{PREFIX}/branches/{source_ref}")
    require(isinstance(source, dict) and source.get("name") == pr["head"]["ref"]
            and (source.get("commit") or {}).get("sha") == head_sha,
            "eligibility-source-branch")
    test_merge = _body(read_json, transcript,
                       f"{PREFIX}/git/commits/{test_merge_sha}")
    require(isinstance(test_merge, dict)
            and test_merge.get("sha") == test_merge_sha
            and isinstance(test_merge.get("parents"), list)
            and [item.get("sha") for item in test_merge["parents"]
                 if isinstance(item, dict)] == [base_sha, head_sha]
            and len(test_merge["parents"]) == 2,
            "eligibility-test-merge")
    head_commit = _body(read_json, transcript, f"{PREFIX}/git/commits/{head_sha}")
    require(isinstance(head_commit, dict) and head_commit.get("sha") == head_sha
            and isinstance(head_commit.get("committer"), dict),
            "eligibility-head-commit")
    head_time = _time(head_commit["committer"].get("date"))
    require(head_time <= now, "eligibility-head-time")
    _policy_evidence(read_json, transcript, policy)
    _check_commit(read_json, transcript, head_sha, policy, head_time, now)
    test_merge_has_checks = _check_commit(
        read_json, transcript, test_merge_sha, policy, head_time, now,
        allow_empty=True)
    _reviews(read_json, transcript, policy, pr_number, head_sha,
             pr["user"]["id"], head_time, now)
    return {
        "repository_id": REPOSITORY_ID,
        "pr_number": pr_number,
        "pr_id": pr.get("id"),
        "pr_node_id": pr.get("node_id"),
        "base_sha": base_sha,
        "head_sha": head_sha,
        "test_merge_sha": test_merge_sha,
        "check_target": "test_merge" if test_merge_has_checks else "head",
        "protection_sha256": policy.protection_sha256,
        "rules_sha256": policy.rules_sha256,
        "complete_census_sha256": _digest(transcript),
    }


def collect_two(read_json, policy: InstalledTargetPolicy,
                pr_number: int, now: int) -> dict:
    first = collect_once(read_json, policy, pr_number, now)
    second = collect_once(read_json, policy, pr_number, now)
    require(first == second, "eligibility-two-read-drift")
    return second
