---
schemaVersion: 1
workId: gs2-04-3-native-relation-adapter
title: GS2-04.3 typed GitHub native relation adapter
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-3-native-relation-adapter/spec.md
sourceClarifications: work/gs2-04-3-native-relation-adapter/clarifications.md
sourceChecklist: work/gs2-04-3-native-relation-adapter/checklist.md
publicOrToolFacingImpact: true
---

# GS2-04.3 typed GitHub native relation adapter Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-3-native-relation-adapter/spec.md sha256:54f54cd4aa6ab7ee1905b7c5751cd38a5ca9a99125f96d23d4321e1e6a4173b1 schemaVersion:1
- clarifications: work/gs2-04-3-native-relation-adapter/clarifications.md sha256:554abba525ffff06e9ca9684d9ecce9cb0ec818f61a4e91e1cafb3b1778f48d6 schemaVersion:1
- checklist: work/gs2-04-3-native-relation-adapter/checklist.md sha256:51b7839cb145864904d43f0c54bbcd107c2d44452c92fa32c0ee1960d3a6c256 schemaVersion:1

## Plan Scope
- Add a pure native-relation adapter to `FS.GG.Coordination.GitHub`; this unit does not perform HTTP or hosted writes.
- Add an independent, closed qualification contract to `FS.GG.Coordination.Qualification.Contracts`.
- Prove the contract with focused unit tests, architecture tests, a committed synthetic fixture, and the registered offline validator.
- Record exact native-SDD lifecycle evidence for the implementation and its focused gates.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] [DEC-002] [DEC-003] complete: Define `NativeRelations.fsi` before its implementation with typed relation kinds, canonical directed edges, complete snapshots, and read failures. Normalize successful reads into a deterministic sorted edge set; refuse malformed endpoints, self edges, duplicates, incomplete page chains, and unproven terminal observations.
- PD-002 [AC-002] [FR-002] [DEC-001] complete: Represent add/remove intent for one exact typed directed edge so planning cannot change unrelated edges, reverse endpoints, or interchange relation kinds.
- PD-003 [AC-003] [FR-003] [DEC-002] [DEC-003] complete: Preserve typed observation outcomes. Duplicate edges, reversed expected endpoints, invalid/self edges, incomplete observations, unsupported reads, authorization failures, and indeterminate reads remain distinguishable refusals.
- PD-004 [AC-004] [FR-004] [DEC-002] complete: Produce pure add/remove plans carrying the exact before snapshot, expected revision, causation, and a collision-safe length-framed SHA-256 idempotency key. An add of an existing edge and removal of an absent edge produce typed no-op plans.
- PD-005 [AC-005] [FR-005] [DEC-004] complete: Compare the plan's exact before snapshot and revision with a mandatory stale re-read before execution. A revision or edge-set mismatch yields a typed re-read/concurrent-state refusal and no effect.
- PD-006 [AC-006] [FR-006] [DEC-005] complete: Verify post-state by applying the planned operation to the exact before set, requiring the expected resulting revision and exact edge-set equality. Refuse revision mismatch, missing intended delta, unexpected related changes, and unrelated-edge changes.
- PD-007 [AC-007] [FR-007] complete: Define a closed qualification inventory for pagination, duplicate edge, reversed endpoint, relation kind, stale revision, incomplete observation, concurrent change, and no-op mutation. Generated and independent producers must enumerate that exact inventory; every mutation must fail while both baselines pass.
- PD-008 [AC-008] [FR-008] [DEC-006] complete: Implement `eng/validate-github-native-relation.fsx` as the literal registered offline command loading public production signatures and the independent qualification contract. Use a committed, canonical synthetic fixture and refuse HTTP, credentials, live writes, Q4 claims, or dependency inversion.

## Contract Impact
- PC-001 [PD-001] publicSurface: `src/FS.GG.Coordination.GitHub/NativeRelations.fsi` is the signature-first public adapter contract.
- PC-002 [PD-007] qualificationContract: `src/FS.GG.Coordination.Qualification.Contracts/GitHubNativeRelationQualification.fsi` is an independent public qualification contract and does not reference the production project.
- PC-003 [PD-008] gateCommand: `eng/validate-github-native-relation.fsx` is the exact offline validator registered by the roadmap gate catalog.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PC-001] semanticTest: Run focused unit tests for complete reads, every typed read refusal, edge-local planning, deterministic idempotency, no-op behavior, stale re-read refusal, and exact post-state verification.
- VO-002 [PD-007] [PC-002] mutationControl: Prove generated and independent producers emit the exact closed inventory, both baselines pass, and every named mutation is rejected.
- VO-003 [PD-008] [PC-003] registeredGate: Run the literal registered gate, focused solution tests, architecture checks, native-SDD analyze/verify, and hosted exact-head qualification before acceptance.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Add the adapter after the existing transport and issue-field contracts. No persisted-data migration and no remote mutation are part of GS2-04.3.

## Generated View Impact
- GV-001 [PD-007] generatedControls: The fixture and generated producer must match the independent producer's exact closed inventory while retaining separate implementations.
- GV-002 [PD-008] lifecycleViews: Native-SDD readiness and evidence views regenerate from current sources; stale or missing generated views cannot satisfy verification.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- MVU is not applicable because this unit is a pure adapter and planning algebra; stateful hosted execution belongs to later reconciliation work.
- The committed fixture is deliberately synthetic and credential-free. Live destructive Q4 proof is deferred by the roadmap to GS2-04.9, not claimed here.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-3-native-relation-adapter`.
