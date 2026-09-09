#!/usr/bin/env python3
"""Capture the fixed GS2-08.2 read-only GitHub observation surface."""

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import re
import subprocess
import sys
import urllib.parse

AUTHORITY = "FS-GG/FS.GG.Coordination.Authority"
AUTHORITY_ID = 1351660651
FLEET_BRANCH = "fsgg/v2/journal/cutover/d5"
TAG_PREFIX = "tags/fsgg/v2/fleet-cutover/"
DEDICATED_APP_ID = None
CONTROL_ISSUE_NUMBER = None

GRAPHQL = """query($cursor:String){repository(owner:\"FS-GG\",name:\"FS.GG.Coordination.Authority\"){branchProtectionRules(first:100,after:$cursor){nodes{id pattern allowsDeletions allowsForcePushes isAdminEnforced requiresApprovingReviews requiresCodeOwnerReviews requiredApprovingReviewCount}pageInfo{hasNextPage endCursor}}}}"""

def sha(value):
    return hashlib.sha256(value).hexdigest()

def frame(value):
    raw = value.encode("utf-8")
    return str(len(raw)).encode() + b":" + raw

def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()

def invoke(args):
    completed = subprocess.run(["gh", "api", *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if completed.returncode != 0:
        status = re.search(rb"(?:HTTP |status code: )(\d{3})", completed.stderr)
        code = int(status.group(1)) if status else None
        state = "proven-absent" if code == 404 else "unknown"
        return completed.stdout, {"state": state, "httpStatus": code, "reason": "provider-read-refused"}
    try:
        return completed.stdout, json.loads(completed.stdout)
    except json.JSONDecodeError:
        return completed.stdout, {"state": "unknown", "httpStatus": None, "reason": "provider-json-unreadable"}

def rest(path, paginate=False):
    args = [path]
    if paginate:
        args += ["--paginate", "--slurp"]
    return invoke(args)

def flatten_pages(value):
    if isinstance(value, list) and (not value or isinstance(value[0], list)):
        return [item for page in value for item in page]
    return value if isinstance(value, list) else []

def refused(value):
    return isinstance(value, dict) and value.get("state") in ("unknown", "proven-absent")

def ruleset(value):
    conditions = value.get("conditions") or {}
    refs = conditions.get("ref_name") or {}
    source = value.get("source")
    bypass = []
    for actor in value.get("bypass_actors") or []:
        bypass.append({"actorId": actor.get("actor_id"), "actorType": actor.get("actor_type"), "bypassMode": actor.get("bypass_mode")})
    return {"id": value.get("id"), "name": value.get("name"), "target": value.get("target"), "enforcement": value.get("enforcement"),
            "inherited": value.get("source_type") != "Repository" or source not in (None, AUTHORITY),
            "include": refs.get("include") or [], "exclude": refs.get("exclude") or [],
            "rules": sorted([rule.get("type") for rule in value.get("rules") or []]), "bypassActors": sorted(bypass, key=lambda x: (x["actorType"] or "", x["actorId"] or 0, x["bypassMode"] or ""))}

def resource(identifier, raw, normalized, complete=True, state="observed"):
    normalized_bytes = canonical(normalized)
    return {"id": identifier, "state": state, "pagesComplete": complete, "rawSha256": sha(raw), "normalizedSha256": sha(normalized_bytes), "normalized": normalized}

def capture():
    resources = []
    gaps = []

    raw_repo, repo = rest(f"repos/{AUTHORITY}")
    if repo.get("state") == "unknown" or repo.get("id") != AUTHORITY_ID or repo.get("full_name") != AUTHORITY: gaps.append("authority-repository")
    resources.append(resource("authority-repository", raw_repo, {"id": repo.get("id"), "fullName": repo.get("full_name")}, repo.get("state") != "unknown", repo.get("state", "observed")))

    raw_commit, commit = rest(f"repos/{AUTHORITY}/commits/main")
    revision = commit.get("sha") if isinstance(commit, dict) else None
    if not isinstance(revision, str) or not re.fullmatch(r"[0-9a-f]{40}", revision): gaps.append("authority-revision")
    resources.append(resource("authority-revision", raw_commit, {"sha": revision}, "authority-revision" not in gaps, "observed" if "authority-revision" not in gaps else "unknown"))

    raw_rules, listed = rest(f"repos/{AUTHORITY}/rulesets?includes_parents=true&per_page=100", True)
    if refused(listed): gaps.append("rulesets-list")
    summaries = flatten_pages(listed)
    details = []
    detail_raw = bytearray(raw_rules)
    for summary in summaries:
        identifier = summary.get("id")
        if not isinstance(identifier, int):
            gaps.append("ruleset-id")
            continue
        raw_detail, detail = rest(f"repos/{AUTHORITY}/rulesets/{identifier}")
        detail_raw.extend(raw_detail)
        if detail.get("state") == "unknown": gaps.append(f"ruleset-detail:{identifier}")
        else: details.append(ruleset(detail))
    resources.append(resource("rulesets", bytes(detail_raw), sorted(details, key=lambda x: x["id"] or 0), not any(x.startswith("ruleset") for x in gaps), "unknown" if "rulesets-list" in gaps else "observed"))

    encoded = urllib.parse.quote(FLEET_BRANCH, safe="")
    raw_effective, effective = rest(f"repos/{AUTHORITY}/rules/branches/{encoded}")
    effective_state = effective.get("state", "observed") if isinstance(effective, dict) else "observed"
    effective_normalized = [{"type": value.get("type")} for value in effective] if isinstance(effective, list) else effective
    resources.append(resource("effective-branch-rules", raw_effective, effective_normalized, effective_state != "unknown", effective_state))
    raw_classic, classic = rest(f"repos/{AUTHORITY}/branches/{encoded}/protection")
    classic_state = classic.get("state", "observed")
    classic_normalized = classic if classic_state != "observed" else {"enforceAdmins": bool((classic.get("enforce_admins") or {}).get("enabled")), "requiredStatusChecks": sorted(((classic.get("required_status_checks") or {}).get("contexts") or [])), "restrictionsPresent": classic.get("restrictions") is not None}
    resources.append(resource("classic-branch-protection", raw_classic, classic_normalized, classic_state != "unknown", classic_state))

    raw_heads, heads = rest(f"repos/{AUTHORITY}/git/matching-refs/heads/{FLEET_BRANCH}?per_page=100", True)
    if refused(heads): gaps.append("fleet-head-refs")
    resources.append(resource("fleet-head-refs", raw_heads, sorted([x.get("ref") for x in flatten_pages(heads)]), not refused(heads), "unknown" if refused(heads) else "observed"))
    raw_tags, tags = rest(f"repos/{AUTHORITY}/git/matching-refs/{TAG_PREFIX}?per_page=100", True)
    if refused(tags): gaps.append("phase-tags")
    resources.append(resource("phase-tags", raw_tags, sorted([x.get("ref") for x in flatten_pages(tags)]), not refused(tags), "unknown" if refused(tags) else "observed"))

    raw_envs, env_pages = rest("repos/FS-GG/.github/environments?per_page=100", True)
    if refused(env_pages): gaps.append("environments")
    env_values = flatten_pages(env_pages)
    names = sorted([x.get("name") for page in env_values for x in (page.get("environments") or [])]) if env_values and isinstance(env_values[0], dict) else []
    env_normalized = {"names": names, "fleetCutover": None}
    env_raw = bytearray(raw_envs)
    if "fleet-cutover" in names:
        raw_detail, detail = rest("repos/FS-GG/.github/environments/fleet-cutover")
        env_raw.extend(raw_detail)
        env_normalized["fleetCutover"] = {"name": detail.get("name"), "protectionRuleTypes": sorted([value.get("type") for value in detail.get("protection_rules") or []]), "reviewers": sorted([{"type": value.get("type"), "id": (value.get("reviewer") or {}).get("id")} for value in detail.get("reviewers") or []], key=lambda value: (value["type"] or "", value["id"] or 0))}
    resources.append(resource("environments", bytes(env_raw), env_normalized, not refused(env_pages), "unknown" if refused(env_pages) else "observed"))

    raw_installations, installation_pages = rest("orgs/FS-GG/installations?per_page=100", True)
    if refused(installation_pages): gaps.append("organization-installations")
    installations = []
    installation_values = [installation for page in flatten_pages(installation_pages) for installation in (page.get("installations") or [])] if flatten_pages(installation_pages) and isinstance(flatten_pages(installation_pages)[0], dict) and "installations" in flatten_pages(installation_pages)[0] else flatten_pages(installation_pages)
    for value in installation_values:
        installations.append({"installationId": value.get("id"), "appId": value.get("app_id"), "slug": value.get("app_slug"), "repositorySelection": value.get("repository_selection"), "permissions": value.get("permissions") or {}, "selectedRepositories": None})
    resources.append(resource("organization-installations", raw_installations, sorted(installations, key=lambda x: x["installationId"] or 0), not refused(installation_pages), "unknown" if refused(installation_pages) else "observed"))

    raw_issues, issue_pages = rest(f"repos/{AUTHORITY}/issues?state=all&per_page=100", True)
    if refused(issue_pages): gaps.append("authority-issues")
    issues = [{"number": x.get("number"), "isPullRequest": "pull_request" in x} for x in flatten_pages(issue_pages)]
    resources.append(resource("authority-issues", raw_issues, sorted(issues, key=lambda x: x["number"] or 0), not refused(issue_pages), "unknown" if refused(issue_pages) else "observed"))

    cursor = None
    graphql_nodes = []
    graphql_raw = bytearray()
    while True:
        args = ["graphql", "-f", f"query={GRAPHQL}"]
        if cursor:
            args += ["-F", f"cursor={cursor}"]
        raw_graphql, graph = invoke(args)
        graphql_raw.extend(raw_graphql)
        try:
            connection = graph["data"]["repository"]["branchProtectionRules"]
            graphql_nodes.extend(connection["nodes"])
            if not connection["pageInfo"]["hasNextPage"]: break
            cursor = connection["pageInfo"]["endCursor"]
            if not cursor: raise KeyError("endCursor")
        except (KeyError, TypeError):
            gaps.append("branch-protection-rules")
            break
    resources.append(resource("branch-protection-rules", bytes(graphql_raw), sorted(graphql_nodes, key=lambda x: x.get("id") or ""), "branch-protection-rules" not in gaps))
    return revision, resources, sorted(set(gaps))

def aggregate(resources, field):
    return sha(b"".join(frame(value["id"]) + frame(value[field]) for value in sorted(resources, key=lambda x: x["id"])))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--previous")
    args = parser.parse_args()
    revision, resources, gaps = capture()
    raw_set = aggregate(resources, "rawSha256")
    normalized_set = aggregate(resources, "normalizedSha256")
    previous_sha = None
    previous_normalized = None
    continuity = "uninitialized"
    capture_pass = 1
    if args.previous:
        previous_bytes = pathlib.Path(args.previous).read_bytes()
        previous_sha = sha(previous_bytes)
        previous = json.loads(previous_bytes)
        previous_normalized = previous.get("normalizedSetSha256")
        continuity = "matched" if previous_normalized == normalized_set else "drift"
        capture_pass = 2
        if continuity != "matched": gaps.append("normalized-set-drift")
    evidence = {"schema":"fsgg.github-ledger-protection-live-capture/v1", "capturePass":capture_pass,
                "repository":AUTHORITY, "repositoryId":AUTHORITY_ID, "revision":revision,
                "capturedAt":dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00","Z"),
                "continuity":continuity, "previousEvidenceSha256":previous_sha, "previousNormalizedSetSha256":previous_normalized,
                "rawSetSha256":raw_set, "normalizedSetSha256":normalized_set, "resources":resources,
                "gaps":sorted(set(gaps)), "providerReadbackClaimed":True, "writesAttempted":0, "applyAuthorized":False}
    pathlib.Path(args.output).write_bytes(canonical(evidence) + b"\n")
    print(f"GITHUB_LEDGER_CAPTURE pass={capture_pass} continuity={continuity} resources={len(resources)} gaps={len(evidence['gaps'])} raw={raw_set} normalized={normalized_set}")
    return 0 if not gaps and (capture_pass == 1 or continuity == "matched") else 2

if __name__ == "__main__":
    sys.exit(main())
