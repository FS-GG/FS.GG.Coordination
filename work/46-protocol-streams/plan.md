---
schemaVersion: 1
workId: 46-protocol-streams
title: Protocol Streams
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/46-protocol-streams/spec.md
sourceClarifications: work/46-protocol-streams/clarifications.md
sourceChecklist: work/46-protocol-streams/checklist.md
publicOrToolFacingImpact: true
---

# Protocol Streams Plan

Prose status: planned

## Source Snapshot
- spec: work/46-protocol-streams/spec.md sha256:4dc6380bd311958b9956f94bf44c9aab8c333fdf238b0b53b3d9aaa41050e984 schemaVersion:1
- clarifications: work/46-protocol-streams/clarifications.md sha256:c2c1a1dea7e955d724d2ac43589590b8cf3ec389ef8941fb00df7efbc71deaa6 schemaVersion:1
- checklist: work/46-protocol-streams/checklist.md sha256:08caac8a5101c272e487492119fc9c018eeee5c82d4cf0b433a661224445659c schemaVersion:1

## Plan Scope
- Work item 46-protocol-streams is planned from the current specification, clarification, and checklist facts.
- Requirement count: 4.
- Clarification decision count: 0.
- Checklist result count: 4.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a closed catalogue for claim, lease, touch-set, operation-lock, election, review, delivery, and operation-receipt streams; each payload kind belongs to exactly one stream kind.
- PD-002 [AC-001] [FR-002] complete: Give every envelope stable stream, subject, generation, sequence, event, predecessor, payload-kind, retention, and checkpoint fields. Sequence one has no predecessor; every later event binds the immediately preceding event in the same stream and generation.
- PD-003 [AC-001] [FR-003] complete: Treat claim/lease/touch-set and operation-lock liveness events as ephemeral. Treat winning elections, accepted exact-head reviews, delivery completions, and operation receipts as durable checkpoints; compaction may remove superseded ephemeral events only after their decision is checkpointed or terminal.
- PD-004 [AC-001] [FR-004] complete: Extend profile 2 additively after accepted GS2-02.5 receipt `f022ebd0ac97f5a8c216ed6bc70854cf4de0e05515cb4fa4e18075f1efb7fa8b`; keep authority, observation, lifecycle, and relation identities unchanged and stop before generalized mutation semantics.

## Contract Impact
- PC-001 [PD-001] [PD-002] protocol: `Protocol.md` remains the sole authored semantic source; generated Quint, contract, F# bindings, source map, receipt, and typed-authority view remain deterministic profile-2 outputs.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] semanticTest: Run deterministic regeneration, Quint typecheck and authored tests, bounded simulation, Apalache ordering/retention invariants, invalid-envelope/gap/cross-stream/retention negative controls, repository suites, and exact roadmap gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Preserve accepted GS2-02.1–02.5 identities and frozen profile 1; protocol streams are an additive profile-2 refinement with no external writer or generalized mutation command.

## Generated View Impact
- GV-001 [PD-001] [PD-004] protocolViews: Refresh only deterministic profile-2 Quint, compiled contract, F# bindings, source map, receipt, typed authority, unit index, and SDD readiness projections from current authored sources.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 46-protocol-streams`.
