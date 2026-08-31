---
schemaVersion: 1
workId: gs2-04-4-project-adapter
title: GS2-04.4 typed GitHub Project adapter
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-4-project-adapter/spec.md
sourceClarifications: work/gs2-04-4-project-adapter/clarifications.md
sourceChecklist: work/gs2-04-4-project-adapter/checklist.md
publicOrToolFacingImpact: true
---

# GS2-04.4 typed GitHub Project adapter Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-4-project-adapter/spec.md sha256:e4ae5992e996f298b71b39397865def188ff132e3e60a6ae7f7122e185993e37 schemaVersion:1
- clarifications: work/gs2-04-4-project-adapter/clarifications.md sha256:dbc43de7841989dca4cb9ca31bbc4960d2124e26a4fef88105dc6ce84277b38a schemaVersion:1
- checklist: work/gs2-04-4-project-adapter/checklist.md sha256:4580b4226cfb5016a2cb7ca9634c37fbb11a24e0b06daad09584a46388de013f schemaVersion:1

## Plan Scope
- Add a pure Project adapter to `FS.GG.Coordination.GitHub`; this unit does not perform HTTP or hosted writes.
- Add an independent, closed qualification contract to `FS.GG.Coordination.Qualification.Contracts`.
- Prove the contract with focused unit tests, architecture tests, a committed synthetic fixture, and the registered offline validator.
- Record exact SDD lifecycle evidence for the implementation and its focused gates.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] [DEC-002] [DEC-003] [DEC-004] complete: Define `ProjectAdapter.fsi` before its implementation with typed Project/content identities, archive state, complete paginated membership snapshots, and explicit read failures. Normalize successful observations into deterministic Project-item order while refusing invalid identities, duplicates, incomplete page chains, and unproven terminal observations.
- PD-002 [AC-002] [FR-002] [DEC-005] complete: Represent Status as a typed field projection carrying Project, item, field, option-set, selected-option, and revision observations. The API exposes no claim/review/operation/completion authorization type, so a Status projection cannot authorize a concurrency-sensitive transition.
- PD-003 [AC-003] [FR-003] [DEC-001] [DEC-002] [DEC-003] [DEC-004] [DEC-005] complete: Preserve typed archived, duplicate, external-repository, draft, redacted/unknown, missing, unsupported, unauthorized, incomplete, unreadable, invalid-field, and indeterminate outcomes. None may collapse to absence, uniqueness, or success.
- PD-004 [AC-004] [FR-004] [DEC-003] [DEC-005] complete: Produce pure membership/Status projection proposals carrying the exact before snapshot, stable item/field/option identities, expected revision, causation, and a collision-safe length-framed SHA-256 idempotency key. Already-satisfied requests produce typed no-op proposals.
- PD-005 [AC-005] [FR-005] [DEC-003] [DEC-005] complete: Compare every proposal guard with a mandatory stale re-read before execution. Any membership, item identity, archive state, field identity, option set, selected option, or revision mismatch yields a typed re-read/concurrent-state refusal and no effect authority.
- PD-006 [AC-006] [FR-006] [DEC-006] complete: Verify post-state by applying the proposal to the exact before projection, requiring an advanced resulting revision and exact snapshot equality. Refuse revision reuse, a missing intended delta, changed identities, and every unrelated item/field change.
- PD-007 [AC-007] [FR-007] complete: Define a closed qualification inventory for pagination, archived item, duplicate item, external item, draft item, missing item, unreadable observation, stale revision, concurrent change, and no-op mutation. Generated and independent producers must enumerate that exact inventory; every mutation must fail while both baselines pass.
- PD-008 [AC-008] [FR-008] [DEC-007] complete: Implement `eng/validate-github-project-adapter.fsx` as the literal registered offline command loading public production signatures and the independent qualification contract. Use a committed canonical synthetic fixture and refuse HTTP, credentials, live writes, Q4 claims, or dependency inversion.

## Contract Impact
- PC-001 [PD-001] [PD-002] publicSurface: `src/FS.GG.Coordination.GitHub/ProjectAdapter.fsi` is the signature-first public adapter contract.
- PC-002 [PD-007] qualificationContract: `src/FS.GG.Coordination.Qualification.Contracts/GitHubProjectAdapterQualification.fsi` is an independent public qualification contract and does not reference the production project.
- PC-003 [PD-008] gateCommand: `eng/validate-github-project-adapter.fsx` is the exact offline validator registered by the roadmap gate catalog.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PC-001] semanticTest: Run focused unit tests for complete membership/Status reads, every typed refusal, deterministic proposals and idempotency, no-op behavior, stale re-read refusal, projection-only authority, and exact post-state verification.
- VO-002 [PD-007] [PC-002] mutationControl: Prove generated and independent producers emit the exact closed inventory, both baselines pass, and every named mutation is rejected.
- VO-003 [PD-008] [PC-003] registeredGate: Run the literal registered gate, focused solution tests, architecture checks, SDD analyze/verify, and hosted exact-head qualification before acceptance.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Add the adapter after the existing transport, issue-field, and native-relation contracts. No persisted-data migration and no remote mutation are part of GS2-04.4.

## Generated View Impact
- GV-001 [PD-007] generatedControls: The fixture and generated producer must match the independent producer's exact closed inventory while retaining separate implementations.
- GV-002 [PD-008] lifecycleViews: SDD readiness and evidence views regenerate from current sources; stale or missing generated views cannot satisfy verification.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- MVU is not applicable because this unit is a pure adapter and planning algebra; stateful hosted execution belongs to later reconciliation work.
- The committed fixture is deliberately synthetic and credential-free. Live destructive Q4 proof is deferred by the roadmap to GS2-04.9, not claimed here.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-4-project-adapter`.
