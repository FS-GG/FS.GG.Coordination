---
schemaVersion: 1
workId: gs2-04-5-comment-projection-adapter
title: GS2-04.5 comment/projection adapter clarifications
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-04-5-comment-projection-adapter/spec.md
publicOrToolFacingImpact: true
---

# GS2-04.5 comment/projection adapter Clarifications

## Source Specification
- work/gs2-04-5-comment-projection-adapter/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking open: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking open: Resolve source ambiguity AMB-005 before checklist.
- CQ-006 [AMB:AMB-006] blocking open: Resolve source ambiguity AMB-006 before checklist.
- CQ-007 [AMB:AMB-007] blocking open: Resolve source ambiguity AMB-007 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: A complete observation preserves database id, node id, created/updated instants, author login, exact UTF-8 body, page index, ordinal, page count, node count, and terminal cursor. Duplicate database or node identities and non-monotonic `(createdAt,databaseId)` server order are malformed observations, not winner-selection inputs.
- CQ-002 [AMB:AMB-002] decision: A recognized projection begins with exactly `<!-- fsgg:projection/v1 -->`, followed by one canonical JSON object with schema `fsgg.coordination.projection-marker/1`, qualified subject, journal kind/shard, positive fencing generation, 40-hex journal commit, lowercase 64-hex authority digest, and lowercase 64-hex projection digest. The projection digest covers the exact UTF-8 human body after the JSON record, avoiding self-reference.
- CQ-003 [AMB:AMB-003] decision: An expected projection identity supplied by durable authority is Deleted only after a complete terminal read proves the server identity absent; an observed identity with changed update instant or body digest is Edited; parse failure is Malformed; a well-formed marker whose subject, authority, or projection digest disagrees is Tampered. Missing without an expected identity remains Missing, and incomplete/unreadable reads never imply deletion.
- CQ-004 [AMB:AMB-004] decision: Comment order, comment id magnitude, creation/update time, and latest matching marker never select or authorize a concurrency-sensitive transition. Trust requires one exact expected server identity and one exact marker-to-journal binding; duplicates or alternatives refuse as ambiguous.
- CQ-005 [AMB:AMB-005] decision: Rendering consumes canonical durable-authority facts plus an explicit rendering policy version, normalizes line endings to LF, emits UTF-8 without BOM, and appends one final LF. The plan identity hashes action, subject, expected server identity/revision, journal generation/commit/digest, rendering policy, desired body digest, and causation.
- CQ-006 [AMB:AMB-006] decision: Any changed server identity, update instant, body digest, marker binding, journal generation/commit/digest, or expected absence forces re-read and replan. Post-state success requires the exact intended marker/body bytes, an advanced revision for replacement, and unchanged unrelated comment identities and digests; missing responses or extra changes are indeterminate/conflict, never success.
- CQ-007 [AMB:AMB-007] decision: Q3 fixtures are deterministic, committed, synthetic, offline, and credential-free. They neither call live GitHub endpoints nor implement protected-ref journal CAS; live destructive correspondence remains GS2-04.9 Q4.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: Preserve exact server identities, page coordinates, terminal completeness, and server order; duplicates and reorder are malformed rather than authority selection.
- DEC-002 [CQ-002] [AMB:AMB-002]: Use one canonical marker schema and hash the exact human-body suffix independently of the marker JSON.
- DEC-003 [CQ-003] [AMB:AMB-003]: Classify Missing, Edited, Deleted, Malformed, Tampered, Incomplete, Unauthorized, and Unreadable from explicit evidence without collapsing them.
- DEC-004 [CQ-004] [AMB:AMB-004]: Trust binds one expected server identity to one durable journal snapshot; comment order and recency never authorize transitions.
- DEC-005 [CQ-005] [AMB:AMB-005]: Rendering and plan identity are byte-deterministic over normalized durable facts, explicit policy, expected revision, and causation.
- DEC-006 [CQ-006] [AMB:AMB-006]: Every stale pre-state forces re-read/replan and exact post-state verification refuses unrelated concurrent or indeterminate change.
- DEC-007 [CQ-007] [AMB:AMB-007]: Q3 remains offline and credential-free; live GitHub and protected journal correspondence are deferred to GS2-04.9 Q4.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
- No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-04-5-comment-projection-adapter`.
