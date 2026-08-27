---
schemaVersion: 1
workId: 42-native-relation-algebra
title: Native Relation Algebra
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/42-native-relation-algebra/spec.md
sourceClarifications: work/42-native-relation-algebra/clarifications.md
sourceChecklist: work/42-native-relation-algebra/checklist.md
publicOrToolFacingImpact: true
---

# Native Relation Algebra Plan

Prose status: planned

## Source Snapshot
- spec: work/42-native-relation-algebra/spec.md sha256:1bd8f7898c6f25ba73d05a8c01a025f2d02511ea647ed75e9773008dc33d1258 schemaVersion:1
- clarifications: work/42-native-relation-algebra/clarifications.md sha256:7d91eabe1c08651d2a23bca217f3500bada8375722096188545ecceca27d8118 schemaVersion:1
- checklist: work/42-native-relation-algebra/checklist.md sha256:42544957e2c9c68bec146a5e2ad1a172e8cdf0f3023d804b2736f7d9e6101d1e schemaVersion:1

## Plan Scope
- Work item 42-native-relation-algebra is planned from the current specification, clarification, and checklist facts.
- Requirement count: 5.
- Clarification decision count: 0.
- Checklist result count: 5.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add stable `REL-ParentChild` and `REL-Blocks` identities plus directed source/target edge values; direction and kind are part of edge equality.
- PD-002 [AC-001] [FR-002] complete: Model relation state as a set and expose only idempotent add-edge and remove-edge intent; duplicate add and absent remove converge without a whole-set replacement operation.
- PD-003 [AC-001] [FR-003] complete: Reject self-edges, preserve every unrelated edge under add/remove, and prove kind and endpoint non-interchangeability with authored tests and bounded invariants.
- PD-004 [AC-001] [FR-004] complete: Keep lifecycle intent/status unchanged by relation actions and derive relation knowledge only from GS2-02.3 `Observed` or `ProvenAbsent` outcomes.
- PD-005 [AC-001] [FR-005] complete: Register GS2-02.5 after accepted receipt `57ee29e235f248d01a75f03f1f7645c984beb0da15f54f7b8e830534546a3e23`, regenerate profile-2 projections, pass exact Q1/pure-Q2 and scalar-collapse/unrelated-edge-loss controls, and stop before protocol streams.

## Contract Impact
- PC-001 [PD-001] [PD-002] protocol: `Protocol.md` remains the sole authored semantic source; generated Quint, contract, bindings, source map, receipt, and F# projection remain deterministic profile-2 outputs.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] semanticTest: Run deterministic regeneration, Quint typecheck and all authored tests, bounded simulation, Apalache set-preservation invariants, scalar-replacement/kind-collapse/unrelated-edge-loss negative controls, repository suites, and exact roadmap gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Preserve accepted GS2-02.1–02.4 identities and frozen profile 1; relation algebra is an additive profile-2 refinement with no external writer or protocol-stream envelope.

## Generated View Impact
- GV-001 [PD-001] [PD-005] protocolViews: Refresh only deterministic profile-2 Quint, compiled contract, F# bindings, source map, receipt, typed authority, and SDD readiness projections from current authored sources.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 42-native-relation-algebra`.
