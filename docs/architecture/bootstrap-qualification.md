# Bootstrap qualification

FS.GG.Coordination qualifies an inert bootstrap substrate through six independently scheduled, read-only prerequisite jobs and one evidence join. The workflow runs for pull requests and pushes to `main`; every third-party action is pinned to an immutable commit and the workflow receives only `contents: read`.

## Qualification plan

`eng/bootstrap-qualification-plan.json` is the single semantic authority for gate identities, dependency edges, stable entrypoints, artifacts, timeouts, job environments, download/upload behavior, typed receipt roles, immutable action pins, action runtimes, triggers, permissions, concurrency, and terminal evidence. The compiled renderer has no parallel gate-identity list or ID-selected workflow branches: a representative ordinary gate addition changes only its plan declaration and stable script. `eng/generate-bootstrap-workflow.fsx -- --root .` deterministically projects the committed workflow; `eng/bootstrap-ci.fsx workflow --root .` rejects a stale projection. The generated YAML is deliberately thin and requires exactly these jobs:

- `deterministic-build`: locked restore and warnings-as-errors Release build.
- `compiler-and-tests`: unit and architecture suites, retaining architecture TRX.
- `canonical-quint`: one shared deterministic preparation followed by separately attributable Q1 and Q2 qualification, retaining a versioned JSON receipt.
- `dependency-and-security`: evaluated dependency policy plus a complete NuGet vulnerability report from the HTTPS public feed.
- `package-install-smoke`: a CI-only Protocol package restored and executed by a fresh consumer.
- `bootstrap-recovery`: reconstruction and clean-consumer execution from committed bytes.
- `evidence-manifest`: an exact-head manifest assembled only after all six prerequisite jobs pass.

Each job invokes one script under `eng/bootstrap-gates/`; command mechanics live behind those stable entrypoints rather than inside YAML. The renderer cannot emit mutable action references, expanded permissions, release/deployment routes, live GitHub write authority, or imported v1 completion machinery. Any manual YAML change is stale by construction.

The validator and evidence implementation compile into `FS.GG.Coordination.Qualification.Contracts.BootstrapCi`. `eng/bootstrap-ci.fsx` is only the production argument/stdout adapter. Architecture tests call the compiled decisions directly and retain bounded green/red process-level parity tests for the adapter.

NuGet action caching was measured and rejected. On exact-head run `33250382392`, the cold miss and warm exact-key hit both left restore at roughly the same scale while the cache action added restore/post overhead; deterministic and package jobs were a few seconds slower on the warm attempt, and compiler timing remained dominated by architecture-runner variance. The dependency set is only about 5.8 MB and locked restore is already short, so retaining the cache would add a fifth action and extra plan/workflow branches without moving the critical path. Every gate keeps locked restore; dependency/security, recovery, and canonical Quint keep their isolated cold paths. Job-level environment values use literals or the `github` context; `runner.temp` is confined to step inputs and runtime shell variables where that context is available.

The plan pins checkout v7.0.1, setup-dotnet v6.0.0, upload-artifact v7.0.1, and download-artifact v8.0.1 by exact commit. Their official `action.yml` manifests were resolved at those commits on 2026-08-29 and each declares `using: node24`; the plan records and validates that runtime inventory.

## Performance and complexity budget

The exact-merge baseline run `33248808361` took about 366 seconds end to end. Its compiler/test job took 315 seconds, including a 275-second architecture step. The retained TRX attributed 267.82 aggregate test-seconds to 58 bootstrap validator cases because every case launched a new FSI process. Direct compiled calls preserve those cases and add bounded green/red adapter parity, representative-gate change amplification, and direct missing-subject inversions for all seven entry points. The focused corpus now completes locally in about five seconds; the complete 176-test architecture suite completes in about 46 seconds on the same host.

Architecture tests cap the generated workflow at 210 lines, the plan at 180, the adapter at 20, the compiled core at 600, and all stable gate scripts together at 60. They also reject reintroduction of workflow byte digests, `requiredRunFragments`, or regular-expression YAML parsing.

## Evidence binding

The final job downloads each prerequisite artifact and copies the reviewed plan bytes into its evidence tree. It then emits `fsgg.coordination.bootstrap-evidence/2`, binding:

- the exact 40-hex candidate revision;
- the exact seven gate identities;
- each gate's stable entrypoint and artifact path;
- SHA-256 of the plan and every gate artifact.

Validation recomputes all digests from downloaded bytes and fails closed on a stale candidate, absent/duplicate/unknown gate, altered command contract, unsafe path, missing artifact, or digest mismatch.

## Canonical Quint execution

Formal qualification starts at the same time as the build and architecture paths; it does not wait behind them. `eng/qualify-canonical-quint.sh` acquires the exact digest-pinned toolchain and invokes the formal validator once. That invocation performs extraction, generation, projection checks, and typechecking once, emits the Q1 marker, and reuses the same prepared modules for Q2.

The eight positive invariants are checked by one pinned Quint multi-invariant invocation. The runner derives its completed inventory from execution and requires exactly 56 rejected Quint mutation processes before Q2 can pass; this corrects the earlier unmeasured assumption of 51. Removing or unexpectedly greening one control changes the derived count and fails qualification. The 41 independent Rust mutation checks use an explicit concurrency cap of two; Apalache checks remain sequential so their counterexample artifacts and process isolation are unchanged. A persistent Apalache server was rejected after it stalled on a counterexample-producing verification despite improving a narrow positive-invariant benchmark.

The retained `fsgg.coordination.canonical-quint-qualification/1` receipt records separate Q1/Q2 outcomes, derived positive and negative inventory cardinalities, preparation/Q2/total durations, direct external and Quint CLI counts, Apalache verify-invocation count, exact tool and input digests, an optional preparation digest, a hashed failure diagnostic, and a result digest over those outcome facts and process counts. Q1 failure is recorded as `failed/not-run`; a later failure is `passed/failed`. The upload step uses the sole permitted `always()` condition so a failed job retains its receipt, while the evidence join remains skipped and cannot accept it. Passing evidence is accepted only when both phases pass, all 8 invariants and 56 rejections are observed, the exact retained process inventory is 85 external processes / 61 Quint CLI processes / 14 Apalache verify invocations, the pins match, and the failure field is null. That exact inventory makes removing or silently skipping any retained positive route fail closed; changing the qualification catalogue requires an intentional producer-and-consumer contract update. SDD lifecycle commands are intentionally outside the reusable formal runner and remain obligations of the active roadmap work item.

## Authority ceiling

Bootstrap qualification builds, tests, inspects dependencies, creates a temporary `0.0.0-bootstrap` package, and uploads CI artifacts. It does not publish packages, create releases, deploy software, contact production mutation routes, or decide review/delivery/done state. Those concerns remain outside this bootstrap boundary.
