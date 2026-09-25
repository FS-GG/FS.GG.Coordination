#!/usr/bin/env python3
"""Import-only native run/job/environment reader for a future Main issuer.

This reader makes two complete independent GitHub reads and records one
environment deployment status whose URL matches the job. GitHub deployment
status URLs are caller-supplied and are corroboration only, never proof of a
job/environment relationship. The future Main issuer must separately verify
signed OIDC environment, subject, environment node ID, and check-run ID. A
required-reviewer environment needs a separate timed native approval proof;
this reader refuses that policy. This module does not verify OIDC, validate
a sealed plan, consume a nonce, mint a token, or authorize any effect. Policy
is installed on Main, never from a request.
"""

from __future__ import annotations

import base64
import dataclasses
import hashlib
import json
import re
import subprocess
import urllib.parse

REPOSITORY = "FS-GG/.github"
REPOSITORY_ID = 1269292704
PREFIX = f"repos/{REPOSITORY}"
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
MAX_PAGES = 10
LINK_PART = re.compile(r'<(https://api\.github\.com/[^<>" ]+)>; rel="(next|last|prev|first)"\Z')


class Refused(RuntimeError):
    pass


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


@dataclasses.dataclass(frozen=True)
class InstalledPolicy:
    workflow_path: str
    workflow_sha: str
    workflow_bytes_sha256: str
    job_name: str
    actor_id: int
    environment_name: str
    environment_id: int
    environment_node_id: str
    environment_rules_sha256: str


@dataclasses.dataclass(frozen=True)
class NativeResponse:
    body: object
    link: str | None


def _validate_policy(policy: InstalledPolicy) -> None:
    require(isinstance(policy, InstalledPolicy)
            and isinstance(policy.workflow_path, str)
            and policy.workflow_path.startswith(".github/workflows/")
            and policy.workflow_path.endswith(".yml")
            and isinstance(policy.workflow_sha, str)
            and HEX40.fullmatch(policy.workflow_sha) is not None
            and isinstance(policy.workflow_bytes_sha256, str)
            and HEX64.fullmatch(policy.workflow_bytes_sha256) is not None
            and isinstance(policy.job_name, str) and policy.job_name != ""
            and type(policy.actor_id) is int and policy.actor_id > 0
            and isinstance(policy.environment_name, str) and policy.environment_name != ""
            and type(policy.environment_id) is int and policy.environment_id > 0
            and isinstance(policy.environment_node_id, str)
            and policy.environment_node_id != ""
            and isinstance(policy.environment_rules_sha256, str)
            and HEX64.fullmatch(policy.environment_rules_sha256) is not None,
            "native-job-policy")


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "native-job-json-duplicate")
        result[key] = value
    return result


def gh_json(path: str):
    """Read-only REST response with the native Link header retained."""
    try:
        completed = subprocess.run(
            ["gh", "api", "-i", path], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            check=False, timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise Refused("native-job-read-unavailable") from error
    require(completed.returncode == 0 and len(completed.stdout) <= 2_100_000,
            "native-job-read-unavailable")
    separator = b"\r\n\r\n" if b"\r\n\r\n" in completed.stdout else b"\n\n"
    require(separator in completed.stdout, "native-job-response-headers")
    header_raw, body_raw = completed.stdout.split(separator, 1)
    require(len(header_raw) <= 65536 and len(body_raw) <= 2_000_000,
            "native-job-response-size")
    try:
        lines = header_raw.decode("ascii").replace("\r\n", "\n").split("\n")
        require(re.fullmatch(r"HTTP/[0-9.]+ 200(?: OK)?", lines[0]) is not None,
                "native-job-response-status")
        headers = {}
        for line in lines[1:]:
            name, sep, value = line.partition(":")
            require(bool(sep) and name and name.lower() not in headers
                    and "\n" not in value, "native-job-response-header")
            headers[name.lower()] = value.strip()
        require(headers.get("content-type", "").startswith("application/json"),
                "native-job-response-type")
        body = json.loads(
            body_raw, object_pairs_hook=_unique_pairs,
            parse_constant=lambda _: (_ for _ in ()).throw(Refused("native-job-json-constant")),
        )
        return NativeResponse(body, headers.get("link"))
    except (UnicodeError, ValueError) as error:
        raise Refused("native-job-read-json") from error


def _link_relations(link: str | None, path: str, page: int) -> dict:
    if link is None:
        return {}
    require(isinstance(link, str) and 0 < len(link) <= 8192, "native-job-link")
    expected_path, _, expected_query = path.partition("?")
    allowed_paths = {"/" + expected_path,
                     "/" + expected_path.replace(PREFIX, f"repositories/{REPOSITORY_ID}", 1)}
    pairs = urllib.parse.parse_qsl(expected_query, keep_blank_values=True)
    require(len(pairs) == len(dict(pairs)), "native-job-link-query")
    expected = dict(pairs)
    expected["page"] = str(page)
    result = {}
    for part in link.split(", "):
        match = LINK_PART.fullmatch(part)
        require(match is not None, "native-job-link-shape")
        url, relation = match.groups()
        parsed = urllib.parse.urlsplit(url)
        query_pairs = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
        require(parsed.scheme == "https" and parsed.netloc == "api.github.com"
                and parsed.path in allowed_paths and not parsed.fragment
                and "%" not in parsed.path and "%" not in parsed.query
                and len(query_pairs) == len(dict(query_pairs))
                and relation not in result,
                "native-job-link-origin")
        query = dict(query_pairs)
        linked_page = query.pop("page", None)
        current = expected.copy()
        current.pop("page")
        require(query == current and linked_page is not None and linked_page.isdigit(),
                "native-job-link-query")
        linked_page = int(linked_page)
        require((relation == "next" and linked_page == page + 1)
                or (relation == "prev" and linked_page == page - 1)
                or (relation == "first" and linked_page == 1)
                or (relation == "last" and linked_page > 0),
                "native-job-link-page")
        result[relation] = linked_page
    return result


def _pages(read_json, path: str, key: str | None = None) -> list:
    items = []
    total = None
    for page in range(1, MAX_PAGES + 1):
        response = read_json(f"{path}&page={page}")
        require(isinstance(response, NativeResponse), "native-job-response-shape")
        links = _link_relations(response.link, f"{path}&page={page}", page)
        body = response.body
        if key is None:
            require(isinstance(body, list), "native-job-page-shape")
            batch = body
        else:
            require(isinstance(body, dict) and type(body.get("total_count")) is int
                    and isinstance(body.get(key), list), "native-job-page-shape")
            require(total is None or total == body["total_count"],
                    "native-job-page-total")
            total = body["total_count"]
            batch = body[key]
        require(len(batch) <= 100, "native-job-page-size")
        items.extend(batch)
        require(len(items) <= 1000, "native-job-page-limit")
        if "next" in links:
            require(page < MAX_PAGES
                    and ("last" not in links or links["last"] >= page + 1),
                    "native-job-page-limit")
            continue
        require("last" not in links or links["last"] == page,
                "native-job-link-last")
        require(total is None or total == len(items), "native-job-page-total")
        if key is None:
            terminal = read_json(f"{path}&page={page + 1}")
            terminal_links = _link_relations(
                terminal.link if isinstance(terminal, NativeResponse) else None,
                f"{path}&page={page + 1}", page + 1,
            )
            require(isinstance(terminal, NativeResponse)
                    and terminal.body == [] and "next" not in terminal_links
                    and ("last" not in terminal_links or terminal_links["last"] <= page),
                    "native-job-page-terminal")
        return items
    raise Refused("native-job-page-terminal")


def _rules_digest(environment: dict, branch_policies: dict) -> str:
    rules = environment.get("protection_rules")
    require(isinstance(rules, list)
            and all(isinstance(rule, dict) and isinstance(rule.get("type"), str)
                    for rule in rules)
            and sorted(rule.get("type") for rule in rules) in
            (["branch_policy"], ["branch_policy", "required_reviewers"])
            and environment.get("deployment_branch_policy") ==
            {"custom_branch_policies": True, "protected_branches": False}
            and isinstance(branch_policies, dict)
            and branch_policies.get("total_count") == 1
            and isinstance(branch_policies.get("branch_policies"), list)
            and len(branch_policies["branch_policies"]) == 1
            and branch_policies["branch_policies"][0].get("name") == "main"
            and branch_policies["branch_policies"][0].get("type") == "branch",
            "native-job-environment-rules")
    selected = {
        "protection_rules": environment["protection_rules"],
        "deployment_branch_policy": environment["deployment_branch_policy"],
        "branch_policies": branch_policies["branch_policies"],
    }
    raw = json.dumps(selected, sort_keys=True, separators=(",", ":")).encode()
    return hashlib.sha256(raw).hexdigest()


def _check_required_approvals(read_json, run_id: int, environment: dict) -> None:
    reviewer_rules = [rule for rule in environment["protection_rules"]
                      if rule["type"] == "required_reviewers"]
    if not reviewer_rules:
        return
    rule = reviewer_rules[0]
    reviewers = rule.get("reviewers")
    require(isinstance(reviewers, list) and reviewers
            and all(isinstance(item, dict) and item.get("type") == "User"
                    and isinstance(item.get("reviewer"), dict)
                    and type(item["reviewer"].get("id")) is int
                    for item in reviewers), "native-job-reviewer-rule")
    expected = [item["reviewer"]["id"] for item in reviewers]
    require(len(set(expected)) == len(expected), "native-job-reviewer-rule")
    approvals = _pages(read_json, f"{PREFIX}/actions/runs/{run_id}/approvals?per_page=100")
    require(len(approvals) == len(expected), "native-job-approvals-count")
    actual = []
    for approval in approvals:
        require(isinstance(approval, dict)
                and approval.get("state") == "approved"
                and isinstance(approval.get("user"), dict)
                and type(approval["user"].get("id")) is int
                and isinstance(approval.get("environments"), list)
                and len(approval["environments"]) == 1
                and isinstance(approval["environments"][0], dict)
                and approval["environments"][0].get("id") == environment["id"]
                and approval["environments"][0].get("name") == environment["name"],
                "native-job-approval-contradiction")
        actual.append(approval["user"]["id"])
    require(set(actual) == set(expected), "native-job-approvers")
    # This native endpoint has no approval timestamp. A separate qualified
    # immutable timed source is mandatory before reviewer-gated admission.
    raise Refused("native-job-approval-time-unavailable")


def collect_once(read_json, policy: InstalledPolicy, run_id: int, check_run_id: int) -> dict:
    """Make one bounded complete read; request IDs are lookup hints only."""
    _validate_policy(policy)
    require(type(run_id) is int and run_id > 0
            and type(check_run_id) is int and check_run_id > 0,
            "native-job-lookup")
    transcript = []

    def recorded(path: str) -> NativeResponse:
        response = read_json(path)
        require(isinstance(response, NativeResponse), "native-job-response-shape")
        encoded = json.dumps(response.body, sort_keys=True, separators=(",", ":")).encode()
        transcript.append((path, hashlib.sha256(encoded).hexdigest(), response.link))
        return response

    def body(path: str):
        response = recorded(path)
        require(response.link is None, "native-job-unexpected-link")
        return response.body

    run = body(f"{PREFIX}/actions/runs/{run_id}")
    require(isinstance(run, dict) and run.get("id") == run_id
            and (run.get("repository") or {}).get("id") == REPOSITORY_ID
            and (run.get("head_repository") or {}).get("id") == REPOSITORY_ID
            and run.get("path") == policy.workflow_path
            and run.get("head_sha") == policy.workflow_sha
            and run.get("head_branch") == "main"
            and run.get("event") == "workflow_dispatch"
            and run.get("run_attempt") == 1
            and run.get("status") == "in_progress"
            and run.get("conclusion") is None
            and (run.get("actor") or {}).get("id") == policy.actor_id,
            "native-job-run")
    jobs = _pages(recorded,
                  f"{PREFIX}/actions/runs/{run_id}/attempts/1/jobs?per_page=100", "jobs")
    require(len(jobs) == 1 and isinstance(jobs[0], dict), "native-job-census")
    job = jobs[0]
    job_url = f"https://github.com/{REPOSITORY}/actions/runs/{run_id}/job/{check_run_id}"
    require(job.get("id") == check_run_id and job.get("run_id") == run_id
            and job.get("head_sha") == policy.workflow_sha
            and job.get("name") == policy.job_name
            and job.get("status") == "in_progress" and job.get("conclusion") is None
            and job.get("html_url") == job_url
            and job.get("check_run_url") ==
            f"https://api.github.com/{PREFIX}/check-runs/{check_run_id}",
            "native-job-identity")

    workflow = body(f"{PREFIX}/contents/{policy.workflow_path}?ref={policy.workflow_sha}")
    require(isinstance(workflow, dict) and workflow.get("type") == "file"
            and workflow.get("path") == policy.workflow_path
            and workflow.get("encoding") == "base64"
            and isinstance(workflow.get("content"), str),
            "native-job-workflow")
    try:
        workflow_bytes = base64.b64decode(workflow["content"].replace("\n", ""), validate=True)
    except (ValueError, TypeError) as error:
        raise Refused("native-job-workflow-encoding") from error
    require(0 < len(workflow_bytes) <= 65536
            and hashlib.sha256(workflow_bytes).hexdigest() == policy.workflow_bytes_sha256,
            "native-job-workflow-bytes")

    environment = body(f"{PREFIX}/environments/{policy.environment_name}")
    require(isinstance(environment, dict)
            and environment.get("id") == policy.environment_id
            and environment.get("node_id") == policy.environment_node_id
            and environment.get("name") == policy.environment_name,
            "native-job-environment")
    branches = body(
        f"{PREFIX}/environments/{policy.environment_name}/deployment-branch-policies?per_page=100"
    )
    require(_rules_digest(environment, branches) == policy.environment_rules_sha256,
            "native-job-environment-drift")
    _check_required_approvals(recorded, run_id, environment)

    deployments = _pages(
        recorded,
        f"{PREFIX}/deployments?sha={policy.workflow_sha}&environment={policy.environment_name}&per_page=100",
    )
    require(len(deployments) <= 100, "native-job-deployment-limit")
    matching = []
    for deployment in deployments:
        require(isinstance(deployment, dict) and type(deployment.get("id")) is int
                and deployment.get("sha") == policy.workflow_sha
                and deployment.get("environment") == policy.environment_name
                and deployment.get("original_environment") == policy.environment_name
                and deployment.get("ref") == "main"
                and deployment.get("statuses_url") ==
                f"https://api.github.com/{PREFIX}/deployments/{deployment['id']}/statuses",
                "native-job-deployment")
        statuses = _pages(
            recorded,
            f"{PREFIX}/deployments/{deployment['id']}/statuses?per_page=100",
        )
        require(all(isinstance(status, dict) for status in statuses),
                "native-job-status")
        bound = [status for status in statuses
                 if status.get("target_url") == job_url and status.get("log_url") == job_url]
        if bound:
            require(len(bound) == len(statuses)
                    and any(status.get("state") == "in_progress" for status in bound),
                    "native-job-status-binding")
            matching.append(deployment["id"])
    require(len(matching) == 1, "native-job-deployment-corroboration")
    return {
        "workflow_path": policy.workflow_path,
        "run_head": policy.workflow_sha,
        "workflow_sha": policy.workflow_sha,
        "run_id": run_id,
        "run_attempt": 1,
        "actor_id": policy.actor_id,
        "check_run_id": check_run_id,
        "environment_name": policy.environment_name,
        "environment_node_id": policy.environment_node_id,
        "environment_id": policy.environment_id,
        "environment_rules_sha256": policy.environment_rules_sha256,
        "workflow_bytes_sha256": policy.workflow_bytes_sha256,
        "corroborating_deployment_id": matching[0],
        "complete_census_sha256": hashlib.sha256(
            json.dumps(transcript, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest(),
    }


def collect_two(read_json, policy: InstalledPolicy, run_id: int, check_run_id: int) -> dict:
    """Require two separately collected complete and identical native snapshots."""
    first = collect_once(read_json, policy, run_id, check_run_id)
    second = collect_once(read_json, policy, run_id, check_run_id)
    require(first == second, "native-job-two-read-drift")
    return second
