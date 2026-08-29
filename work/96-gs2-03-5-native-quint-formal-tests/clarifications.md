---
schemaVersion: 1
workId: 96-gs2-03-5-native-quint-formal-tests
title: "GS2-03.5 native Quint model, property, and formal tests"
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/96-gs2-03-5-native-quint-formal-tests/spec.md
publicOrToolFacingImpact: true
---

# GS2-03.5 native Quint model, property, and formal tests Clarifications

## Source Specification
- work/96-gs2-03-5-native-quint-formal-tests/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking open: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking open: Resolve source ambiguity AMB-005 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: Apply explicit progress obligations to all six named state spaces; encode fairness assumptions and bounded interpretations as reviewed catalogue fields, never hidden backend defaults.
- CQ-002 [AMB:AMB-002] decision: Use deliberately invalid, digest-bound subject variants to produce retained counterexamples without weakening or replacing the production property.
- CQ-003 [AMB:AMB-003] decision: Require every example, simulation, witness, and property module to import a source-derived canonical executable root and contain only parameters, executions, and assertions.
- CQ-004 [AMB:AMB-004] decision: Report exhaustive results only within each registered finite bound and pinned backend, including explored states or traces, elapsed time, peak memory, and artifact bytes.
- CQ-005 [AMB:AMB-005] decision: Co-bind the canonical Quint trace and normalized ITF projection to the same source, closure, property, bounds, toolchain, ordered states, and expected violation; reject either artifact when the pair diverges.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: Apply explicit progress obligations to all six named state spaces; encode fairness assumptions and bounded interpretations as reviewed catalogue fields, never hidden backend defaults.
- DEC-002 [CQ-002] [AMB:AMB-002]: Use deliberately invalid, digest-bound subject variants to produce retained counterexamples without weakening or replacing the production property.
- DEC-003 [CQ-003] [AMB:AMB-003]: Require every example, simulation, witness, and property module to import a source-derived canonical executable root and contain only parameters, executions, and assertions.
- DEC-004 [CQ-004] [AMB:AMB-004]: Report exhaustive results only within each registered finite bound and pinned backend, including explored states or traces, elapsed time, peak memory, and artifact bytes.
- DEC-005 [CQ-005] [AMB:AMB-005]: Co-bind the canonical Quint trace and normalized ITF projection to the same source, closure, property, bounds, toolchain, ordered states, and expected violation; reject either artifact when the pair diverges.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 96-gs2-03-5-native-quint-formal-tests`.
