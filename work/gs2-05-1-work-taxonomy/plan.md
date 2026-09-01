---
schemaVersion: 1
workId: gs2-05-1-work-taxonomy
title: Gs2 05 1 Work Taxonomy
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-05-1-work-taxonomy/spec.md
sourceClarifications: work/gs2-05-1-work-taxonomy/clarifications.md
sourceChecklist: work/gs2-05-1-work-taxonomy/checklist.md
publicOrToolFacingImpact: true
---

# Gs2 05 1 Work Taxonomy Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-05-1-work-taxonomy/spec.md sha256:027b4574aad85b51732ec5857beb2ee0367d1b6a476eb7d19e0f1352a0cf9400 schemaVersion:1
- clarifications: work/gs2-05-1-work-taxonomy/clarifications.md sha256:937522520daca933608925d475a7b15ac69ee8584546dd1ea6c20a5524679e62 schemaVersion:1
- checklist: work/gs2-05-1-work-taxonomy/checklist.md sha256:df65ad6c18aaf87d7b4929e2c138257283ebceb23bc9d3deb1eb7abea9063e67 schemaVersion:1

## Plan Scope
- Add the public pure taxonomy and migration planner in `src/FS.GG.Coordination.Core/WorkTaxonomy.fsi` and `.fs`, with no GitHub or IO dependency.
- Add a canonical frozen corpus and expected classifications under `evidence/github-substrate-v2/gs2-05-1/`, including source and self digests.
- Add a plain Quint model plus separate test module under the same evidence directory; it models a single deterministic planner, not distributed actors.
- Add `eng/validate-github-work-taxonomy.fsx` as the registered Q2 gate and focused unit/architecture coverage for API behavior and boundary purity.
- Complete immutable qualification evidence and SDD readiness artifacts only after all implementation and inversions pass.

## Plan Decisions
- PD-001 [AC-001] [AC-002] [FR-001] complete: Use closed F# unions for `NativeIssueType` and `LifecycleApplicability`; expose canonical wire names from one implementation so validators never maintain a second vocabulary.
- PD-002 [AC-003] [FR-002] complete: Represent the corpus as canonical JSON rows spanning the supported legacy signals and already-native no-op cases. Each row carries stable id, source completeness/revision facts, repository scope, hierarchy facts, and expected result; canonical bytes bind a checked-in SHA-256 manifest.
- PD-003 [AC-001] [AC-004] [FR-003] complete: Split `classify` from `plan`. Classification returns one target or ordered diagnostics; planning first rejects duplicate identities or any refused row, then returns dispositions ordered by ordinal stable identity. Never emit a partial plan.
- PD-004 [AC-001] [AC-005] [FR-004] complete: Define a closed diagnostic union for missing identity, unknown native/Class/Kind, contradictory, ambiguous, unsupported, lossy hierarchy/scope, duplicate, incomplete, stale, and unreadable observations. Independent fixtures assert exact codes and absence of dispositions.
- PD-005 [AC-003] [AC-004] [AC-005] [FR-005] complete: Use separate independent expectation data and validator logic to prove corpus totality and omission detection, plus shuffle/idempotency/no-op/byte-stability tests. Mutation copies are temporary and never replace checked-in evidence.
- PD-006 [AC-006] [FR-006] complete: Model `{ input, outcome, planned }` as one cohesive state in plain Quint. Keep `canPlan`/`applyPlan` and `canRefuse`/`applyRefusal` pure, wire separate guarded actions, and add planned/refused witnesses plus sole-authority, preservation, uniqueness, and deterministic-outcome invariants.
- PD-007 [AC-006] [FR-007] complete: The registered FSI command orchestrates source/corpus/digest/Quint/unit/architecture assertions without network access. Repository build and SDD gates remain separate exact-head obligations recorded in evidence.

## Contract Impact
- PC-001 [PD-001] public API: `FS.GG.Coordination.Core.WorkTaxonomy` introduces only repository-local pure types and functions; no existing public member changes.
- PC-002 [PD-002] evidence schema: `fsgg.github-substrate-v2.work-taxonomy-corpus/1`, `...expectations/1`, and `...qualification/1` are canonical JSON with ordinal keys and SHA-256 bindings.
- PC-003 [PD-007] command: `dotnet fsi eng/validate-github-work-taxonomy.fsx -- .` is the sole registered GS2-05.1 Q2 entry point and performs zero network writes.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Unit tests cover every accepted mapping, lifecycle applicability, exact dispositions, all refusal families, ordering, idempotency, and no-op behavior.
- VO-002 [PD-002] [PD-005] [PC-002] independentOracle: The independent expectations and omission inversion prove the implementation cannot certify a self-described or incomplete corpus.
- VO-003 [PD-006] boundedModel: `quint typecheck` and sampled `quint run` reach non-zero planned and refused witnesses while every declared invariant holds.
- VO-004 [PD-007] [PC-003] architectureTest: Architecture tests prove the taxonomy assembly remains pure and the registered script contains no GitHub/network mutation surface.
- VO-005 [PD-007] exactCandidate: Run the registered Q2 command, focused tests, warning-free build, inversions, SDD analyze/verify/ship, and protected CI on one exact PR head.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: GS2-05.1 produces migration dispositions and evidence only. No disposition is applied to GitHub; live conversion belongs to a later registered cutover unit.

## Generated View Impact
- GV-001 [PD-001] workModel: `readiness/gs2-05-1-work-taxonomy/work-model.json` refreshes from current SDD sources or reports stale generated state.
- GV-002 [PD-002] qualificationEvidence: `evidence/github-substrate-v2/gs2-05-1/qualification.json` is generated only from canonical checked-in inputs and records exact artifact digests and passed obligation ids.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-05-1-work-taxonomy`.
