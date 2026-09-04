---
schemaVersion: 1
workId: 299-gs2-07-2-narrow-reconciliation
title: GS2-07.2 narrow reconciliation
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/299-gs2-07-2-narrow-reconciliation/spec.md
sourceClarifications: work/299-gs2-07-2-narrow-reconciliation/clarifications.md
sourceChecklist: work/299-gs2-07-2-narrow-reconciliation/checklist.md
publicOrToolFacingImpact: true
---

# GS2-07.2 narrow reconciliation Plan

Prose status: planned

## Source Snapshot
- spec: work/299-gs2-07-2-narrow-reconciliation/spec.md sha256:3ee9e3c492a3727015fb3532a9d58fb96e57c85840dd6f8b5c7689edcdb86673 schemaVersion:1
- clarifications: work/299-gs2-07-2-narrow-reconciliation/clarifications.md sha256:79dd3ba704564c4d87792c0e08ca11cc70d03d2546205270a924a399b2030347 schemaVersion:1
- checklist: work/299-gs2-07-2-narrow-reconciliation/checklist.md sha256:ed1d3eded400eedf61ba2f4ed0d827948487d860b42237414923ce393ffe2d10 schemaVersion:1

## Plan Scope
- Work item 299-gs2-07-2-narrow-reconciliation is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 0.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a closed `GitHubReconciliationEventKind` inventory for issue, relation, Project, repository, ruleset, run/check, release, and installation events, and normalize each input to repository, subject identity, and positive revision before scheduling.
- PD-002 [AC-001] [FR-001] complete: Compile scheduling intent into one deterministic entry per normalized repository/subject key, retaining the newest revision under duplicate or reordered delivery, with a length-framed SHA-256 seal over all contract fields.
- PD-003 [AC-001] [FR-001] complete: Model fresh-observe/reduce/sealed-plan/apply/verify as the sole accepted writer route; event and command origins are schedule-only and direct-write, unsealed-plan, altered routing, altered seal, replay, stale, conflicting, and cross-scope inputs are typed refusals.
- PD-004 [AC-001] [FR-001] complete: Supply generated and independently authored full-inventory controls, focused mutation tests, a no-network/no-production validator, retained evidence, clean-candidate roadmap manifest/gates, and an append-only acceptance receipt after protected merge.

## Contract Impact
- PC-001 [PD-001] additiveApi: Add `GitHubNarrowReconciliationQualification` in the qualification-contract assembly; no existing public contract or canonical Quint byte changes.
- PC-002 [PD-002] canonicalSerialization: Serialization, scheduling keys, queue receipts, dispositions, and seals are deterministic length-framed values with explicit parse/verify/replay refusal semantics.
- PC-003 [PD-003] writerAuthority: Only the complete reconciler phase sequence can author derived state; command and event routes expose scheduling intent without mutation capability.
- PC-004 [PD-004] qualificationGate: The already registered `github-narrow-reconciliation-contract` Q3 command becomes executable and binds retained generated/independent evidence without network or production mutation.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Unit tests cover every supported event kind and refuse unsupported, missing, malformed, unknown, incomplete, cross-scope, stale-revision, and conflicting-subject inputs.
- VO-002 [PD-002] [PC-002] mutationTest: Generated plus independently authored controls and a deliberate mutation prove duplicate/reorder convergence, newest-revision retention, scheduling-key/deduplication behavior, ordering, seal, and byte-identical replay.
- VO-003 [PD-003] [PC-003] semanticTest: Tests prove command/event origins cannot write, incomplete or reordered reconciler phases and unsealed/altered plans refuse, and only fresh-observe/reduce/sealed-plan/apply/verify succeeds.
- VO-004 [PD-004] [PC-004] integrationTest: Warning-as-error Release build, focused and full suites, exact roadmap manifest/gates, SDD verify/ship, exact-head critique, hosted checks, guarded merge, protected-main verification, and append-only receipt all pass.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveOnly: Existing qualification contracts and consumers remain byte-for-byte compatible; the new module, tests, validator, evidence, and documentation are additive and require no data or runtime migration.

## Generated View Impact
- GV-001 [PD-004] lifecycleEvidence: SDD-generated readiness views derive from the source-authored plan/tasks/evidence, while ignored roadmap candidate/gate results derive from the exact clean commit and tracked catalog; neither becomes independent authority.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 299-gs2-07-2-narrow-reconciliation`.
