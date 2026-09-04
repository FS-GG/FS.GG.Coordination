---
schemaVersion: 1
workId: 299-gs2-07-2-narrow-reconciliation
title: GS2-07.2 narrow reconciliation
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-07.2 narrow reconciliation Specification

Prose status: specified

## User Value
Pure deterministic narrow reconciliation schedules complete supported GitHub event inventory to one deduplicating subject queue and reserves all derived-state writes to the shared reconciler.

## Scope
- SB-001: Only GS2-07.2 qualification contracts, tests, validator, evidence, architecture documentation, SDD artifacts, and later append-only acceptance receipt; no successor scope.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can pure deterministic narrow reconciliation schedules complete supported GitHub event inventory to one deduplicating subject queue and reserves all derived-state writes to the shared reconciler.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given GS2-07.2 narrow reconciliation is available, when the user exercises it, then they can pure deterministic narrow reconciliation schedules complete supported GitHub event inventory to one deduplicating subject queue and reserves all derived-state writes to the shared reconciler.

## Functional Requirements
- FR-001: Given duplicate or reordered supported events for one normalized subject, compilation must emit exactly one queue entry at the newest relevant revision with byte-identical replay; every registered malformed, scope, routing, sealing, and direct-write mutation must be refused, and exact Q3/full test gates must pass without network or production mutation. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 299-gs2-07-2-narrow-reconciliation`.
