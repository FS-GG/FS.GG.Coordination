#!/usr/bin/env python3
"""Run genuine production source mutations against independent bounded preflight controls.

Own the compiler lane before invoking. Candidate bytes are restored after every mutant;
all runs use one compiler, no build server and no product/native workload permission.
"""
from pathlib import Path
import argparse
import json
import os
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src/FS.GG.Coordination.Orchestration.Execution/PreparedAttempt.fs"
PROJECT = ROOT / "tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj"
MUTANTS = [
    ("bypass-discovery", [
        ("for check in spec.Checks do\n                        if Result.isOk result", "for check in spec.Checks |> List.filter (fun c -> c.Kind = CapsuleCheckKind.Import) do\n                        if Result.isOk result"),
        ("(spec.Checks |> List.map _.Id |> Set.ofList)", "(spec.Checks |> List.filter (fun c -> c.Kind = CapsuleCheckKind.Import) |> List.map _.Id |> Set.ofList)")
    ], "FullyQualifiedName~PreparedAttemptTests"),
    ("drop-invalidation", [
        ("else\n            { state with\n                InputIdentity = identity\n                ObservedChecks = Set.empty\n                PreparedIdentity = None\n            }",
         "else\n            { state with\n                InputIdentity = identity\n                PreparedIdentity = Some identity\n            }")
    ], "FullyQualifiedName~PreparedAttemptReplayTests"),
    ("drop-expiry-effects", [
        ('let effects =\n                expired |> List.collect (fun id -> [ "retire:" + id; "cleanup:" + id ])', "let effects = []")
    ], "FullyQualifiedName~PreparedAttemptReplayTests"),
    ("false-cleanup-completion", [
        ('not terminationObserved\n            || not (\n                Map.containsKey identity state.Owned\n                || Set.contains identity state.CleanupPending\n            )',
         'not terminationObserved && false\n            || not (\n                Map.containsKey identity state.Owned\n                || Set.contains identity state.CleanupPending\n            ) && false')
    ], "FullyQualifiedName~PreparedAttemptReplayTests"),
    ("bypass-custody-filter", [
        ('"result" when s.Filtered && not s.ResultObserved', '"result" when s.Bound && not s.ResultObserved')
    ], "FullyQualifiedName~custody requirements"),
    ("json-as-custody-cleanup", [
        ('"cleanup" when s.GroupTerminated && s.Pending', '"cleanup" when s.ResultObserved && s.Pending')
    ], "FullyQualifiedName~custody requirements"),
    ("leader-only-custody", [
        ('accept { s with LeaderExited = true } [ "observe-leader-exit" ]', 'accept { s with LeaderExited = true; GroupTerminated = true; GroupLive = false } [ "observe-leader-exit" ]')
    ], "FullyQualifiedName~custody requirements"),
    ("drop-timeout-termination", [
        ('[ "signal-owned-group"; "await-group-termination" ]', '[ "signal-owned-group" ]')
    ], "FullyQualifiedName~custody requirements"),
]


def run_tests(selection, output):
    env = dict(os.environ, DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="1", DOTNET_PROCESSOR_COUNT="1")
    command = ["dotnet", "test", str(PROJECT), "--no-restore", "-m:1", "/nr:false", "-p:UseSharedCompilation=false",
               "--filter", selection, "--logger", "console;verbosity=minimal"]
    if hasattr(os, "sched_getaffinity"):
        command = ["taskset", "-c", str(min(os.sched_getaffinity(0))), *command]
    started = time.monotonic()
    with output.open("w") as log:
        result = subprocess.run(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=60)
    return result.returncode, time.monotonic() - started


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    directory = args.output or Path(tempfile.mkdtemp(prefix="fsgg-preflight-mutants-"))
    directory.mkdir(parents=True, exist_ok=True)
    original = SOURCE.read_text()
    observations = []
    try:
        for name, replacements, selection in MUTANTS:
            changed = original
            for before, after in replacements:
                if changed.count(before) != 1:
                    raise RuntimeError(f"{name}: causal subject changed; refuse stale mutant")
                changed = changed.replace(before, after)
            SOURCE.write_text(changed)
            path = directory / (name + ".log")
            code, elapsed = run_tests(selection, path)
            output = path.read_text()
            SOURCE.write_text(original)
            if code == 0 or "Failed!" not in output or "error FS" in output:
                raise RuntimeError(f"{name}: no genuine test divergence; inspect {path.name}")
            observations.append({"mutant": name, "exit": code, "elapsedSeconds": elapsed, "output": path.name})
            print(f"CAUSAL_MUTANT_CAUGHT {name}", flush=True)
    finally:
        SOURCE.write_text(original)
    code, elapsed = run_tests("FullyQualifiedName~PreparedAttempt", directory / "restored-positive.log")
    if code != 0:
        raise RuntimeError("restored production source did not pass")
    (directory / "result.json").write_text(json.dumps({"schema": "fsgg.preflight-production-mutants/1",
        "mutants": observations, "restoredPositiveSeconds": elapsed, "samplingIsProof": False}, indent=2) + "\n")
    print(f"PREFLIGHT_PRODUCTION_MUTANTS_OK count={len(observations)}")


if __name__ == "__main__":
    main()
