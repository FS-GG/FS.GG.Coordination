---
schemaVersion: 1
workId: 86-gs2-03-3-generated-structural-tests
title: GS2-03.3 generated structural tests
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/86-gs2-03-3-generated-structural-tests/spec.md
publicOrToolFacingImpact: true
---

# GS2-03.3 generated structural tests Clarifications

## Source Specification
- work/86-gs2-03-3-generated-structural-tests/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: CQ-001: Do not enumerate concrete state combinations; cover each qualified action-effect registration and leave behavioral reachability to GS2-03.5.
- CQ-002 [AMB:AMB-002] decision: CQ-002: Round-trip canonical schema descriptors and exact ordered fields; do not invent domain sample values.
- CQ-003 [AMB:AMB-003] decision: CQ-003: Create one projection-freshness case for every compiled-output manifest entry, even when typed content also supplies command, mutation, permission, or schema cases.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: CQ-001: Do not enumerate concrete state combinations; cover each qualified action-effect registration and leave behavioral reachability to GS2-03.5.
- DEC-002 [CQ-002] [AMB:AMB-002]: CQ-002: Round-trip canonical schema descriptors and exact ordered fields; do not invent domain sample values.
- DEC-003 [CQ-003] [AMB:AMB-003]: CQ-003: Create one projection-freshness case for every compiled-output manifest entry, even when typed content also supplies command, mutation, permission, or schema cases.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 86-gs2-03-3-generated-structural-tests`.
