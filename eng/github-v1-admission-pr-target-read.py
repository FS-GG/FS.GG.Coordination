#!/usr/bin/env python3
"""Import-only complete native PR/source/target census for a future sealed plan.

The PR number is a lookup hint. This reader fixes the repository and target,
collects two independent complete observations, and returns public revision
facts plus a digest of every response. It does not decide checks, reviews,
claims, rule eligibility, admission, or merge authority and has no write path.
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import pathlib
import re
import sys
import urllib.parse

SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-job-native-read.py")
SPEC = importlib.util.spec_from_file_location("v1_admission_native_transport", SOURCE)
NATIVE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = NATIVE
SPEC.loader.exec_module(NATIVE)

PREFIX = NATIVE.PREFIX
REPOSITORY_ID = NATIVE.REPOSITORY_ID
HEX40 = re.compile(r"[0-9a-f]{40}\Z")


def require(value: bool, reason: str) -> None:
    if not value:
        raise NATIVE.Refused(reason)


def _sha(value) -> bool:
    return isinstance(value, str) and HEX40.fullmatch(value) is not None


def complete_check_runs(recorded, commit_sha: str) -> list:
    """Enumerate suites first; by-ref check runs silently cap at 1,000 suites."""
    require(_sha(commit_sha), "native-target-check-ref")
    suites = NATIVE._pages(
        recorded, f"{PREFIX}/commits/{commit_sha}/check-suites?per_page=100",
        "check_suites",
    )
    require(all(isinstance(suite, dict) and type(suite.get("id")) is int
                and suite.get("head_sha") == commit_sha
                for suite in suites)
            and len({suite["id"] for suite in suites}) == len(suites),
            "native-target-check-suites")
    runs = []
    for suite in suites:
        batch = NATIVE._pages(
            recorded,
            f"{PREFIX}/check-suites/{suite['id']}/check-runs?per_page=100&filter=all",
            "check_runs",
        )
        require(all(isinstance(item, dict) and type(item.get("id")) is int
                    and item.get("head_sha") == commit_sha
                    and (item.get("check_suite") or {}).get("id") == suite["id"]
                    for item in batch), "native-target-check-runs")
        runs.extend(batch)
    require(len({item["id"] for item in runs}) == len(runs),
            "native-target-check-duplicate")
    return runs


def collect_once(read_json, pr_number: int) -> dict:
    """Observe one bounded complete PR/target census without authorization."""
    require(type(pr_number) is int and pr_number > 0, "native-target-lookup")
    transcript = []

    def recorded(path: str) -> NATIVE.NativeResponse:
        response = read_json(path)
        require(isinstance(response, NATIVE.NativeResponse), "native-target-response")
        canonical = json.dumps(response.body, sort_keys=True, separators=(",", ":")).encode()
        transcript.append((path, hashlib.sha256(canonical).hexdigest(), response.link))
        return response

    def body(path: str):
        response = recorded(path)
        require(response.link is None, "native-target-unexpected-link")
        return response.body

    pr = body(f"{PREFIX}/pulls/{pr_number}")
    require(isinstance(pr, dict)
            and pr.get("number") == pr_number
            and type(pr.get("id")) is int and pr["id"] > 0
            and isinstance(pr.get("node_id"), str) and bool(pr["node_id"])
            and pr.get("state") == "open" and pr.get("draft") is False
            and pr.get("merged") is False
            and isinstance(pr.get("base"), dict)
            and isinstance(pr.get("head"), dict),
            "native-target-pr")
    base = pr["base"]
    head = pr["head"]
    require(base.get("ref") == "main" and _sha(base.get("sha"))
            and (base.get("repo") or {}).get("id") == REPOSITORY_ID
            and _sha(head.get("sha"))
            and (head.get("repo") or {}).get("id") == REPOSITORY_ID
            and isinstance(head.get("ref"), str)
            and 0 < len(head["ref"]) <= 200
            and head["ref"] != "main"
            and not head["ref"].startswith("refs/"),
            "native-target-revisions")
    main = body(f"{PREFIX}/branches/main")
    require(isinstance(main, dict) and main.get("name") == "main"
            and main.get("protected") is True
            and (main.get("commit") or {}).get("sha") == base["sha"],
            "native-target-main")
    encoded_ref = urllib.parse.quote(head["ref"], safe="")
    source = body(f"{PREFIX}/branches/{encoded_ref}")
    require(isinstance(source, dict) and source.get("name") == head["ref"]
            and (source.get("commit") or {}).get("sha") == head["sha"],
            "native-target-source")

    require(type(pr.get("commits")) is int and 0 < pr["commits"] <= 250
            and type(pr.get("changed_files")) is int
            and 0 < pr["changed_files"] <= 3000,
            "native-target-census-size")
    commits = NATIVE._pages(recorded, f"{PREFIX}/pulls/{pr_number}/commits?per_page=100")
    require(len(commits) == pr["commits"]
            and all(isinstance(commit, dict) and _sha(commit.get("sha")) for commit in commits)
            and len({commit["sha"] for commit in commits}) == len(commits)
            and commits[-1]["sha"] == head["sha"],
            "native-target-commits")
    files = NATIVE._pages(recorded, f"{PREFIX}/pulls/{pr_number}/files?per_page=100")
    require(len(files) == pr["changed_files"]
            and all(isinstance(item, dict)
                    and isinstance(item.get("filename"), str)
                    and bool(item["filename"])
                    and _sha(item.get("sha"))
                    and isinstance(item.get("status"), str)
                    for item in files)
            and len({item["filename"] for item in files}) == len(files),
            "native-target-files")
    reviews = NATIVE._pages(recorded, f"{PREFIX}/pulls/{pr_number}/reviews?per_page=100")
    require(all(isinstance(item, dict) and type(item.get("id")) is int
                for item in reviews)
            and len({item["id"] for item in reviews}) == len(reviews),
            "native-target-reviews")
    checks = complete_check_runs(recorded, head["sha"])
    statuses = NATIVE._pages(recorded,
                             f"{PREFIX}/commits/{head['sha']}/statuses?per_page=100")
    require(all(isinstance(item, dict) and type(item.get("id")) is int
                for item in statuses)
            and len({item["id"] for item in statuses}) == len(statuses),
            "native-target-statuses")
    return {
        "repository_id": REPOSITORY_ID,
        "pr_number": pr_number,
        "pr_id": pr["id"],
        "pr_node_id": pr["node_id"],
        "base_ref": "refs/heads/main",
        "base_sha": base["sha"],
        "head_ref": head["ref"],
        "head_sha": head["sha"],
        "commit_count": len(commits),
        "file_count": len(files),
        "review_count": len(reviews),
        "check_run_count": len(checks),
        "status_count": len(statuses),
        "complete_census_sha256": hashlib.sha256(
            json.dumps(transcript, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest(),
    }


def collect_two(read_json, pr_number: int) -> dict:
    first = collect_once(read_json, pr_number)
    second = collect_once(read_json, pr_number)
    require(first == second, "native-target-two-read-drift")
    return second
