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
- spec: work/262-workflow-selection/spec.md sha256:78fe582ec4f8dbd965b67220c6eead07958ae82f6f74a582646586c5ceb0cfdf schemaVersion:1
- clarifications: work/262-workflow-selection/clarifications.md sha256:911b939ded6b360d0b29f7a38c4ca215d60fdd176eaa6966ebd0566b1b04838b schemaVersion:1
- checklist: work/262-workflow-selection/checklist.md sha256:8b791ff54faefbaa294acbba1a7eec95668ca48a3a06a4e10867f25f73241425 schemaVersion:1

## Plan Scope
- Work item 262-workflow-selection is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 3.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a signature-first pure Core selector and CLI whose sealed inventory binds actual repository-owned workflow, policy-job, composite-step, reusable-job, aggregate, graph, merge-group, sentinel, fleet-disable, and deletion-ledger contracts.
- PD-002 [AC-002] [FR-002] complete: Represent rules, obligations, and graph edges as closed discriminated unions; derive roots from arbitrary paths/non-file inputs, validate current base/settings, compute reachability from the combined roots, then union unconditional obligations and verify minimality.
- PD-003 [AC-003] [FR-003] complete: Materialize one typed child outcome per obligation independently from provisioning, so stable aggregates always resolve while NotApplicable expensive children provision nothing.
- PD-004 [AC-004] [FR-004] complete: Retain exact GitHub Actions run/job observations for every repository and make Q7 independently reproduce baseline metrics, provenance completeness/freshness/uniqueness/variation, reviewed targets, and missed-obligation count.
- PD-005 [AC-005] [FR-005] complete: Compare scheduled full-suite sentinel failures against the selected closure and derive a fleet-wide disabled decision whenever the difference is non-empty.
- PD-006 [AC-006] [FR-006] complete: Retain generated mutations and separately named independent fixtures for every control, reject unknown JSON properties, prove exact replay and unchanged canonical Quint bytes, and expose no mutation effect.

## Contract Impact
- PC-001 [PD-001] public API: Add `WorkflowSelection.fsi` in Core and `workflow-select` in the CLI, while extending the qualification signature with observation/deletion provenance.
- PC-002 [PD-002] [PD-003] Q3 gate: Implement `github-workflow-selection-contract` through `eng/validate-github-workflow-selection.fsx` over the retained corpus and independent expectations.
- PC-003 [PD-004] [PD-005] Q7 gate: Implement `github-workflow-selection-supply-chain-contract` through a distinct validator that reuses the sealed report but independently evaluates performance, sentinel, fleet-disable, and deletion-ledger controls.
- PC-004 [PD-006] retained evidence: Add versioned `corpus.json`, `observed-workflow-runs.json`, `deletion-ledger.json`, runtime inventory/request fixtures, independent expectations, and operator-readable evidence without changing canonical Quint.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Prove the valid baseline, smallest transitive/unconditional closure, stable aggregate resolution, typed NotApplicable/no-provisioning behavior, merge-group recomputation, and all fail-closed impact variants.
- VO-002 [PD-004] [PD-005] [PC-003] supplyChainTest: Prove every baseline-to-target comparison, scheduled sentinel agreement, missed-obligation detection, deterministic fleet disable, and complete deletion ledger through the distinct Q7 validator.
- VO-003 [PD-006] [PC-002] [PC-004] architectureTest: Prove both catalog/index command bindings, exact independent-case inventory, unknown-property rejection, canonical Quint preservation, and absence of mutation or successor surfaces.
- VO-004 [PD-006] [PC-004] integrationTest: Run a warning-as-error Release build, focused and full unit/architecture suites, retained evidence self-tests, and both roadmap gates from one clean committed candidate.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The public contract, CLI, corpus schema, reusable/composite/sentinel workflows, and validators are additive in Coordination; no consumer repository or fleet setting is changed and no package is published.

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
