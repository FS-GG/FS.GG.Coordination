---
schemaVersion: 1
workId: 262-workflow-selection
title: Workflow Selection
stage: clarify
changeTier: tier1
status: needsAnswers
sourceSpec: work/262-workflow-selection/spec.md
publicOrToolFacingImpact: true
---

# Workflow Selection Clarifications

## Source Specification
- work/262-workflow-selection/spec.md

## Clarification Questions
- **CQ-001** (AMB-001): Does an unselected expensive child disappear, or remain as an explicit aggregate input?
- **CQ-002** (AMB-002): Does this unit execute fleet optimization and disablement in production?
- **CQ-003** (AMB-003): Can a mixed change reuse the union of independently computed closures?

## Answers
- CQ-001 → It remains as a typed NotApplicable outcome consumed by stable aggregates, while its expensive job is never provisioned.
- CQ-002 → No. Q7 qualifies retained measurements, accepted targets, sentinel comparisons, and deterministic fleet-disable decisions without applying fleet mutations.
- CQ-003 → No. Mixed changes are classified together and the full transitive plus unconditional closure is recomputed from the combined roots.

## Decisions
- **DEC-001** [CQ-001] [AMB:AMB-001] [FR-003] [AC-003]: Separate child outcome materialization from expensive job provisioning; every required aggregate consumes either Selected or NotApplicable.
- **DEC-002** [CQ-002] [AMB:AMB-002] [FR-004] [FR-005] [AC-004] [AC-005]: Keep GS2-06.7 qualification-only; baseline, target, sentinel, and fleet-disable facts are pure sealed inputs and outputs.
- **DEC-003** [CQ-003] [AMB:AMB-003] [FR-002] [AC-002]: Recompute one closure from the complete combined root set so mixed changes cannot omit shared or newly reachable obligations.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
- None. AMB-001, AMB-002, and AMB-003 are resolved above.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 262-workflow-selection`.
