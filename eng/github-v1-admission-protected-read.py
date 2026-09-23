#!/usr/bin/env python3
"""Read native GS2 v1-admission approval evidence; never sign or mutate GitHub."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import io
import json
import os
import pathlib
import re
import subprocess
import sys
import zipfile


REPOSITORY = "FS-GG/.github"
REPOSITORY_ID = 1269292704
ENVIRONMENT = "fleet-cutover"
ENVIRONMENT_ID = 21550151971
WORKFLOW = ".github/workflows/gs2-v1-admission-protected-authorization.yml"
WORKFLOW_SHA256 = "e0682cdcb201781ef67308b1546e0e21090a6efebe1c92316cc9fbe110040767"
MEMBER = "protected-v1-admission-genesis.json"


class Refused(RuntimeError):
    pass


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise Refused(reason)


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def gh_json(path: str, paginate: bool = False):
    command = ["gh", "api", path]
    if paginate:
        command += ["--paginate", "--slurp"]
    try:
        completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                   check=False, timeout=30)
    except subprocess.TimeoutExpired as error:
        raise Refused("native-read-timeout") from error
    require(completed.returncode == 0 and len(completed.stdout) <= 2_000_000, "native-read-unavailable")
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise Refused("native-read-invalid-json") from error


def gh_bytes(path: str) -> bytes:
    try:
        completed = subprocess.run(["gh", "api", path], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                   check=False, timeout=30)
    except subprocess.TimeoutExpired as error:
        raise Refused("native-artifact-timeout") from error
    require(completed.returncode == 0 and len(completed.stdout) <= 65_536, "native-artifact-unavailable")
    return completed.stdout


def pages(value, key: str | None = None) -> list:
    require(isinstance(value, list) and value, "native-pagination-unavailable")
    flattened = []
    for page in value:
        if key is not None:
            require(isinstance(page, dict) and isinstance(page.get(key), list), "native-pagination-shape")
            flattened.extend(page[key])
        else:
            require(isinstance(page, list), "native-pagination-shape")
            flattened.extend(page)
    return flattened


def artifact_member(archive: bytes, expected_digest: str) -> bytes:
    require(re.fullmatch(r"[0-9a-f]{64}", expected_digest) is not None, "native-artifact-digest-shape")
    require(len(archive) <= 65_536, "native-artifact-archive-size")
    require(sha256(archive) == expected_digest, "native-artifact-digest")
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            members = bundle.infolist()
            require(len(members) == 1 and members[0].filename == MEMBER, "native-artifact-members")
            member = members[0]
            require(not member.is_dir() and member.file_size <= 8192, "native-artifact-member-size")
            require((member.external_attr >> 16) & 0o170000 != 0o120000, "native-artifact-symlink")
            return bundle.read(member)
    except Refused:
        raise
    except (OSError, ValueError, zipfile.BadZipFile, RuntimeError) as error:
        raise Refused("native-artifact-zip") from error


def collect(run_id: int, read_json=gh_json, read_bytes=gh_bytes, observed_at: str | None = None) -> dict:
    require(isinstance(run_id, int) and run_id > 0, "native-run-id")
    prefix = f"repos/{REPOSITORY}"
    run = read_json(f"{prefix}/actions/runs/{run_id}")
    require(isinstance(run, dict), "native-run-shape")
    head = run.get("head_sha")
    require(isinstance(head, str) and re.fullmatch(r"[0-9a-f]{40}", head) is not None, "native-run-head")
    require(run.get("id") == run_id and (run.get("repository") or {}).get("id") == REPOSITORY_ID,
            "native-run-identity")
    require(run.get("path") == WORKFLOW and run.get("event") == "workflow_dispatch"
            and run.get("head_branch") == "main" and run.get("run_attempt") == 1
            and run.get("status") == "completed" and run.get("conclusion") == "success",
            "native-run-state")
    actor = (run.get("actor") or {}).get("id")
    require(isinstance(actor, int) and actor > 0, "native-run-actor")

    workflow = read_json(f"{prefix}/contents/{WORKFLOW}?ref={head}")
    require(isinstance(workflow, dict) and workflow.get("type") == "file"
            and workflow.get("encoding") == "base64", "native-workflow-shape")
    try:
        workflow_bytes = base64.b64decode(str(workflow["content"]).replace("\n", ""), validate=True)
    except (KeyError, ValueError) as error:
        raise Refused("native-workflow-encoding") from error
    require(sha256(workflow_bytes) == WORKFLOW_SHA256, "native-workflow-drift")

    environment = read_json(f"{prefix}/environments/{ENVIRONMENT}")
    require(isinstance(environment, dict) and environment.get("id") == ENVIRONMENT_ID
            and environment.get("name") == ENVIRONMENT, "native-environment-identity")
    rules = environment.get("protection_rules")
    require(isinstance(rules, list) and len(rules) == 2, "native-environment-rules")
    reviewer_rules = [rule for rule in rules if rule.get("type") == "required_reviewers"]
    branch_rules = [rule for rule in rules if rule.get("type") == "branch_policy"]
    require(len(reviewer_rules) == len(branch_rules) == 1, "native-environment-rules")
    reviewer_rule = reviewer_rules[0]
    reviewers = reviewer_rule.get("reviewers")
    require(reviewer_rule.get("prevent_self_review") is True and isinstance(reviewers, list),
            "native-environment-reviewers")
    reviewer_ids = [item.get("reviewer", {}).get("id") for item in reviewers if item.get("type") == "User"]
    require(len(reviewer_ids) == len(reviewers) == 2 and set(reviewer_ids) == {1645484, 4456104},
            "native-environment-reviewers")
    require(environment.get("deployment_branch_policy") ==
            {"custom_branch_policies": True, "protected_branches": False}, "native-environment-branch-mode")
    policies = read_json(f"{prefix}/environments/{ENVIRONMENT}/deployment-branch-policies?per_page=100")
    require(isinstance(policies, dict) and policies.get("total_count") == 1
            and isinstance(policies.get("branch_policies"), list)
            and len(policies["branch_policies"]) == 1
            and policies["branch_policies"][0].get("name") == "main"
            and policies["branch_policies"][0].get("type") == "branch", "native-environment-branch-policy")

    approval_pages = read_json(f"{prefix}/actions/runs/{run_id}/approvals?per_page=100", True)
    approvals = pages(approval_pages)
    require(0 < len(approvals) <= 2, "native-approvals-count")
    normalized_approvals = []
    for approval in approvals:
        require(isinstance(approval, dict) and isinstance(approval.get("environments"), list),
                "native-approval-shape")
        normalized_approvals.append({"reviewerId": (approval.get("user") or {}).get("id"),
                                     "state": approval.get("state"),
                                     "environmentIds": [item.get("id") for item in approval["environments"]]})
    require(all(item["state"] == "approved" and item["environmentIds"] == [ENVIRONMENT_ID]
                and item["reviewerId"] in reviewer_ids and item["reviewerId"] != actor
                for item in normalized_approvals)
            and len({item["reviewerId"] for item in normalized_approvals}) == len(normalized_approvals),
            "native-approval-binding")

    artifact_pages = read_json(f"{prefix}/actions/runs/{run_id}/artifacts?per_page=100", True)
    artifacts = pages(artifact_pages, "artifacts")
    require(len(artifacts) == 1 and artifact_pages[0].get("total_count") == 1, "native-artifact-count")
    artifact = artifacts[0]
    require(isinstance(artifact, dict) and artifact.get("name") ==
            f"gs2-v1-admission-protected-authorization-{run_id}" and artifact.get("expired") is False,
            "native-artifact-identity")
    origin = artifact.get("workflow_run") or {}
    require(origin.get("id") == run_id and origin.get("repository_id") == REPOSITORY_ID
            and origin.get("head_repository_id") == REPOSITORY_ID
            and origin.get("head_sha") == head and origin.get("head_branch") == "main",
            "native-artifact-origin")
    digest = artifact.get("digest")
    require(isinstance(digest, str) and digest.startswith("sha256:"), "native-artifact-digest-shape")
    archive = read_bytes(f"{prefix}/actions/artifacts/{artifact.get('id')}/zip")
    receipt_bytes = artifact_member(archive, digest.removeprefix("sha256:"))

    if observed_at is None:
        observed_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "schema": "fsgg.v1-admission-genesis-native-read/1",
        "observedAt": observed_at,
        "runRepositoryId": REPOSITORY_ID,
        "runId": run_id,
        "runEvent": run["event"],
        "runPath": run["path"],
        "runRef": "refs/heads/main",
        "runHead": head,
        "runConclusion": run["conclusion"],
        "runActorId": actor,
        "runAttempt": run["run_attempt"],
        "workflowReadRevision": head,
        "workflowBytesBase64": base64.b64encode(workflow_bytes).decode(),
        "artifactReadRunId": origin["id"],
        "artifactBytesBase64": base64.b64encode(receipt_bytes).decode(),
        "environmentId": environment["id"],
        "environmentName": environment["name"],
        "environmentBranchPolicy": "custom-main",
        "environmentReviewerIds": reviewer_ids,
        "environmentPreventsSelfReview": True,
        "approvals": normalized_approvals,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-id", type=int, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    try:
        value = collect(args.run_id)
        require(not args.output.is_symlink() and not args.output.exists(), "native-output-exists")
        descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, "wb") as output:
            output.write(json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n")
        print(json.dumps({"schema": "fsgg.v1-admission-genesis-native-read-result/1",
                          "runId": args.run_id, "evidenceSha256": sha256(args.output.read_bytes())},
                         sort_keys=True, separators=(",", ":")))
        return 0
    except (Refused, OSError, KeyError, TypeError, AttributeError) as error:
        print(f"v1 admission native read refused: {error}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
