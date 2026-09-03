---
schemaVersion: 1
workId: 262-workflow-selection
title: Workflow Selection
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/262-workflow-selection/spec.md
sourceClarifications: work/262-workflow-selection/clarifications.md
sourceChecklist: work/262-workflow-selection/checklist.md
publicOrToolFacingImpact: true
---

# Workflow Selection Plan

Prose status: planned

## Source Snapshot
- spec: work/262-workflow-selection/spec.md sha256:83bfc6a2527df233367a2fffd91909372781d84137d53fdcf89318ea825c407d schemaVersion:1
- clarifications: work/262-workflow-selection/clarifications.md sha256:24fe399bd0532b1c516b422bc26da43a23f188f966328f398588758537d1ea70 schemaVersion:1
- checklist: work/262-workflow-selection/checklist.md sha256:94088f1ba035d0cb50503d29a1918bda136cd0dbe92761670dbd4d9998b06fd6 schemaVersion:1

## Plan Scope
- Work item 262-workflow-selection is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 3.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a signature-first pure compiler whose snapshot binds typed workflow, policy-job, composite-step, reusable-job, aggregate, graph, impact, merge-group, performance, sentinel, fleet-disable, and deletion-ledger facts in one length-framed seal.
- PD-002 [AC-002] [FR-002] complete: Represent obligations and graph edges as closed discriminated unions; compute reachability from the complete combined root set, then union unconditional obligations and verify minimality against the graph.
- PD-003 [AC-003] [FR-003] complete: Materialize one typed child outcome per obligation independently from provisioning, so stable aggregates always resolve while NotApplicable expensive children provision nothing.
- PD-004 [AC-004] [FR-004] complete: Keep measured baseline and accepted target values as exact per-repository sealed inputs and have a distinct Q7 projection validate every metric and missed-obligation count.
- PD-005 [AC-005] [FR-005] complete: Compare scheduled full-suite sentinel failures against the selected closure and derive a fleet-wide disabled decision whenever the difference is non-empty.
- PD-006 [AC-006] [FR-006] complete: Retain generated mutations and separately named independent fixtures for every control, reject unknown JSON properties, prove exact replay and unchanged canonical Quint bytes, and expose no mutation effect.

## Contract Impact
- PC-001 [PD-001] public API: Add `GitHubWorkflowSelectionQualification.fsi` with closed inventory, graph, impact, outcome, target, sentinel, report, finding, control, compile, verify, and validation contracts.
- PC-002 [PD-002] [PD-003] Q3 gate: Implement `github-workflow-selection-contract` through `eng/validate-github-workflow-selection.fsx` over the retained corpus and independent expectations.
- PC-003 [PD-004] [PD-005] Q7 gate: Implement `github-workflow-selection-supply-chain-contract` through a distinct validator that reuses the sealed report but independently evaluates performance, sentinel, fleet-disable, and deletion-ledger controls.
- PC-004 [PD-006] retained evidence: Add versioned `corpus.json`, `independent-expectations.json`, and an operator-readable evidence contract without changing the canonical Quint protocol.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Prove the valid baseline, smallest transitive/unconditional closure, stable aggregate resolution, typed NotApplicable/no-provisioning behavior, merge-group recomputation, and all fail-closed impact variants.
- VO-002 [PD-004] [PD-005] [PC-003] supplyChainTest: Prove every baseline-to-target comparison, scheduled sentinel agreement, missed-obligation detection, deterministic fleet disable, and complete deletion ledger through the distinct Q7 validator.
- VO-003 [PD-006] [PC-002] [PC-004] architectureTest: Prove both catalog/index command bindings, exact independent-case inventory, unknown-property rejection, canonical Quint preservation, and absence of mutation or successor surfaces.
- VO-004 [PD-006] [PC-004] integrationTest: Run a warning-as-error Release build, focused and full unit/architecture suites, retained evidence self-tests, and both roadmap gates from one clean committed candidate.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The public contract, corpus schema, and two validators are additive qualification surfaces; no existing consumer or production workflow is changed.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh normalized SDD sources and commit the compact ship verdict; retain the provider inputs needed for clean-checkout regeneration rather than depending on ignored local outputs.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 262-workflow-selection`.
