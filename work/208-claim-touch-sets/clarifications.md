---
schemaVersion: 1
workId: 208-claim-touch-sets
title: GS2-05.5 claims and touch sets
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/208-claim-touch-sets/spec.md
publicOrToolFacingImpact: true
---

# GS2-05.5 claims and touch sets Clarifications

## Source Specification
- work/208-claim-touch-sets/spec.md

## Clarification Questions
- CQ-001: What exact state authorizes an external claim effect?
- CQ-002: What authority does lease expiry grant to a successor?
- CQ-003: How are touch paths canonicalized and conflicts decided?
- CQ-004: How are multiple grants acquired and recovered after conflict?
- CQ-005: Does GS2-05.5 require a Quint protocol change?
- CQ-006: Which execution and qualification boundary belongs in this unit?

## Answers
- CQ-001 → A complete authoritative claim-journal reread whose current nonterminal head matches subject, owner, complete touch set, commit, and fencing generation. Projections, clocks, responses, and object existence never authorize.
- CQ-002 → Expiry makes a rival eligible to attempt a successor expected-parent CAS. It does not revoke, transfer, or authorize; authority changes only after the exact successor proposal is accepted and reread.
- CQ-003 → Repository identity partitions domains. Within one repository, canonical equal paths and ancestor/descendant paths conflict. Empty, duplicate, traversal, root-wide, and noncanonical paths refuse before planning.
- CQ-004 → Reuse `ShardedJournalAdapter.planSaga` total ordering, persist the complete touch set and expected generations before effects, release the unconsumed suffix, and append reverse-order fenced compensation that retains original results.
- CQ-005 → No. The existing GS2-03.10 model already defines sibling exclusion, generation advancement, and stale-effect fencing. Implementation must satisfy it unchanged.
- CQ-006 → Pure planning, controlled-fixture application/recovery, one Q3 gate, generated and independent evidence, and formal re-verification are in scope. Production IO, credentials, review/delivery, lifecycle, shadowing, and successor units are out.

## Decisions
- DEC-001 [CQ-001] [FR-002] [FR-006] [AC-001] [AC-004]: Make exact authoritative journal reread the only effect authorization and require every claim binding, not merely commit/generation.
- DEC-002 [CQ-002] [FR-003] [AC-002]: Separate successor eligibility from ownership and effect authority; expiry opens only an expected-parent CAS attempt.
- DEC-003 [CQ-003] [FR-001] [FR-004] [AC-003]: Define repository-scoped canonical path ancestry as the conflict law and reject unsafe or ambiguous touch syntax.
- DEC-004 [CQ-004] [FR-005] [FR-007] [AC-003]: Reuse the existing total order and saga conflict plan, with a required persisted full-plan receipt before effects and reverse append-only compensation.
- DEC-005 [CQ-005] [FR-008] [FR-009] [AC-005]: Preserve the Quint source unchanged and bind each implementation phase to canonical compiler and pure-model verification.
- DEC-006 [CQ-006] [FR-007] [FR-008] [FR-009] [AC-005]: Keep production IO unrepresentable, register exactly one Q3 contract, and bind generated plus independently authored controls to accepted GS2-05.4.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 208-claim-touch-sets`.
