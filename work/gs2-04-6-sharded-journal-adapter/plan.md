---
schemaVersion: 1
workId: gs2-04-6-sharded-journal-adapter
title: Gs2 04 6 Sharded Journal Adapter
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-6-sharded-journal-adapter/spec.md
sourceClarifications: work/gs2-04-6-sharded-journal-adapter/clarifications.md
sourceChecklist: work/gs2-04-6-sharded-journal-adapter/checklist.md
publicOrToolFacingImpact: true
---

# GS2-04.6 Sharded Git Journal Adapter Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-6-sharded-journal-adapter/spec.md sha256:2914aa699dc05cc507211ffbfdc682fa24513606c1388435df948b8eee761fea schemaVersion:1
- clarifications: work/gs2-04-6-sharded-journal-adapter/clarifications.md sha256:7f23dbc640bfdf3129755e30d32447d70d22c5b887e604f1644d51f1e4b3c12d schemaVersion:1
- checklist: work/gs2-04-6-sharded-journal-adapter/checklist.md sha256:5c878a83209051219379dce5fb53d128cb3986d7d1060571a734bdd8d482163c schemaVersion:1

## Plan Scope
- Add a pure-first `ShardedJournalAdapter` module to `FS.GG.Coordination.GitHub`, exposing domain types and total validation/planning functions through `.fsi` before `.fs`.
- Keep Git process execution and live GitHub transport outside this unit; the adapter emits typed receive-pack plans and evaluates typed observations.
- Add an independently authored qualification contract/result model, executable FSI validator, real local Git-object fixtures, focused unit tests, architecture controls, and retained evidence.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] complete: Implement canonical aggregate normalization/digest/shard and a closed journal-kind-to-ref derivation that cannot emit a path outside the protected family.
- PD-002 [AC-002] [FR-002] [DEC-001] complete: Define canonical event/head/snapshot records, serialize through one deterministic codec, and validate commit/tree/ancestry observations as a complete replay rather than independent object checks.
- PD-003 [AC-003] [FR-003] [DEC-002] complete: Model a stable-operation CAS proposal with exact parent/ref/refspec/lease arguments and reconcile typed transport plus authoritative reread evidence into four closed outcomes.
- PD-004 [AC-004] [FR-004] [DEC-003] complete: Model fenced grants and require a complete fresh authoritative head with exact aggregate/commit/generation before any effect plan becomes usable.
- PD-005 [AC-005] [FR-005] [DEC-004] complete: Derive globally sorted acquisition keys and an append-only saga plan that persists the touch set, releases unconsumed grants, and compensates applied effects in reverse order.
- PD-006 [AC-006] [FR-006] [DEC-005] complete: Model complete repository/ruleset/effective-rule observations and validate exact ids, names, activation, target, rule split, writer bypass, and zero integrity bypass.
- PD-007 [AC-007] [FR-007] [DEC-003] complete: Use explicit observation/outcome unions so incomplete, projection-only, unauthorized, unreadable, deleted, divergent, or drifted facts never collapse into absence or authority.
- PD-008 [AC-008] [FR-008] [DEC-006] complete: Generate the positive qualification inventory from the closed case vocabulary and keep independently authored mutations for every named failure class, with a deterministic typed result artifact.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] publicSurface: Add `src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fsi` before its `.fs`, compile both in the project, and keep all failure/outcome cases explicit and structurally comparable.
- PC-002 [PD-008] toolContract: Implement the already-registered literal `dotnet fsi eng/validate-github-sharded-journal.fsx -- .` command and emit `fsgg.coordination.github-sharded-journal-qualification/1` results without changing catalog identity.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PC-001] semanticTest: Add focused unit tests and local Git-object fixtures for positive behavior and every independently named refusal, including canonical-byte, ancestry, CAS ambiguity, fencing, ruleset, and saga cases.
- VO-002 [PD-008] [PC-002] gateMutation: Run the registered validator green, invert at least one load-bearing accepted predicate and observe red, restore it, then bind catalog/index/candidate/results through `roadmap-work manifest` and `roadmap-work gates`.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The adapter is a new public module with no runtime wiring or legacy migration; existing comment/project adapters remain unchanged and no v1 authority is retired in GS2-04.6.

## Generated View Impact
- GV-001 [PD-008] [VO-002] evidenceViews: Refresh `readiness/gs2-04-6-sharded-journal-adapter` through analyze, evidence, verify, and ship; retain the independently authored Q3 result and ignored roadmap manifest/gate outputs bound to the exact commit.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- The existing protected administrative run proves the repository/ruleset boundary, but this Q3 adapter must still reject drifted synthetic observations and must not perform a live write.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-6-sharded-journal-adapter`.
