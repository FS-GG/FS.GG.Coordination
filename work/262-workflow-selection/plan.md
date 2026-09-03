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
- spec: work/262-workflow-selection/spec.md sha256:afdb3914311c6063287157f22701170123a71e99cdb6b9cb5e84c714697eaa54 schemaVersion:1
- clarifications: work/262-workflow-selection/clarifications.md sha256:291569128c0dee268b5be0deecb509c313e7665f7dc1184fc3607d58dc0a1877 schemaVersion:1
- checklist: work/262-workflow-selection/checklist.md sha256:a389fa2f98d438b0ba3e0850c564dd0c6350d0ee7c25b02d6480432607be007b schemaVersion:1

## Plan Scope
- Work item 262-workflow-selection is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 3.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a signature-first pure Core selector and CLI whose sealed inventory binds actual repository-owned workflow, policy-job, composite-step, reusable-job, aggregate, graph, merge-group, sentinel, fleet-disable, and deletion-ledger contracts.
- PD-002 [AC-002] [FR-002] complete: Represent rules, obligations, graph edges, and the runtime authority document as closed contracts; derive exact checkout identity from Git/GITHUB_SHA, prove the sealed retained base is its direct ancestor, derive settings identity from the reviewed repository-settings receipt, then derive roots from arbitrary paths/non-file inputs, validate base/current/settings/queued-head authority, compute reachability from the combined roots, union unconditional obligations, and verify minimality.
- PD-003 [AC-003] [FR-003] complete: Materialize one typed child outcome per obligation independently from provisioning, so stable aggregates always resolve while NotApplicable expensive children provision nothing.
- PD-004 [AC-004] [FR-004] complete: Retain exact GitHub Actions run/job observations for every repository and make Q7 independently reproduce baseline metrics, provenance completeness/freshness/uniqueness/variation, reviewed targets, and missed-obligation count.
- PD-005 [AC-005] [FR-005] complete: Compare scheduled full-suite sentinel failures against the selected closure and derive a fleet-wide disabled decision whenever the difference is non-empty.
- PD-006 [AC-006] [FR-006] complete: Retain generated mutations and separately named independent fixtures for every control, including protected-head advancement and stale paired evidence; reject unknown JSON properties and unavailable or mismatched authority, prove exact replay and unchanged canonical Quint bytes, and expose no mutation effect.

## Contract Impact
- PC-001 [PD-001] [PD-002] public API: Add `WorkflowSelection.fsi` in Core and `workflow-select` in the CLI, including a strict `workflow-selection-authority/1` input mutually exclusive with legacy authority flags, while extending the qualification signature with observation/deletion provenance.
- PC-002 [PD-002] [PD-003] Q3 gate: Implement `github-workflow-selection-contract` through `eng/validate-github-workflow-selection.fsx` over the retained corpus and independent expectations.
- PC-003 [PD-004] [PD-005] Q7 gate: Implement `github-workflow-selection-supply-chain-contract` through a distinct validator that reuses the sealed report but independently evaluates performance, sentinel, fleet-disable, and deletion-ledger controls.
- PC-004 [PD-006] retained evidence: Add versioned `corpus.json`, `observed-workflow-runs.json`, `deletion-ledger.json`, runtime inventory/request fixtures, independent expectations, and operator-readable evidence without changing canonical Quint.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Prove the valid baseline, exact checkout and reviewed settings authority, protected-head advancement and stale-paired refusal, smallest transitive/unconditional closure, stable aggregate resolution, typed NotApplicable/no-provisioning behavior, merge-group recomputation, and all fail-closed impact variants.
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
