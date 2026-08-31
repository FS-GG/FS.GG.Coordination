---
schemaVersion: 1
workId: gs2-04-4-project-adapter
title: GS2-04.4 Project adapter clarifications
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-04-4-project-adapter/spec.md
publicOrToolFacingImpact: true
---

# GS2-04.4 Project adapter Clarifications

## Source Specification
- work/gs2-04-4-project-adapter/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking open: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking open: Resolve source ambiguity AMB-005 before checklist.
- CQ-006 [AMB:AMB-006] blocking open: Resolve source ambiguity AMB-006 before checklist.
- CQ-007 [AMB:AMB-007] blocking open: Resolve source ambiguity AMB-007 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: Canonical content kinds are RepositoryIssue and PullRequest with content node id, owner/name, and number; DraftIssue with Project item id and draft node id; and Redacted or Unknown carrying only identities actually observed. A repository issue or pull request whose owner/name differs from the requested repository remains typed external-repository content and is never coerced into the subject.
- CQ-002 [AMB:AMB-002] decision: More than one Project item for one canonical content identity is an ambiguous duplicate observation and refuses authoritative resolution or planning. No item is selected by order, archive state, or Status.
- CQ-003 [AMB:AMB-003] decision: Archived matching items remain explicit membership observations but do not satisfy active membership and are mutation-ineligible. Planning requires exactly one unarchived eligible item; archived-only membership returns a typed Archived outcome.
- CQ-004 [AMB:AMB-004] decision: Missing is authoritative only after a non-empty revision and contiguous, count-consistent, terminal Project item page chain proves zero matches. Incomplete, unauthorized, unsupported, and unreadable observations remain separate typed outcomes and can never imply missing.
- CQ-005 [AMB:AMB-005] decision: Status authority requires exact Project id, item id, one Status field id, a complete duplicate-free option set, and an exact desired option id. A missing Status value is authoritative only within that complete field observation; duplicate fields/options, unknown options, and absent completeness refuse planning.
- CQ-006 [AMB:AMB-006] decision: Verification is exact and does not tolerate unrelated concurrent Project changes. Success requires the planned membership or Status delta, an advanced resulting revision, and unchanged unrelated items and fields; any extra change returns a typed conflict and requires re-read/replan.
- CQ-007 [AMB:AMB-007] decision: Q3 is deterministic, committed, synthetic, offline, and credential-free. Live destructive correspondence remains GS2-04.9 Q4.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: Canonical content kinds preserve repository issue, pull request, draft, redacted, unknown, and external-repository identity without coercion or invention.
- DEC-002 [CQ-002] [AMB:AMB-002]: Duplicate canonical content identities are ambiguous malformed projection evidence and refuse resolution/planning.
- DEC-003 [CQ-003] [AMB:AMB-003]: Archived matches remain observable but are not active membership and cannot authorize a mutation proposal.
- DEC-004 [CQ-004] [AMB:AMB-004]: Only a complete revision-bound terminal observation can prove missing; all read failures remain distinct.
- DEC-005 [CQ-005] [AMB:AMB-005]: Status planning requires exact Project/item/field/option identities and a complete duplicate-free option set.
- DEC-006 [CQ-006] [AMB:AMB-006]: Post-state verification is exact and refuses every unrelated concurrent change.
- DEC-007 [CQ-007] [AMB:AMB-007]: Q3 fixtures are offline and credential-free; live correspondence is deferred to GS2-04.9 Q4.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-04-4-project-adapter`.
