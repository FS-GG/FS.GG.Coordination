---
schemaVersion: 1
workId: 115-gs2-03-8-critique-evidence
title: GS2-03.8 critique evidence gates
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.8 critique evidence gates Specification

Prose status: specified

## User Value
trust one exact coordination candidate only when five typed critique perspectives agree over the same candidate and evidence, without introducing another authorization dependency

## Scope
- SB-001: GS2-03.8 typed critique findings, exact candidate/evidence binding, distinct phase identities, deterministic roll-up, executable versioned review schema, validation, inversion evidence, and accountable acceptance only

## Non-Goals
- SB-002: No reviewer quorum, second person/account/agent authorization, native GitHub approval rule, external separation-of-duties control, live authority mutation, publication, deployment, production write, runtime review/delivery interpreter, or GS2-03.9 implementation.
- SB-003: No change to the accepted qualification-manifest/1 bytes or reinterpretation of previously accepted evidence.

## User Stories
- US-001 (P1): As the Accountable Delivery Owner, I can inspect a canonical critique bundle and know that all required perspectives evaluated the same exact candidate and evidence set before I make the sole acceptance decision.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002] [FR-003] [FR-004] [FR-005] [FR-006] [FR-007] [FR-008]: Given one exact candidate, a closed evidence fingerprint set, and five perspective findings produced by the Accountable Delivery Owner under distinct phase identities, when critique evidence is generated and validated, then every finding is independently content-addressed and bound, the aggregate result is derived rather than asserted, every stale/absent/duplicate/substituted/red/prose-only form is refused, and no additional authorizer is required.

## Functional Requirements
- FR-001: The contract MUST bind the exact 40-character candidate revision, tracked-tree SHA-256, GS2-03.8 unit-contract SHA-256, and one stable Accountable Delivery Owner identity. (Stories: US-001; Acceptance: AC-001)
- FR-002: The contract MUST bind a non-empty, closed, ordinal evidence inventory by stable id and lowercase SHA-256 and derive one evidence-set fingerprint from its canonical bytes. (Stories: US-001; Acceptance: AC-001)
- FR-003: Architecture, security, adapter, migration, and cutover MUST each appear exactly once as a typed finding with a unique stable finding id, unique phase identity, owner identity, decision, content SHA-256, canonical completion time, candidate fingerprint, evidence-set fingerprint, and self-digest. (Stories: US-001; Acceptance: AC-001)
- FR-004: The same Accountable Delivery Owner MAY perform every perspective; phase identities MUST separate evidence generations without becoming separate authorities, and the owner MUST remain the sole acceptance authority. (Stories: US-001; Acceptance: AC-001)
- FR-005: The roll-up MUST use a fixed derivation contract and MUST be passed only when every required, current, bound finding is passed; required/passing perspective inventory, finding-set fingerprint, authority identity, outcome, and self-digest MUST be recomputed and verified. (Stories: US-001; Acceptance: AC-001)
- FR-006: Canonical generation and validation MUST fail closed on malformed shape, unknown schema, omission, addition, duplication, noncanonical order/time, stale or substituted candidate/evidence/content binding, digest disagreement, any changes-required finding hidden by green, and prose-only or roll-up-only evidence. (Stories: US-001; Acceptance: AC-001)
- FR-007: Evidence storage MUST preserve reviews/v1, add and execute a reviews/v2 schema selected by its versioned policy path, and apply supported JSON Schema validation to review records instead of trusting hand-written top-level checks alone. (Stories: US-001; Acceptance: AC-001)
- FR-008: Warning-free build, unit/architecture tests, evidence-storage self-test, exact-head hosted qualification, retained five-perspective evidence, protected merge verification, and gate inversions MUST prove the contract without implementing GS2-03.9 mutation coverage. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded; ADR-0079 fixes the authority interpretation.

## Public Or Tool-Facing Impact
- Adds an additive typed qualification contract and versioned evidence schema; it changes no runtime mutation API.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 115-gs2-03-8-critique-evidence`.
