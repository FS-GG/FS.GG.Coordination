#!/usr/bin/env python3
"""Secret-free, zero-effect execute-main boundary fixtures for Delivery1124.

The observer, public anchor, policy and receipts are synthetic. No GitHub token
is supplied. A valid ten-gate receipt must reach the missing-token refusal;
invalid evidence must stop earlier. Invalid key sentinels cannot mint a token.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

GATES = [
    "contract-coherence / coherence", "projection", "roster-closure", "drift",
    "claim-generation", "Lint every shell file in the repo (pinned shellcheck)",
    "claim-fence", "architecture-map reconcile", "no-silent-revert", "board-linkage",
]
SETTLEMENT = ["contract-coherence / coherence", "routine-eligibility"]
KEYS = [f"V2_ORDINARY_{kind}{field}" for kind in ("", "REHEARSAL_")
        for field in ("APP_ID", "APP_PRIVATE_KEY", "AUTHORIZER_PRIVATE_KEY")]
POLICY_ID = "v2-ci-i1-ordinary-settlement-v1"


def encoded(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def anchor():
    repository = "FS-GG/FS.GG.Coordination.Authority"
    rulesets = {}
    effective = []
    for name, number, rules in [("writer", 24802693, ["creation", "update"]),
                                ("integrity", 24802698, ["deletion", "non_fast_forward"])]:
        rulesets[name] = dict(id=number, name=f"ordinary-v2-main-{name}", enforcement="active",
                             updatedAt="2026-01-01T00:00:00Z",
                             conditions={"ref_name": {"include": ["refs/heads/main"], "exclude": []}},
                             rules=[{"type": rule} for rule in rules],
                             bypassActors=[dict(actorId=app, actorType="Integration", bypassMode="always")
                                           for app in [1, 4882140]] if name == "writer" else [])
        effective.extend(dict(type=rule, rulesetId=number, rulesetSourceType="Repository",
                              rulesetSource=repository) for rule in rules)
    return dict(schema="fsgg.github.v2-ci-ordinary-settlement-anchor/1", policyId=POLICY_ID,
                operationClass="ordinary-post-merge-delivery-settlement",
                authorizer=dict(keyId="synthetic1124", algorithm="RSA-PSS-SHA256", publicKeySpkiSha256="e"*64),
                writer=dict(appId=1, installationId=1, repository=repository, repositoryId=1351660651,
                            permissions={"contents": "write", "metadata": "read"}),
                rulesets=rulesets, effectiveRules=effective, acceptedAt="2026-01-01T00:02:00Z", sourceCommit="a"*40)


def fixture():
    policy = dict(policyId=POLICY_ID, credentialJob={"installed": True},
                  qualification=dict(requiredChecks=SETTLEMENT.copy(), requiredGateChecks=GATES.copy()))
    def check(name):
        return dict(name=name, appId=15368, sourceSha="b"*40, conclusion="success")
    receipt = dict(schema="fsgg.github.v2-ci-secret-free-predecessor-receipt/1", status="qualified",
                   policyId=POLICY_ID, sourceSha="a"*40, qualificationSha="b"*40, qualifiedTreeSha="d"*40,
                   pullRequest=17, pullRequestNodeId="PR_synthetic1124", pullRequestBaseSha="c"*40,
                   mergeCommitSha="a"*40, workflowRevision="a"*40, environment="ordinary-v2",
                   operationClass="ordinary-post-merge-delivery-settlement", credentialAccess=False, activation=True,
                   requiredChecks=list(map(check, SETTLEMENT)), requiredGateChecks=list(map(check, GATES)))
    return receipt, policy


def run(tool: Path, dotnet: Path | None):
    command = [str(tool)] if dotnet is None else [str(dotnet), str(tool)]
    scenarios = ["strict-ten", "old-eight", "missing-no-silent-revert", "missing-board-linkage",
                 "extra-gate", "duplicate-gate", "duplicate-policy-gate", "duplicate-settlement", "duplicate-policy-settlement", "failed-gate", "wrong-app",
                 "stale-gate-source", "policy-digest", "wrong-source", "observer-refusal"]
    with tempfile.TemporaryDirectory(prefix="sdk1124-fixtures-") as temporary:
        root = Path(temporary)
        (root / "policy").mkdir()
        (root / "tools").mkdir()
        (root / "policy/v2-ci-ordinary-settlement-main-anchor.json").write_bytes(encoded(anchor()))
        observer = root / "tools/v2-ci-ordinary-observe.py"
        observer.write_text("import json,os,pathlib,sys\n"
                            f"keys={KEYS!r}\n"
                            "assert all(k not in os.environ for k in keys)\n"
                            "assert 'GH_TOKEN' not in os.environ\n"
                            "pathlib.Path('observer.json').write_text(json.dumps({'keysAbsent':True,'action':sys.argv[1]}))\n"
                            "sys.exit(int(pathlib.Path('observer-exit').read_text()))\n")
        for scenario in scenarios:
            receipt, policy = fixture()
            if scenario == "old-eight":
                receipt["requiredGateChecks"] = receipt["requiredGateChecks"][:-2]
                policy["qualification"]["requiredGateChecks"] = GATES[:-2]
            elif scenario.startswith("missing-"):
                name = scenario.removeprefix("missing-")
                receipt["requiredGateChecks"] = [v for v in receipt["requiredGateChecks"] if v["name"] != name]
            elif scenario == "extra-gate":
                receipt["requiredGateChecks"].append(dict(name="unselected-gate", appId=15368, sourceSha="b"*40, conclusion="success"))
            elif scenario == "duplicate-gate":
                receipt["requiredGateChecks"].append(copy.deepcopy(receipt["requiredGateChecks"][0]))
            elif scenario == "duplicate-policy-gate":
                policy["qualification"]["requiredGateChecks"].append(GATES[0])
            elif scenario == "duplicate-settlement":
                receipt["requiredChecks"].append(copy.deepcopy(receipt["requiredChecks"][0]))
            elif scenario == "duplicate-policy-settlement":
                policy["qualification"]["requiredChecks"].append(SETTLEMENT[0])
            elif scenario == "failed-gate":
                receipt["requiredGateChecks"][0]["conclusion"] = "skipped"
            elif scenario == "wrong-app":
                receipt["requiredGateChecks"][0]["appId"] = 1
            elif scenario == "stale-gate-source":
                receipt["requiredGateChecks"][0]["sourceSha"] = "f"*40
            elif scenario == "wrong-source":
                receipt.update(sourceProfile="audio-v1", sourceRepository="FS-GG/FS.GG.Audio", sourceRepositoryId=1292226968)
            policy_bytes = encoded(policy)
            receipt["policySha256"] = hashlib.sha256(policy_bytes).hexdigest() if scenario != "policy-digest" else "0"*64
            (root / "policy/v2-ci-ordinary-settlement.json").write_bytes(policy_bytes)
            receipt_path = root / "receipt.json"
            receipt_path.write_bytes(encoded(receipt))
            (root / "observer-exit").write_text("1" if scenario == "observer-refusal" else "0")
            (root / "observer.json").unlink(missing_ok=True)
            env = {k: os.environ[k] for k in ["PATH", "DOTNET_ROOT", "DOTNET_CLI_HOME", "DOTNET_PROCESSOR_COUNT", "DOTNET_NOLOGO"] if k in os.environ}
            env.update(HOME=str(root), GITHUB_WORKSPACE=str(root), FSGG_V2_PREFLIGHT_RECEIPT=str(receipt_path),
                       FSGG_V2_SOURCE_PROFILE="dotgithub-v1")
            env.update({key: "1" if key.endswith("APP_ID") else "invalid-synthetic-key" for key in KEYS})
            actual = subprocess.run(command + ["ordinary-settlement", "execute-main"], cwd=root, env=env,
                                    capture_output=True, timeout=20, check=False)
            reason = ("missing-environment:GH_TOKEN" if scenario == "strict-ten" else
                      "current-source-observer-refused" if scenario == "observer-refusal" else "preflight-receipt-binding")
            expected = f'ordinary-settlement-refused:SettlementProviderUnavailable "{reason}"\n'.encode()
            if actual.returncode != 3 or actual.stdout or actual.stderr != expected:
                raise AssertionError(f"{scenario}: exit={actual.returncode} stdout={actual.stdout!r} stderr={actual.stderr!r}")
            observed = json.loads((root / "observer.json").read_bytes())
            if observed != {"keysAbsent": True, "action": "verify-main"}:
                raise AssertionError(f"{scenario}: observer boundary changed")
    print(f"ORDINARY_SDK_PROFILE_FIXTURES passed={len(scenarios)} nativeEffects=0 githubTokenAbsent=true syntheticKeysOnly=true")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", required=True, type=Path)
    parser.add_argument("--dotnet", type=Path, help="Use an SDK host when --tool names a built CLI DLL")
    args = parser.parse_args()
    run(args.tool.resolve(strict=True), args.dotnet.resolve(strict=True) if args.dotnet else None)
