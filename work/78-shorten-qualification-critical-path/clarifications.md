---
schemaVersion: 1
workId: 78-shorten-qualification-critical-path
title: Shorten qualification critical path
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/78-shorten-qualification-critical-path/spec.md
publicOrToolFacingImpact: true
---

# Shorten qualification critical path Clarifications

## Source Specification
- work/78-shorten-qualification-critical-path/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking open: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking open: Resolve source ambiguity AMB-005 before checklist.
- CQ-006 [AMB:AMB-006] blocking open: Resolve source ambiguity AMB-006 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: CQ-001: decision: Use canonical versioned JSON as the reviewed semantic plan; its normalized typed fields, not workflow bytes, are authority.
- CQ-002 [AMB:AMB-002] decision: CQ-002: decision: Deterministically generate the committed thin workflow from the plan and reject projection drift; harmless presentation changes do not change the plan digest or authorize behavior.
- CQ-003 [AMB:AMB-003] decision: CQ-003: decision: Move contract loading, plan decisions, receipt validation, evidence collection, and workflow projection checks into the compiled Qualification.Contracts module; keep eng/bootstrap-ci.fsx as a thin #load/argument/stdout adapter and retain one adapter parity corpus.
- CQ-004 [AMB:AMB-004] decision: CQ-004: decision: Evaluate NuGet global-package caching for ordinary gates under an exact OS plus global.json plus all-lockfile key, but retain it only if hosted miss/hit measurements materially reduce runner time; keep dependency/security explicitly cold.
- CQ-005 [AMB:AMB-005] decision: CQ-005: decision: Target repeated FSI startup in BootstrapCiTests first because it owns 267.82 aggregate test-seconds; convert pure mutations to direct compiled calls, retain bounded process-level adapter tests, and do not reduce test identities or assertions.
- CQ-006 [AMB:AMB-006] decision: CQ-006: decision: Compare successful exact-head samples, use settled execution excluding queue, report unchanged canonical Quint variance separately, and require at least 30 percent improvement in compiler/tests and recovery plus 10 percent aggregate runner-minute improvement.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: CQ-001: decision: Use canonical versioned JSON as the reviewed semantic plan; its normalized typed fields, not workflow bytes, are authority.
- DEC-002 [CQ-002] [AMB:AMB-002]: CQ-002: decision: Deterministically generate the committed thin workflow from the plan and reject projection drift; harmless presentation changes do not change the plan digest or authorize behavior.
- DEC-003 [CQ-003] [AMB:AMB-003]: CQ-003: decision: Move contract loading, plan decisions, receipt validation, evidence collection, and workflow projection checks into the compiled Qualification.Contracts module; keep eng/bootstrap-ci.fsx as a thin #load/argument/stdout adapter and retain one adapter parity corpus.
- DEC-004 [CQ-004] [AMB:AMB-004]: CQ-004: decision: Evaluate NuGet global-package caching for ordinary gates under an exact OS plus global.json plus all-lockfile key, but retain it only if hosted miss/hit measurements materially reduce runner time; keep dependency/security explicitly cold.
- DEC-005 [CQ-005] [AMB:AMB-005]: CQ-005: decision: Target repeated FSI startup in BootstrapCiTests first because it owns 267.82 aggregate test-seconds; convert pure mutations to direct compiled calls, retain bounded process-level adapter tests, and do not reduce test identities or assertions.
- DEC-006 [CQ-006] [AMB:AMB-006]: CQ-006: decision: Compare successful exact-head samples, use settled execution excluding queue, report unchanged canonical Quint variance separately, and require at least 30 percent improvement in compiler/tests and recovery plus 10 percent aggregate runner-minute improvement.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 78-shorten-qualification-critical-path`.
