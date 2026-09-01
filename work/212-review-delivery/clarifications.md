---
schemaVersion: 1
workId: 212-review-delivery
title: GS2-05.6 review and delivery
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/212-review-delivery/spec.md
publicOrToolFacingImpact: true
---

# GS2-05.6 review and delivery Clarifications

## Source Specification
- work/212-review-delivery/spec.md

## Clarification Questions
- CQ-001: Which facts identify the stable chain and immutable epoch?
- CQ-002: When is a fresh phase seat required, and does it replace accountable authority?
- CQ-003: What exact evidence authorizes a review-dependent effect?
- CQ-004: What distinguishes merge from completion?
- CQ-005: How are delivery and done receipts made authoritative and replay-safe?
- CQ-006: Does implementation require a Quint change, and what execution boundary belongs here?

## Answers
- CQ-001 → Stable chain identity derives from canonical subject identity only. Epoch identity derives from that chain plus the canonical complete snapshot digest; the snapshot is intentionally excluded from the chain.
- CQ-002 → Every new snapshot epoch and every succession inside an unchanged epoch requires a never-reused fresh seat. The one accountable authority persists and is not re-issued merely because the snapshot changed.
- CQ-003 → A complete current nonterminal Review-journal reread matching stable chain, epoch key, full snapshot digest, current seat, pass verdict, journal commit, and fencing generation.
- CQ-004 → `NotMerged`, `Merged`, and `ProtectedVerified` are closed distinct states. Merge acceptance may seal delivery but cannot seal done; done additionally requires a successful protected-main run for the exact merge commit.
- CQ-005 → Canonical receipts are appended by expected-parent CAS to the Operation journal and bind review grant, exact merge/run evidence, operation commit, and generation. Exact replay returns the same receipt; any same-operation divergent material refuses.
- CQ-006 → No Quint change. Pure planning and controlled fixtures compose the existing ReviewEpoch and sharded-journal contracts; production IO is deferred.

## Decisions
- DEC-001 [CQ-001] [FR-001] [FR-003] [AC-001]: Separate stable chain identity from immutable full-snapshot epoch identity and make incomplete snapshots unrepresentable as authority.
- DEC-002 [CQ-002] [FR-002] [AC-002]: Preserve one accountable authority while requiring a fresh unique phase seat for every epoch and same-epoch succession.
- DEC-003 [CQ-003] [FR-003] [FR-006] [AC-001] [AC-002]: Make exact current Review-journal reread the sole review authorization and retain historical verdicts as non-authorizing evidence.
- DEC-004 [CQ-004] [FR-004] [FR-006] [AC-003]: Represent merge and protected-main verification as distinct typed states and prohibit done before the latter.
- DEC-005 [CQ-005] [FR-005] [FR-006] [AC-003]: Use the Operation journal for canonical, fenced, exact-replay delivery and done receipts.
- DEC-006 [CQ-006] [FR-007] [FR-008] [FR-009] [AC-004]: Preserve formal source, keep production IO unrepresentable, and register one independent Q3 contract after accepted GS2-05.5.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 212-review-delivery`.
