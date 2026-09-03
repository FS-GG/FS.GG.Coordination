---
schemaVersion: 1
workId: 282-roadmap-evidence-lifecycle
title: Roadmap Evidence Lifecycle
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/282-roadmap-evidence-lifecycle/spec.md
sourceClarifications: work/282-roadmap-evidence-lifecycle/clarifications.md
sourceChecklist: work/282-roadmap-evidence-lifecycle/checklist.md
publicOrToolFacingImpact: true
---

# Roadmap Evidence Lifecycle Plan

Prose status: planned

## Source Snapshot
- spec: work/282-roadmap-evidence-lifecycle/spec.md sha256:4f02983f07955e0014aadfbfd2e6badb07924b9a681335ed8dc78de462012c9f schemaVersion:1
- clarifications: work/282-roadmap-evidence-lifecycle/clarifications.md sha256:77e8adf77ecb2121c77b49dd46e1ddc9dd7e11d7934881fdcf6920cbd526f110 schemaVersion:1
- checklist: work/282-roadmap-evidence-lifecycle/checklist.md sha256:546f27283ccfd613596ad5c3350033465d5928d776cfda63d172f31dbae4c83b schemaVersion:1

## Plan Scope
- Work item 282-roadmap-evidence-lifecycle is planned from the current specification, clarification, and checklist facts.
- Requirement count: 8.
- Clarification decision count: 3.
- Checklist result count: 8.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a pre-acceptance section that requires an isolated exact-candidate Git checkout and forbids ignored or untracked carry-over.
- PD-002 [AC-001] [FR-002] complete: Require every provider artifact to pass tracked-file and declared-digest checks before canonical verification.
- PD-003 [AC-001] [FR-003] complete: Resolve the canonical tool version from the provider contract and run verification twice, requiring coherent no-change results and a clean tree after each run.
- PD-004 [AC-002] [FR-004] complete: Define hosted evidence as an exact-head hosted run plus retained typed decision; classify local and detached executions as supporting-only.
- PD-005 [AC-003] [FR-005] complete: Define a two-PR lifecycle under one continuously reserved issue, with a release-to-`In progress`, verified markerless reservation, and immediate reclaim to mint the receipt phase's claim generation.
- PD-006 [AC-003] [FR-006] complete: Forbid receipt-only board items and delay the one Done stamp until the receipt PR and protected verification land.
- PD-007 [AC-004] [FR-007] complete: Require the four-leg descendant matrix for authority intended to survive ordinary protected advancement.
- PD-008 [AC-005] [FR-008] complete: Extend the validator token contract and architecture mutation table so deletion of any new clause fails independently.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PD-008] agentWorkflow: `.agents/skills/github-substrate-v2-work/SKILL.md` gains mandatory evidence and multi-phase completion rules; no CLI or runtime wire format changes.
- PC-002 [PD-008] validationContract: `eng/validate-roadmap-work-skill.fsx` requires stable clause tokens, and `RoadmapWorkArchitectureTests.fs` proves every token's absence is red.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PC-001] semanticTest: Run the skill validator against the complete skill and require a green result.
- VO-002 [PD-008] [PC-002] mutationTest: Delete each required clause token independently and require the validator to return nonzero with the missing token named.
- VO-003 [PD-008] [PC-002] regressionTest: Run focused RoadmapWork architecture tests, full unit and architecture suites, and warning-free Release build.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Existing roadmap units and receipts remain readable; the stricter clauses govern future acceptance attempts without rewriting historical evidence.

## Generated View Impact
- GV-001 [PD-001] none: No runtime-generated view changes; only this work item's ordinary SDD readiness projections are regenerated.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 282-roadmap-evidence-lifecycle`.
