---
schemaVersion: 1
workId: gs2-04-2-issue-field-adapter
title: GS2-04.2 issue and field adapter clarifications
stage: clarify
changeTier: tier1
status: needsAnswers
sourceSpec: work/gs2-04-2-issue-field-adapter/spec.md
publicOrToolFacingImpact: true
---

# GS2-04.2 issue and field adapter clarifications Clarifications

## Source Specification
- work/gs2-04-2-issue-field-adapter/spec.md

## Clarification Questions
- **CQ-001** (AMB-001): Are semantic identities defined by display names alone or by typed repository-owned declarations?
- **CQ-002** (AMB-002): Are extra live options tolerated when an expected option set is declared closed?
- **CQ-003** (AMB-003): May a create, update, or clear plan be emitted without a complete observed revision?
- **CQ-004** (AMB-004): What result represents current and desired values that are already equal?
- **CQ-005** (AMB-005): May Q3 fixture qualification call external GitHub endpoints?

## Answers
- CQ-001 → a repository-owned semantic declaration binds the expected entity kind, stable semantic name, field data type, and closed option declarations; resolution requires one matching live identity.
- CQ-002 → no; missing, extra, renamed, duplicate-name, or duplicate-id options are drift before a value or plan becomes authoritative.
- CQ-003 → no; every mutation plan requires complete observation and explicit expected-revision evidence. Absent, unreadable, stale, or incomplete revision evidence refuses planning.
- CQ-004 → return a typed no-op decision with no mutation step while preserving revision and causation identity for deterministic evidence.
- CQ-005 → no; Q3 uses deterministic committed fixtures and pure adapter transitions. Live destructive correspondence belongs to GS2-04.9 Q4.

## Decisions
- **DEC-001** [CQ-001] [AMB:AMB-001] [FR-001] [AC-001]: Resolve semantic identities against typed repository-owned declarations, not unqualified display names; zero or multiple live matches are distinct refusals.
- **DEC-002** [CQ-002] [AMB:AMB-002] [FR-002] [AC-002]: Treat option sets as exact closed contracts with unique semantic names and live IDs before current values or plans are authoritative.
- **DEC-003** [CQ-003] [AMB:AMB-003] [FR-003] [FR-004] [FR-005] [AC-003] [AC-004] [AC-005]: Bind every mutation plan to a complete observation revision and refuse absent, unreadable, stale, or incomplete evidence.
- **DEC-004** [CQ-004] [AMB:AMB-004] [FR-006] [AC-006]: Represent equality as a mutation-free typed no-op carrying observation and causation identity, never as an update step.
- **DEC-005** [CQ-005] [AMB:AMB-005] [FR-007] [FR-008] [AC-007] [AC-008]: Keep GS2-04.2 qualification deterministic, offline, credential-free, and separate from Q4 sandbox correspondence.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
- None. All five blocking ambiguities are resolved by DEC-001 through DEC-005.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-04-2-issue-field-adapter`.
