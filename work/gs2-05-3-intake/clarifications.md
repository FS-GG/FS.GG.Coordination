---
schemaVersion: 1
workId: gs2-05-3-intake
title: GS2-05.3 intake contract
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-05-3-intake/spec.md
publicOrToolFacingImpact: true
---

# GS2-05.3 intake contract Clarifications

## Source Specification
- work/gs2-05-3-intake/spec.md

## Clarification Questions
- CQ-001: What is apply allowed to mutate in GS2-05.3?
- CQ-002: What observation is complete enough for planning and inspect?
- CQ-003: Which protocol records may intake initialize?
- CQ-004: How are plans sealed, resumed, and compensated without turning response bodies into authority?

## Answers
- CQ-001 → Only controlled in-memory or recorded-fixture state through an injected effect interpreter. Production GitHub transport, live resources, and credentials are outside the unit.
- CQ-002 → Every declared intake surface has an explicit observation outcome and terminal pagination proof. Missing, repeated, cyclic, unreadable, unauthorized, unsupported, partial, stale, and indeterminate facts remain distinct.
- CQ-003 → Only revision-bound initial journal, scheduling-intent, contract, touch-set, and derived projection intents needed to admit one work item. Claims, review, delivery, lifecycle projection, and roadmap compilation are not initialized.
- CQ-004 → The plan digest covers canonical request, complete observation revision/digest, ordered operation identities and dependencies, expected revisions, preconditions, postconditions, compensations, and desired result. Durable per-operation outcomes bind the same plan digest; authoritative rereads decide completion.

## Decisions
- DEC-001 [CQ-001] [FR-003] [FR-008] [AC-002] [AC-004]: Keep effect execution behind a controlled-state interpreter and expose no production writer or credentialed command.
- DEC-002 [CQ-002] [FR-001] [FR-002] [FR-005] [AC-001] [AC-003]: Require an explicit complete observation envelope before planning; no response body, page count, projection, or inferred absence substitutes for authoritative terminal observation.
- DEC-003 [CQ-003] [FR-006] [FR-008] [AC-003] [AC-004]: Initialize only the five revision-bound intent families named by the registered contract and reject successor behavior.
- DEC-004 [CQ-004] [FR-002] [FR-003] [FR-004] [AC-001] [AC-002]: Seal the whole canonical plan, reread before and after effects, persist outcomes before progression, resume only matching completed steps, roll forward when the postcondition already holds, and compensate applied steps in reverse dependency order.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-05-3-intake`.
