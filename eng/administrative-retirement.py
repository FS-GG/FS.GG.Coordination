#!/usr/bin/env python3
"""Plan, execute, reconcile, and verify lost-host administrative retirement.

The helper owns only its two repository rulesets and the exact PR/issue named in
its private configuration. Every effect is preceded by a durable checkpoint and
followed by a fresh native readback. A failed request remains an observation
obligation; restarting never blindly replays it.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import fcntl
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
from contextlib import contextmanager

CONFIG_SCHEMA = "fsgg.coordination.administrative-retirement-config/1"
ARCHIVE_SCHEMA = "fsgg.coordination.administrative-retirement-archive/1"
CHECKPOINT_SCHEMA = "fsgg.coordination.administrative-retirement-operation/1"
RECEIPT_SCHEMA = "fsgg.coordination.administrative-retirement-operation-receipt/1"
API_VERSION = "2022-11-28"
COMMAND_TIMEOUT_SECONDS = 45
OID = re.compile(r"[0-9a-f]{40}\Z")
DIGEST = re.compile(r"[0-9a-f]{64}\Z")
PERMANENT_RULES = ["creation", "update:no-fetch-and-merge", "deletion"]
TEMPORARY_RULES = ["update:no-fetch-and-merge"]
CENSUS_KINDS = ["repository", "candidate-ref", "candidate-commit", "candidate-parent-commit", "candidate-tree", "candidate-parent-tree", "retirement-commit", "merge-commit", "merge-tree", "pull-request", "pull-request-files", "issue", "comments", "checks", "rulesets", "base-protection", "merge-modes", "subject-exclusion"]


class Refused(RuntimeError):
    pass


class UnknownEffect(RuntimeError):
    pass


def native_run(command, refusal, **kwargs):
    """Keep command and process exception text out of surfaced failures."""
    try:
        return subprocess.run(command, **kwargs)
    except (OSError, subprocess.SubprocessError):
        raise UnknownEffect(refusal) from None


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def sha(value):
    return hashlib.sha256(value).hexdigest()


def read_regular(path):
    target = pathlib.Path(path)
    if target.is_symlink() or not target.is_file():
        raise Refused("input-must-be-regular-nonsymlink:" + str(target))
    return target.read_bytes()


def read_json(path):
    try:
        return json.loads(read_regular(path))
    except json.JSONDecodeError as error:
        raise Refused("invalid-json:" + str(path)) from error


def private_parent(path):
    parent = pathlib.Path(path).parent
    if parent.is_symlink():
        raise Refused("checkpoint-parent-symlink")
    parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    if parent.stat().st_mode & 0o077:
        raise Refused("checkpoint-parent-not-private")
    return parent


def write_private(path, value):
    write_private_bytes(path, canonical(value) + b"\n")


def write_private_bytes(path, value):
    target = pathlib.Path(path)
    parent = private_parent(target)
    if target.exists() and (target.is_symlink() or not target.is_file()):
        raise Refused("checkpoint-target-refused")
    fd, temporary = tempfile.mkstemp(prefix=target.name + ".", dir=parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "wb") as output:
            output.write(value)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, target)
        directory = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


@contextmanager
def operation_lock(config):
    target = pathlib.Path(config["checkpoint"] + ".lock")
    private_parent(target)
    descriptor = os.open(target, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX)
        yield
    finally:
        fcntl.flock(descriptor, fcntl.LOCK_UN)
        os.close(descriptor)


def utc_now():
    value = dt.datetime.now(dt.timezone.utc)
    return value.strftime("%Y-%m-%dT%H:%M:%S.") + f"{value.microsecond:06d}0+00:00"


def framed(values):
    return "|".join(f"{len(str(value).encode())}:{value}" for value in values)


def dotnet_bool(value):
    return "True" if value else "False"


def require_hex(value, pattern, name):
    if not isinstance(value, str) or not pattern.fullmatch(value):
        raise Refused("invalid-" + name)


def validate_config(value):
    required = {"schema", "repository", "repositoryId", "issueNumber", "pullRequestNumber", "branchRef", "baseRef", "candidateHead", "candidateTree", "candidateParent", "retirementHead", "retirementTree", "retirementParent", "acceptedClientCommit", "acceptedClientArtifactDigest", "operationAuthorityDigest", "operationId", "archiveManifest", "subjectExclusion", "checkpoint"}
    if set(value) != required or value.get("schema") != CONFIG_SCHEMA:
        raise Refused("config-shape")
    if not isinstance(value["repository"], str) or value["repository"].count("/") != 1 or value["repositoryId"] <= 0:
        raise Refused("repository-binding")
    if value["issueNumber"] <= 0 or value["pullRequestNumber"] <= 0:
        raise Refused("number-binding")
    if not isinstance(value["branchRef"], str) or not value["branchRef"].startswith("refs/heads/"):
        raise Refused("branch-binding")
    if not isinstance(value["baseRef"], str) or not value["baseRef"].startswith("refs/heads/") or value["baseRef"] == value["branchRef"]:
        raise Refused("base-binding")
    for name in ("candidateHead", "candidateTree", "candidateParent", "retirementHead", "retirementTree", "retirementParent", "acceptedClientCommit"):
        require_hex(value[name], OID, name)
    if value["retirementHead"] == value["candidateHead"] or value["retirementTree"] != value["candidateTree"] or value["retirementParent"] != value["candidateHead"]:
        raise Refused("retirement-head-closure")
    for name in ("acceptedClientArtifactDigest", "operationAuthorityDigest"):
        require_hex(value[name], DIGEST, name)
    if not isinstance(value["operationId"], str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]{7,80}", value["operationId"]):
        raise Refused("operation-id")
    return value


def gh_request(path, method="GET", body=None, allow_not_found=False):
    command = ["gh", "api", "--method", method, "-H", f"X-GitHub-Api-Version: {API_VERSION}", path]
    if body is not None:
        command.extend(["--input", "-"])
    completed = native_run(command, f"github-{method.lower()}-command-unavailable", input=None if body is None else canonical(body), stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
    if completed.returncode != 0:
        detail = completed.stderr.decode(errors="replace")[:200].strip()
        if allow_not_found and ("HTTP 404" in detail or "status code 404" in detail):
            value = {"status": "absent", "httpStatus": 404}
            return value, {}, canonical(value)
        raise UnknownEffect(f"github-{method.lower()}-unknown")
    payload = completed.stdout
    try:
        value = json.loads(payload) if payload.strip() else None
    except json.JSONDecodeError as error:
        raise Refused("github-json") from error
    return value, {}, canonical(value) if value is not None else b"null"


def gh_pages(path):
    command = ["gh", "api", "--method", "GET", "-H", f"X-GitHub-Api-Version: {API_VERSION}", "--paginate", "--slurp", path]
    completed = native_run(command, "github-pages-command-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
    if completed.returncode != 0:
        raise UnknownEffect("github-get-unknown")
    try:
        pages = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise Refused("github-pages-json") from error
    if not isinstance(pages, list):
        raise Refused("github-pages-shape")
    return pages, canonical(pages)


def gh_graphql(repository, number):
    owner, repo = repository.split("/")
    query = "query($owner:String!,$repo:String!,$number:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$number){id autoMergeRequest{enabledAt} mergeQueueEntry{id}}}}"
    completed = native_run(["gh", "api", "graphql", "-f", f"query={query}", "-F", f"owner={owner}", "-F", f"repo={repo}", "-F", f"number={number}"], "github-merge-modes-command-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
    if completed.returncode != 0:
        raise UnknownEffect("github-merge-modes-unknown")
    try:
        return json.loads(completed.stdout)["data"]["repository"]["pullRequest"]
    except (KeyError, TypeError, json.JSONDecodeError) as error:
        raise Refused("github-merge-modes-json") from error


def gh_graphql_mutation(name, node_id):
    if name == "disable-auto-merge":
        query = "mutation($id:ID!){disablePullRequestAutoMerge(input:{pullRequestId:$id}){pullRequest{id}}}"
    elif name == "dequeue":
        query = "mutation($id:ID!){dequeuePullRequest(input:{pullRequestId:$id}){pullRequest{id}}}"
    else:
        raise Refused("graphql-mutation-name")
    completed = native_run(["gh", "api", "graphql", "-f", f"query={query}", "-F", f"id={node_id}"], "github-graphql-command-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
    if completed.returncode != 0:
        raise UnknownEffect("github-graphql-unknown:" + name)
    return json.loads(completed.stdout)


def rule_payload(name, ref_name, rules):
    values = []
    for rule in rules:
        if rule == "update:no-fetch-and-merge":
            values.append({"type": "update", "parameters": {"update_allows_fetch_and_merge": False}})
        else:
            values.append({"type": rule})
    return {"name": name, "target": "branch", "enforcement": "active", "bypass_actors": [], "conditions": {"ref_name": {"include": [ref_name], "exclude": []}}, "rules": values}


def normalized_rule(value):
    rules = []
    for rule in value.get("rules", []):
        kind = rule.get("type")
        if kind == "update" and ("parameters" not in rule or (rule.get("parameters") or {}).get("update_allows_fetch_and_merge") is False):
            rules.append("update:no-fetch-and-merge")
        elif kind in ("creation", "deletion"):
            rules.append(kind)
        else:
            rules.append("unsupported:" + str(kind))
    condition = (value.get("conditions") or {}).get("ref_name") or {}
    return {"id": value.get("id"), "name": value.get("name"), "target": value.get("target"), "enforcement": value.get("enforcement"), "bypass": value.get("bypass_actors"), "include": condition.get("include"), "exclude": condition.get("exclude"), "rules": rules}


def matching_rule(rulesets, name):
    matches = [value for value in rulesets if value.get("name") == name]
    if len(matches) > 1:
        raise Refused("duplicate-helper-ruleset:" + name)
    return matches[0] if matches else None


def validate_archive(config):
    path = pathlib.Path(config["archiveManifest"])
    manifest_bytes = read_regular(path)
    value = json.loads(manifest_bytes)
    required = {"schema", "repository", "issueNumber", "pullRequestNumber", "candidateHead", "candidateTree", "candidateParent", "retirementHead", "retirementTree", "retirementParent", "gitBundle", "gitBundleSha256", "capturedAt"}
    if set(value) != required or value["schema"] != ARCHIVE_SCHEMA:
        raise Refused("archive-shape")
    for key in ("repository", "issueNumber", "pullRequestNumber", "candidateHead", "candidateTree", "candidateParent", "retirementHead", "retirementTree", "retirementParent"):
        expected = config[key]
        if value[key] != expected:
            raise Refused("archive-binding:" + key)
    bundle = pathlib.Path(value["gitBundle"])
    bundle_bytes = read_regular(bundle)
    if sha(bundle_bytes) != value["gitBundleSha256"] or not DIGEST.fullmatch(value["gitBundleSha256"]):
        raise Refused("archive-bundle-digest")
    with tempfile.TemporaryDirectory(prefix="fsgg-retirement-archive-") as scratch:
        clone = pathlib.Path(scratch) / "archive.git"
        verifier = pathlib.Path(scratch) / "verify.git"
        initialized = native_run(["git", "init", "--quiet", "--bare", str(verifier)], "archive-verifier-init-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
        if initialized.returncode != 0:
            raise Refused("archive-verifier-init")
        verified = native_run(["git", "-C", str(verifier), "bundle", "verify", str(bundle)], "archive-bundle-verify-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
        if verified.returncode != 0:
            raise Refused("archive-bundle-invalid")
        imported = native_run(["git", "clone", "--quiet", "--bare", str(bundle), str(clone)], "archive-bundle-import-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
        if imported.returncode != 0:
            raise Refused("archive-bundle-import")
        def git_value(revision):
            result = native_run(["git", "-C", str(clone), "rev-parse", revision], "archive-closure-read-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
            if result.returncode != 0:
                raise Refused("archive-closure-missing:" + revision)
            return result.stdout.decode().strip()
        if git_value(config["candidateHead"]) != config["candidateHead"] or git_value(config["candidateHead"] + "^{tree}") != config["candidateTree"] or git_value(config["candidateHead"] + "^") != config["candidateParent"] or git_value(config["retirementHead"]) != config["retirementHead"] or git_value(config["retirementHead"] + "^{tree}") != config["retirementTree"] or git_value(config["retirementHead"] + "^") != config["retirementParent"]:
            raise Refused("archive-closure-mismatch")
    return {"digest": sha(manifest_bytes), "location": str(path.resolve()), "bundleDigest": value["gitBundleSha256"], "bundleLocation": str(bundle.resolve())}


def validate_exclusion(config):
    path = pathlib.Path(config["subjectExclusion"])
    raw = read_regular(path)
    value = json.loads(raw)
    expected = {"schema": "fsgg.coordination.retired-subject-exclusion/1", "repository": config["repository"], "issueNumber": config["issueNumber"], "candidateHead": config["candidateHead"], "excluded": True}
    if value != expected:
        raise Refused("subject-exclusion-binding")
    return {"digest": sha(raw), "location": str(path.resolve())}


def retain_census(config, snapshot, label):
    checkpoint_path = pathlib.Path(config["checkpoint"])
    raw_values = snapshot.pop("nativeCensusRaw", None)
    if raw_values is None or set(raw_values) != set(CENSUS_KINDS):
        raise Refused("native-census-raw-incomplete")
    artifact_root = checkpoint_path.parent / (checkpoint_path.name + "." + label + ".native-census")
    artifact_root.mkdir(mode=0o700, parents=False, exist_ok=True)
    artifacts = {}
    for kind in CENSUS_KINDS:
        target = artifact_root / (kind + ".json")
        write_private_bytes(target, raw_values[kind])
        retained = read_regular(target)
        artifacts[kind] = {"location": str(target.resolve()), "sha256": sha(retained), "bytes": len(retained)}
    snapshot["nativeCensus"]["artifacts"] = artifacts
    target = checkpoint_path.parent / (checkpoint_path.name + "." + label + ".native-census.json")
    write_private(target, snapshot["nativeCensus"])
    raw = read_regular(target)
    snapshot["nativeCensusDigest"] = sha(raw)
    snapshot["nativeCensusLocation"] = str(target.resolve())
    return snapshot


def validate_retained_census(location, digest, expected=None):
    raw = read_regular(location)
    if sha(raw) != digest:
        raise Refused("native-census-artifact-digest")
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as error:
        raise Refused("native-census-artifact-json") from error
    if expected is not None and value != expected:
        raise Refused("native-census-artifact-binding")
    if not value.get("complete") or set(value.get("kinds") or []) != set(CENSUS_KINDS):
        raise Refused("native-census-artifact-incomplete")
    artifacts = value.get("artifacts") or {}
    digests = value.get("digests") or {}
    if set(artifacts) != set(CENSUS_KINDS) or set(digests) != set(CENSUS_KINDS):
        raise Refused("native-census-raw-manifest-incomplete")
    for kind, binding in artifacts.items():
        if set(binding) != {"location", "sha256", "bytes"}:
            raise Refused("native-census-raw-binding-shape:" + kind)
        artifact = read_regular(binding["location"])
        if sha(artifact) != binding["sha256"] or digests[kind] != binding["sha256"] or len(artifact) != binding["bytes"]:
            raise Refused("native-census-raw-binding:" + kind)
    return value


def validate_changed_paths(candidate_tree, parent_tree, files, merge_tree=None):
    def leaf_entries(value, label):
        if value.get("truncated") is not False or not isinstance(value.get("tree"), list):
            raise Refused(label + "-tree-incomplete")
        entries = {}
        for item in value["tree"]:
            if item.get("type") == "tree":
                continue
            path = item.get("path")
            if not isinstance(path, str) or not path or path in entries:
                raise Refused(label + "-tree-shape")
            entries[path] = {key: item.get(key) for key in ("mode", "type", "sha")}
        return entries

    candidate_entries = leaf_entries(candidate_tree, "candidate")
    parent_entries = leaf_entries(parent_tree, "candidate-parent")
    changed_paths = {path for path in set(candidate_entries) | set(parent_entries) if candidate_entries.get(path) != parent_entries.get(path)}
    pull_paths = set()
    for item in files:
        filename = item.get("filename")
        status = item.get("status")
        if not isinstance(filename, str) or status not in ("added", "removed", "modified", "renamed", "copied", "changed"):
            raise Refused("pull-request-files-shape")
        pull_paths.add(filename)
        if status == "renamed":
            previous = item.get("previous_filename")
            if not isinstance(previous, str):
                raise Refused("pull-request-rename-shape")
            pull_paths.add(previous)
        if status == "removed":
            if filename in candidate_entries:
                raise Refused("candidate-removed-path-present")
        elif candidate_entries.get(filename, {}).get("sha") != item.get("sha"):
            raise Refused("candidate-path-blob-mismatch")
    if pull_paths != changed_paths:
        raise Refused("candidate-changed-path-census-mismatch")
    if merge_tree is None:
        return None
    merge_entries = leaf_entries(merge_tree, "merge")
    delivered = []
    for path in sorted(changed_paths):
        expected = candidate_entries.get(path)
        observed = merge_entries.get(path)
        if observed != expected:
            raise Refused("merged-changed-path-mismatch")
        delivered.append({"path": path, "candidate": expected, "merged": observed})
    return sha(canonical(delivered))


def observe(config):
    repository = config["repository"]
    branch = config["branchRef"].removeprefix("refs/heads/")
    base_branch = config["baseRef"].removeprefix("refs/heads/")
    repo, _, repo_raw = gh_request(f"repos/{repository}")
    ref, _, ref_raw = gh_request(f"repos/{repository}/git/ref/heads/{branch}", allow_not_found=True)
    commit, _, commit_raw = gh_request(f"repos/{repository}/git/commits/{config['candidateHead']}")
    parent_commit, _, parent_commit_raw = gh_request(f"repos/{repository}/git/commits/{config['candidateParent']}")
    candidate_tree, _, candidate_tree_raw = gh_request(f"repos/{repository}/git/trees/{config['candidateTree']}?recursive=1")
    parent_tree_oid = (parent_commit.get("tree") or {}).get("sha")
    require_hex(parent_tree_oid, OID, "candidate-parent-tree")
    parent_tree, _, parent_tree_raw = gh_request(f"repos/{repository}/git/trees/{parent_tree_oid}?recursive=1")
    retirement_commit, _, retirement_commit_raw = gh_request(f"repos/{repository}/git/commits/{config['retirementHead']}", allow_not_found=True)
    pull, _, pull_raw = gh_request(f"repos/{repository}/pulls/{config['pullRequestNumber']}")
    issue, _, issue_raw = gh_request(f"repos/{repository}/issues/{config['issueNumber']}")
    comment_pages, comments_raw = gh_pages(f"repos/{repository}/issues/{config['issueNumber']}/comments?per_page=100")
    comments = [item for page in comment_pages for item in page]
    check_pages, checks_raw = gh_pages(f"repos/{repository}/commits/{config['candidateHead']}/check-runs?per_page=100")
    checks = [item for page in check_pages for item in page.get("check_runs", [])]
    file_pages, files_raw = gh_pages(f"repos/{repository}/pulls/{config['pullRequestNumber']}/files?per_page=100")
    files = [item for page in file_pages for item in page]
    ruleset_pages, _ = gh_pages(f"repos/{repository}/rulesets?includes_parents=false&per_page=100")
    summaries = [item for page in ruleset_pages for item in page]
    rulesets = []
    detail_raw = []
    for summary in summaries:
        detail, _, raw = gh_request(f"repos/{repository}/rulesets/{summary.get('id')}")
        rulesets.append(detail); detail_raw.append(raw)
    rules_raw = canonical([json.loads(raw) for raw in detail_raw])
    try:
        protection, _, protection_raw = gh_request(f"repos/{repository}/branches/{base_branch}/protection", allow_not_found=True)
    except UnknownEffect:
        raise Refused("main-protection-unreadable")
    modes = gh_graphql(repository, config["pullRequestNumber"])
    exclusion = validate_exclusion(config)
    if repo.get("id") != config["repositoryId"] or repo.get("full_name") != repository:
        raise Refused("repository-identity-mismatch")
    parents = commit.get("parents") or []
    if (commit.get("tree") or {}).get("sha") != config["candidateTree"] or len(parents) != 1 or parents[0].get("sha") != config["candidateParent"]:
        raise Refused("candidate-closure-mismatch")
    pull_head = (pull.get("head") or {}).get("sha")
    if pull.get("number") != config["pullRequestNumber"] or pull_head not in (config["candidateHead"], config["retirementHead"]) or (pull.get("head") or {}).get("ref") != branch:
        raise Refused("pull-request-identity-mismatch")
    if (pull.get("base") or {}).get("ref") != base_branch or ((pull.get("base") or {}).get("repo") or {}).get("full_name") != repository:
        raise Refused("pull-request-base-mismatch")
    if issue.get("number") != config["issueNumber"]:
        raise Refused("issue-identity-mismatch")
    permanent_name = config["operationId"] + "-retired-candidate"
    temporary_name = config["operationId"] + "-temporary-main-hold"
    permanent = matching_rule(rulesets, permanent_name)
    temporary = matching_rule(rulesets, temporary_name)
    disposition = "open"
    merge = None
    if pull.get("merged_at"):
        disposition = "merged-exact-before-retirement"
        merge = pull.get("merge_commit_sha")
        require_hex(merge, OID, "merge-commit")
        if pull_head != config["candidateHead"]:
            disposition = "merged-other"
    elif pull.get("state") == "closed":
        disposition = "closed-unmerged"
    ref_head = (ref.get("object") or {}).get("sha")
    if ref_head not in (config["candidateHead"], config["retirementHead"]) and not (disposition == "merged-exact-before-retirement" and ref.get("status") == "absent"):
        raise Refused("candidate-ref-mismatch")
    retirement_closure = retirement_commit.get("status") != "absent" and (retirement_commit.get("tree") or {}).get("sha") == config["retirementTree"] and [item.get("sha") for item in retirement_commit.get("parents") or []] == [config["retirementParent"]]
    validate_changed_paths(candidate_tree, parent_tree, files)
    merge_raw = canonical({"status": "absent"})
    merge_tree_raw = canonical({"status": "absent"})
    delivered_path_digest = None
    if disposition == "merged-exact-before-retirement":
        merge_commit, _, merge_raw = gh_request(f"repos/{repository}/git/commits/{merge}")
        merge_tree_oid = (merge_commit.get("tree") or {}).get("sha")
        require_hex(merge_tree_oid, OID, "merge-tree")
        merge_tree, _, merge_tree_raw = gh_request(f"repos/{repository}/git/trees/{merge_tree_oid}?recursive=1")
        delivered_path_digest = validate_changed_paths(candidate_tree, parent_tree, files, merge_tree)
    census_values = {"repository": repo_raw, "candidate-ref": ref_raw, "candidate-commit": commit_raw, "candidate-parent-commit": parent_commit_raw, "candidate-tree": candidate_tree_raw, "candidate-parent-tree": parent_tree_raw, "retirement-commit": retirement_commit_raw, "merge-commit": merge_raw, "merge-tree": merge_tree_raw, "pull-request": pull_raw, "pull-request-files": files_raw, "issue": issue_raw, "comments": comments_raw, "checks": checks_raw, "rulesets": rules_raw, "base-protection": protection_raw, "merge-modes": canonical(modes), "subject-exclusion": read_regular(config["subjectExclusion"])}
    census = {"schema": "fsgg.coordination.administrative-retirement-native-census/1", "complete": set(census_values) == set(CENSUS_KINDS), "kinds": CENSUS_KINDS, "digests": {key: sha(value) for key, value in sorted(census_values.items())}}
    return {"observedAt": utc_now(), "repository": repository, "repositoryId": repo["id"], "branchRef": config["branchRef"], "observedBranchHead": ref_head, "retirementCommitObserved": retirement_closure, "protectedBaseRef": config["baseRef"], "candidateHead": config["candidateHead"], "candidateTree": config["candidateTree"], "candidateParent": config["candidateParent"], "pullRequestDisposition": disposition, "pullRequestHead": (pull.get("head") or {}).get("sha"), "pullRequestNodeId": pull.get("node_id"), "mergeCommit": merge, "mergedCandidateHead": config["candidateHead"] if disposition == "merged-exact-before-retirement" else None, "mergedCandidateTree": config["candidateTree"] if disposition == "merged-exact-before-retirement" else None, "mergedCandidateParent": config["candidateParent"] if disposition == "merged-exact-before-retirement" else None, "deliveredPathDigest": delivered_path_digest, "autoMergeEnabled": modes.get("autoMergeRequest") is not None, "mergeQueueEntry": (modes.get("mergeQueueEntry") or {}).get("id") if modes.get("mergeQueueEntry") else None, "issueState": issue.get("state"), "issueStateReason": issue.get("state_reason"), "permanentRule": normalized_rule(permanent) if permanent else None, "temporaryRule": normalized_rule(temporary) if temporary else None, "baseProtectionDigest": sha(protection_raw), "subjectExclusion": exclusion, "nativeCensus": census, "nativeCensusRaw": census_values, "nativeCensusDigest": sha(canonical(census))}


def expected_rule(config, permanent):
    name = config["operationId"] + ("-retired-candidate" if permanent else "-temporary-main-hold")
    ref_name = config["branchRef"] if permanent else config["baseRef"]
    kinds = PERMANENT_RULES if permanent else TEMPORARY_RULES
    payload = rule_payload(name, ref_name, kinds)
    return payload, sha(canonical(payload))


def rule_matches(rule, payload):
    return rule is not None and rule == normalized_rule(dict(payload, id=rule.get("id")))


def immutable_config(config):
    return {key: config[key] for key in sorted(config) if key != "checkpoint"}


def plan(config):
    if pathlib.Path(config["checkpoint"]).exists():
        return load_checkpoint(config)
    archive = validate_archive(config)
    snapshot = retain_census(config, observe(config), "initial")
    if snapshot["pullRequestDisposition"] == "merged-other":
        raise Refused("unexpected-merge-outcome")
    if snapshot["pullRequestDisposition"] == "open" and (snapshot["observedBranchHead"] != config["candidateHead"] or snapshot["pullRequestHead"] != config["candidateHead"]):
        raise Refused("initial-open-candidate-mismatch")
    if snapshot["pullRequestDisposition"] not in ("open", "merged-exact-before-retirement"):
        raise Refused("initial-pull-request-not-actionable")
    permanent, permanent_digest = expected_rule(config, True)
    temporary, temporary_digest = expected_rule(config, False)
    if snapshot["permanentRule"] and not rule_matches(snapshot["permanentRule"], permanent):
        raise Refused("permanent-ruleset-drift")
    if snapshot["temporaryRule"] and not rule_matches(snapshot["temporaryRule"], temporary):
        raise Refused("temporary-ruleset-drift")
    identity_keys = ("repository", "repositoryId", "issueNumber", "pullRequestNumber", "branchRef", "baseRef", "candidateHead", "candidateTree", "candidateParent", "retirementHead", "retirementTree", "retirementParent", "acceptedClientCommit", "acceptedClientArtifactDigest", "operationAuthorityDigest")
    value = {"schema": CHECKPOINT_SCHEMA, "operationId": config["operationId"], "configDigest": sha(canonical(immutable_config(config))), "identityDigest": sha(canonical({key: config[key] for key in identity_keys})), "archive": archive, "initialCensusDigest": snapshot["nativeCensusDigest"], "initialCensusLocation": snapshot["nativeCensusLocation"], "baseProtectionDigest": snapshot["baseProtectionDigest"], "permanentRuleDigest": permanent_digest, "temporaryRuleDigest": temporary_digest, "stage": "planned", "pendingEffect": None, "temporaryRuleId": None, "permanentRuleId": None, "cleanupRequired": False, "effects": [], "plannedAt": snapshot["observedAt"]}
    unsigned = dict(value); value["planDigest"] = sha(canonical(unsigned))
    write_private(config["checkpoint"], value)
    return value


def load_checkpoint(config):
    value = read_json(config["checkpoint"])
    if value.get("schema") != CHECKPOINT_SCHEMA or value.get("operationId") != config["operationId"]:
        raise Refused("checkpoint-binding")
    unsigned = dict(value); observed = unsigned.pop("planDigest", None)
    # Only immutable plan fields contribute to the plan digest; execution fields evolve.
    immutable = {key: unsigned[key] for key in ("schema", "operationId", "configDigest", "identityDigest", "archive", "initialCensusDigest", "initialCensusLocation", "baseProtectionDigest", "permanentRuleDigest", "temporaryRuleDigest", "stage", "pendingEffect", "temporaryRuleId", "permanentRuleId", "cleanupRequired", "effects", "plannedAt")}
    if immutable["stage"] != "planned" or immutable["pendingEffect"] is not None or immutable["temporaryRuleId"] is not None or immutable["permanentRuleId"] is not None or immutable["cleanupRequired"] or immutable["effects"]:
        base = dict(immutable, stage="planned", pendingEffect=None, temporaryRuleId=None, permanentRuleId=None, cleanupRequired=False, effects=[])
    else:
        base = immutable
    if observed != sha(canonical(base)):
        raise Refused("checkpoint-plan-digest")
    if value["configDigest"] != sha(canonical(immutable_config(config))):
        raise Refused("checkpoint-current-config-mismatch")
    archive = validate_archive(config)
    if value["archive"] != archive:
        raise Refused("checkpoint-current-archive-mismatch")
    validate_retained_census(value["initialCensusLocation"], value["initialCensusDigest"])
    permanent, permanent_digest = expected_rule(config, True)
    temporary, temporary_digest = expected_rule(config, False)
    if value["permanentRuleDigest"] != permanent_digest or value["temporaryRuleDigest"] != temporary_digest:
        raise Refused("checkpoint-current-rule-mismatch")
    return value


def checkpoint(config, value, stage, pending=None, **changes):
    updated = dict(value, stage=stage, pendingEffect=pending, **changes)
    write_private(config["checkpoint"], updated)
    return updated


def record_effect(config, value, name, request_digest, snapshot):
    snapshot = retain_census(config, snapshot, f"{len(value['effects']):02d}-{name}")
    effects = list(value["effects"])
    effects.append({"name": name, "requestDigest": request_digest, "observedAt": snapshot["observedAt"], "nativeCensusDigest": snapshot["nativeCensusDigest"]})
    return checkpoint(config, value, name + "-observed", None, effects=effects)


def effect_recorded(value, name):
    return any(effect.get("name") == name for effect in value.get("effects") or [])


def create_rule(config, value, permanent):
    payload, digest = expected_rule(config, permanent)
    kind = "permanent-rule" if permanent else "temporary-rule"
    snapshot = observe(config)
    current = snapshot["permanentRule" if permanent else "temporaryRule"]
    if current:
        if not rule_matches(current, payload):
            raise Refused(kind + "-drift")
        if effect_recorded(value, kind):
            expected_id = value["permanentRuleId" if permanent else "temporaryRuleId"]
            if expected_id != current["id"]:
                raise Refused(kind + "-checkpoint-id-drift")
            return value
    else:
        value = checkpoint(config, value, value["stage"], {"name": kind, "requestDigest": digest}, cleanupRequired=value["cleanupRequired"] or not permanent)
        gh_request(f"repos/{config['repository']}/rulesets", "POST", payload)
        snapshot = observe(config); current = snapshot["permanentRule" if permanent else "temporaryRule"]
        if not rule_matches(current, payload):
            retain_census(config, snapshot, f"{len(value['effects']):02d}-{kind}-refused-readback")
            raise Refused(kind + "-readback")
    rule_id = current["id"]
    value = record_effect(config, value, kind, digest, snapshot)
    return checkpoint(config, value, value["stage"], None, **({"permanentRuleId": rule_id} if permanent else {"temporaryRuleId": rule_id, "cleanupRequired": True}))


def push_retirement_head(config, archive):
    credential = native_run(["gh", "auth", "token"], "retirement-head-credential-command-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
    if credential.returncode != 0 or not credential.stdout.strip():
        raise Refused("retirement-head-credential-unavailable")
    try:
        token = credential.stdout.decode().strip()
    except UnicodeError:
        raise Refused("retirement-head-credential-invalid") from None
    environment = dict(os.environ, GIT_TERMINAL_PROMPT="0", GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_COUNT="1", GIT_CONFIG_KEY_0="http.extraHeader", GIT_CONFIG_VALUE_0="Authorization: Basic " + base64.b64encode(("x-access-token:" + token).encode()).decode())
    with tempfile.TemporaryDirectory(prefix="fsgg-retirement-push-") as scratch:
        clone = pathlib.Path(scratch) / "archive.git"
        imported = native_run(["git", "clone", "--quiet", "--bare", archive["bundleLocation"], str(clone)], "retirement-head-bundle-import-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
        if imported.returncode != 0:
            raise Refused("retirement-head-bundle-import")
        command = ["git", "-C", str(clone), "push", "--porcelain", f"--force-with-lease={config['branchRef']}:{config['candidateHead']}", f"https://github.com/{config['repository']}.git", f"{config['retirementHead']}:{config['branchRef']}"]
        return native_run(command, "retirement-head-push-command-unavailable", stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=COMMAND_TIMEOUT_SECONDS, env=environment)


def advance_retirement_head(config, value):
    digest = sha(canonical({"ref": config["branchRef"], "old": config["candidateHead"], "new": config["retirementHead"]}))
    snapshot = observe(config)
    if snapshot["pullRequestDisposition"] == "merged-exact-before-retirement":
        return value, snapshot
    if snapshot["observedBranchHead"] == config["retirementHead"] and snapshot["pullRequestHead"] == config["retirementHead"] and snapshot["retirementCommitObserved"]:
        if any(effect["name"] == "retirement-head-advanced" for effect in value["effects"]):
            return value, snapshot
        return record_effect(config, value, "retirement-head-advanced", digest, snapshot), snapshot
    if snapshot["pullRequestDisposition"] != "open":
        raise Refused("retirement-head-pr-not-open")
    if snapshot["observedBranchHead"] != config["candidateHead"] or snapshot["pullRequestHead"] != config["candidateHead"]:
        raise Refused("retirement-head-stale-native-state")
    value = checkpoint(config, value, value["stage"], {"name": "advance-retirement-head", "requestDigest": digest, "old": config["candidateHead"], "new": config["retirementHead"]})
    result = None
    try:
        result = push_retirement_head(config, value["archive"])
    except UnknownEffect:
        pass
    snapshot = observe(config)
    if snapshot["observedBranchHead"] == config["retirementHead"] and snapshot["pullRequestHead"] == config["retirementHead"] and snapshot["retirementCommitObserved"]:
        return record_effect(config, value, "retirement-head-advanced", digest, snapshot), snapshot
    if snapshot["pullRequestDisposition"] == "merged-exact-before-retirement":
        return value, snapshot
    if result is not None and result.returncode != 0:
        raise Refused("retirement-head-push-refused")
    raise UnknownEffect("retirement-head-push-outcome-unknown")


def settle_pull_request(config, value):
    snapshot = observe(config)
    if snapshot["pullRequestDisposition"] == "merged-other":
        raise Refused("unexpected-merge-outcome")
    if snapshot["pullRequestDisposition"] == "open":
        if snapshot["autoMergeEnabled"]:
            digest = sha(canonical({"pullRequestNodeId": snapshot["pullRequestNodeId"]}))
            value = checkpoint(config, value, value["stage"], {"name": "disable-auto-merge", "requestDigest": digest})
            gh_graphql_mutation("disable-auto-merge", snapshot["pullRequestNodeId"])
            snapshot = observe(config)
            if snapshot["autoMergeEnabled"]:
                raise Refused("auto-merge-disable-readback")
            value = record_effect(config, value, "auto-merge-disabled", digest, snapshot)
        if snapshot["mergeQueueEntry"]:
            digest = sha(canonical({"pullRequestNodeId": snapshot["pullRequestNodeId"], "entry": snapshot["mergeQueueEntry"]}))
            value = checkpoint(config, value, value["stage"], {"name": "dequeue-pull-request", "requestDigest": digest})
            gh_graphql_mutation("dequeue", snapshot["pullRequestNodeId"])
            snapshot = observe(config)
            if snapshot["mergeQueueEntry"]:
                raise Refused("dequeue-readback")
            value = record_effect(config, value, "pull-request-dequeued", digest, snapshot)
        payload = {"state": "closed"}; digest = sha(canonical(payload))
        value = checkpoint(config, value, value["stage"], {"name": "close-pull-request", "requestDigest": digest})
        gh_request(f"repos/{config['repository']}/pulls/{config['pullRequestNumber']}", "PATCH", payload)
        snapshot = observe(config)
    if snapshot["pullRequestDisposition"] not in ("closed-unmerged", "merged-exact-before-retirement"):
        raise Refused("pull-request-outcome-unresolved")
    if effect_recorded(value, "pull-request-settled"):
        return value, snapshot
    return record_effect(config, value, "pull-request-settled", sha(canonical({"disposition": snapshot["pullRequestDisposition"]})), snapshot), snapshot


def settle_issue(config, value, snapshot):
    reason = "completed" if snapshot["pullRequestDisposition"] == "merged-exact-before-retirement" else "not_planned"
    if snapshot["issueState"] == "closed" and snapshot["issueStateReason"] == reason:
        if effect_recorded(value, "issue-settled"):
            return value
        return record_effect(config, value, "issue-settled", sha(canonical({"state": "closed", "state_reason": reason})), observe(config))
    payload = {"state": "closed", "state_reason": reason}; digest = sha(canonical(payload))
    value = checkpoint(config, value, value["stage"], {"name": "close-issue", "requestDigest": digest})
    gh_request(f"repos/{config['repository']}/issues/{config['issueNumber']}", "PATCH", payload)
    observed = observe(config)
    if observed["issueState"] != "closed" or observed["issueStateReason"] != reason:
        raise Refused("issue-settlement-readback")
    return record_effect(config, value, "issue-settled", digest, observed)


def terminal(config, checkpoint_value, snapshot, allow_temporary):
    permanent, _ = expected_rule(config, True)
    temporary, _ = expected_rule(config, False)
    if snapshot["pullRequestDisposition"] not in ("closed-unmerged", "merged-exact-before-retirement"):
        raise Refused("pull-request-outcome-unresolved")
    if snapshot["pullRequestDisposition"] == "closed-unmerged" and (snapshot["pullRequestHead"] != config["retirementHead"] or snapshot["observedBranchHead"] != config["retirementHead"] or not snapshot["retirementCommitObserved"]):
        raise Refused("retirement-head-fence-missing")
    if snapshot["pullRequestDisposition"] == "merged-exact-before-retirement" and snapshot["pullRequestHead"] != config["candidateHead"]:
        raise Refused("merged-candidate-head-mismatch")
    if snapshot["autoMergeEnabled"] or snapshot["mergeQueueEntry"]:
        raise Refused("pull-request-mutation-enabled")
    if not rule_matches(snapshot["permanentRule"], permanent):
        raise Refused("permanent-fence-missing")
    if allow_temporary:
        if not rule_matches(snapshot["temporaryRule"], temporary):
            raise Refused("temporary-main-hold-missing")
    elif snapshot["temporaryRule"] is not None:
        raise Refused("temporary-main-hold-still-active")
    reason = "completed" if snapshot["pullRequestDisposition"] == "merged-exact-before-retirement" else "not_planned"
    if snapshot["issueState"] != "closed" or snapshot["issueStateReason"] != reason:
        raise Refused("issue-disposition")
    if not snapshot["subjectExclusion"] or not snapshot["nativeCensus"]["complete"]:
        raise Refused("terminal-evidence-incomplete")
    if snapshot["baseProtectionDigest"] != checkpoint_value["baseProtectionDigest"]:
        raise Refused("base-protection-drift")
    return snapshot


def finish_cleanup(config, value):
    snapshot = observe(config)
    temporary = snapshot["temporaryRule"]
    pending = value.get("pendingEffect") or {}
    expected_id = pending.get("ruleId") or value.get("temporaryRuleId")
    if temporary is not None:
        payload, _ = expected_rule(config, False)
        if not rule_matches(temporary, payload) or expected_id not in (None, temporary["id"]):
            raise Refused("temporary-main-hold-cleanup-identity")
        value = checkpoint(config, value, value["stage"], {"name": "remove-temporary-main-hold", "ruleId": temporary["id"]}, cleanupRequired=True)
        gh_request(f"repos/{config['repository']}/rulesets/{temporary['id']}", "DELETE")
        snapshot = observe(config)
    snapshot = retain_census(config, terminal(config, value, snapshot, False), f"{len(value['effects']):02d}-temporary-main-hold-removed")
    effects = list(value["effects"])
    effects.append({"name": "temporary-main-hold-removed", "requestDigest": sha(canonical({"ruleId": expected_id})), "observedAt": snapshot["observedAt"], "nativeCensusDigest": snapshot["nativeCensusDigest"]})
    provisional = dict(value, stage="completed", pendingEffect=None, cleanupRequired=False, temporaryRuleId=None, effects=effects)
    result = receipt(config, provisional, snapshot)
    completed = checkpoint(config, value, "completed", None, cleanupRequired=False, temporaryRuleId=None, effects=effects, completedReceipt=result, completedSnapshot=snapshot)
    return completed, snapshot, result


def paused_result(value):
    return {"schema": "fsgg.coordination.administrative-retirement-operation-progress/1", "operationId": value["operationId"], "planDigest": value["planDigest"], "stage": value["stage"], "cleanupRequired": value["cleanupRequired"]}


def execute(config, stop_after="completed"):
    value = load_checkpoint(config)
    if value["stage"] == "completed":
        terminal(config, value, retain_census(config, observe(config), "verification"), False)
        if "completedReceipt" not in value or "completedSnapshot" not in value or receipt(config, value, value["completedSnapshot"]) != value["completedReceipt"]:
            raise Refused("completed-receipt-missing")
        validate_retained_census(value["completedSnapshot"]["nativeCensusLocation"], value["completedSnapshot"]["nativeCensusDigest"], value["completedSnapshot"]["nativeCensus"])
        return value["completedReceipt"]
    if value.get("cleanupRequired") and (value.get("pendingEffect") or {}).get("name") == "remove-temporary-main-hold":
        value, snapshot, result = finish_cleanup(config, value)
        return result
    value = create_rule(config, value, False)
    if stop_after == "temporary-hold":
        return paused_result(value)
    value, _ = advance_retirement_head(config, value)
    value = create_rule(config, value, True)
    if stop_after == "permanent-fence":
        return paused_result(value)
    value, snapshot = settle_pull_request(config, value)
    validate_exclusion(config)
    value = settle_issue(config, value, snapshot)
    snapshot = terminal(config, value, observe(config), True)
    digestable_snapshot = {key: item for key, item in snapshot.items() if key != "nativeCensusRaw"}
    if not effect_recorded(value, "terminal-before-cleanup"):
        value = record_effect(config, value, "terminal-before-cleanup", sha(canonical(digestable_snapshot)), snapshot)
    if stop_after == "settled":
        return paused_result(value)
    value, snapshot, result = finish_cleanup(config, value)
    return result


def receipt(config, checkpoint_value, snapshot):
    disposition = snapshot["pullRequestDisposition"]
    identity_digest = sha(framed([config["repository"], config["repositoryId"], config["issueNumber"], config["pullRequestNumber"], config["branchRef"], config["baseRef"], config["candidateHead"], config["candidateTree"], config["candidateParent"], config["retirementHead"], config["retirementTree"], config["retirementParent"], config["acceptedClientCommit"], config["acceptedClientArtifactDigest"], config["operationAuthorityDigest"]]).encode())
    permanent = snapshot["permanentRule"]
    observation_values = [identity_digest, snapshot["observedAt"], dotnet_bool(True), disposition, snapshot["pullRequestHead"], snapshot.get("mergeCommit") or "", snapshot.get("mergedCandidateHead") or "", snapshot.get("mergedCandidateTree") or "", snapshot.get("mergedCandidateParent") or "", snapshot.get("protectedBaseRef") if disposition == "merged-exact-before-retirement" else "", snapshot.get("deliveredPathDigest") or "", snapshot.get("observedBranchHead") or "", dotnet_bool(snapshot["retirementCommitObserved"]), dotnet_bool(snapshot["autoMergeEnabled"]), snapshot.get("mergeQueueEntry") or "", checkpoint_value["archive"]["digest"], checkpoint_value["archive"]["location"], config["candidateHead"], config["candidateTree"], config["candidateParent"], dotnet_bool(True), snapshot["nativeCensusDigest"], snapshot["nativeCensusLocation"], permanent["id"], checkpoint_value["permanentRuleDigest"], dotnet_bool(True), permanent["include"][0], ",".join(sorted(permanent["rules"])), dotnet_bool(bool(permanent["bypass"])), "", "", "", "", dotnet_bool(False), dotnet_bool(True), "closed-completed" if disposition == "merged-exact-before-retirement" else "closed-not-planned"]
    observation_digest = sha(framed(observation_values).encode())
    typed = {"Schema": "fsgg.coordination.administrative-retirement/1", "IdentityDigest": identity_digest, "NativeCensusDigest": snapshot["nativeCensusDigest"], "ObservationDigest": observation_digest, "Disposition": disposition, "CandidateHead": config["candidateHead"], "CandidateArchiveDigest": checkpoint_value["archive"]["digest"], "RetirementHead": config["retirementHead"], "FrozenBranchHead": snapshot.get("observedBranchHead"), "BranchFenceRuleId": permanent["id"], "SubjectExcluded": True, "OriginalJournalAvailable": False, "OriginalCompletionRecorded": False, "OriginalUsageKnown": False, "RetiredAt": snapshot["observedAt"]}
    typed["ReceiptDigest"] = sha(framed([typed["Schema"], typed["IdentityDigest"], typed["NativeCensusDigest"], typed["ObservationDigest"], disposition, typed["CandidateHead"], typed["CandidateArchiveDigest"], typed["RetirementHead"], typed.get("FrozenBranchHead") or "", typed["BranchFenceRuleId"], dotnet_bool(True), dotnet_bool(False), dotnet_bool(False), dotnet_bool(False), typed["RetiredAt"]]).encode())
    typed_identity = {"Repository": config["repository"], "RepositoryId": config["repositoryId"], "IssueNumber": config["issueNumber"], "PullRequestNumber": config["pullRequestNumber"], "BranchRef": config["branchRef"], "ProtectedBaseRef": config["baseRef"], "CandidateHead": config["candidateHead"], "CandidateTree": config["candidateTree"], "CandidateParent": config["candidateParent"], "RetirementHead": config["retirementHead"], "RetirementTree": config["retirementTree"], "RetirementParent": config["retirementParent"], "AcceptedClientCommit": config["acceptedClientCommit"], "AcceptedClientArtifactDigest": config["acceptedClientArtifactDigest"], "OperationAuthorityDigest": config["operationAuthorityDigest"]}
    typed_observation = {"ObservedAt": snapshot["observedAt"], "CompleteNativeCensus": True, "PullRequestDisposition": disposition, "PullRequestHead": snapshot["pullRequestHead"], "MergeCommit": snapshot.get("mergeCommit"), "MergedCandidateHead": snapshot.get("mergedCandidateHead"), "MergedCandidateTree": snapshot.get("mergedCandidateTree"), "MergedCandidateParent": snapshot.get("mergedCandidateParent"), "ProtectedBaseRef": snapshot.get("protectedBaseRef") if disposition == "merged-exact-before-retirement" else None, "DeliveredPathDigest": snapshot.get("deliveredPathDigest"), "ObservedBranchHead": snapshot.get("observedBranchHead"), "RetirementCommitObserved": snapshot["retirementCommitObserved"], "AutoMergeEnabled": False, "MergeQueueEntry": None, "CandidateArchiveDigest": checkpoint_value["archive"]["digest"], "CandidateArchiveLocation": checkpoint_value["archive"]["location"], "ArchivedHead": config["candidateHead"], "ArchivedTree": config["candidateTree"], "ArchivedParent": config["candidateParent"], "CandidateArchiveIndependent": True, "NativeCensusDigest": snapshot["nativeCensusDigest"], "NativeCensusLocation": snapshot["nativeCensusLocation"], "BranchFenceRuleId": permanent["id"], "BranchFenceDigest": checkpoint_value["permanentRuleDigest"], "BranchFenceRef": permanent["include"][0], "BranchFenceRules": sorted(permanent["rules"]), "BranchFenceActive": True, "BranchFenceHasBypass": bool(permanent["bypass"]), "TemporaryMainRuleId": None, "TemporaryMainRuleDigest": None, "TemporaryMainRuleRef": None, "TemporaryMainRuleRules": [], "TemporaryMainRuleActive": False, "SubjectExcluded": True, "IssueDisposition": "closed-completed" if disposition == "merged-exact-before-retirement" else "closed-not-planned"}
    value = {"schema": RECEIPT_SCHEMA, "result": "AdministrativelyRetiredWithLostHistory", "identityDigest": checkpoint_value["identityDigest"], "planDigest": checkpoint_value["planDigest"], "nativeCensusDigest": snapshot["nativeCensusDigest"], "nativeCensusLocation": snapshot["nativeCensusLocation"], "candidateArchiveDigest": checkpoint_value["archive"]["digest"], "candidateHead": config["candidateHead"], "retirementHead": config["retirementHead"], "frozenBranchHead": snapshot.get("observedBranchHead"), "pullRequestDisposition": disposition, "branchFenceRuleId": permanent["id"], "subjectExcluded": True, "originalJournalAvailable": False, "originalCompletionRecorded": False, "originalUsageKnown": False, "retiredAt": snapshot["observedAt"], "typedIdentity": typed_identity, "typedObservation": typed_observation, "typedReceipt": typed}
    value["receiptDigest"] = sha(canonical(value))
    return value


def verify(config):
    value = load_checkpoint(config)
    if value["stage"] != "completed" or value["cleanupRequired"]:
        raise Refused("operation-not-complete")
    terminal(config, value, retain_census(config, observe(config), "verification"), False)
    if "completedReceipt" not in value or "completedSnapshot" not in value or receipt(config, value, value["completedSnapshot"]) != value["completedReceipt"]:
        raise Refused("completed-receipt-missing")
    validate_retained_census(value["completedSnapshot"]["nativeCensusLocation"], value["completedSnapshot"]["nativeCensusDigest"], value["completedSnapshot"]["nativeCensus"])
    return value["completedReceipt"]


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("plan", "execute", "verify"))
    parser.add_argument("--config", required=True)
    parser.add_argument("--output")
    parser.add_argument("--stop-after", choices=("temporary-hold", "permanent-fence", "settled", "completed"), default="completed")
    args = parser.parse_args(argv)
    config = validate_config(read_json(args.config))
    with operation_lock(config):
        result = plan(config) if args.mode == "plan" else execute(config, args.stop_after) if args.mode == "execute" else verify(config)
    if args.output:
        write_private(args.output, result)
    print(canonical(result).decode())
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (Refused, UnknownEffect) as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(2)
