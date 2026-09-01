---
schemaVersion: 1
workId: gs2-05-1-work-taxonomy
title: GS2-05.1 work taxonomy contract
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-05-1-work-taxonomy/spec.md
publicOrToolFacingImpact: true
---

# GS2-05.1 work taxonomy contract Clarifications

## Source Specification
- work/gs2-05-1-work-taxonomy/spec.md

## Clarification Questions
- CQ-001: Which fact is authoritative after migration?
- CQ-002: How are absent Kind and absent Class interpreted in legacy prestates?
- CQ-003: What is the atomic grain and formal-model shape?

## Answers
- CQ-001 → Native issue type alone. Class and Kind survive only in fingerprinted prestate evidence and the list of projections a disposition retires.
- CQ-002 → Absent Kind means `work` exactly as the current board schema specifies. Absent Class is not defaulted; it is accepted only when an unambiguous standing Kind, hierarchical anchor evidence, or already-native type completely determines the result, otherwise it is refused.
- CQ-003 → One pure disposition is computed per stable row identity, but any invalid row refuses the corpus-wide plan. The formal model is one cohesive single-actor state with no communication medium; live apply is outside GS2-05.1.

## Decisions
- DEC-001 [CQ-001] [FR-001] [FR-003] [AC-001] [AC-002]: Native issue type is the sole classification authority after migration; no compatibility parser may consult Class or Kind to authorize post-migration behavior.
- DEC-002 [CQ-002] [FR-002] [FR-004] [AC-003] [AC-005]: Canonicalize absent Kind to work, never default absent Class, admit only the enumerated mapping, and fail closed on conflicting or incomplete evidence.
- DEC-003 [CQ-003] [FR-003] [FR-005] [AC-004]: Planning is all-or-nothing over canonically ordered stable row identities. A valid already-native row emits an explicit byte-stable no-op disposition; any invalid row emits diagnostics and no plan.
- DEC-004 [CQ-003] [FR-006] [AC-006]: Use plain Quint, not Choreo: the subject is a single deterministic repository-local classifier/planner with no participants, failures, messages, or time. Model planned and refused actions separately so both are reachable without inventing no-op transitions.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-05-1-work-taxonomy`.
