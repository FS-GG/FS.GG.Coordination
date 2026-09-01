---
schemaVersion: 1
workId: gs2-05-2-organization-issue-fields
title: GS2-05.2 organization issue-field contract
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-05-2-organization-issue-fields/spec.md
publicOrToolFacingImpact: true
---

# GS2-05.2 organization issue-field contract Clarifications

## Source Specification
- work/gs2-05-2-organization-issue-fields/spec.md

## Clarification Questions
- CQ-001: Which fields carry authority rather than projection?
- CQ-002: When is a hold reason present, and can it itself schedule work?
- CQ-003: What is the canonical shape of contract and touch-set projections?
- CQ-004: What is the migration and formal-model atomic grain?

## Answers
- CQ-001 → Scheduling Intent alone carries human or policy scheduling authority. Lifecycle Status is derived; Contract and touch set are revision-bound projections.
- CQ-002 → Hold reason is required only for Backlog or Paused and absent for Ready or Cancelled. It explains intent but never overrides it.
- CQ-003 → Contract is absent or a lowercase 64-hex authoritative record digest. Touch set is a non-empty, sorted, duplicate-free list of normalized repository-relative path patterns bound to an authoritative record digest.
- CQ-004 → One pure disposition is computed per stable row, but any invalid row refuses the corpus-wide plan. The Quint model is one cohesive single-actor state; live apply is outside GS2-05.2.

## Decisions
- DEC-001 [CQ-001] [FR-001] [FR-004] [AC-001]: Never read Status, Contract text, or touch-set text as independent intent/protocol authority; a missing authoritative binding is refusal, not absence.
- DEC-002 [CQ-002] [FR-002] [AC-001]: Enforce the exact intent/hold matrix and keep reason informational within the selected intent.
- DEC-003 [CQ-003] [FR-004] [FR-007] [AC-002] [AC-003]: Require digest-bound projections, canonical path normalization and ordering, and refuse missing, duplicate, absolute, escaping, or noncanonical patterns.
- DEC-004 [CQ-004] [FR-005] [FR-006] [FR-008] [AC-002] [AC-004]: Plan all-or-nothing over canonically ordered row identities. Use plain Quint with planned and refused actions separately reachable.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-05-2-organization-issue-fields`.
