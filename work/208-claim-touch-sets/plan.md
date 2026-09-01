---
schemaVersion: 1
workId: 208-claim-touch-sets
title: GS2-05.5 claims and touch sets
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/208-claim-touch-sets/spec.md
sourceClarifications: work/208-claim-touch-sets/clarifications.md
sourceChecklist: work/208-claim-touch-sets/checklist.md
publicOrToolFacingImpact: true
---

# GS2-05.5 claims and touch sets Plan

Prose status: planned

## Source Snapshot
- spec: work/208-claim-touch-sets/spec.md sha256:1af8bcb47ca888f1ab44f321d9b5cb8dca8a29a6b24c4bf80eafcaa6e18dbc7a schemaVersion:1
- clarifications: work/208-claim-touch-sets/clarifications.md sha256:225d0e27399ae08b505f345b87c5190c4fd170f6b164b1d39966387fb3d0c5db schemaVersion:1
- checklist: work/208-claim-touch-sets/checklist.md sha256:24458f40ca4b66dfc6ead76a48856223fd169940193651740f463f76e7381823 schemaVersion:1

## Plan Scope
- Work item 208-claim-touch-sets is planned from the current specification, clarification, and checklist facts.
- Requirement count: 9.
- Clarification decision count: 6.
- Checklist result count: 9.

## Plan Decisions
- PD-001 [AC-001] [AC-003] [FR-001] complete: Add closed canonical subject/repository/touch types and normalization to `GitHubClaimTouchSetQualification.fsi/.fs`; reject unsafe paths and derive stable claim/conflict identities.
- PD-002 [AC-001] [FR-002] complete: Add acquisition intent, current claim observation, expected-parent proposal, grant, operation identity, and plan seal types; compose validation with `ShardedJournalAdapter.address`, `validate`, `planCas`, and exact reread reconciliation.
- PD-003 [AC-002] [FR-003] complete: Model current owner and lease deadline in authority-bearing claim payloads, expose a pure successor eligibility decision, and require an accepted successor CAS before a rival grant can authorize.
- PD-004 [AC-003] [AC-004] [FR-004] complete: Implement repository-scoped equal/ancestor path conflicts over canonical touches and isolate all native/Project/comment/webhook observations in projection-only hint types that cannot satisfy an authority parameter.
- PD-005 [AC-003] [FR-005] complete: Compose multi-touch planning with `ShardedJournalAdapter.planSaga`; bind a full persisted-plan receipt before effects and reuse `planConflict` for suffix release and reverse append-only compensation.
- PD-006 [AC-004] [FR-006] complete: Add a controlled-fixture `ClaimTouchSetAdapter` that rereads and validates every journal, owner, commit, generation, touch set, and terminal bit immediately before each effect and returns typed refusals.
- PD-007 [AC-001] [AC-003] [AC-004] [FR-007] complete: Seal canonical plans, make exact replay idempotent, retain indeterminate/conflict recovery facts, expose closed read/effect cost formulas, and prove unrelated projection cardinality cannot affect results.
- PD-008 [AC-005] [FR-008] complete: Keep public changes additive, preserve existing journal APIs, and after adapter, qualification, and evidence phases run `canonical-quint-compiler` plus `canonical-quint-pure-model`; any failure returns to planning and the spec remains unchanged.
- PD-009 [AC-005] [FR-009] complete: Advance the pinned roadmap revision, register GS2-05.5 with accepted GS2-05.4 as sole prerequisite, add one exact Q3 gate/catalog row, generated corpus, independent expectations, validator, exact digests, and prerequisite inversion coverage.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] public F# qualification surface: additive canonical claim/touch, eligibility, acquisition, grant, saga, proof, cost, and diagnostic contracts in `GitHubClaimTouchSetQualification.fsi`.
- PC-002 [PD-006] [PD-007] public F# adapter surface: additive controlled-fixture store, plan persistence, apply/recover, and effect-authorization functions in `ClaimTouchSetAdapter.fsi`; no production transport is exposed.
- PC-003 [PD-004] [PD-006] authority split: journal records and current generations decide ownership/effects; projection hints are structurally separate and cannot satisfy authority-bearing inputs.
- PC-004 [PD-008] formal authority: `src/FS.GG.Coordination.Protocol/Protocol.md` is unchanged and its canonical compiler, safety, witness, and mutation suite remain green.
- PC-005 [PD-009] roadmap unit index: GS2-05.5 carries accepted GS2-05.4 as sole prerequisite, one Q3 gate, exact command/contract digests, and no successor authority.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Unit tests cover normalization, path safety, stable identity, sibling proposal exclusion, next-generation binding, active/expired lease eligibility, accepted successor authority, exact replay, and every typed refusal.
- VO-002 [PD-004] [PD-005] [PC-001] concurrency: Generated and independent tests cover equal/ancestor overlap, repository partitioning, deterministic total order, duplicate domain refusal, full-plan persistence, missing persistence, acquired-prefix validation, suffix release, and reverse retained-result compensation.
- VO-003 [PD-006] [PD-007] [PC-002] recovery: Controlled-fixture tests cover exact authoritative reread, wrong owner/touch/commit/generation, terminal/incomplete/divergent state, projection inversions, interruption, parent conflict, unknown response, replay, and bounded-cost invariance.
- VO-004 [PD-008] [PC-004] formal: After every substantive phase run the canonical Quint compiler and pure-model commands; all safety invariants remain unviolated, all expected reachability/mutation witnesses remain reachable/red, and the spec bytes remain unchanged.
- VO-005 [PD-008] [PC-001] [PC-002] architectureTest: Build signatures and implementations together, preserve all existing public members, source-scan the new adapter/validator for production transport and credential vocabulary, and prove journal primitives are composed rather than duplicated.
- VO-006 [PD-009] [PC-005] qualification: The offline Q3 validator agrees with independently authored expectations, rejects every generated mutation, binds accepted GS2-05.4 and exact roadmap sequencing, and retains its contract/catalog digests.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Existing protocol, journal, intake, and roadmap-intake surfaces remain unchanged; claim/touch-set behavior is a new explicit API and no caller is implicitly migrated.
- PM-002 [PC-003] authorityFirst: Production adoption is deferred to later cutover units; GS2-05.5 proves target authority semantics but cannot write live GitHub state.
- PM-003 [PC-005] sequenced: Accepted GS2-05.4 is required before GS2-05.5 qualification, and GS2-05.6 remains unavailable until a later accepted GS2-05.5 receipt is admitted.

## Generated View Impact
- GV-001 [PD-001] workModel: Regenerate `readiness/208-claim-touch-sets/work-model.json` from the finalized nine requirements, six decisions, five contract impacts, six verification obligations, and dependency-ordered task graph before verification; any source digest drift blocks ship.
- GV-002 [PD-009] unitIndex: `eng/github-substrate-v2-units.json` and `eng/github-substrate-v2-gates.json` are structured authority whose exact digests are architecture-tested.
- GV-003 [PD-009] qualificationEvidence: generated corpus and independently authored expectations are retained separately under `evidence/github-substrate-v2/gs2-05-5/`.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Implementation Status
- Registration and prerequisite proof: DONE — GS2-05.5 resolves against roadmap `b776da76`, exact Q3 command digest, and accepted GS2-05.4 receipt only.
- Claim/touch-set qualification and controlled-fixture adapter: DONE — warning-free Release build, 7 focused unit tests, 12 focused architecture tests, and 18 generated plus independent Q3 controls pass.
- Formal source delta: NONE — `Protocol.md` remains `7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218`.
- Formal post-implementation checkpoint: DONE — the pinned canonical compiler, all eleven TLC-backed witnesses, and all 126 mutation controls passed against the unchanged protocol source; retained summary: `artifacts/canonical-quint/gs2-05-5-post-implementation.json`.
- Current checkpoint: run the complete repository build, unit, architecture, Q3, SDD, and roadmap-work delivery gates over the final candidate.
- Rollback criterion: any canonical Quint identity, invariant, reachability witness, or mutation-control failure returns this work to planning; the spec will not be changed to fit the implementation.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 208-claim-touch-sets`.
