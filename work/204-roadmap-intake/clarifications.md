---
schemaVersion: 1
workId: 204-roadmap-intake
title: GS2-05.4 roadmap intake
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/204-roadmap-intake/spec.md
publicOrToolFacingImpact: true
---

# GS2-05.4 roadmap intake Clarifications

## Source Specification
- work/204-roadmap-intake/spec.md

## Clarification Questions
- CQ-001: Which representation is authoritative compiler input?
- CQ-002: How are existing issues matched without guessing?
- CQ-003: Which relationships and fields are semantic authorities after apply?
- CQ-004: How is the bounded cost contract calculated?
- CQ-005: Which remote facts may drift inspection classify, and what is unrelated?
- CQ-006: How much mutation and transport behavior belongs in GS2-05.4?

## Answers
- CQ-001 → A versioned typed roadmap definition plus an explicitly scoped observation. Markdown and Project rows may be generated or observed projections but are never parsed as semantic authority.
- CQ-002 → Each roadmap node has a stable source key and expected ownership fingerprint. It may match zero targets (create) or exactly one compatible target (reuse/update). Multiple, conflicting, or foreign matches are typed refusals.
- CQ-003 → Native issue identity, parent/sub-issue edges, and dependency edges are authoritative. Dates and organization issue fields carry scheduling intent. Project membership/status/fields and copied body/comment metadata are derived projections only.
- CQ-004 → The compiler declares closed formulas over roadmap node count, hierarchy-edge count, dependency-edge count, and emitted projection count. Unrelated Project and Backlog cardinality is not an input to those formulas or the sealed plan.
- CQ-005 → Only targets carrying the exact roadmap ownership identity are owned. Inspection reports all missing, extra, or mismatched facts for that set and ignores unrelated targets even when they share titles, fields, or Project membership.
- CQ-006 → Pure validation/planning/inspection and exact-plan application over a deterministic controlled fixture are in scope. Production transport, credentials, live organization/Project mutation, and global scanning are explicitly out of scope.

## Decisions
- DEC-001 [CQ-001] [FR-001] [AC-001]: Introduce a closed typed roadmap definition and refuse Markdown/Project parsing as input authority.
- DEC-002 [CQ-002] [FR-002] [FR-003] [AC-001] [AC-002]: Resolve stable source keys only against an explicitly scoped observation; zero compatible matches means create, one means reuse/update, and every other cardinality or collision refuses.
- DEC-003 [CQ-003] [FR-007] [AC-005]: Model native hierarchy and dependency edges separately from Project projections so a Project/body inversion cannot alter graph meaning or readiness.
- DEC-004 [CQ-004] [FR-005] [AC-004]: Derive declared reads and maximum effects solely from owned roadmap nodes/edges/projections and prove invariance under unrelated-cardinality growth.
- DEC-005 [CQ-005] [FR-006] [AC-004]: Attach an ownership identity to planned targets and use it as the sole drift boundary; title or Project similarity never implies ownership.
- DEC-006 [CQ-006] [FR-004] [FR-008] [AC-003] [AC-005]: Reuse the accepted sealed-plan safety model over an in-memory controlled fixture and keep production GitHub IO unrepresentable in the GS2-05.4 adapter.
- DEC-007 [CQ-001] [CQ-006] [FR-009] [AC-005]: Register one Q3 gate with generated cases, independent expectations, author-independent source checks, and explicit mutations; accepted GS2-05.9 remains the sole roadmap prerequisite.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 204-roadmap-intake`.
