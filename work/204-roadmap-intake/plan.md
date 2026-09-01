---
schemaVersion: 1
workId: 204-roadmap-intake
title: GS2-05.4 roadmap intake
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/204-roadmap-intake/spec.md
sourceClarifications: work/204-roadmap-intake/clarifications.md
sourceChecklist: work/204-roadmap-intake/checklist.md
publicOrToolFacingImpact: true
---

# GS2-05.4 roadmap intake Plan

Prose status: planned

## Source Snapshot
- spec: work/204-roadmap-intake/spec.md sha256:ef8ef8cb8c07fbaa55666e8818d9e96e635007398e1087212ddd9bde6db091c6 schemaVersion:1
- clarifications: work/204-roadmap-intake/clarifications.md sha256:7d922b700d8c70ac409e8465c47b1c23647b15de7a822e76f8011099723f19df schemaVersion:1
- checklist: work/204-roadmap-intake/checklist.md sha256:8dd8985fe9dd4eb8a5c8230b07214d812e542ea86f8122d4ec3ddb5bb041dc80 schemaVersion:1

## Plan Scope
- Work item 204-roadmap-intake is planned from the current specification, clarification, and checklist facts.
- Requirement count: 9.
- Clarification decision count: 7.
- Checklist result count: 9.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add closed typed roadmap/node/type/date/field definitions to `GitHubRoadmapIntakeQualification.fsi`; accept one root Epic and bounded native work nodes, never Markdown or Project rows.
- PD-002 [AC-001] [FR-002] complete: Implement pure graph validation, stable source-key create-or-reuse matching, canonical sort order, effect generation, closed cost formulas, and a SHA-256 plan seal in the qualification-contract assembly.
- PD-003 [AC-002] [FR-003] complete: Accumulate stable diagnostics for every identity, graph, type, field, date, pagination, observation, and plan-integrity refusal before returning any applicable effect.
- PD-004 [AC-003] [FR-004] complete: Add an in-memory `RoadmapIntakeAdapter` fixture whose exact-plan apply records per-effect receipts and supports interruption, replay, resume, roll-forward, reverse compensation, authorization/unsupported refusal, indeterminate outcomes, and authoritative reread.
- PD-005 [AC-003] [AC-004] [FR-005] complete: Make declared reads and maximum effects a closed formula over owned nodes/edges/projections, carry unrelated cardinality only as ignored observation metadata, and assert byte/cost invariance across growth fixtures.
- PD-006 [AC-004] [FR-006] complete: Implement pure inspection over ownership-bound targets and emit stable missing/extra/mismatch findings for issues, relationships, dates, fields, and membership while excluding unrelated targets.
- PD-007 [AC-004] [AC-005] [FR-007] complete: Keep native hierarchy/dependency facts in authority-bearing types and Project/body facts in separate projection-only types; prove Project/body inversions cannot alter graph or readiness outputs.
- PD-008 [AC-005] [FR-008] complete: Keep the public additions offline and additive, enforce implementation/signature order in project files, and use architecture source scans to reject transport, credential, global reconciliation, full-Project, and unrelated-Backlog vocabulary.
- PD-009 [AC-005] [FR-009] complete: Replace the GS2-05.4 stub in `eng/github-substrate-v2-units.json`, add one Q3 command/catalog row, generated corpus and independent expectations, exact digests, prerequisite inversion coverage, and no successor-unit authority.

## Contract Impact
- PC-001 [PD-001] [PD-002] public F# qualification surface: additive roadmap definition, observation, plan, effect, cost, diagnostic, drift, and result contracts in `GitHubRoadmapIntakeQualification.fsi`.
- PC-002 [PD-004] public F# adapter surface: additive controlled-fixture store and exact-plan application functions in `RoadmapIntakeAdapter.fsi`; no production transport is exposed.
- PC-003 [PD-007] authority split: native issue/hierarchy/dependency types decide semantic identity and graph meaning; Project/body types are explicitly projection-only and cannot satisfy execution facts.
- PC-004 [PD-009] roadmap unit index: GS2-05.4 carries accepted GS2-05.9 as sole prerequisite, one Q3 gate, exact command/contract digests, and unchanged successor boundaries.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Unit tests cover canonical valid plans and each duplicate, ambiguous, dangling, cyclic, unsupported, date, pagination, stale-observation, collision, and altered-plan refusal with accumulated stable diagnostics.
- VO-002 [PD-004] [PC-002] recovery: Controlled-fixture tests cover create/reuse/update/link/unlink idempotency, interruption, replay, resume, roll-forward, reverse compensation, unauthorized, unsupported, partial, indeterminate, and authoritative-readback outcomes.
- VO-003 [PD-005] [PD-006] [PC-001] boundedDrift: Grow unrelated Project/Backlog observations from zero through large cardinalities and require identical plan bytes, costs, and owned-drift output; inject missing, extra, and mismatch cases for every owned projection kind.
- VO-004 [PD-007] [PC-003] gateInversion: Independently invert Project status, Project fields, copied blocker text, and native hierarchy/dependency facts; projection inversions leave graph/readiness unchanged while native-fact inversions go red.
- VO-005 [PD-008] [PC-001] [PC-002] architectureTest: Build signatures and implementations together, preserve all existing public members, and source-scan the new adapter/validator for production transport, credential, global reconcile, full-Project, and unrelated-Backlog vocabulary.
- VO-006 [PD-009] [PC-004] qualification: The offline Q3 validator must agree with independently authored expectations, reject every generated mutation, bind accepted GS2-05.9 and exact roadmap sequencing, and retain its contract/catalog digests.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Existing intake and RoadmapWork public surfaces remain unchanged; roadmap intake is a new explicit API and no existing caller is implicitly migrated.
- PM-002 [PC-003] authorityFirst: Production adoption is deferred until later cutover units; GS2-05.4 only proves the target native-authority semantics and cannot write live GitHub state.
- PM-003 [PC-004] sequenced: Accepted GS2-05.9 is required before GS2-05.4 qualification, and GS2-05.5 remains unavailable until a later accepted GS2-05.4 receipt is admitted.

## Generated View Impact
- GV-001 [PD-001] workModel: readiness/204-roadmap-intake/work-model.json refreshes from current lifecycle sources and must be current before ship.
- GV-002 [PD-009] unitIndex: `eng/github-substrate-v2-units.json` and `eng/github-substrate-v2-gates.json` are structured authority whose exact digests are architecture-tested.
- GV-003 [PD-009] qualificationEvidence: generated corpus and independently authored expectations are retained separately under `evidence/github-substrate-v2/gs2-05-4/`.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 204-roadmap-intake`.
