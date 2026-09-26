#!/usr/bin/env python3
"""Fail-closed, exact-head profile selection for the Coordination CI pilot."""

import hashlib
import io
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import zipfile
from datetime import datetime, timezone


ROOT = pathlib.Path(__file__).resolve().parent.parent
PLAN = json.loads((ROOT / "eng/optimistic-qualification-plan.json").read_text())
SHA40 = re.compile(r"[0-9a-f]{40}\Z")
SHA64 = re.compile(r"[0-9a-f]{64}\Z")


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT).decode().strip()


def valid_full_prior(prior):
    if prior["authentic"] is not True or prior["complete"] is not True:
        raise ValueError("prior not authentic and complete")
    if prior["runId"] <= 0 or prior["attempt"] <= 0:
        raise ValueError("prior run identity invalid")
    completed = datetime.fromisoformat(prior["completedAt"].replace("Z", "+00:00"))
    expires = datetime.fromisoformat(prior["expiresAt"].replace("Z", "+00:00"))
    now = datetime.now(timezone.utc)
    if completed.tzinfo is None or expires.tzinfo is None or not (completed <= now < expires):
        raise ValueError("prior is stale or expired")
    if not SHA64.fullmatch(prior["executedReceiptSha256"]):
        raise ValueError("prior full receipt digest invalid")


def profile(selection_path, obligation_path, output_path):
    selection = json.loads(pathlib.Path(selection_path).read_text())
    obligation = json.loads(pathlib.Path(obligation_path).read_text())
    candidate = obligation["candidate"]
    base = obligation["baseRevision"]
    if not SHA40.fullmatch(candidate) or not SHA40.fullmatch(base):
        raise ValueError("invalid candidate or base revision")

    selected = "full"
    reason = "default-full"
    changes = []
    donor = None
    try:
        if os.environ.get("GITHUB_EVENT_NAME") != "pull_request":
            raise ValueError("non-PR event")
        if os.environ.get("FSGG_READY_PR") != "true":
            raise ValueError("PR not ready")
        if os.environ.get("FSGG_PR_BASE_SHA") != base:
            raise ValueError("candidate base differs from PR base")
        if git("rev-parse", "HEAD") != candidate:
            raise ValueError("checkout is not exact candidate head")
        subprocess.run(["git", "merge-base", "--is-ancestor", base, candidate], cwd=ROOT, check=True, stderr=subprocess.DEVNULL)
        if selection["schema"] != "fsgg.coordination.qualification-selection/1":
            raise ValueError("selection schema")
        if selection["candidateObligationSha256"] != obligation["obligationSha256"]:
            raise ValueError("selection candidate")
        if selection["disposition"] != "reused" or selection["coherentState"] != "pending":
            raise ValueError("selection does not authorize scoped run")
        if selection["semanticDelta"]["empty"] is not True:
            raise ValueError("semantic delta")
        prior = selection["prior"]
        valid_full_prior(prior)
        donor = prior["executedReceiptSha256"]
        if not SHA64.fullmatch(donor) or not SHA64.fullmatch(selection["selectionSha256"]):
            raise ValueError("invalid selection or full donor digest")
        raw = subprocess.check_output(
            ["git", "diff", "--name-status", "-z", "--no-renames", base, candidate], cwd=ROOT
        )
        fields = raw.decode("utf-8").split("\0")
        if fields[-1] != "":
            raise ValueError("malformed exact diff")
        fields.pop()
        if len(fields) == 0 or len(fields) % 2:
            raise ValueError("empty or malformed exact diff")
        changes = [{"status": fields[i], "path": fields[i + 1]} for i in range(0, len(fields), 2)]
        allowed = set(PLAN["profiles"]["scoped"]["exactModifiedPaths"])
        if not allowed or any(row["status"] != "M" or row["path"] not in allowed for row in changes):
            raise ValueError("diff outside audited nonauthority allowlist")
        selected = "scoped"
        reason = "ready-pr-reused-full-donor-audited-exact-diff"
    except (KeyError, TypeError, ValueError, OverflowError, subprocess.CalledProcessError, UnicodeError) as error:
        reason = str(error)[:160] or "unknown-profile-input"

    record = {
        "schema": "fsgg.coordination.optimistic-profile/1",
        "candidate": candidate,
        "baseRevision": base,
        "candidateObligationSha256": obligation["obligationSha256"],
        "profile": selected,
        "reason": reason,
        "selectionSha256": selection.get("selectionSha256"),
        "fullDonorReceiptSha256": donor if selected == "scoped" else None,
        "changes": changes if selected == "scoped" else [],
    }
    payload = json.dumps(record, sort_keys=True, separators=(",", ":")).encode()
    record["profileSha256"] = hashlib.sha256(payload).hexdigest()
    pathlib.Path(output_path).write_text(json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n")
    shards = ["base", *PLAN["formalFanout"]["semanticShards"][1:], PLAN["formalFanout"]["performanceShard"]]
    full = [{"kind": "formal", "shard": shard} for shard in shards]
    nonformal = [{"kind": "partition", "partition": index} for index in (0, 2, 3, 4, 5)]
    matrix = {"include": ([full[0], *nonformal] if selected == "scoped" else [full[0], *nonformal, *full[1:]])}
    with pathlib.Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write(f"profile={selected}\n")
        output.write("matrix=" + json.dumps(matrix, separators=(",", ":")) + "\n")
    print(f"optimistic CI profile: {selected} ({reason})")


def verify_scoped(selection_path, obligation_path, profile_path):
    selection = json.loads(pathlib.Path(selection_path).read_text())
    obligation = json.loads(pathlib.Path(obligation_path).read_text())
    record = json.loads(pathlib.Path(profile_path).read_text())
    recorded_digest = record.pop("profileSha256")
    payload = json.dumps(record, sort_keys=True, separators=(",", ":")).encode()
    if hashlib.sha256(payload).hexdigest() != recorded_digest:
        raise ValueError("scoped profile digest differs")
    if os.environ.get("GITHUB_EVENT_NAME") != "pull_request" or os.environ.get("FSGG_READY_PR") != "true":
        raise ValueError("scoped aggregate requires ready PR")
    if os.environ.get("FSGG_PR_BASE_SHA") != obligation["baseRevision"]:
        raise ValueError("scoped aggregate base differs from PR base")
    if git("rev-parse", "HEAD") != obligation["candidate"]:
        raise ValueError("scoped aggregate checkout differs from candidate")
    if record["schema"] != "fsgg.coordination.optimistic-profile/1" or record["profile"] != "scoped":
        raise ValueError("invalid scoped profile")
    if record["candidate"] != obligation["candidate"] or record["baseRevision"] != obligation["baseRevision"]:
        raise ValueError("scoped profile candidate or base differs")
    if record["candidateObligationSha256"] != obligation["obligationSha256"]:
        raise ValueError("scoped profile obligation differs")
    if selection["schema"] != "fsgg.coordination.qualification-selection/1":
        raise ValueError("scoped selection schema differs")
    if selection["candidateObligationSha256"] != obligation["obligationSha256"]:
        raise ValueError("scoped selection candidate differs")
    if selection["disposition"] != "reused" or selection["semanticDelta"]["empty"] is not True:
        raise ValueError("scoped selection is not reused")
    if record["selectionSha256"] != selection["selectionSha256"]:
        raise ValueError("scoped selection digest differs")
    prior = selection["prior"]
    valid_full_prior(prior)
    if record["fullDonorReceiptSha256"] != prior["executedReceiptSha256"]:
        raise ValueError("scoped full donor digest differs")
    verify_full_donor(prior, selection_path, obligation_path)
    raw = subprocess.check_output(
        ["git", "diff", "--name-status", "-z", "--no-renames", obligation["baseRevision"], obligation["candidate"]], cwd=ROOT
    )
    fields = raw.decode("utf-8").split("\0")
    if fields[-1] != "" or len(fields) < 3 or (len(fields) - 1) % 2:
        raise ValueError("invalid scoped exact diff")
    fields.pop()
    changes = [{"status": fields[i], "path": fields[i + 1]} for i in range(0, len(fields), 2)]
    allowed = set(PLAN["profiles"]["scoped"]["exactModifiedPaths"])
    if changes != record["changes"] or any(row["status"] != "M" or row["path"] not in allowed for row in changes):
        raise ValueError("scoped exact diff differs or is outside allowlist")
    print("scoped profile and full donor binding verified")


def verify_full_donor(prior, selection_path, current_obligation_path):
    """Read the full donor independently of the selection artifact's claims."""
    repository = os.environ["GITHUB_REPOSITORY"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("invalid GitHub repository")
    run_id = prior["runId"]
    run = json.loads(subprocess.check_output(["gh", "api", f"repos/{repository}/actions/runs/{run_id}"]))
    workflow_path = run["path"].split("@", 1)[0]
    expected_path = ".github/workflows/optimistic-parallel-validation.yml"
    if (run["id"] != run_id or run["run_attempt"] != prior["attempt"]
            or run["status"] != "completed" or run["conclusion"] != "success"
            or run["updated_at"] != prior["completedAt"]
            or not (workflow_path == expected_path or workflow_path.endswith("/" + expected_path))
            or run["repository"]["full_name"] != repository
            or str(run_id) == os.environ.get("GITHUB_RUN_ID")):
        raise ValueError("full donor GitHub run is not successful and distinct")
    listing = json.loads(subprocess.check_output([
        "gh", "api", f"repos/{repository}/actions/runs/{run_id}/artifacts?per_page=100"
    ]))
    if listing["total_count"] > 100:
        raise ValueError("full donor artifact listing is truncated")
    candidates = [artifact for artifact in listing["artifacts"]
                  if artifact["name"] == f"coherent-aggregate-{run['head_sha']}"
                  and artifact["expired"] is False
                  and artifact["workflow_run"]["id"] == run_id
                  and artifact["workflow_run"]["head_sha"] == run["head_sha"]]
    if len(candidates) != 1:
        raise ValueError("full donor artifact is missing or ambiguous")
    artifact = candidates[0]
    expires = datetime.fromisoformat(artifact["expires_at"].replace("Z", "+00:00"))
    if expires <= datetime.now(timezone.utc) or artifact["expires_at"] != prior["expiresAt"]:
        raise ValueError("full donor artifact expired or differs from classification")
    archive = subprocess.check_output([
        "gh", "api", f"repos/{repository}/actions/artifacts/{artifact['id']}/zip"
    ])
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        names = bundle.namelist()
        required = ("candidate-obligation.json", "partition-plan.json", "coherent-aggregate-receipt.json")
        if any(names.count(name) != 1 for name in required):
            raise ValueError("full donor artifact lacks canonical evidence")
        evidence = {name: bundle.read(name) for name in required}
    if hashlib.sha256(evidence["coherent-aggregate-receipt.json"]).hexdigest() != prior["executedReceiptSha256"]:
        raise ValueError("full donor receipt digest differs")
    old = json.loads(evidence["candidate-obligation.json"])
    if old["candidate"] != run["head_sha"] or old["obligationSha256"] != prior["candidateObligationSha256"]:
        raise ValueError("full donor candidate differs")
    current = json.loads(pathlib.Path(current_obligation_path).read_text())
    identity_fields = (
        "behavioralSha256", "compiledContractSha256", "toolchainProfileSha256",
        "verificationBoundsSha256", "formalCorpusSha256", "harnessSha256", "bindingSha256",
    )
    if any(old[field] != current[field] for field in identity_fields):
        raise ValueError("scoped pilot requires unchanged full semantic and binding identities")
    with tempfile.TemporaryDirectory() as scratch:
        paths = {}
        for name, data in evidence.items():
            path = pathlib.Path(scratch) / name
            path.write_bytes(data)
            paths[name] = str(path)
        current_plan = pathlib.Path(current_obligation_path).with_name("partition-plan.json")
        subprocess.run([
            "dotnet", "fsi", str(ROOT / "eng/optimistic-validation.fsx"), "--", "validate-prior",
            "--obligation", str(current_obligation_path), "--current-plan", str(current_plan),
            "--prior-obligation", paths["candidate-obligation.json"],
            "--prior-plan", paths["partition-plan.json"],
            "--aggregate-receipt", paths["coherent-aggregate-receipt.json"],
        ], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
        expected_selection = pathlib.Path(scratch) / "expected-selection.json"
        subprocess.run([
            "dotnet", "fsi", str(ROOT / "eng/optimistic-validation.fsx"), "--", "classify",
            "--obligation", str(current_obligation_path), "--current-plan", str(current_plan),
            "--prior-obligation", paths["candidate-obligation.json"],
            "--prior-run", str(run_id), "--prior-attempt", str(prior["attempt"]),
            "--prior-receipt", prior["executedReceiptSha256"],
            "--prior-completed", prior["completedAt"], "--prior-expires", prior["expiresAt"],
            "--prior-plan", paths["partition-plan.json"],
            "--aggregate-receipt", paths["coherent-aggregate-receipt.json"],
            "--output", str(expected_selection),
        ], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
        if expected_selection.read_bytes() != pathlib.Path(selection_path).read_bytes():
            raise ValueError("scoped selection differs from independent full-donor classification")


if __name__ == "__main__":
    if len(sys.argv) == 5 and sys.argv[1] == "verify-scoped":
        verify_scoped(*sys.argv[2:])
    elif len(sys.argv) == 4:
        profile(*sys.argv[1:])
    else:
        raise SystemExit("usage: optimistic-profile.py [verify-scoped] SELECTION OBLIGATION PROFILE")
