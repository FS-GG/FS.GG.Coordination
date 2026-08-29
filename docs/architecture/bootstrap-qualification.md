# Bootstrap qualification

FS.GG.Coordination qualifies an inert bootstrap substrate through one read-only route decision, six conditionally scheduled prerequisite jobs, and one evidence join that always evaluates the selected route. The workflow runs for pull requests and pushes to `main`; every third-party action is pinned to an immutable commit and the workflow receives only `contents: read` plus `actions: read` for immutable cross-run artifact retrieval.

## Qualification plan

`eng/bootstrap-qualification-plan.json` is the single semantic authority for reuse policy, gate identities, dependency edges, stable entrypoints, artifacts, timeouts, job environments, download/upload behavior, typed receipt roles, immutable action pins, action runtimes, triggers, permissions, concurrency, and terminal evidence. The compiled renderer has no parallel gate-identity list or ID-selected ordinary-gate branches: a representative ordinary gate addition changes only its plan declaration and stable script. `eng/generate-bootstrap-workflow.fsx -- --root .` deterministically projects the committed workflow; `eng/bootstrap-ci.fsx workflow --root .` rejects a stale projection. The generated YAML is deliberately thin and requires:

- `reuse-decision`: construct the complete-tree qualification subject; search a bounded census of retained terminal artifacts; validate the owning workflow run and prior manifest; emit a canonical `reuse`, `execute`, or `refuse` receipt.

- `deterministic-build`: locked restore and warnings-as-errors Release build.
- `compiler-and-tests`: unit and architecture suites, retaining architecture TRX.
- `canonical-quint`: one shared deterministic preparation followed by separately attributable Q1 and Q2 qualification, retaining a versioned JSON receipt.
- `dependency-and-security`: evaluated dependency policy plus a complete NuGet vulnerability report from the HTTPS public feed.
- `package-install-smoke`: a CI-only Protocol package restored and executed by a fresh consumer.
- `bootstrap-recovery`: reconstruction and clean-consumer execution from committed bytes.
- `evidence-manifest`: the terminal exact-head authority. It runs under `always()` after the decision and six gate contexts, validates a refusal as red, joins current artifacts after execution, or downloads and re-hashes the selected prior run's complete artifact set after reuse.

Each job invokes one script under `eng/bootstrap-gates/`; command mechanics live behind those stable entrypoints rather than inside YAML. The renderer cannot emit mutable action references, expanded permissions, release/deployment routes, live GitHub write authority, or imported v1 completion machinery. Any manual YAML change is stale by construction.

The validator and evidence implementation compile into `FS.GG.Coordination.Qualification.Contracts.BootstrapCi`. `eng/bootstrap-ci.fsx` is only the production argument/stdout adapter. Architecture tests call the compiled decisions directly and retain bounded green/red process-level parity tests for the adapter.

NuGet action caching was measured and rejected. On exact-head run `33250382392`, the cold miss and warm exact-key hit both left restore at roughly the same scale while the cache action added restore/post overhead; deterministic and package jobs were a few seconds slower on the warm attempt, and compiler timing remained dominated by architecture-runner variance. The dependency set is only about 5.8 MB and locked restore is already short, so retaining the cache would add a fifth action and extra plan/workflow branches without moving the critical path. Every gate keeps locked restore; dependency/security, recovery, and canonical Quint keep their isolated cold paths. Job-level environment values use literals or the `github` context; `runner.temp` is confined to step inputs and runtime shell variables where that context is available.

The plan pins checkout v7.0.1, setup-dotnet v6.0.0, upload-artifact v7.0.1, and download-artifact v8.0.1 by exact commit. Their official `action.yml` manifests were resolved at those commits on 2026-08-29 and each declares `using: node24`; the plan records and validates that runtime inventory. Cross-run download uses the official action's `run-id` and `github-token` inputs, and the token has `actions: read` but no Actions write authority.

## Exact-head reuse

Reuse is an evidence authorization path, not a dependency cache. `QualificationReuse` hashes the complete tracked tree from canonical mode/path/length/byte frames with SHA-256, so a tracked byte, executable bit, symlink target, path, model, fixture, gate, workflow, tool manifest, or dependency change forces execution. Named plan, workflow, toolchain, dependency, gate-set, environment, and review-policy component digests make the otherwise coarse full-tree boundary auditable. Commit parents, authorship, timestamps, messages, and signatures are outside the Git tree; a provenance-only commit may therefore reuse an identical tree without hiding semantic drift.

Discovery reads at most 100 live `bootstrap-evidence-manifest` artifacts created at or after the plan's explicit v3 reuse-evidence epoch; pre-contract history is excluded before any artifact download, so misses do not become slower as legacy runs accumulate. A candidate must belong to this exact workflow path, exact successful completed attempt, a different run, the declared prior head, the current plan, and the current complete subject. Legacy manifests and manifests produced by reuse are not selectable, preventing authority chains whose intermediate run lacks the six execution artifacts. No compatible candidate or a preselection lookup failure is an `execute` miss. Once a candidate is selected, missing, expired, changed, malformed, or contradictory evidence is `refuse`; the terminal check cannot silently fall back after the expensive jobs were skipped.

The reuse receipt is canonical compact JSON with a self-digest and binds the current exact head, complete subject, prior head/run/attempt, prior manifest digest, artifact expiry, and named reason. It also carries observational source runner minutes derived from the completed run's job timestamps. That value is nullable when the jobs census cannot be measured and never participates in the equivalence decision. At terminal acceptance the workflow re-downloads all prior artifacts, validates the prior execution receipt and manifest, re-hashes every gate artifact, and emits a new `bootstrap-evidence/3` manifest whose candidate is the current head while its `prior` block preserves the source provenance. Current-head independent review remains a separate mandatory delivery record and reviews the route and receipt; no prior review is relabelled onto the current head.

## Performance and complexity budget

The exact-merge baseline run `33248808361` took about 366 seconds end to end. Its compiler/test job took 315 seconds, including a 275-second architecture step. The retained TRX attributed 267.82 aggregate test-seconds to 58 bootstrap validator cases because every case launched a new FSI process. Direct compiled calls preserve those cases and add bounded green/red adapter parity, representative-gate change amplification, and direct missing-subject inversions for all seven entry points. The focused corpus now completes locally in about five seconds; the complete 226-test architecture suite completes in about 47 seconds on the same host.

Hosted run `33255549867` established the full-execution source at 522 seconds wall / 922 runner-seconds. Its byte-identical-tree successor `33255929882` settled through reuse in 55 seconds wall / 49 runner-seconds while the six execution gates were skipped and terminal evidence re-hashed the retained artifacts. The observed saving was 467 wall-seconds and 873 runner-seconds (14m33s). Full measurements and the comparable pre-change cohort are retained in the work package performance evaluation.

Architecture tests cap the generated workflow at 260 lines, the plan at 190, the FSI adapter at 22, the existing compiled workflow/evidence adapter at 900, the pure reuse domain at 300, and all stable gate scripts together at 140. The added surface is separated by responsibility: GitHub discovery stays in one shell adapter, canonical authority stays in the network-free reuse domain, and topology stays in the plan/renderer. The suite also rejects reintroduction of workflow byte digests, `requiredRunFragments`, regular-expression YAML parsing, or GitHub coupling in the pure reuse domain.

## Evidence binding

The final job downloads current or selected-prior prerequisite artifacts and copies the reviewed plan bytes into its evidence tree. It then emits `fsgg.coordination.bootstrap-evidence/3`, binding:

- the exact 40-hex candidate revision;
- the `execute` or `reuse` route, complete subject digest, decision self-digest, and immutable prior identity when present;
- the exact seven gate identities;
- each gate's stable entrypoint and artifact path;
- SHA-256 of the plan and every gate artifact.

Validation recomputes all digests from downloaded bytes and fails closed on a stale candidate, absent/duplicate/unknown gate, altered command contract, unsafe path, missing artifact, or digest mismatch.

## Canonical Quint execution

Formal qualification starts at the same time as the build and architecture paths; it does not wait behind them. `eng/qualify-canonical-quint.sh` acquires the exact digest-pinned toolchain and invokes the formal validator once. That invocation performs extraction, generation, projection checks, and typechecking once, emits the Q1 marker, and reuses the same prepared modules for Q2.

The eight positive invariants are checked by one pinned Quint multi-invariant invocation. The runner derives its completed inventory from execution and requires exactly 101 rejected Quint processes before Q2 can pass: the established mutation inventory, one safety-invalid run for each GS2-03.5 family, and paired TLC transition-removal plus Rust ITF-projection reproductions for every temporal obligation. Removing or unexpectedly greening one control changes the derived count and fails qualification. The independent Rust mutation checks use an explicit concurrency cap of two. TLC temporal checks and Apalache checks remain sequential so their artifacts and process isolation are deterministic. Formal calibration records observed Rust samples, TLC generated/distinct states and transitions, elapsed time, aggregate process-tree peak RSS, and co-bound artifact bytes against explicit budgets. A persistent Apalache server was rejected after it stalled on a counterexample-producing verification despite improving a narrow positive-invariant benchmark.

The retained `fsgg.coordination.canonical-quint-qualification/1` receipt records separate Q1/Q2 outcomes, derived positive and negative inventory cardinalities, preparation/Q2/total durations, direct external and Quint CLI counts, formal verify-invocation count, six manifest/trace/ITF digest tuples, exact tool and input digests, an optional preparation digest, a hashed failure diagnostic, and a result digest over those outcome facts and evidence identities. Q1 failure is recorded as `failed/not-run`; a later failure is `passed/failed`. The upload step uses the sole permitted `always()` condition so a failed job retains its receipt, while the evidence join remains skipped and cannot accept it. Passing evidence is accepted only when both phases pass, all 8 canonical invariants and 101 rejections are observed, the exact retained process inventory is 151 external processes / 126 Quint CLI processes / 32 formal verify invocations, all six counterexample tuples are present, the pins match, and the failure field is null. That exact inventory makes removing or silently skipping any retained positive route fail closed; changing the qualification catalogue requires an intentional producer-and-consumer contract update. SDD lifecycle commands are intentionally outside the reusable formal runner and remain obligations of the active roadmap work item.

## Authority ceiling

Bootstrap qualification builds, tests, inspects dependencies, creates a temporary `0.0.0-bootstrap` package, and uploads CI artifacts. It does not publish packages, create releases, deploy software, contact production mutation routes, or decide review/delivery/done state. Those concerns remain outside this bootstrap boundary.
