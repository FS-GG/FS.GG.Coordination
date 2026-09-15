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
**Exit code:** pending

**Primary failure:** runtime-exception
- Signal: `eng/workflow-selection-sentinel.sh` launched PostgreSQL assemblies without `run-private-postgres.sh`.
- Hypothesis: explicit repository-owned provisioning will make the full test census representative and deterministic.

**Fix applied:**
- Same as iteration 2; this iteration validates the repaired sentinel.

**Narrow re-run result:** pass — sentinel contract architecture test and shell syntax validation.
**Full verify result:** deferred
