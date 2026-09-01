---
schemaVersion: 1
workId: 212-review-delivery
title: Review Delivery
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/212-review-delivery/spec.md
sourceClarifications: work/212-review-delivery/clarifications.md
sourceChecklist: work/212-review-delivery/checklist.md
publicOrToolFacingImpact: true
---

# Review Delivery Plan

Prose status: planned

## Source Snapshot
- spec: work/212-review-delivery/spec.md sha256:b4ef6149cc6054c9263fbad5cf2779312aa0ec7f4528a88339ff0b7d40e1129a schemaVersion:1
- clarifications: work/212-review-delivery/clarifications.md sha256:8c33ee37b0aeb5ef28e81e56980203d5c5cb3c8ee9b99cc9fc9589159fd0af6c schemaVersion:1
- checklist: work/212-review-delivery/checklist.md sha256:8da358771f06b201f4a8f88bfaef59af1baa73e163c72bdb611cc4baf9587daa schemaVersion:1

## Plan Scope
- Work item 212-review-delivery is planned from the current specification, clarification, and checklist facts.
- Requirement count: 9.
- Clarification decision count: 6.
- Checklist result count: 9.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add canonical complete-snapshot bytes and digest, stable subject-derived chain identity, and chain-plus-snapshot epoch identity; reject incomplete or noncanonical snapshots before any journal plan exists.
- PD-002 [AC-002] [FR-002] complete: Model accountable authority separately from phase seats, derive fresh deterministic seats from epoch and monotonic ordinal, and permit succession only when chain and epoch remain unchanged.
- PD-003 [AC-001] [AC-002] [FR-003] complete: Append review epoch records to the Review journal through expected-parent CAS and issue a fenced grant only after exact authoritative reread; authorization revalidates chain, snapshot, epoch, seat, pass, commit, generation, and nonterminal state.
- PD-004 [AC-003] [FR-004] complete: Introduce a closed delivery-state union for not merged, exact merged commit, and successful protected-main verification whose run commit must equal the merge commit.
- PD-005 [AC-003] [FR-005] complete: Canonicalize delivery/done records and plan their append through the Operation journal; bind review grant plus exact merge/run evidence and define exact replay versus divergent-operation refusal.
- PD-006 [AC-001] [AC-003] [FR-006] complete: Return typed refusals for every incomplete, stale, divergent, terminal, identity, seat, verdict, merge/run, fence, replay, and authority failure without falling back to projections.
- PD-007 [AC-004] [FR-007] complete: Keep the adapter pure except for caller-supplied controlled journal observations, expose constant read/effect bounds, and source-scan the new surface against production transport and credential vocabulary.
- PD-008 [AC-004] [FR-008] complete: Preserve `Protocol.md` byte-for-byte and run the canonical compiler, eleven TLC witnesses, and mutation controls after substantive implementation.
- PD-009 [AC-004] [FR-009] complete: Advance pinned roadmap authority, register GS2-05.6 with accepted GS2-05.5 as sole prerequisite, and add one exact Q3 gate with generated corpus, independent expectations, validator, and prerequisite inversion.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] public F# review surface: additive snapshot, chain, epoch, seat, verdict, authority record, plan, grant, cost, and typed-refusal contracts in `ReviewDeliveryAdapter.fsi`.
- PC-002 [PD-004] [PD-005] public F# delivery surface: additive merge/run state, operation record, receipt, replay, planning, and authorization contracts; no production transport is exposed.
- PC-003 [PD-003] [PD-005] authority split: Review and Operation journal heads decide authorization; mutable PR, Project, comment, check, and API projections cannot satisfy authority-bearing parameters.
- PC-004 [PD-008] formal authority: `src/FS.GG.Coordination.Protocol/Protocol.md` remains unchanged and its complete canonical qualification stays green.
- PC-005 [PD-009] roadmap index: GS2-05.6 carries accepted GS2-05.5 as sole prerequisite, one Q3 gate, exact command/contract digests, and no successor authority.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Unit tests cover stable-chain/epoch separation, snapshot canonicality, fresh seats, same-epoch succession, historical-pass invalidation, exact grant issuance, and every review refusal.
- VO-002 [PD-004] [PD-005] [PC-002] recovery: Unit tests cover all three delivery states, exact merge/run binding, delivery-before-done ordering, expected-parent conflicts, unknown outcomes, exact replay, divergent replay, and stale operation fences.
- VO-003 [PD-003] [PD-006] [PC-003] architectureTest: Build signatures and implementations together, prove journal primitive composition, reject projection authority and production vocabulary, and bind complete current rereads at effect time.
- VO-004 [PD-008] [PC-004] formal: Run canonical compiler, every expected safety/reachability witness, and every mutation control against unchanged protocol bytes; any failure returns the work to planning.
- VO-005 [PD-009] [PC-005] qualification: Q3 generated and independently authored controls agree, every mutation is red, accepted GS2-05.5 and exact roadmap sequencing are bound, and catalog/contract digests verify.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Existing protocol, journal, claim, intake, and roadmap surfaces remain unchanged; review/delivery behavior is a new explicit API and no caller is implicitly migrated.
- PM-002 [PC-003] authorityFirst: Production adoption is deferred to later cutover units; GS2-05.6 proves target authority semantics but cannot write live GitHub state.
- PM-003 [PC-005] sequenced: Accepted GS2-05.5 is required before GS2-05.6 qualification, and GS2-05.7 remains unavailable until a later accepted GS2-05.6 receipt is admitted.

## Generated View Impact
- GV-001 [PD-001] [PD-009] workModel: Regenerate `readiness/212-review-delivery/work-model.json` from the finalized requirements, decisions, contract impacts, verification obligations, and dependency-ordered tasks; source digest drift blocks ship.
- GV-002 [PD-009] unitIndex: `eng/github-substrate-v2-units.json` and `eng/github-substrate-v2-gates.json` are structured authority whose exact digests are architecture-tested.
- GV-003 [PD-009] qualificationEvidence: Generated corpus and independently authored expectations remain separate under `evidence/github-substrate-v2/gs2-05-6/`.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Implementation Status
- Registration and prerequisite proof: DONE — GS2-05.6 resolves against roadmap `9bd7849e`, exact Q3 command digest, and accepted GS2-05.5 receipt only.
- Review/delivery adapter: DONE — stable chains, immutable full-snapshot epochs, fresh phase seats, current-pass fencing, explicit merge/protected verification, and Review/Operation journal receipts are implemented as additive offline contracts.
- Generated and independent qualification: DONE — all 18 Q3 controls pass without production writes.
- Formal source delta: NONE — `Protocol.md` remains `7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218`.
- Formal post-implementation checkpoint: DONE — canonical compilation, all eleven TLC-backed witnesses, and the mutation suite passed in 499769 ms.
- Current checkpoint: run the complete clean repository build, unit, architecture, Q3, SDD, and roadmap-work gates over the final candidate.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 212-review-delivery`.
