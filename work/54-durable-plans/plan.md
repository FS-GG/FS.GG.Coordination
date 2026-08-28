---
schemaVersion: 1
workId: 54-durable-plans
title: Durable Plans
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/54-durable-plans/spec.md
sourceClarifications: work/54-durable-plans/clarifications.md
sourceChecklist: work/54-durable-plans/checklist.md
publicOrToolFacingImpact: true
---

# Durable Plans Plan

Prose status: planned

## Source Snapshot
- spec: work/54-durable-plans/spec.md sha256:eade1d45446b1351e53e691c42398685a7dbfddea4e94c24eff1f963450017b8 schemaVersion:1
- clarifications: work/54-durable-plans/clarifications.md sha256:31c2317206496f128366490e912d31db0d3bffd29ab20822af28d136d5474bde schemaVersion:1
- checklist: work/54-durable-plans/checklist.md sha256:2241f6c5907462766af59c624df873539d935362ff7fe2d89750f0dff07cfda5 schemaVersion:1

## Plan Scope
- Work item 54-durable-plans is planned from the current specification, clarification, and checklist facts.
- Requirement count: 5.
- Clarification decision count: 0.
- Checklist result count: 5.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a durable step record that binds one plan and step identity, a positive sequence, its exact predecessor, the compiled mutation intent, stable causation and correlation identities, and one explicit compensation boundary. A plan is linear in this unit: the first step has no predecessor and every later step names the immediately preceding step.
- PD-002 [AC-001] [FR-002] complete: Bind a durable checkpoint to the exact step and mutation result. Applied or idempotent terminal receipts authorize the next ordered step; uncertain receipts classify as receipt re-read and authorize no advancement, identity substitution, or assumed effect.
- PD-003 [AC-001] [FR-003] complete: Compensation may target only an applied non-compensation step in the same explicit boundary and must proceed in reverse sequence. Reject cross-boundary, forward-order, duplicate, unknown, terminal-no-op, or uncertain predecessors.
- PD-004 [AC-001] [FR-004] complete: Define a closed four-way disposition catalogue: advance for terminal applied/idempotent receipts, receipt-reread for uncertain receipts, compensate for terminal refusal after an applied step in the same boundary, and replan for terminal refusal without such a predecessor. Exact record bindings make every substitution fail closed.
- PD-005 [AC-001] [FR-005] complete: Extend profile 2 additively after accepted GS2-02.7 receipt `f6e976306e89c8e84d62748ae8428963e64039850877a5741865c45e6b3f67f2`; preserve prior catalogues, update the roadmap pin to `6333a5178873cbe84b18c36cac963d01df6dc76e`, and stop before desired-state specifications or any external writer.

## Contract Impact
- PC-001 [PD-001] [PD-002] protocol: `Protocol.md` remains the sole authored semantic source; generated Quint, compiled contract, F# bindings, source map, receipt, and typed-authority view remain deterministic profile-2 outputs.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PC-001] semanticTest: Run deterministic regeneration, Quint typecheck and authored tests, bounded simulation, independent ordering/identity/receipt/boundary/disposition mutants, repository suites, and exact Q1/Q2 roadmap gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Preserve accepted GS2-02.1–02.7 identities and frozen profile 1; durable-plan algebra is an additive profile-2 refinement with no executor or external writer.

## Generated View Impact
- GV-001 [PD-001] [PD-005] protocolViews: Refresh only deterministic profile-2 Quint, compiled contract, F# bindings, source map, receipt, typed authority, roadmap-work evidence, and SDD readiness projections from current authored sources.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 54-durable-plans`.
