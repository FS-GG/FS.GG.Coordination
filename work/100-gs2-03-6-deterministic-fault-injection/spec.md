---
schemaVersion: 1
workId: 100-gs2-03-6-deterministic-fault-injection
title: GS2-03.6 deterministic fault injection
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.6 deterministic fault injection Specification

Prose status: specified

## User Value
deterministically qualify failure recovery and fail-closed refusal before authority adapters exist

## Scope
- SB-001: repository-local qualification contracts, architecture tests, retained evidence, and SDD/readiness artifacts only

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can deterministically qualify failure recovery and fail-closed refusal before authority adapters exist.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given GS2-03.6 deterministic fault injection is available, when the user exercises it, then they can deterministically qualify failure recovery and fail-closed refusal before authority adapters exist.

## Functional Requirements
- FR-001: cover failure injection before and after every modeled external step (Stories: US-001; Acceptance: AC-001)
- FR-002: cover lost responses, duplicate events, reordered events, partial pages, exhausted rate budgets, revoked permission, and concurrent exact-revision mutation (Stories: US-001; Acceptance: AC-001)
- FR-003: every scenario must deterministically converge or emit an exact typed refusal (Stories: US-001; Acceptance: AC-001)
- FR-004: evidence must be canonical, digest-bound, storage-indexed, and rejected by independent inversion controls (Stories: US-001; Acceptance: AC-001)
- FR-005: reuse the accepted canonical protocol authority without a shadow behavioral model (Stories: US-001; Acceptance: AC-001)
- FR-006: perform no network access, live GitHub mutation, adapter implementation, deployment, publication, production write, or GS2-03.7 work (Stories: US-001; Acceptance: AC-001)
- FR-007: pass warning-free build, architecture tests, evidence-storage validation, exact-head hosted qualification, independent review, protected merge, and exact-merge verification (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 100-gs2-03-6-deterministic-fault-injection`.
