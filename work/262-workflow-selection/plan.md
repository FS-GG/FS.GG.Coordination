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
- spec: work/262-workflow-selection/spec.md sha256:e983eeb21cf45b26664efdbaba315050b16dfe0accb73e137b9ca354778784c5 schemaVersion:1
- clarifications: work/262-workflow-selection/clarifications.md sha256:16228bfda41ce11da655a300e1e0a88c4b86f4daca6429e88f5b118df6519a2d schemaVersion:1
- checklist: work/262-workflow-selection/checklist.md sha256:38615def91fab8a18a30caf0a9f27e355f7ae7673d24f323ee275a8b6cfd001e schemaVersion:1

## Plan Scope
- Work item 262-workflow-selection is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 3.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a signature-first pure Core selector and CLI whose sealed inventory binds actual repository-owned workflow, policy-job, composite-step, reusable-job, aggregate, graph, merge-group, sentinel, fleet-disable, and deletion-ledger contracts.
- PD-002 [AC-002] [FR-002] complete: Represent rules, obligations, graph edges, reviewed content authority, and runtime authority as closed contracts; validate exact inventory/source-request/settings bytes and semantics, derive exact checkout identity from Git/GITHUB_SHA without a commit-distance limit, derive merge-group paths and queued head/base from the live event, then compute reachability from arbitrary paths/non-file inputs, union unconditional obligations, and verify minimality.
- PD-003 [AC-003] [FR-003] complete: Materialize one typed child outcome per obligation independently from provisioning, so stable aggregates always resolve while NotApplicable expensive children provision nothing.
- PD-004 [AC-004] [FR-004] complete: Retain exact GitHub Actions run/job observations for every repository and make Q7 independently reproduce baseline metrics, provenance completeness/freshness/uniqueness/variation, reviewed targets, and missed-obligation count.
- PD-005 [AC-005] [FR-005] complete: Compare scheduled full-suite sentinel failures against the selected closure and derive a fleet-wide disabled decision whenever the difference is non-empty.
- PD-006 [AC-006] [FR-006] complete: Retain generated mutations and separately named independent fixtures for every control, including multiple unrelated protected advances, relevant content drift, stale paired evidence, forged GITHUB_SHA, stale settings, queued-head/base mismatch, and missing/ambiguous authority; reject unknown JSON properties, retain a disabled typed decision on refusal, prove exact replay and unchanged canonical Quint bytes, and expose no mutation effect.

## Contract Impact
- PC-001 [PD-001] [PD-002] public API: Add `WorkflowSelection.fsi` in Core and `workflow-select` in the CLI, including a strict `workflow-selection-authority/1` input mutually exclusive with legacy authority flags, while extending the qualification signature with observation/deletion provenance.
- PC-002 [PD-002] [PD-003] Q3 gate: Implement `github-workflow-selection-contract` through `eng/validate-github-workflow-selection.fsx` over the retained corpus and independent expectations.
- PC-003 [PD-004] [PD-005] Q7 gate: Implement `github-workflow-selection-supply-chain-contract` through a distinct validator that reuses the sealed report but independently evaluates performance, sentinel, fleet-disable, and deletion-ledger controls.
- PC-004 [PD-006] retained evidence: Add versioned `corpus.json`, `observed-workflow-runs.json`, `deletion-ledger.json`, runtime inventory/request fixtures, independent expectations, and operator-readable evidence without changing canonical Quint.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Prove the valid baseline, exact checkout and reviewed content/settings authority, three unrelated protected advances, stale-paired and relevant-drift refusal, smallest transitive/unconditional closure, stable aggregate resolution, typed NotApplicable/no-provisioning behavior, live merge-group recomputation, and all fail-closed impact variants.
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
