---
schemaVersion: 1
workId: 200-staged-intake-admission
title: GS2-05.9 staged intake admission
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/200-staged-intake-admission/spec.md
sourceClarifications: work/200-staged-intake-admission/clarifications.md
sourceChecklist: work/200-staged-intake-admission/checklist.md
publicOrToolFacingImpact: true
---

# GS2-05.9 staged intake admission Plan

Prose status: planned

## Source Snapshot
- spec: work/200-staged-intake-admission/spec.md sha256:532cb8e1e0cf9bd4d11d990cb7ab12dccf3a4e21f6d772b25aa2c2b18bfe4f1b schemaVersion:1
- clarifications: work/200-staged-intake-admission/clarifications.md sha256:15d18f59bd2c0cdc9328d8ca20958d87ca4be61d4f3ebbb151acbcec81649f1c schemaVersion:1
- checklist: work/200-staged-intake-admission/checklist.md sha256:18d58672f4d94bf96593da6a61fa4079125ae7d71029f91af595c13732c665e4 schemaVersion:1

## Plan Scope
- Work item 200-staged-intake-admission is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 5.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [AC-002] [FR-001] complete: Add canonical `DiscoveryDetail`, `StagedCaptureRequest`, authority-read, cost, observation, and plan result types. A staged request carries root-cause, verification, and touch-set discovery states explicitly; known values are canonical non-empty text and unknown/deferred states require non-empty reasons.
- PD-002 [AC-001] [AC-004] [FR-002] complete: Define a closed six-case `CaptureAuthorityRead` union for item-local reads, reject duplicates, missing read classes, negative cardinalities, or more than six entries, and derive mutations from the actual sealed v1 plan. Reject a plan whose effect count exceeds six; do not include unrelated cardinality in the canonical intent or sealed decision.
- PD-003 [AC-001] [AC-004] [FR-003] complete: Keep every capture read and effect item-local by construction. Add architecture/source assertions over the closed union and a gate inversion that injects a global-reconcile case and must red.
- PD-004 [AC-003] [FR-004] complete: Add a closed nine-case `ReadyPromotionSurface` union and fact record. Canonicalize and sort facts, reject duplicates/invalid values, emit one stable `INTAKE-PROMOTION-MISSING-*` diagnostic for each omission, and build a v1 canonical intent containing Ready scheduling plus the declared touch set only after the set is complete. Claim and pull request are intentionally not representable.
- PD-005 [AC-002] [FR-005] complete: Keep all existing public intake types and members byte-for-member compatible. Staged capture returns the unchanged `CanonicalIntakeIntent`/`IntakePlanDecision`; callers apply planned effects only through existing `applyControlled`, so every GS2-05.3 seal, fencing, replay, recovery, compensation, and readback control remains authoritative.
- PD-006 [AC-004] [FR-006] complete: Add GS2-05.9 to `eng/github-substrate-v2-units.json` with GS2-05.3 as prerequisite and GS2-05.4 as dependent, bind `.github@2ff646743e770f0ec6be5566acd04df0b1a83dec` roadmap digest, and register focused staged-cost, phase-boundary, compatibility, and architecture evidence without changing unrelated units.

## Contract Impact
- PC-001 [PD-001] [PD-004] public F# surface: additive staged-capture and Ready-promotion types/functions in `IntakeAdapter.fsi`; existing intake members remain unchanged.
- PC-002 [PD-005] compatibility: staged operations emit `fsgg.coord.intake/v1` canonical intents and delegate application to the existing sealed executor.
- PC-003 [PD-006] roadmap unit index: GS2-05.9 and the GS2-05.4 prerequisite edge are exact structured authority bound to the canonical roadmap revision.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] semanticTest: Unit tests accept known/unknown/deferred/unspecified discovery states, refuse malformed values and incomplete/duplicate authority-read inventories, assert canonical repeatability, and prove capture uses exactly the closed item-local read inventory with at most six actual effects.
- VO-002 [PD-002] [PD-003] gateInversion: Grow unrelated Project/Backlog counts from zero to a large value and require byte-identical staged results and identical cost; independently inject a seventh/global read operation and require a red diagnostic; independently inject a global-reconcile source case and require the architecture gate to red.
- VO-003 [PD-004] semanticTest: Exercise complete Ready promotion and remove each of the nine required facts one at a time, asserting the exact surface-specific diagnostic and the absence of any claim/PR requirement; duplicate and blank facts are separately red.
- VO-004 [PD-005] [PC-002] compatibility: Run all existing GitHub intake tests unchanged, then apply a staged plan through `applyControlled` across execute, drift, partial failure, resume, replay, roll-forward, compensation, and authoritative reread cases.
- VO-005 [PD-006] [PC-003] architectureTest: Parse the exact unit index and canonical `.github` roadmap revision, assert GS2-05.9 occurs once with accepted GS2-05.3 prerequisite and GS2-05.4 depends on it, and invert each edge/digest independently to red.
- VO-006 [PD-001] [PD-004] [PC-001] publicSurface: Build implementation/signature together and assert every new public member is declared while no existing signature member is removed.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Existing callers continue using `validate`, `inspect`, `plan`, and `applyControlled` unchanged; staged callers opt into `validateCapture`, `planCapture`, and `prepareReadyPromotion` and receive v1-compatible values.
- PM-002 [PC-002] failClosed: No implicit conversion upgrades an incomplete discovery to Ready. Promotion is a separate explicit call, and missing facts remain stable diagnostics.
- PM-003 [PC-003] sequenced: GS2-05.4 remains blocked on the accepted GS2-05.9 receipt; landing implementation alone does not satisfy that edge.

## Generated View Impact
- GV-001 [PD-001] workModel: readiness/200-staged-intake-admission/work-model.json refreshes from current lifecycle sources and must be current before ship.
- GV-002 [PD-006] unitIndex: `eng/github-substrate-v2-units.json` is edited as structured authority and its exact roadmap bindings are architecture-tested.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 200-staged-intake-admission`.
