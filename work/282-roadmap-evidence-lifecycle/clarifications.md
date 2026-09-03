---
schemaVersion: 1
workId: 282-roadmap-evidence-lifecycle
title: Harden roadmap evidence and receipt lifecycle
stage: clarify
changeTier: tier1
status: needsAnswers
sourceSpec: work/282-roadmap-evidence-lifecycle/spec.md
publicOrToolFacingImpact: true
---

# Harden roadmap evidence and receipt lifecycle Clarifications

## Source Specification
- work/282-roadmap-evidence-lifecycle/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.

## Answers
- CQ-001 → Use the existing owning issue for both implementation and receipt PRs. Rotate the claim generation through a read-back-verified markerless `In progress` reservation so the second PR gets a fresh merge fence without making the item schedulable; post-merge facts do not justify a second scheduling row.
- CQ-002 → No. Only a GitHub-hosted exact-head run with a retained typed artifact can satisfy a hosted evidence field.
- CQ-003 → No. Require two consecutive runs so the second proves the first did not leave a stale generated projection or local mutation.

## Decisions
- **DEC-001** [CQ-001] [AMB:AMB-001] [FR-005] [FR-006] [AC-003]: Keep one owning issue open and continuously reserved through the implementation merge and receipt PR. Release with `Status=In progress`, verify that markerless reservation, and immediately reacquire to mint the receipt phase's claim generation; stamp Done once after the receipt lands.
- **DEC-002** [CQ-002] [AMB:AMB-002] [FR-004] [AC-002]: Reserve `hosted` for an actual exact-head hosted run and retained typed decision artifact.
- **DEC-003** [CQ-003] [AMB:AMB-003] [FR-003] [AC-001]: Require two consecutive canonical coherent no-change verification runs from the isolated candidate.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
- None. AMB-001, AMB-002, and AMB-003 are resolved by DEC-001 through DEC-003.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 282-roadmap-evidence-lifecycle`.
