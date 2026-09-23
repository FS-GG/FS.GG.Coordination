#!/usr/bin/env python3
"""Read one exact Coordination source commit and its stable main ancestry."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import re
import subprocess
import sys


REPOSITORY = "FS-GG/FS.GG.Coordination"
REPOSITORY_ID = 1346720714
OID = re.compile(r"[0-9a-f]{40}\Z")


class Refused(RuntimeError):
    pass


def require(condition, reason):
    if not condition:
        raise Refused(reason)


def api(path):
    endpoint = f"repos/{REPOSITORY}" + ("/" + path if path else "")
    try:
        result = subprocess.run(["gh", "api", "--method", "GET", endpoint],
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=30, check=False)
    except subprocess.TimeoutExpired as error:
        raise Refused("source-api-timeout") from error
    require(result.returncode == 0 and len(result.stdout) <= 2_000_000, "source-api-unavailable")
    try:
        return json.loads(result.stdout)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise Refused("source-api-json") from error


def sha(value, reason):
    require(isinstance(value, str) and OID.fullmatch(value) is not None, reason)
    return value


def main_head(read_api):
    value = read_api("git/ref/heads/main")
    require(isinstance(value, dict) and value.get("ref") == "refs/heads/main"
            and isinstance(value.get("object"), dict)
            and value["object"].get("type") == "commit", "source-main-ref")
    return sha(value["object"].get("sha"), "source-main-head")


def collect(source_commit, read_api=api, observed_at=None):
    source_commit = sha(source_commit, "source-commit")
    repository = read_api("")
    require(isinstance(repository, dict) and repository.get("id") == REPOSITORY_ID
            and repository.get("full_name") == REPOSITORY, "source-repository-identity")
    first_head = main_head(read_api)
    commit = read_api(f"git/commits/{source_commit}")
    require(isinstance(commit, dict) and commit.get("sha") == source_commit
            and isinstance(commit.get("tree"), dict), "source-commit-object")
    tree = sha(commit["tree"].get("sha"), "source-tree")
    if source_commit == first_head:
        status, base, head, merge_base = "identical", source_commit, first_head, source_commit
    else:
        comparison = read_api(f"compare/{source_commit}...{first_head}")
        require(isinstance(comparison, dict)
                and comparison.get("status") in {"ahead", "behind", "diverged", "identical"}
                and isinstance(comparison.get("base_commit"), dict)
                and isinstance(comparison.get("head_commit"), dict)
                and isinstance(comparison.get("merge_base_commit"), dict), "source-compare-shape")
        status = comparison["status"]
        base = sha(comparison["base_commit"].get("sha"), "source-compare-base")
        head = sha(comparison["head_commit"].get("sha"), "source-compare-head")
        merge_base = sha(comparison["merge_base_commit"].get("sha"), "source-compare-merge-base")
        require(base == source_commit and head == first_head, "source-compare-binding")
    second_head = main_head(read_api)
    require(second_head == first_head, "source-main-moved")
    if observed_at is None:
        observed_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "schema": "fsgg.v1-admission-genesis-source-read/1",
        "observedAt": observed_at,
        "repository": REPOSITORY,
        "repositoryId": REPOSITORY_ID,
        "sourceCommit": source_commit,
        "sourceTree": tree,
        "firstMainHead": first_head,
        "secondMainHead": second_head,
        "compareStatus": status,
        "compareBase": base,
        "compareHead": head,
        "mergeBase": merge_base,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    try:
        result = collect(args.commit)
        require(not args.output.is_symlink() and not args.output.exists(), "source-output-exists")
        descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, "wb") as output:
            output.write(json.dumps(result, sort_keys=True, separators=(",", ":")).encode() + b"\n")
        print(json.dumps({"schema": "fsgg.v1-admission-genesis-source-read-result/1",
                          "sourceCommit": result["sourceCommit"],
                          "sourceTree": result["sourceTree"],
                          "mainHead": result["firstMainHead"]}, sort_keys=True, separators=(",", ":")))
        return 0
    except (Refused, OSError, ValueError, TypeError) as error:
        print("v1 admission source read refused: " + str(error), file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
