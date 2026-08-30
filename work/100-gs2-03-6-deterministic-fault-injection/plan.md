---
schemaVersion: 1
workId: 100-gs2-03-6-deterministic-fault-injection
title: GS2-03.6 deterministic fault injection
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/100-gs2-03-6-deterministic-fault-injection/spec.md
sourceClarifications: work/100-gs2-03-6-deterministic-fault-injection/clarifications.md
sourceChecklist: work/100-gs2-03-6-deterministic-fault-injection/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.6 deterministic fault injection Plan

Prose status: planned

## Source Snapshot
- spec: work/100-gs2-03-6-deterministic-fault-injection/spec.md sha256:2b3ab7aaf2d0773e385e282794b8a5a66324ed7e31639436bc183f1d84074d0a schemaVersion:1
- clarifications: work/100-gs2-03-6-deterministic-fault-injection/clarifications.md sha256:0182e07f4e85587b60acc5dd243f15ff9f7a65d7783654347c2a73591e8d26d4 schemaVersion:1
- checklist: work/100-gs2-03-6-deterministic-fault-injection/checklist.md sha256:a39602687e14cf3e1b776d72c348da94237df5b155da0e4a52bb885ae51bf6b1 schemaVersion:1

## Plan Scope
- Work item 100-gs2-03-6-deterministic-fault-injection is planned from the current specification, clarification, and checklist facts.
- Requirement count: 7.
- Clarification decision count: 0.
- Checklist result count: 7.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Define the modeled external-step inventory from the accepted protocol's registered actions and mutation/revision metadata, then generate both before-step and after-step injections for every inventory row.
- PD-002 [AC-001] [FR-002] complete: Use one deterministic scheduler to execute lost-response, duplicate-event, reordered-event, partial-page, rate-budget, permission-revocation, and concurrent-revision scenarios; scenario data varies inputs, not behavior.
- PD-003 [AC-001] [FR-003] complete: Represent terminal results as a closed outcome union whose converged branch carries the final canonical state digest and whose refused branch carries an exact stable refusal code and unchanged-authority digest.
- PD-004 [AC-001] [FR-004] complete: Generate a canonical JSON fault matrix under evidence/github-substrate-v2, bind it to accepted protocol artifacts and its own self digest, index its exact bytes, and reject omissions, substitutions, reordering, truncation, forged outcomes, and stale source identities.
- PD-005 [AC-001] [FR-005] complete: Derive external-step and permission/revision inputs from the compiled protocol outputs already qualified by GS2-03.3; the harness may interpret those inputs but must not define an independent transition system.
- PD-006 [AC-001] [FR-006] complete: Keep all execution in FS.GG.Coordination.Qualification.Contracts using in-memory fixtures and filesystem reads beneath the repository root; introduce no HTTP/client dependency or runtime adapter surface.
- PD-007 [AC-001] [FR-007] complete: Exercise the committed artifact through ArchitectureTests and the existing evidence-storage gate, then bind the clean candidate to roadmap manifest/gates, hosted Bootstrap CI, independent review, protected merge, and exact-main verification.

## Contract Impact
- PC-001 [PD-001] qualification contract: Add a public F# qualification module with deterministic generate, validate, and check entry points plus a versioned canonical JSON schema; no runtime or network-facing API changes.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Architecture tests must prove complete two-sided step coverage, every named failure class, exact convergence/refusal outcomes, deterministic regeneration, source binding, and independent red controls; the evidence-storage validator must accept the indexed exact bytes.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Introduce schema fsgg.coordination.fault-injection/1 as a new retained qualification artifact; existing evidence and protocol schemas remain byte-compatible and unsupported versions fail before interpretation.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh the SDD work model and both generated agent projections after the authored plan changes; generated views must bind the current source digests before implementationReady is accepted.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 100-gs2-03-6-deterministic-fault-injection`.
