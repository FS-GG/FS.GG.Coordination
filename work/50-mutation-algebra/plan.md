---
schemaVersion: 1
workId: 50-mutation-algebra
title: Mutation Algebra
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/50-mutation-algebra/spec.md
sourceClarifications: work/50-mutation-algebra/clarifications.md
sourceChecklist: work/50-mutation-algebra/checklist.md
publicOrToolFacingImpact: true
---

# Mutation Algebra Plan

Prose status: planned

## Source Snapshot
- spec: work/50-mutation-algebra/spec.md sha256:ab2fbe2f90b01138ae841e5230381f105c736d5f3e6cd748cf90bbcf7d9bdadf schemaVersion:1
- clarifications: work/50-mutation-algebra/clarifications.md sha256:00490ff7d0b4f13b7532c0d0a1298b2b773b710d89301a5c202842efe22390dd schemaVersion:1
- checklist: work/50-mutation-algebra/checklist.md sha256:6283887d56a8a8159b61e6616f10a6cf26e6762ff0862c8d61de0904cd4fa533 schemaVersion:1

## Plan Scope
- Work item 50-mutation-algebra is planned from the current specification, clarification, and checklist facts.
- Requirement count: 5.
- Clarification decision count: 0.
- Checklist result count: 5.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add exactly eight mutation kinds—create, append, add-edge, remove-edge, set, clear, transition, and compensate—with closed payload-kind compatibility and one stable target subject per intent.
- PD-002 [AC-001] [FR-002] complete: Bind operation id, subject, kind-defined target and payload shapes, expected revision, idempotency key, mutation kind, and canonical payload digest. Exact replay of an applied key-and-intent returns terminal `MOUT-Idempotent` with the original resulting revision and no state change; any bound-field substitution is a conflict.
- PD-003 [AC-001] [FR-003] complete: Classify applied, idempotent replay, rejected, and revision conflict as terminal. Classify rate-limited, unavailable, timed-out, and incomplete observation as uncertain; uncertainty preserves the same operation and idempotency identity and permits only observation or exact replay.
- PD-004 [AC-001] [FR-004] complete: A compensation intent names one terminal applied non-compensation operation and its resulting revision. Reject self-reference, compensation-of-compensation, unknown or uncertain predecessors, wrong resulting revision, and duplicate compensation under a different key.
- PD-005 [AC-001] [FR-005] complete: Extend profile 2 additively after accepted GS2-02.6 receipt `e544b127125d1e4a175c6f5bcc130038a2aa908513b8d90473fea903be02bdd1`; preserve prior catalogues and stop before ordered durable plans or any external writer.

## Contract Impact
- PC-001 [PD-001] [PD-002] protocol: `Protocol.md` remains the sole authored semantic source; generated Quint, contract, F# bindings, source map, receipt, and typed-authority view remain deterministic profile-2 outputs.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PC-001] semanticTest: Run deterministic regeneration, Quint typecheck and authored tests, bounded simulation, mutation/idempotency/outcome/compensation invariants, independent binding-removal mutants, repository suites, and exact roadmap gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Preserve accepted GS2-02.1–02.6 identities and frozen profile 1; mutation algebra is an additive profile-2 refinement with no external writer or durable-plan executor.

## Generated View Impact
- GV-001 [PD-001] [PD-005] protocolViews: Refresh only deterministic profile-2 Quint, compiled contract, F# bindings, source map, receipt, typed authority, unit index, roadmap-work evidence, and SDD readiness projections from current authored sources.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 50-mutation-algebra`.
