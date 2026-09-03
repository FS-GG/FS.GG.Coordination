---
schemaVersion: 1
workId: 260-gs2-06-7-registration
title: GS2-06.7 Workflow-Selection Frontier Registration
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/260-gs2-06-7-registration/spec.md
sourceClarifications: work/260-gs2-06-7-registration/clarifications.md
sourceChecklist: work/260-gs2-06-7-registration/checklist.md
publicOrToolFacingImpact: true
---

# GS2-06.7 Workflow-Selection Frontier Registration Plan

Prose status: planned

## Source Snapshot
- spec: work/260-gs2-06-7-registration/spec.md sha256:9ca04fdd24c4332a26d5782d0d482c391f2897dd93657c7c7cc15ba74705f0ce schemaVersion:1
- clarifications: work/260-gs2-06-7-registration/clarifications.md sha256:ef72427dc4e8901a17d97467d4c69ce15cee2118633354b1f44a978d1bce8e78 schemaVersion:1
- checklist: work/260-gs2-06-7-registration/checklist.md sha256:958b067eefb13c76e90ef18e6d542d3d793509314050c5f3bed5d929102e5e12 schemaVersion:1

## Plan Scope
- Work item 260-gs2-06-7-registration is planned from the current specification, clarification, and checklist facts.
- Requirement count: 5.
- Clarification decision count: 1.
- Checklist result count: 5.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Replace only the roadmap revision and digest with the exact accepted `.github` source bytes.
- PD-002 [AC-001] [FR-002] complete: Append one GS2-06.7 record; compute its canonical contract digest after the complete permission and exit contracts are frozen; do not modify prior records.
- PD-003 [AC-001] [FR-003] complete: Append distinct catalog commands and bind their executable-plus-argument identities in the unit gate contracts, ordered Q3 then Q7, so semantic selection and CI/supply-chain evidence remain independently executable.
- PD-004 [AC-002] [FR-004] complete: Extend `RoadmapWorkArchitectureTests` with distinct in-memory/temporary mutations for roadmap bytes, index authority, and the selected gate command; each control must be run and observed red.
- PD-005 [AC-003] [FR-005] complete: Limit documentation to the changed registration contract and preserve the absent validator as a deliberate future implementation boundary.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] roadmapIndex: `eng/github-substrate-v2-units.json` pins new authority and admits the exact GS2-06.7 contract; `eng/github-substrate-v2-gates.json` pins its two future commands without executing them.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Run exact `roadmap-work inspect` and `prerequisites`; require the accepted GS2-06.6 receipt and `ready: true`; run focused and full repository tests and a warning-free Release build.
- VO-002 [PD-004] [PC-001] mutationTest: Independently alter roadmap content, the index roadmap revision/digest, and the selected gate command; require each named control to turn red for its intended mismatch.
- VO-003 [PD-005] scopeAudit: Verify the diff has no `validate-github-workflow-selection.fsx`, workflow, accepted-receipt, GS2-06.8, release, deployment, or production-state mutation.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Existing accepted units and receipts remain byte-identical; GS2-06.7 implementation begins only after this registration is protected-main accepted.

## Generated View Impact
- GV-001 [PD-001] [PD-002] [PD-003] workModel: Refresh the SDD work model, analysis, verification, and ship views from the exact registration sources; tracked index/catalog bytes remain authored contract inputs rather than generated SDD output.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 260-gs2-06-7-registration`.
