---
schemaVersion: 1
workId: 79-optimize-canonical-quint-q1-q2
title: Optimize canonical Quint Q1/Q2 execution
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/79-optimize-canonical-quint-q1-q2/spec.md
publicOrToolFacingImpact: true
---

# Optimize canonical Quint Q1/Q2 execution Clarifications

## Source Specification
- work/79-optimize-canonical-quint-q1-q2/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking open: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking open: Resolve source ambiguity AMB-005 before checklist.
- CQ-006 [AMB:AMB-006] blocking open: Resolve source ambiguity AMB-006 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: CQ-001: decision: Combine the eight positive invariants in one Quint --invariants invocation only after local result equivalence; retain separate invocations if pinned-tool behavior differs.
- CQ-002 [AMB:AMB-002] decision: CQ-002: decision: Benchmark mutation concurrency 1, 2, and 4; begin with an explicit cap of 2 and retain the smallest stable winner under hosted memory and reliability constraints.
- CQ-003 [AMB:AMB-003] decision: CQ-003: decision: Adopt Quint server reuse only if five hosted samples show at least 10 percent median improvement with identical outcomes and stable resources; otherwise record its rejection.
- CQ-004 [AMB:AMB-004] decision: CQ-004: decision: Emit Q1 immediately after the one shared deterministic preparation succeeds; emit Q2 after positive verification and all 51 negative controls finish.
- CQ-005 [AMB:AMB-005] decision: CQ-005: decision: Emit one schema-versioned JSON receipt with phase timings, process counts, pinned tool identities, input/preparation/result digests, and Q1/Q2 outcomes.
- CQ-006 [AMB:AMB-006] decision: CQ-006: decision: Remove hard-coded SDD commands from the reusable Quint runner; enforce SDD through the active roadmap item lifecycle and retained item evidence.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: CQ-001: decision: Combine the eight positive invariants in one Quint --invariants invocation only after local result equivalence; retain separate invocations if pinned-tool behavior differs.
- DEC-002 [CQ-002] [AMB:AMB-002]: CQ-002: decision: Benchmark mutation concurrency 1, 2, and 4; begin with an explicit cap of 2 and retain the smallest stable winner under hosted memory and reliability constraints.
- DEC-003 [CQ-003] [AMB:AMB-003]: CQ-003: decision: Adopt Quint server reuse only if five hosted samples show at least 10 percent median improvement with identical outcomes and stable resources; otherwise record its rejection.
- DEC-004 [CQ-004] [AMB:AMB-004]: CQ-004: decision: Emit Q1 immediately after the one shared deterministic preparation succeeds; emit Q2 after positive verification and all 51 negative controls finish.
- DEC-005 [CQ-005] [AMB:AMB-005]: CQ-005: decision: Emit one schema-versioned JSON receipt with phase timings, process counts, pinned tool identities, input/preparation/result digests, and Q1/Q2 outcomes.
- DEC-006 [CQ-006] [AMB:AMB-006]: CQ-006: decision: Remove hard-coded SDD commands from the reusable Quint runner; enforce SDD through the active roadmap item lifecycle and retained item evidence.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 79-optimize-canonical-quint-q1-q2`.
