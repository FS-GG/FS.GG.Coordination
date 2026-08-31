---
schemaVersion: 1
workId: gs2-04-3-native-relation-adapter
title: GS2-04.3 native relation adapter clarifications
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-04-3-native-relation-adapter/spec.md
publicOrToolFacingImpact: true
---

# GS2-04.3 native relation adapter clarifications Clarifications

## Source Specification
- work/gs2-04-3-native-relation-adapter/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking open: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking open: Resolve source ambiguity AMB-005 before checklist.
- CQ-006 [AMB:AMB-006] blocking open: Resolve source ambiguity AMB-006 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: ParentChild is canonical parent-to-child; Blocks is canonical blocker-to-blocked. Relation kind and endpoint direction are never inferred or interchanged.
- CQ-002 [AMB:AMB-002] decision: Duplicate observations, including byte-identical repeated edges, are malformed completeness evidence and refuse authoritative read/planning; idempotency belongs to requested mutation intent, not observation normalization.
- CQ-003 [AMB:AMB-003] decision: Authority requires a non-empty revision plus a contiguous page chain with exact page/node counts and terminal-page proof for the complete requested relation scope before absence or removal is authoritative.
- CQ-004 [AMB:AMB-004] decision: Revision mismatch returns a typed ReReadRequired decision carrying planned and observed revisions and no mutation effect; only a fresh complete observation may produce a replacement plan.
- CQ-005 [AMB:AMB-005] decision: Verification is exact and does not tolerate unrelated concurrent changes; success requires the planned edge delta, expected resulting revision, and unchanged unrelated edge set. Concurrent changes return a typed conflict and require re-read/replan.
- CQ-006 [AMB:AMB-006] decision: Q3 is deterministic, committed, synthetic, offline, and credential-free. Live destructive correspondence remains GS2-04.9 Q4.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: ParentChild is canonical parent-to-child; Blocks is canonical blocker-to-blocked. Relation kind and endpoint direction are never inferred or interchanged.
- DEC-002 [CQ-002] [AMB:AMB-002]: Duplicate observations, including byte-identical repeated edges, are malformed completeness evidence and refuse authoritative read/planning; idempotency belongs to requested mutation intent, not observation normalization.
- DEC-003 [CQ-003] [AMB:AMB-003]: Authority requires a non-empty revision plus a contiguous page chain with exact page/node counts and terminal-page proof for the complete requested relation scope before absence or removal is authoritative.
- DEC-004 [CQ-004] [AMB:AMB-004]: Revision mismatch returns a typed ReReadRequired decision carrying planned and observed revisions and no mutation effect; only a fresh complete observation may produce a replacement plan.
- DEC-005 [CQ-005] [AMB:AMB-005]: Verification is exact and does not tolerate unrelated concurrent changes; success requires the planned edge delta, expected resulting revision, and unchanged unrelated edge set. Concurrent changes return a typed conflict and require re-read/replan.
- DEC-006 [CQ-006] [AMB:AMB-006]: Q3 is deterministic, committed, synthetic, offline, and credential-free. Live destructive correspondence remains GS2-04.9 Q4.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-04-3-native-relation-adapter`.
