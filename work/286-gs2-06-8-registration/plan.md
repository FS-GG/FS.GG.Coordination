---
schemaVersion: 1
workId: 286-gs2-06-8-registration
title: GS2-06.8 Fleet Dry-Plan Frontier Registration
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/286-gs2-06-8-registration/spec.md
sourceClarifications: work/286-gs2-06-8-registration/clarifications.md
sourceChecklist: work/286-gs2-06-8-registration/checklist.md
publicOrToolFacingImpact: true
---

# GS2-06.8 Fleet Dry-Plan Frontier Registration Plan

Prose status: planned

## Source Snapshot
- spec: work/286-gs2-06-8-registration/spec.md sha256:73d82d2656b2da6a38d9a62c5ed643b778d6b0799935376c0db4b5a95ddd0b3d schemaVersion:1
- clarifications: work/286-gs2-06-8-registration/clarifications.md sha256:9ecbd5ad31292400124d79551273755bcab2468175ea6c28a6f8d98982ff24d4 schemaVersion:1
- checklist: work/286-gs2-06-8-registration/checklist.md sha256:fe8062d7b5c6ef0d8eece3e1af4db78d6c8141af54f47faa9c288d2fc4183391 schemaVersion:1

## Plan Scope
- Work item 286-gs2-06-8-registration is planned from the current specification, clarification, and checklist facts.
- Requirement count: 5.
- Clarification decision count: 1.
- Checklist result count: 5.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Replace only the roadmap revision and digest with the exact accepted `.github` source bytes.
- PD-002 [AC-001] [FR-002] complete: Append one GS2-06.8 record; compute its canonical contract digest after the complete permission and comprehensive exit contracts are frozen; do not modify prior records.
- PD-003 [AC-001] [FR-003] complete: Reuse the accepted GS2-06.1–GS2-06.7 catalog commands in unit order, append one Q5 fleet-dry-plan command, and bind every executable-plus-argument identity so comprehensive closure remains independently executable.
- PD-004 [AC-002] [FR-004] complete: Extend `RoadmapWorkArchitectureTests` with distinct in-memory/temporary mutations for roadmap bytes, index authority, and the selected gate command; each control must be run and observed red.
- PD-005 [AC-003] [FR-005] complete: Limit documentation to the changed registration contract and preserve the absent Q5 validator as a deliberate future implementation boundary.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] roadmapIndex: `eng/github-substrate-v2-units.json` pins new authority and admits the exact GS2-06.8 comprehensive contract; `eng/github-substrate-v2-gates.json` pins its future Q5 command without executing it.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Run exact `roadmap-work inspect` and `prerequisites`; require the accepted GS2-06.7 receipt and `ready: true`; run focused and full repository tests and a warning-free Release build.
- VO-002 [PD-004] [PC-001] mutationTest: Independently alter roadmap content, the index roadmap revision/digest, and the selected gate command; require each named control to turn red for its intended mismatch.
- VO-003 [PD-005] scopeAudit: Verify the diff has no `validate-github-fleet-dry-plans.fsx`, fleet observation, accepted receipt, GS2-07.1, settings application, release, deployment, or production-state mutation.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Existing accepted units and receipts remain byte-identical; GS2-06.8 implementation begins only after this registration is protected-main accepted.

## Generated View Impact
- GV-001 [PD-001] [PD-002] [PD-003] workModel: Refresh the SDD work model, analysis, verification, and ship views from the exact registration sources; tracked index/catalog bytes remain authored contract inputs rather than generated SDD output.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 286-gs2-06-8-registration`.
