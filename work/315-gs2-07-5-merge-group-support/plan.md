---
schemaVersion: 1
workId: 315-gs2-07-5-merge-group-support
title: GS2-07.5 merge-group support
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/315-gs2-07-5-merge-group-support/spec.md
sourceClarifications: work/315-gs2-07-5-merge-group-support/clarifications.md
sourceChecklist: work/315-gs2-07-5-merge-group-support/checklist.md
publicOrToolFacingImpact: true
---

# GS2-07.5 merge-group support Plan

Prose status: planned

## Source Snapshot
- spec: work/315-gs2-07-5-merge-group-support/spec.md sha256:ce856456e0c9bd779607d3c961a5d8000b0972787624ac6ae7ae7e31b1d966ab schemaVersion:1
- clarifications: work/315-gs2-07-5-merge-group-support/clarifications.md sha256:1ab4539924ef2f65c8fe15ce721ffe2e0de9cda420dd65df6bc5efcd2b1aeed6 schemaVersion:1
- checklist: work/315-gs2-07-5-merge-group-support/checklist.md sha256:13d00b3142ba33ba7ad16078795126749b1bf4821de51583f4fc9b2954570c18 schemaVersion:1

## Plan Scope
- Work item 315-gs2-07-5-merge-group-support is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 0.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a pure `GitHubMergeGroupQualification` module. It accepts a canonical checks-requested merge-group event only after exact syntax, equality of configured-expected and freshly observed check inventories, complete sealed aggregate result success, unexpired base observation, and equality between observed and freshly re-observed base repository/ref/SHA/revision, claim, review, head, dependency, release, and settings facts. Public parse/verify repeats the complete semantic validation independently of seal correctness.

## Contract Impact
- PC-001 [PD-001] additiveApi: Add an isolated public qualification-contract module with canonical length-framed serialization, SHA-256 sealing, explicit refusal codes, and no dependency on GitHub IO, network, queue, settings, release, package, mutation, or canonical Quint protocol modules.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Run focused positive/boundary tests, stale and changed base repository/ref/SHA/revision tests, configured-versus-observed inventory and full-result sealing tests, correctly resealed malformed-plan tests, all generated adversarial mutations, independently authored control inventory, registered Q3 command, full unit and architecture suites, warning-free Release build, exact replay, fresh-checkout two-pass SDD verification, and clean-candidate roadmap qualification.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveOnly: No runtime migration, workflow publication, queue operation, network access, settings, release, package, stable-channel, or successor-unit authority is introduced.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh only standard SDD readiness projections. Retained control inventories and merge-group results remain source-owned evidence bound by the roadmap candidate manifest.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 315-gs2-07-5-merge-group-support`.
