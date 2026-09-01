---
schemaVersion: 1
workId: 200-staged-intake-admission
title: GS2-05.9 staged intake admission
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/200-staged-intake-admission/spec.md
publicOrToolFacingImpact: true
---

# GS2-05.9 staged intake admission Clarifications

## Source Specification
- work/200-staged-intake-admission/spec.md

## Clarification Questions
- CQ-001: How does staged capture reuse the existing transaction guarantees without requiring a complete Ready record?
- CQ-002: What counts against the fixed capture budget, and what must remain cardinality-independent?
- CQ-003: Which facts gate Ready promotion, and are claim or pull-request facts among them?
- CQ-004: Where may organization-wide reconciliation and unrelated Backlog work occur?
- CQ-005: How is `fsgg.coord.intake/v1` compatibility expressed without freezing the new staged surface into the old request shape?

## Answers
- CQ-001 → Staged capture builds a canonical v1 intake intent from a smaller discovery contract and delegates sealed planning and controlled application to the unchanged GS2-05.3 path. The staged wrapper adds no weaker executor.
- CQ-002 → Distinct declared item-local authority-read operations and actual planned mutation effects count. Both are capped at six. Unrelated Project and Backlog cardinalities are explicit observation metadata used only by qualification; changing them cannot change cost or plan bytes.
- CQ-003 → Root cause, touch set, verification contract, dependency declaration, route decision, native issue type, organization fields, repository scope, and work classification are required. Claim and pull-request evidence are excluded because they are created only after Ready scheduling and claiming.
- CQ-004 → Only event-targeted, scheduled, or explicitly requested maintenance outside the capture operation vocabulary may reconcile globally or traverse/retriage unrelated work.
- CQ-005 → Existing public types and functions remain unchanged. New staged functions emit canonical intents that the same v1 planner and controlled executor consume, and expose the compatibility schema in their result.

## Decisions
- DEC-001 [CQ-001] [CQ-005] [FR-001] [FR-005] [AC-001] [AC-002]: Add a staged wrapper that canonicalizes discovery state into the existing v1 transaction path; do not fork or weaken apply semantics.
- DEC-002 [CQ-002] [FR-002] [AC-001] [AC-004]: Model the six allowed authority-read classes as a closed item-local union, count actual mutation effects after planning, and exclude unrelated-cardinality metadata from the sealed result.
- DEC-003 [CQ-003] [FR-004] [AC-003]: Model the nine Ready facts as a closed union, reject duplicates and omissions with one stable surface-specific diagnostic each, and deliberately omit claim/PR from the vocabulary.
- DEC-004 [CQ-004] [FR-003] [AC-004]: Keep global maintenance unrepresentable in staged capture; structural inventory and independent source mutation must prove the boundary can go red.
- DEC-005 [CQ-005] [FR-005] [AC-002]: Preserve every existing signature member and use additive public types/functions only; existing tests remain the compatibility control.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 200-staged-intake-admission`.
