---
schemaVersion: 1
workId: gs2-04-2-issue-field-adapter
title: GS2-04.2 typed GitHub issue and field adapter
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-2-issue-field-adapter/spec.md
sourceClarifications: work/gs2-04-2-issue-field-adapter/clarifications.md
sourceChecklist: work/gs2-04-2-issue-field-adapter/checklist.md
publicOrToolFacingImpact: true
---

# GS2-04.2 typed GitHub issue and field adapter Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-2-issue-field-adapter/spec.md sha256:2d9a5bc24596eb449d821877d8706c38addaba4385cec17ded79354fecbf8ffd schemaVersion:1
- clarifications: work/gs2-04-2-issue-field-adapter/clarifications.md sha256:bba7cf1af1fd0dfdaece8ca45d5bc2542adcdae74b12f10cb2b0cc3acab1ef98 schemaVersion:1
- checklist: work/gs2-04-2-issue-field-adapter/checklist.md sha256:92982a3edabe6a8b9a2d12719dbb773c0389948f3644214ffb7c86b458522b91 schemaVersion:1

## Plan Scope
- Add a repository-local pure issue-schema adapter in `FS.GG.Coordination.GitHub`; it consumes typed complete/incomplete observations, resolves semantic repository/issue/type/field/option identities, validates closed field declarations, reads values, and derives guarded plans without executing HTTP.
- Add a qualification contract in `FS.GG.Coordination.Qualification.Contracts` that owns the closed GS2-04.2 Q3 mutation/control inventory independently of the adapter implementation.
- Add focused unit tests, independent architecture inversions, canonical offline fixtures, the registered `eng/validate-github-issue-field.fsx` runner, and exact lifecycle evidence.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] complete: Declare `IssueFields.fsi` before `IssueFields.fs`; represent semantic and live identities with closed F# unions/records, validate names and IDs at the public boundary, and resolve only one kind-and-name match from a terminal complete observation.
- PD-002 [AC-002] [FR-002] [DEC-002] complete: Treat each expected field as a typed declaration and validate live field kind plus an exact closed option-name set; duplicate names, duplicate live IDs, missing, extra, or renamed options are distinct drift failures.
- PD-003 [AC-003] [FR-003] [DEC-001] [DEC-003] complete: Carry `Complete`, `Incomplete`, `Unsupported`, `Unauthorized`, and `Indeterminate` as observation outcomes; resolution and planning preserve these meanings and never translate an unreadable or partial observation into absence.
- PD-004 [AC-004] [FR-004] [DEC-003] complete: A complete observation carries a non-empty revision, page count, node count, and terminal-page proof; value collection validates those facts and refuses duplicate targets rather than returning partial maps.
- PD-005 [AC-005] [FR-005] [DEC-003] complete: Derive create, update, and clear operations as pure typed plans over complete current state, expected revision, and causation identity; compute a stable SHA-256 idempotency identity from the canonical operation payload.
- PD-006 [AC-006] [FR-006] [DEC-004] complete: Equality and already-absent clears return a typed `NoOp` carrying revision and idempotency identity with no operation; invalid, stale, incomplete, or ambiguous input returns a refusal.
- PD-007 [AC-007] [FR-007] complete: Define a closed qualification inventory for pagination, duplicate identity, type drift, option drift, stale revision, incomplete observation, and no-op mutation; independently authored architecture controls must match the inventory and prove every mutation red with its baseline green.
- PD-008 [AC-008] [FR-008] [DEC-005] complete: Make the registered validator a literal offline FSI command over the compiled public surface and committed fixture; reject credential lookup, GitHub endpoints, HTTP clients, live-write verbs, Q4 claims, and production-to-qualification dependency inversion.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] publicSurface: `src/FS.GG.Coordination.GitHub/IssueFields.fsi` is the Tier-1 typed adapter contract and its implementation remains signature-constrained.
- PC-002 [PD-007] qualificationContract: `src/FS.GG.Coordination.Qualification.Contracts/GitHubIssueFieldQualification.fsi` declares the closed control/result model consumed by tests and the registered command.
- PC-003 [PD-008] gateCommand: `eng/validate-github-issue-field.fsx` preserves the registered literal identity and emits deterministic diagnostics only from repository-local inputs.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PC-001] semanticTest: Unit tests exercise unique resolution, exact option validation, complete values, guarded plans, no-op decisions, and every fail-closed observation/revision branch.
- VO-002 [PD-007] [PC-002] mutationControl: Generated and independently authored inventories agree exactly, every named mutation turns red, and no self-attested or missing control satisfies Q3.
- VO-003 [PD-008] [PC-003] registeredGate: The literal registered command, full solution, architecture suite, SDD analyze/verify/ship, Bootstrap qualification, and exact-head hosted checks pass without network writes.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Add the issue-schema adapter after the accepted transport module with no replacement of transport behavior and no stored or remote data migration.

## Generated View Impact
- GV-001 [PD-007] generatedControls: The deterministic fixture and generated control producer remain identifier-exact with the independent architecture inventory while their implementations stay separate.
- GV-002 [PD-008] lifecycleViews: `readiness/gs2-04-2-issue-field-adapter/*` and `evidence/github-substrate-v2/gs2-04-2/*` regenerate from exact source and command receipts; stale or missing views do not satisfy acceptance.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- This unit is pure adapter and plan algebra, so Elmish/MVU is not applicable; later hosted reconciliation owns the stateful workflow boundary.
- Deterministic committed fixtures are synthetic evidence by design for Q3 and will be named as such in tests and the PR; GS2-04.9 owns real destructive sandbox correspondence.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-2-issue-field-adapter`.
