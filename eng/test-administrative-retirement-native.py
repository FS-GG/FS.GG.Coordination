#!/usr/bin/env python3
"""Run bounded administrative-retirement checks on disposable GitHub refs and PRs."""

import argparse
import importlib.util
import json
import os
import pathlib
import signal
import shutil
import subprocess
import tarfile
import tempfile
import time
import urllib.parse
import uuid

ROOT = pathlib.Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("retirement", ROOT / "eng/administrative-retirement.py")
retirement = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(retirement)
REPOSITORY = "FS-GG/.github"
WHOLE_FIXTURE_TIMEOUT_SECONDS = 1800
ACCEPTED_CLIENT_COMMIT = "ee899605213e202e96232a8c6c0cb8bf84808948"
ACCEPTED_CLIENT_ARTIFACT_DIGEST = "a129a2d790f99cb2d5597a40492f660d449eeb69395626fe6699b081a7c50831"


def command(args, cwd=None, accept_failure=False, env=None, timeout=60):
    result = subprocess.run(args, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=timeout, check=False, env=env)
    if result.returncode and not accept_failure:
        raise RuntimeError(f"command failed ({result.returncode}): {' '.join(args[:3])}: {result.stderr[:300]}")
    return result


def record_probe(evidence, label, result, path, method):
    if evidence is None:
        return
    evidence.mkdir(mode=0o700, parents=True, exist_ok=True)
    payload = {"schema": "fsgg.coordination.administrative-retirement-native-probe/1", "label": label, "method": method, "path": path, "returnCode": result.returncode, "stdout": result.stdout.decode(errors="replace"), "stderr": result.stderr.decode(errors="replace")}
    (evidence / (label + ".json")).write_bytes(retirement.canonical(payload))


def api(path, method="GET", body=None, accept_failure=False, evidence=None, label=None):
    args = ["gh", "api", "--method", method, "-H", f"X-GitHub-Api-Version: {retirement.API_VERSION}", path]
    if body is not None:
        args += ["--input", "-"]
    result = subprocess.run(args, input=None if body is None else retirement.canonical(body), stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60, check=False)
    record_probe(evidence, label or (method.lower() + "-" + retirement.sha(path.encode())[:12]), result, path, method)
    if result.returncode and not accept_failure:
        raise RuntimeError(f"GitHub {method} failed: {path}: {result.stderr.decode(errors='replace')[:300]}")
    return result


def start_api(path, method, body):
    args = ["gh", "api", "--method", method, "-H", f"X-GitHub-Api-Version: {retirement.API_VERSION}", path, "--input", "-"]
    child = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    child.stdin.write(retirement.canonical(body)); child.stdin.close(); child.stdin = None
    return child


def require_refusal(result, label, kind):
    detail = result.stderr.decode(errors="replace") + "\n" + result.stdout.decode(errors="replace")
    expected = ["GH013", "Repository rule violations", "repository rule"] if kind == "rule" else ["Pull Request is not mergeable", "pull request is not mergeable", "not mergeable", "closed"]
    if result.returncode == 0 or not any(token.lower() in detail.lower() for token in expected):
        raise RuntimeError(f"{label} lacked expected native refusal: rc={result.returncode} detail={detail[:240]}")


def cleanup_rules(operation_id, evidence=None):
    listed = json.loads(api(f"repos/{REPOSITORY}/rulesets?includes_parents=false&per_page=100").stdout)
    for rule in listed:
        if rule.get("name") in (operation_id + "-retired-candidate", operation_id + "-temporary-main-hold"):
            api(f"repos/{REPOSITORY}/rulesets/{rule['id']}", "DELETE", accept_failure=True, evidence=evidence, label=f"cleanup-rule-{rule['id']}")


def cleanup_scope(root, suffix):
    operation_id = f"fixture-retire-{suffix}"
    base = f"fsgg/fixture/{suffix}-base"
    candidate = f"fsgg/fixture/{suffix}-candidate"
    evidence = pathlib.Path(root) / suffix / "cleanup-probes"
    cleanup_rules(operation_id, evidence)
    pulls = api(f"repos/{REPOSITORY}/pulls?state=open&head=FS-GG:{candidate}&base={base}&per_page=100", accept_failure=True)
    if pulls.returncode == 0:
        for pull in json.loads(pulls.stdout):
            api(f"repos/{REPOSITORY}/pulls/{pull['number']}", "PATCH", {"state": "closed"}, accept_failure=True, evidence=evidence, label=f"cleanup-pr-{pull['number']}")
    issue_title = f"Administrative retirement fixture issue {suffix}"
    query = urllib.parse.quote(f'repo:{REPOSITORY} is:issue in:title "{issue_title}"')
    found = api(f"search/issues?q={query}&per_page=100", accept_failure=True, evidence=evidence, label="cleanup-issue-search")
    if found.returncode == 0:
        for issue in json.loads(found.stdout).get("items", []):
            if issue.get("title") == issue_title and issue.get("state") == "open":
                api(f"repos/{REPOSITORY}/issues/{issue['number']}", "PATCH", {"state": "closed", "state_reason": "not_planned"}, accept_failure=True, evidence=evidence, label=f"cleanup-issue-{issue['number']}")
    api(f"repos/{REPOSITORY}/git/refs/heads/{candidate}", "DELETE", accept_failure=True, evidence=evidence, label="cleanup-candidate-ref")
    api(f"repos/{REPOSITORY}/git/refs/heads/{base}", "DELETE", accept_failure=True, evidence=evidence, label="cleanup-base-ref")
    remaining_rules = json.loads(api(f"repos/{REPOSITORY}/rulesets?includes_parents=false&per_page=100", evidence=evidence, label="verify-rules-clean").stdout)
    if any(rule.get("name") in (operation_id + "-retired-candidate", operation_id + "-temporary-main-hold") for rule in remaining_rules):
        raise RuntimeError("fixture rule cleanup incomplete")
    for ref, label in ((candidate, "verify-candidate-ref-clean"), (base, "verify-base-ref-clean")):
        result = api(f"repos/{REPOSITORY}/git/ref/heads/{ref}", accept_failure=True, evidence=evidence, label=label)
        if result.returncode == 0 or "404" not in result.stderr.decode(errors="replace"):
            raise RuntimeError("fixture ref cleanup incomplete: " + ref)
    closed_pulls = api(f"repos/{REPOSITORY}/pulls?state=all&head=FS-GG:{candidate}&base={base}&per_page=100", evidence=evidence, label="verify-pulls-closed")
    if any(item.get("state") != "closed" for item in json.loads(closed_pulls.stdout)):
        raise RuntimeError("fixture pull request cleanup incomplete")
    verified_issues = api(f"search/issues?q={query}&per_page=100", evidence=evidence, label="verify-issues-closed")
    if any(item.get("title") == issue_title and item.get("state") != "closed" for item in json.loads(verified_issues.stdout).get("items", [])):
        raise RuntimeError("fixture issue cleanup incomplete")


def run_scenario(root, suffix, old_client, old_client_environment, race_merge, sha_control=False):
    operation_id = f"fixture-retire-{suffix}"
    base = f"fsgg/fixture/{suffix}-base"
    candidate = f"fsgg/fixture/{suffix}-candidate"
    repository = pathlib.Path(root) / suffix
    intent = {"schema": "fsgg.coordination.administrative-retirement-native-fixture-intent/1", "operationId": operation_id, "base": base, "candidate": candidate, "issueTitle": f"Administrative retirement fixture issue {suffix}", "pullTitle": f"Administrative retirement fixture {suffix}"}
    repository.mkdir(mode=0o700)
    (repository / "remote-intent.json").write_bytes(retirement.canonical(intent))
    evidence = repository / "probes"; evidence.mkdir(mode=0o700)
    command(["gh", "repo", "clone", REPOSITORY, str(repository / "checkout"), "--", "--quiet"])
    repository = repository / "checkout"
    command(["git", "config", "user.email", "fixture@example.invalid"], repository)
    command(["git", "config", "user.name", "FS-GG retirement fixture"], repository)
    main = command(["git", "rev-parse", "origin/main"], repository).stdout.strip()
    command(["git", "checkout", "--quiet", "-b", base, main], repository)
    command(["git", "push", "--quiet", "origin", f"HEAD:refs/heads/{base}"], repository)
    command(["git", "checkout", "--quiet", "-b", candidate], repository)
    fixture_file = repository / f"retirement-{suffix}.txt"
    fixture_file.write_text(f"isolated administrative retirement fixture {suffix}\n")
    command(["git", "add", fixture_file.name], repository)
    command(["git", "commit", "--quiet", "-m", f"Administrative retirement fixture {suffix}"], repository)
    head = command(["git", "rev-parse", "HEAD"], repository).stdout.strip()
    tree = command(["git", "rev-parse", "HEAD^{tree}"], repository).stdout.strip()
    parent = command(["git", "rev-parse", "HEAD^"], repository).stdout.strip()
    authority = retirement.sha((operation_id + ":authority").encode())
    tombstone_environment = dict(os.environ, GIT_AUTHOR_NAME="FS-GG administrative retirement", GIT_AUTHOR_EMAIL="retirement@fs.gg", GIT_AUTHOR_DATE="2000-01-01T00:00:00Z", GIT_COMMITTER_NAME="FS-GG administrative retirement", GIT_COMMITTER_EMAIL="retirement@fs.gg", GIT_COMMITTER_DATE="2000-01-01T00:00:00Z")
    retirement_head = command(["git", "commit-tree", tree, "-p", head, "-m", f"Administrative retirement tombstone {operation_id} authority={authority}"], repository, env=tombstone_environment).stdout.strip()
    command(["git", "update-ref", "refs/heads/retirement-tombstone", retirement_head], repository)
    command(["git", "push", "--quiet", "origin", f"HEAD:refs/heads/{candidate}"], repository)
    issue = json.loads(api(f"repos/{REPOSITORY}/issues", "POST", {"title": intent["issueTitle"], "body": "Disposable isolated R2 qualification fixture."}, evidence=evidence, label="create-issue").stdout)
    pull = json.loads(api(f"repos/{REPOSITORY}/pulls", "POST", {"title": intent["pullTitle"], "head": candidate, "base": base, "body": "Disposable isolated R2 qualification fixture."}, evidence=evidence, label="create-pull").stdout)
    number = pull["number"]
    bundle = pathlib.Path(root) / f"{suffix}.bundle"
    command(["git", "bundle", "create", str(bundle), "refs/heads/retirement-tombstone"], repository)
    archive = pathlib.Path(root) / f"{suffix}-archive.json"
    archive.write_bytes(retirement.canonical({"schema": retirement.ARCHIVE_SCHEMA, "repository": REPOSITORY, "issueNumber": issue["number"], "pullRequestNumber": number, "candidateHead": head, "candidateTree": tree, "candidateParent": parent, "retirementHead": retirement_head, "retirementTree": tree, "retirementParent": head, "gitBundle": str(bundle), "gitBundleSha256": retirement.sha(bundle.read_bytes()), "capturedAt": retirement.utc_now()}))
    exclusion = pathlib.Path(root) / f"{suffix}-exclusion.json"
    exclusion.write_bytes(retirement.canonical({"schema": "fsgg.coordination.retired-subject-exclusion/1", "repository": REPOSITORY, "issueNumber": issue["number"], "candidateHead": head, "excluded": True}))
    evidence = pathlib.Path(root) / f"{suffix}-private"; evidence.mkdir(mode=0o700)
    config = {"schema": retirement.CONFIG_SCHEMA, "repository": REPOSITORY, "repositoryId": 1269292704, "issueNumber": issue["number"], "pullRequestNumber": number, "branchRef": "refs/heads/" + candidate, "baseRef": "refs/heads/" + base, "candidateHead": head, "candidateTree": tree, "candidateParent": parent, "retirementHead": retirement_head, "retirementTree": tree, "retirementParent": head, "acceptedClientCommit": ACCEPTED_CLIENT_COMMIT, "acceptedClientArtifactDigest": ACCEPTED_CLIENT_ARTIFACT_DIGEST, "operationAuthorityDigest": authority, "operationId": operation_id, "archiveManifest": str(archive), "subjectExclusion": str(exclusion), "checkpoint": str(evidence / "checkpoint.json")}
    config_path = pathlib.Path(root) / f"{suffix}-config.json"; config_path.write_bytes(retirement.canonical(config))
    race = None
    delayed = None
    try:
        with retirement.operation_lock(config):
            retirement.plan(config)
        positive_base = api(f"repos/{REPOSITORY}/git/refs/heads/{base}", "PATCH", {"sha": main, "force": False}, evidence=evidence, label="positive-base-update")
        positive_candidate = api(f"repos/{REPOSITORY}/git/refs/heads/{candidate}", "PATCH", {"sha": head, "force": False}, evidence=evidence, label="positive-candidate-update")
        race = start_api(f"repos/{REPOSITORY}/pulls/{number}/merge", "PUT", {"sha": head, "merge_method": "squash"}) if race_merge else None
        delayed_ready = evidence / "accepted-old-client-delayed-ready.json"
        delayed_release = evidence / "accepted-old-client-delayed-release"
        delayed_response = evidence / "accepted-old-client-delayed-response.json"
        if not race_merge and not sha_control:
            delayed_args = [*old_client, "intercept-merge", REPOSITORY, str(issue["number"]), str(number), pull["node_id"], "refs/heads/" + candidate, "refs/heads/" + base, head, str(delayed_ready), str(delayed_release), str(delayed_response)]
            delayed = subprocess.Popen(delayed_args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=old_client_environment)
            deadline = time.monotonic() + 180
            while not delayed_ready.exists() and delayed.poll() is None and time.monotonic() < deadline:
                time.sleep(0.05)
            if not delayed_ready.exists():
                raise RuntimeError("accepted old client did not reach the delayed SHA-bound merge request")
        with retirement.operation_lock(config):
            retirement.execute(config, "temporary-hold")
        if race is not None:
            race_out, race_error = race.communicate(timeout=60)
            race_result = subprocess.CompletedProcess([], race.returncode, race_out, race_error)
            record_probe(evidence, "released-merge-request", race_result, f"repos/{REPOSITORY}/pulls/{number}/merge", "PUT")
            if race.returncode != 0 or not json.loads(race_out).get("merged"):
                raise RuntimeError("released isolated merge did not win the installation race: " + race_error.decode(errors="replace")[:200])
        else:
            open_merge = api(f"repos/{REPOSITORY}/pulls/{number}/merge", "PUT", {"sha": head, "merge_method": "squash"}, accept_failure=True, evidence=evidence, label="temporary-hold-open-merge-refusal")
            require_refusal(open_merge, "temporary base hold open merge", "rule")
        base_update = api(f"repos/{REPOSITORY}/git/refs/heads/{base}", "PATCH", {"sha": head, "force": True}, accept_failure=True, evidence=evidence, label="temporary-hold-base-update-refusal")
        require_refusal(base_update, "temporary base update fence", "rule")
        with retirement.operation_lock(config):
            retirement.execute(config, "permanent-fence")
        candidate_update = api(f"repos/{REPOSITORY}/git/refs/heads/{candidate}", "PATCH", {"sha": parent, "force": True}, accept_failure=True, evidence=evidence, label="permanent-candidate-update-refusal")
        candidate_delete = api(f"repos/{REPOSITORY}/git/refs/heads/{candidate}", "DELETE", accept_failure=True, evidence=evidence, label="permanent-candidate-delete-refusal")
        require_refusal(candidate_update, "permanent candidate update fence", "rule")
        require_refusal(candidate_delete, "permanent candidate deletion fence", "rule")
        with retirement.operation_lock(config):
            retirement.execute(config, "settled")
        real_request = retirement.gh_request
        injected = {"done": False}
        def lose_cleanup_response(path, method="GET", body=None, allow_not_found=False):
            result = real_request(path, method, body, allow_not_found)
            if method == "DELETE" and path.endswith("/rulesets/" + str(json.loads(pathlib.Path(config["checkpoint"]).read_text())["temporaryRuleId"])) and not injected["done"]:
                injected["done"] = True
                raise retirement.UnknownEffect("injected-native-cleanup-response-loss")
            return result
        with retirement.operation_lock(config), unittest_mock(retirement, lose_cleanup_response):
            try:
                retirement.execute(config)
            except retirement.UnknownEffect:
                pass
            else:
                raise RuntimeError("native response loss was not injected")
        with retirement.operation_lock(config):
            receipt = retirement.execute(config)
            replay = retirement.verify(config)
        if not race_merge and not sha_control:
            delayed_release.write_text("release exact cached H request\n")
            delayed_stdout, delayed_stderr = delayed.communicate(timeout=60)
            (evidence / "accepted-old-client-delayed-result.json").write_bytes(retirement.canonical({"schema": "fsgg.coordination.administrative-retirement-old-client-delayed-result/1", "returnCode": delayed.returncode, "stdout": delayed_stdout, "stderr": delayed_stderr}))
            native_response = json.loads(delayed_response.read_text())
            response_body = str(native_response.get("body") or "").lower()
            if delayed.returncode != 0 or "ADMINISTRATIVE_RETIREMENT_OLD_CLIENT_DELAYED_NATIVE_409_HEAD_MISMATCH" not in delayed_stdout or native_response.get("kind") != "response" or native_response.get("statusCode") != 409 or "head branch was modified" not in response_body:
                raise RuntimeError("exact accepted old client cached-H request was not rejected by native SHA comparison")
            base_after_delayed = json.loads(api(f"repos/{REPOSITORY}/git/ref/heads/{base}", evidence=evidence, label="base-after-delayed-old-merge").stdout)
            if (base_after_delayed.get("object") or {}).get("sha") != main:
                raise RuntimeError("protected fixture base changed after delayed old merge")
            args = [*old_client, "post-terminal", REPOSITORY, str(issue["number"]), str(number), pull["node_id"], "refs/heads/" + candidate, "refs/heads/" + base, head, "", "", ""]
            adopted = command(args, env=old_client_environment, timeout=180)
            (evidence / "accepted-old-client-result.json").write_bytes(retirement.canonical({"schema": "fsgg.coordination.administrative-retirement-old-client-result/1", "acceptedClientCommit": ACCEPTED_CLIENT_COMMIT, "acceptedClientArtifactDigest": ACCEPTED_CLIENT_ARTIFACT_DIGEST, "returnCode": adopted.returncode, "stdout": adopted.stdout, "stderr": adopted.stderr}))
            if "ADMINISTRATIVE_RETIREMENT_OLD_CLIENT_HEAD_CONFLICT_AND_REFUSED_MERGE" not in adopted.stdout:
                raise RuntimeError("old-client retirement-head conflict and merge refusal were not observed")
        elif sha_control:
            stale_merge = api(f"repos/{REPOSITORY}/pulls/{number}/merge", "PUT", {"sha": head, "merge_method": "squash"}, accept_failure=True, evidence=evidence, label="stale-H-native-merge-refusal")
            stale_detail = stale_merge.stdout.decode(errors="replace") + stale_merge.stderr.decode(errors="replace")
            if stale_merge.returncode == 0 or "head branch was modified" not in stale_detail.lower():
                raise RuntimeError("stale H was not rejected by the native merge SHA comparison")
            base_after_stale = json.loads(api(f"repos/{REPOSITORY}/git/ref/heads/{base}", evidence=evidence, label="base-after-stale-H").stdout)
            if (base_after_stale.get("object") or {}).get("sha") != main:
                raise RuntimeError("fixture base changed after stale H merge request")
            current_merge = api(f"repos/{REPOSITORY}/pulls/{number}/merge", "PUT", {"sha": retirement_head, "merge_method": "squash"}, evidence=evidence, label="current-T-native-merge-positive")
            if not json.loads(current_merge.stdout).get("merged"):
                raise RuntimeError("current T positive merge control did not merge")
        if receipt != replay or receipt["typedReceipt"]["OriginalCompletionRecorded"]:
            raise RuntimeError("terminal typed receipt replay/fabrication check failed")
        pulls = json.loads(api(f"repos/{REPOSITORY}/pulls?state=all&head=FS-GG:{candidate}&base={base}&per_page=100").stdout)
        if len(pulls) != 1 or pulls[0]["state"] != "closed":
            raise RuntimeError("closed pull request was reopened or duplicated")
        return {"operationId": operation_id, "issue": issue["number"], "pullRequest": number, "scenario": "released-merge-wins-race" if race_merge else ("sha-current-positive-control" if sha_control else "closed-unmerged-delayed-old-client"), "disposition": receipt["pullRequestDisposition"], "receiptDigest": receipt["receiptDigest"], "nativeCensusDigest": receipt["nativeCensusDigest"], "positiveControls": {"baseUpdate": positive_base.returncode == 0, "candidateUpdate": positive_candidate.returncode == 0}}
    finally:
        if race is not None and race.poll() is None:
            race.terminate()
            try: race.wait(timeout=5)
            except subprocess.TimeoutExpired:
                race.kill(); race.wait(timeout=5)
        if delayed is not None and delayed.poll() is None:
            delayed.terminate()
            try: delayed.wait(timeout=5)
            except subprocess.TimeoutExpired:
                delayed.kill(); delayed.wait(timeout=5)
        cleanup_rules(operation_id, evidence)
        api(f"repos/{REPOSITORY}/git/refs/heads/{candidate}", "DELETE", accept_failure=True)
        api(f"repos/{REPOSITORY}/git/refs/heads/{base}", "DELETE", accept_failure=True)
        current_issue = api(f"repos/{REPOSITORY}/issues/{issue['number']}", accept_failure=True)
        if current_issue.returncode == 0 and json.loads(current_issue.stdout).get("state") == "open":
            api(f"repos/{REPOSITORY}/issues/{issue['number']}", "PATCH", {"state": "closed", "state_reason": "not_planned"}, accept_failure=True)


class unittest_mock:
    def __init__(self, module, replacement): self.module, self.replacement, self.original = module, replacement, module.gh_request
    def __enter__(self): self.module.gh_request = self.replacement
    def __exit__(self, *_): self.module.gh_request = self.original


def main():
    parser = argparse.ArgumentParser(); parser.add_argument("--output", required=True); parser.add_argument("--evidence-root", required=True); args = parser.parse_args()
    root = pathlib.Path(args.evidence_root)
    root.mkdir(mode=0o700, parents=True, exist_ok=False)
    if root.stat().st_mode & 0o077:
        raise RuntimeError("evidence root must be private")
    stamp = uuid.uuid4().hex[:16]
    suffixes = [stamp + "-closed", stamp + "-race", stamp + "-sha-control"]
    def whole_fixture_timeout(_signal, _frame):
        raise TimeoutError("native fixture exceeded bounded whole-operation deadline")
    previous_alarm = signal.signal(signal.SIGALRM, whole_fixture_timeout)
    signal.alarm(WHOLE_FIXTURE_TIMEOUT_SECONDS)
    try:
        archive = subprocess.run(["git", "archive", ACCEPTED_CLIENT_COMMIT], cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60, check=True)
        tar_path = root / "accepted-old-client-source.tar"
        tar_path.write_bytes(archive.stdout)
        old_root = root / "accepted-old-client-source"; old_root.mkdir()
        with tarfile.open(tar_path) as source_archive:
            source_archive.extractall(old_root, filter="data")
        dotnet = os.environ.get("FSGG_DOTNET", "dotnet")
        resolved_dotnet = shutil.which(dotnet)
        if resolved_dotnet is None:
            raise RuntimeError("dotnet executable is unavailable")
        version = command([resolved_dotnet, "--version"], timeout=30).stdout.strip()
        if version != "10.0.400":
            raise RuntimeError(f"native fixture requires dotnet 10.0.400, observed {version}")
        dotnet_root = str(pathlib.Path(resolved_dotnet).resolve().parent)
        build_root = root / "accepted-old-client-build"; build_root.mkdir(mode=0o700)
        client_project = build_root / "old-client.fsproj"
        client_project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>linux-x64</RuntimeIdentifier><SelfContained>true</SelfContained></PropertyGroup><ItemGroup><Compile Include="{ROOT / 'eng/validate-administrative-retirement-old-client.fs'}" Link="Program.fs" /><ProjectReference Include="{old_root / 'src/FS.GG.Coordination.Orchestration.Host/FS.GG.Coordination.Orchestration.Host.fsproj'}" /></ItemGroup></Project>''')
        environment = dict(os.environ, DOTNET_ROOT=dotnet_root, PATH=dotnet_root + os.pathsep + os.environ.get("PATH", ""), NUGET_PACKAGES=str(build_root / "nuget"))
        built = command([resolved_dotnet, "build", "--configuration", "Release", str(client_project)], env=environment, timeout=180)
        (build_root / "build-result.json").write_bytes(retirement.canonical({"acceptedClientCommit": ACCEPTED_CLIENT_COMMIT, "acceptedClientArtifactDigest": ACCEPTED_CLIENT_ARTIFACT_DIGEST, "wrapperSha256": retirement.sha((ROOT / "eng/validate-administrative-retirement-old-client.fs").read_bytes()), "stdout": built.stdout, "stderr": built.stderr}))
        old_client = [str(build_root / "bin/Release/net10.0/linux-x64/old-client")]
        closed = run_scenario(root, suffixes[0], old_client, environment, False)
        merged = run_scenario(root, suffixes[1], old_client, environment, True)
        sha_control = run_scenario(root, suffixes[2], old_client, environment, False, True)
        pathlib.Path(args.output).write_bytes(retirement.canonical({"schema": "fsgg.coordination.administrative-retirement-native-fixture/1", "repository": REPOSITORY, "evidenceRoot": str(root.resolve()), "scenarios": [closed, merged, sha_control]}))
    except Exception as error:
        (root / "failure.json").write_bytes(retirement.canonical({"schema": "fsgg.coordination.administrative-retirement-native-fixture-failure/1", "errorType": type(error).__name__, "error": str(error)[:1000]}))
        raise
    finally:
        signal.alarm(0)
        signal.signal(signal.SIGALRM, previous_alarm)
        for suffix in suffixes: cleanup_scope(root, suffix)
    print("ADMINISTRATIVE_RETIREMENT_NATIVE_FIXTURE_OK")


if __name__ == "__main__":
    main()
