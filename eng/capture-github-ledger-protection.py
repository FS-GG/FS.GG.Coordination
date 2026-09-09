#!/usr/bin/env python3
"""Capture the fixed GS2-08.2 read-only GitHub observation surface."""

import argparse
import base64
import datetime as dt
import hashlib
import json
import pathlib
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.error
import urllib.request

AUTHORITY = "FS-GG/FS.GG.Coordination.Authority"
AUTHORITY_ID = 1351660651
FLEET_BRANCH = "fsgg/v2/journal/cutover/d5"
TAG_PREFIX = "tags/fsgg/v2/fleet-cutover/"
API_ROOT = "https://api.github.com"

GRAPHQL = """query($cursor:String){repository(owner:\"FS-GG\",name:\"FS.GG.Coordination.Authority\"){branchProtectionRules(first:100,after:$cursor){nodes{id pattern allowsDeletions allowsForcePushes isAdminEnforced requiresApprovingReviews requiresCodeOwnerReviews requiredApprovingReviewCount}pageInfo{hasNextPage endCursor}}}}"""

def sha(value):
    return hashlib.sha256(value).hexdigest()

def frame(value):
    raw = value.encode("utf-8")
    return str(len(raw)).encode() + b":" + raw

def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()

def b64url(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")

def provider_refusal(code=None, reason="provider-read-refused"):
    state = "proven-absent" if code == 404 else "unknown"
    return {"state": state, "httpStatus": code, "reason": reason}

def invoke(args):
    completed = subprocess.run(["gh", "api", *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if completed.returncode != 0:
        status = re.search(rb"(?:HTTP |status code: )(\d{3})", completed.stderr)
        code = int(status.group(1)) if status else None
        return completed.stdout, provider_refusal(code)
    try:
        return completed.stdout, json.loads(completed.stdout)
    except json.JSONDecodeError:
        return completed.stdout, {"state": "unknown", "httpStatus": None, "reason": "provider-json-unreadable"}

def rest(path, paginate=False):
    args = [path]
    if paginate:
        args += ["--paginate", "--slurp"]
    return invoke(args)

def app_request(path, token, method="GET", body=None, retain_raw=True):
    request = urllib.request.Request(
        API_ROOT + path,
        data=canonical(body) if body is not None else None,
        method=method,
        headers={"Accept": "application/vnd.github+json", "Authorization": "Bearer " + token,
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "fsgg-ledger-protection-capture"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
        return raw if retain_raw else b"", json.loads(raw)
    except urllib.error.HTTPError as error:
        return b"", provider_refusal(error.code)
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
        return b"", provider_refusal()

def mint_installation_token(app_id, installation_id, key_fd):
    now = int(time.time())
    header = b64url(canonical({"alg":"RS256", "typ":"JWT"}))
    payload = b64url(canonical({"iat":now-60, "exp":now+540, "iss":app_id}))
    signing_input = (header + "." + payload).encode("ascii")
    try:
        signed = subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", f"/proc/self/fd/{key_fd}"],
            input=signing_input, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            pass_fds=(key_fd,), check=False)
    except (OSError, ValueError):
        return None
    if signed.returncode != 0 or not signed.stdout:
        return None
    jwt = header + "." + payload + "." + b64url(signed.stdout)
    _, response = app_request(
        f"/app/installations/{installation_id}/access_tokens", jwt, "POST",
        {"repository_ids":[AUTHORITY_ID], "permissions":{"contents":"read"}}, retain_raw=False)
    token = response.get("token") if isinstance(response, dict) else None
    return token if isinstance(token, str) and token else None

def app_selected_repositories(app_id, installation_id, key_fd):
    token = mint_installation_token(app_id, installation_id, key_fd)
    if token is None:
        return b"", provider_refusal(reason="app-installation-token-refused")
    raw = bytearray()
    pages = []
    page = 1
    while True:
        page_raw, value = app_request(f"/installation/repositories?per_page=100&page={page}", token)
        raw.extend(page_raw)
        if refused(value):
            return bytes(raw), value
        repositories = value.get("repositories") if isinstance(value, dict) else None
        if not isinstance(repositories, list):
            return bytes(raw), provider_refusal(reason="provider-json-unreadable")
        pages.append(value)
        total = value.get("total_count")
        observed = sum(len(item.get("repositories") or []) for item in pages)
        if (isinstance(total, int) and observed >= total) or (not isinstance(total, int) and len(repositories) < 100):
            return bytes(raw), pages
        page += 1

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

def environment_detail(value, policy_values):
    gaps = []
    protection_rules = value.get("protection_rules")
    if not isinstance(protection_rules, list):
        gaps.append("fleet-cutover-protection-rules")
        protection_rules = []
    if any(not isinstance(rule, dict) for rule in protection_rules):
        gaps.append("fleet-cutover-protection-rules")
    protection_rules = [rule for rule in protection_rules if isinstance(rule, dict)]
    required_reviewers = [rule for rule in protection_rules if rule.get("type") == "required_reviewers"]
    reviewer_rule = required_reviewers[0] if len(required_reviewers) == 1 else None
    if reviewer_rule is None:
        gaps.append("fleet-cutover-required-reviewers-rule")
    prevent_self_review = reviewer_rule.get("prevent_self_review") if reviewer_rule is not None else None
    if not isinstance(prevent_self_review, bool):
        gaps.append("fleet-cutover-prevent-self-review")
        prevent_self_review = None
    reviewers = reviewer_rule.get("reviewers") if reviewer_rule is not None else None
    if not isinstance(reviewers, list):
        gaps.append("fleet-cutover-reviewers")
        reviewers = []
    reviewer_ids = []
    for reviewer in reviewers:
        subject = reviewer.get("reviewer") if isinstance(reviewer, dict) else None
        identifier = subject.get("id") if isinstance(subject, dict) else None
        if type(identifier) is not int or identifier <= 0:
            gaps.append("fleet-cutover-reviewer-id")
        else:
            reviewer_ids.append(identifier)
    branch_policy = value.get("deployment_branch_policy") or {}
    normalized = {
        "name": value.get("name"),
        "protectionRuleTypes": sorted([rule.get("type") for rule in protection_rules]),
        "reviewerIds": sorted(reviewer_ids),
        "preventSelfReview": prevent_self_review,
        "canAdminsBypass": bool(value.get("can_admins_bypass")),
        "protectedBranches": bool(branch_policy.get("protected_branches")),
        "customBranchPolicies": bool(branch_policy.get("custom_branch_policies")),
        "deploymentBranchPatterns": sorted([policy.get("name") for policy in policy_values])}
    return normalized, sorted(set(gaps))

def resource(identifier, raw, normalized, complete=True, state="observed"):
    normalized_bytes = canonical(normalized)
    return {"id": identifier, "state": state, "pagesComplete": complete, "rawSha256": sha(raw), "normalizedSha256": sha(normalized_bytes), "normalized": normalized}

def capture(app_key_fds, control_issue_number):
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
    resources.append(resource("fleet-head-refs", raw_heads, sorted([{"name":x.get("ref"),"objectSha":(x.get("object") or {}).get("sha")} for x in flatten_pages(heads)], key=lambda x: (x["name"] or "",x["objectSha"] or "")), not refused(heads), "unknown" if refused(heads) else "observed"))
    raw_tags, tags = rest(f"repos/{AUTHORITY}/git/matching-refs/{TAG_PREFIX}?per_page=100", True)
    if refused(tags): gaps.append("phase-tags")
    resources.append(resource("phase-tags", raw_tags, sorted([{"name":x.get("ref"),"objectSha":(x.get("object") or {}).get("sha")} for x in flatten_pages(tags)], key=lambda x: (x["name"] or "",x["objectSha"] or "")), not refused(tags), "unknown" if refused(tags) else "observed"))

    raw_envs, env_pages = rest("repos/FS-GG/.github/environments?per_page=100", True)
    if refused(env_pages): gaps.append("environments")
    env_values = flatten_pages(env_pages)
    names = sorted([x.get("name") for page in env_values for x in (page.get("environments") or [])]) if env_values and isinstance(env_values[0], dict) else []
    env_normalized = {"names": names, "fleetCutover": None}
    env_raw = bytearray(raw_envs)
    if "fleet-cutover" in names:
        raw_detail, detail = rest("repos/FS-GG/.github/environments/fleet-cutover")
        env_raw.extend(raw_detail)
        raw_policies, policies = rest("repos/FS-GG/.github/environments/fleet-cutover/deployment-branch-policies?per_page=100", True)
        env_raw.extend(raw_policies)
        if refused(policies): gaps.append("fleet-cutover-deployment-branch-policies")
        policy_values = [policy for page in flatten_pages(policies) for policy in (page.get("branch_policies") or [])] if flatten_pages(policies) and isinstance(flatten_pages(policies)[0], dict) else []
        env_normalized["fleetCutover"], environment_gaps = environment_detail(detail, policy_values)
        gaps.extend(environment_gaps)
    environment_unknown = refused(env_pages) or any(value.startswith("fleet-cutover-") for value in gaps)
    resources.append(resource("environments", bytes(env_raw), env_normalized, not environment_unknown, "unknown" if environment_unknown else "observed"))

    raw_installations, installation_pages = rest("orgs/FS-GG/installations?per_page=100", True)
    if refused(installation_pages): gaps.append("organization-installations")
    installations = []
    desired = json.loads((pathlib.Path(__file__).resolve().parent.parent / "evidence/github-substrate-v2/gs2-08-2/desired-policy.json").read_bytes())
    desired_roles = {role: desired.get(role, {}) for role in ("ordinaryWriter", "cutoverWriter")}
    desired_apps = {value.get("appId") for value in desired_roles.values()} - {None}
    expected_installations = {value.get("appId"): value.get("installationId") for value in desired_roles.values() if value.get("appId") is not None}
    observed_desired_apps = set()
    installation_values = [installation for page in flatten_pages(installation_pages) for installation in (page.get("installations") or [])] if flatten_pages(installation_pages) and isinstance(flatten_pages(installation_pages)[0], dict) and "installations" in flatten_pages(installation_pages)[0] else flatten_pages(installation_pages)
    for value in installation_values:
        selected = None
        if value.get("app_id") in desired_apps:
            app_id = value.get("app_id")
            observed_desired_apps.add(app_id)
            if value.get("id") != expected_installations.get(app_id):
                gaps.append(f"installation-id:{app_id}")
            key_fd = app_key_fds.get(app_id)
            if key_fd is None:
                raw_selected, selected_pages = rest(f"user/installations/{value.get('id')}/repositories?per_page=100", True)
                selected_endpoint = f"GET /user/installations/{value.get('id')}/repositories?per_page=100"
            else:
                raw_selected, selected_pages = app_selected_repositories(app_id, value.get("id"), key_fd)
                selected_endpoint = "GET /installation/repositories?per_page=100"
            raw_installations += raw_selected
            if refused(selected_pages): gaps.append(f"selected-repositories:{value.get('app_id')}")
            else:
                selected_values = [repo for page in flatten_pages(selected_pages) for repo in (page.get("repositories") or [])] if flatten_pages(selected_pages) and isinstance(flatten_pages(selected_pages)[0], dict) else []
                selected = sorted([repo.get("full_name") for repo in selected_values])
        else:
            selected_endpoint = None
        installations.append({"installationId": value.get("id"), "appId": value.get("app_id"), "slug": value.get("app_slug"), "repositorySelection": value.get("repository_selection"), "permissions": value.get("permissions") or {}, "selectedRepositoriesEndpoint": selected_endpoint, "selectedRepositories": selected})
    for role,value in desired_roles.items():
        if value.get("appId") not in observed_desired_apps: gaps.append(f"desired-installation-missing:{role}")
    resources.append(resource("organization-installations", raw_installations, sorted(installations, key=lambda x: x["installationId"] or 0), not refused(installation_pages), "unknown" if refused(installation_pages) else "observed"))

    raw_issues, issue_pages = rest(f"repos/{AUTHORITY}/issues?state=all&per_page=100", True)
    if refused(issue_pages): gaps.append("authority-issues")
    issues = [{"number": x.get("number"), "isPullRequest": "pull_request" in x} for x in flatten_pages(issue_pages)]
    if control_issue_number is not None and not any(value["number"] == control_issue_number and not value["isPullRequest"] for value in issues):
        gaps.append("bound-control-issue")
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
    bindings = {
        "ordinaryWriterAppId": desired_roles["ordinaryWriter"].get("appId"),
        "ordinaryWriterInstallationId": desired_roles["ordinaryWriter"].get("installationId"),
        "cutoverWriterAppId": desired_roles["cutoverWriter"].get("appId"),
        "cutoverWriterInstallationId": desired_roles["cutoverWriter"].get("installationId"),
        "controlIssueNumber": control_issue_number}
    return revision, resources, sorted(set(gaps)), bindings

def aggregate(resources, field):
    return sha(b"".join(frame(value["id"]) + frame(value[field]) for value in sorted(resources, key=lambda x: x["id"])))

def compare_continuity(previous, normalized_set, bindings):
    gaps = []
    continuity = "matched" if previous.get("normalizedSetSha256") == normalized_set else "drift"
    if previous.get("bindings") != bindings:
        continuity = "drift"
        gaps.append("binding-drift")
    return continuity, gaps

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--previous")
    parser.add_argument("--ordinary-app-key-fd", type=int)
    parser.add_argument("--cutover-app-key-fd", type=int)
    parser.add_argument("--control-issue-number", type=int)
    args = parser.parse_args()
    if args.control_issue_number is not None and args.control_issue_number <= 0:
        parser.error("the control issue number must be positive")
    desired = json.loads((pathlib.Path(__file__).resolve().parent.parent / "evidence/github-substrate-v2/gs2-08-2/desired-policy.json").read_bytes())
    app_key_fds = {}
    for role,fd in (("ordinaryWriter",args.ordinary_app_key_fd),("cutoverWriter",args.cutover_app_key_fd)):
        if fd is not None:
            if fd < 3:
                parser.error("App private keys must be supplied on inherited file descriptors >= 3")
            app_key_fds[desired[role]["appId"]] = fd
    revision, resources, gaps, bindings = capture(app_key_fds, args.control_issue_number)
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
        continuity, continuity_gaps = compare_continuity(previous, normalized_set, bindings)
        gaps.extend(continuity_gaps)
        capture_pass = 2
        if continuity != "matched": gaps.append("normalized-set-drift")
    evidence = {"schema":"fsgg.github-ledger-protection-live-capture/v1", "capturePass":capture_pass,
                "repository":AUTHORITY, "repositoryId":AUTHORITY_ID, "revision":revision,
                "capturedAt":dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00","Z"),
                "continuity":continuity, "previousEvidenceSha256":previous_sha, "previousNormalizedSetSha256":previous_normalized,
                "rawSetSha256":raw_set, "normalizedSetSha256":normalized_set, "resources":resources,
                "bindings":bindings,
                "gaps":sorted(set(gaps)), "providerReadbackClaimed":True, "writesAttempted":0, "applyAuthorized":False}
    output = pathlib.Path(args.output)
    output.write_bytes(canonical(evidence) + b"\n")
    output.chmod(0o600)
    print(f"GITHUB_LEDGER_CAPTURE pass={capture_pass} continuity={continuity} resources={len(resources)} gaps={len(evidence['gaps'])} raw={raw_set} normalized={normalized_set}")
    return 0 if not gaps and (capture_pass == 1 or continuity == "matched") else 2

if __name__ == "__main__":
    sys.exit(main())
