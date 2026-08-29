---
schemaVersion: 1
workId: 78-shorten-qualification-critical-path
title: Shorten Qualification Critical Path
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/78-shorten-qualification-critical-path/spec.md
sourceClarifications: work/78-shorten-qualification-critical-path/clarifications.md
sourceChecklist: work/78-shorten-qualification-critical-path/checklist.md
publicOrToolFacingImpact: true
---

# Shorten Qualification Critical Path Plan

Prose status: planned

## Source Snapshot
- spec: work/78-shorten-qualification-critical-path/spec.md sha256:c7556a646f7fa67d219249152be0a55cbd071ae7f252338d4fcd6b1049bf3ea5 schemaVersion:1
- clarifications: work/78-shorten-qualification-critical-path/clarifications.md sha256:091011265c33987f4db10a85b75526d1f82c2a835ec0875b736ac8174c702b3a schemaVersion:1
- checklist: work/78-shorten-qualification-critical-path/checklist.md sha256:beca79ed85fc343d9de20b80e05f773753328a4b794a7ca1533d07617875efc1 schemaVersion:1

## Plan Scope
- Work item 78-shorten-qualification-critical-path is planned from the current specification, clarification, and checklist facts.
- Requirement count: 9.
- Clarification decision count: 6.
- Checklist result count: 9.

## Plan Decisions
- PD-001 [AC-001] [AC-002] [FR-001] [FR-002] [DEC-001] [DEC-002] complete: Introduce `fsgg.coordination.bootstrap-qualification-plan/2` as the sole typed declaration of exact gate IDs, dependency edges, entry points, artifacts, timeouts, cache policy, action pins, permissions, triggers, and terminal evidence identity.
- PD-002 [AC-001] [AC-002] [FR-002] [FR-007] [DEC-002] complete: Deterministically render the thin committed workflow from the plan; validation compares typed plan semantics and generator currency rather than treating arbitrary workflow bytes or step labels as authority.
- PD-003 [AC-003] [FR-003] [DEC-003] [DEC-005] complete: Move bootstrap contract loading, workflow-plan validation, vulnerability validation, receipt validation, evidence collection, and evidence verification into the compiled Qualification.Contracts module; retain `eng/bootstrap-ci.fsx` as a thin adapter.
- PD-004 [AC-003] [FR-003] [DEC-005] complete: Rewrite the pure architecture mutation corpus to call the compiled validator in-process; retain one green and bounded red production-adapter parity set so process wiring remains covered without one FSI startup per assertion.
- PD-005 [AC-004] [FR-004] complete: Replace artifact action pins with official upload v7.0.1 and download v8.0.1 Node 24 SHAs; register checkout, setup-dotnet, cache, upload, and download identities once in the plan and verify exact official runtime evidence.
- PD-006 [AC-006] [FR-006] [DEC-004] complete: Add exact-key NuGet caching only to ordinary restore gates, keyed by runner OS plus `global.json` and every lock file; keep dependency/security cold and make miss/absence ordinary restore paths.
- PD-007 [AC-002] [FR-001] [FR-008] complete: Give each execution gate one stable `eng/bootstrap-gates/<id>.sh` entry point and preserve its existing subject, artifact, failure behavior, exact-head boundary, and timeout.
- PD-008 [AC-001] [FR-007] complete: Add a complexity budget and mutation-proven topology tests that reject duplicate plan declarations, unknown entry points, action/pin/runtime drift, changed permissions/triggers, missing dependencies, artifact collisions, and stale workflow projection.
- PD-009 [AC-005] [AC-007] [FR-005] [DEC-006] complete: Record the five-run pre-change baseline and candidate cohort with queue/setup/subject/fan-in/total and runner-minute measurements; adopt only a candidate meeting every threshold.
- PD-010 [AC-008] [FR-009] complete: Version the terminal exact-head evidence contract independently of execution mechanics so #80 can later target it without this item authorizing reuse.

## Contract Impact
- PC-001 [PD-001] [PD-002] semantic plan: `eng/bootstrap-qualification-plan.json` is the one reviewed topology and action authority; `.github/workflows/bootstrap-qualification.yml` is its deterministic projection.
- PC-002 [PD-003] [PD-004] compiled validator: `FS.GG.Coordination.Qualification.Contracts.BootstrapCi` returns typed outcomes in-process and the FSI adapter projects them to stable stdout/stderr/exit codes.
- PC-003 [PD-005] [PD-006] acquisition boundary: exact Node 24 action pins and exact dependency-cache policy are plan data with structural inversions.
- PC-004 [PD-007] execution entry points: one shell entry point per gate owns command sequencing and artifact staging; workflow generation references paths, not copied command bodies.
- PC-005 [PD-010] terminal evidence: one versioned exact-head manifest binds plan digest, gate identities, artifact digests, and commands/entry-point identities.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-008] [PC-001] planProjection: Validate the canonical plan and generated workflow; mutate every closed set, dependency edge, permission, trigger, timeout, entry point, artifact, cache rule, action pin/runtime, and projection currency predicate to named red outcomes.
- VO-002 [PD-003] [PD-004] [PC-002] compiledParity: Run all existing pure receipt/evidence/vulnerability/workflow mutations in-process, plus bounded green/red FSI adapter parity; compare diagnostic sets and output contracts.
- VO-003 [PD-005] [PC-003] actionRuntime: Resolve each official pinned action manifest, prove immutable SHA and `node24`, and require hosted annotation inventory to contain no Node 20 warning.
- VO-004 [PD-006] [PC-003] cacheSafety: Exercise exact hit, miss, absent, and stale key cases; require locked restore plus byte-identical semantic evidence and preserve a cold dependency/security route.
- VO-005 [PD-007] [PC-004] gateInversions: Run every stable entry point green and independently mutate or remove its subject so each gate and the terminal join fail for the named reason.
- VO-006 [PD-008] [PC-001] complexityBudget: Mechanically report one semantic declaration, one entry point, no mirrored exact command list, and at most two non-test edit locations for a representative gate change.
- VO-007 [PD-009] performanceComparison: Publish baseline/candidate cohorts with median/p95 settled time, architecture step, runner minutes, queue delay, and rejected candidates; require AC-005 thresholds.
- VO-008 [PD-010] [PC-005] terminalContract: Mutate candidate SHA, plan digest, gate set, artifact identity/digest, entry-point identity, and execution disposition; every mutation is red before #80 integration.

## Performance Intent
- Baseline exact-merge run 33248808361: workflow 366s, recovery 338s, canonical Quint 332s, compiler/tests 315s, architecture step 275s; aggregate architecture test execution 668.00s, of which BootstrapCiTests account for 267.82s.
- Primary lever: remove repeated FSI startup from pure bootstrap validator mutations while retaining adapter parity.
- Secondary lever: exact-key NuGet acquisition caching, adopted only when hosted evidence improves runner consumption without semantic drift.
- Acceptance thresholds: at least 15% lower median settled workflow execution, 30% lower architecture-step median, 10% lower runner minutes, and no p95 regression across comparable successful cohorts.

## Migration Posture
- PM-001 [PC-001] [PC-002] additiveProof: Add the plan, compiled core, entry points, and generator beside the legacy contract; prove parity and projection currency before switching workflow authority.
- PM-002 [PC-001] [PC-004] atomicCutover: Switch the generated workflow and evidence contract only after every gate entry point and inversion is green.
- PM-003 [PC-001] cleanup: Delete `requiredRunFragments`, hand-rolled YAML command parsing, and workflow byte digest authority in the same item; do not leave dual semantic authorities.

## Generated View Impact
- GV-001 [PD-001] workflowProjection: the committed workflow regenerates deterministically from the reviewed plan and must be current.
- GV-002 [PD-009] performanceEvidence: `work/78-shorten-qualification-critical-path/performance-evaluation.md` retains comparable source-linked measurements, not generated runtime artifacts.
- GV-003 [PD-001] workModel: readiness/78-shorten-qualification-critical-path/work-model.json refreshes from current lifecycle sources and must be current before ship.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Cache optimization is secondary: the baseline restore/build step is 29 seconds while architecture execution is 275 seconds.
- The generator may reject harmless manual workflow formatting as stale projection, but only plan semantics contribute to authority and evidence identity.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 78-shorten-qualification-critical-path`.
