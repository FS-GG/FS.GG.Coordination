## Iteration 1 — 2026-09-15T10:15:00Z

**Verify command:** `dotnet test FS.GG.Coordination.sln -c Release`
**Exit code:** 1

**Primary failure:** test-failure
- Signal: `CodexExecutionProviderTests.zero exit and final candidate require terminal turn event: LaunchAmbiguous "codex-completed-before-thread-started"`, plus Git commits in materialized test workspaces without a local author identity.
- Hypothesis: completion and `thread.started` can both settle before launch admission inspects `Task.WhenAny`, while cloned fixture repositories do not inherit the source repository's local Git identity.

**Fix applied:**
- `src/FS.GG.Coordination.Orchestration.Execution.Codex/CodexExecutionProvider.fs` — admit a launch whenever the bounded stdout pump has already observed a valid thread identity, even if process completion settled concurrently.
- `tests/FS.GG.Coordination.Orchestration.Execution.Codex.Tests/ExecutorRuntimeTests.fs` — bind fixture-only author identity on each direct test commit in a materialized clone.
- `eng/bootstrap-gates/optimistic-dispatch-recovery.sh` — pass paginated JSON from `gh` to `jq` instead of combining incompatible GitHub CLI formatting flags.
- `tests/FS.GG.Coordination.UnitTests/QualificationReuseSelectionTests.fs` — retain the paginated census contract and assert the compatible command boundary.

**Narrow re-run result:** pass — Codex execution 37/37; unit 376/376; mocked current-CLI dispatcher path exited zero.
**Full verify result:** deferred — the raw solution command is not the repository-native composition: it started PostgreSQL projects without their provisioner and ran timing-sensitive projects concurrently.

## Iteration 2 — 2026-09-15T11:30:00Z

**Verify command:** repository-native bootstrap qualification and full-suite sentinel on the committed candidate
**Exit code:** 1

**Primary failure:** runtime-exception
- Signal: exact-head sentinel run `34964808046` reported `FileNotFoundException: /tmp/o0-postgresql-current-path` from every PostgreSQL test project; the repaired Codex suite passed 37/37.
- Hypothesis: the sentinel's direct solution-wide test command bypasses the repository-owned PostgreSQL provisioners.

**Fix applied:**
- `eng/workflow-selection-sentinel.sh` — enumerate all test projects, run ordinary projects directly, and run every PostgreSQL project through its isolated private provisioner.
- `tests/FS.GG.Coordination.ArchitectureTests/GitHubWorkflowSelectionArchitectureTests.fs` — require complete project discovery and the provisioned PostgreSQL path.

**Narrow re-run result:** pass
**Full verify result:** fail — the sentinel itself bypassed required database provisioning.

## Iteration 3 — 2026-09-15T11:51:00Z

**Verify command:** repository-native bootstrap qualification and full-suite sentinel on the repaired exact head
**Exit code:** 1

**Primary failure:** test-failure
- Signal: exact-head sentinel run `34965698496` provisioned the PostgreSQL projects and passed the repaired Codex suite, then two Observer deadline tests refused planner launch.
- Hypothesis: the real-clock tests reused a fixture budget whose absolute deadline was 2026-09-10, so the fixture expired as the calendar advanced; their 75 ms and 100 ms command windows also measured thread-pool admission latency on a loaded runner rather than the bounded-planner contract.

**Fix applied:**
- `tests/FS.GG.Coordination.Orchestration.Observer.Tests/ObserverTests.fs` — bind the fixture budget deadline to the test's current clock, give both real-clock boundary tests a two-second launch window, and assert the call still returns inside a ten-second wall-clock bound. The noncooperative planner, synchronous-entry assertion, expiry outcome, and retained reservation assertions remain intact.
- `eng/workflow-selection-sentinel.sh` — canonicalize the result directory before PostgreSQL launchers change their working directory, so every TRX path remains rooted at the sentinel output directory even when `RUNNER_TEMP` is relative.

**Narrow re-run result:** pass — both Observer boundary tests passed in three consecutive focused runs; the sentinel architecture contract and shell syntax check passed.
**Full verify result:** fail — run `34966512789` passed every project selected by the repaired loop and all policy/package validators, then correctly disabled selection because its PostgreSQL launchers defaulted to unavailable runner-local binaries. The `*.Tests.fsproj` glob also omitted the repository's `UnitTests` and `ArchitectureTests` projects.

## Iteration 4 — 2026-09-15T12:22:00Z

**Verify command:** focused sentinel project-census and provisioner contract
**Exit code:** 0

**Primary failure:** environment-mismatch
- Signal: the exact-head sentinel emitted no PostgreSQL test run because every private provisioner stopped at its binary-mode prerequisite; its ordinary glob selected 7 projects and skipped the two test projects whose names end in `UnitTests.fsproj` and `ArchitectureTests.fsproj`.
- Hypothesis: selecting every top-level `*Tests.fsproj` project and explicitly using the workflow runner's Docker provisioner mode covers the complete repository test census through the existing isolated launchers.

**Fix applied:**
- `eng/workflow-selection-sentinel.sh` — widen the test-project suffix to `*Tests.fsproj` and bind PostgreSQL launches to Docker mode.
- `tests/FS.GG.Coordination.ArchitectureTests/GitHubWorkflowSelectionArchitectureTests.fs` — pin the complete suffix and Docker provisioner contract.

**Narrow re-run result:** pass — the census selects exactly 9 test projects, including 3 PostgreSQL projects and both Unit/Architecture suites; the architecture contract and shell syntax pass.
**Full verify result:** deferred — no additional optional full-suite dispatch under the operator's cost constraint.
