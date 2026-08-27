---
schemaVersion: 1
workId: 42-native-relation-algebra
title: Implement native relation algebra
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Implement native relation algebra Specification

Prose status: specified

## User Value
Typed parent/child and blocking relations converge through idempotent edge-set intent while unrelated edges and lifecycle intent remain preserved.

## Scope
- SB-001: Implement GS2-02.5 only in the canonical literate Quint protocol, deterministic profile-2 projections, executable unit contract, and focused verification.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can typed parent/child and blocking relations converge through idempotent edge-set intent while unrelated edges and lifecycle intent remain preserved.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Implement native relation algebra is available, when the user exercises it, then they can typed parent/child and blocking relations converge through idempotent edge-set intent while unrelated edges and lifecycle intent remain preserved.

## Functional Requirements
- FR-001: Catalogue typed parent-child and blocking edge kinds with stable identities and directed endpoints. (Stories: US-001; Acceptance: AC-001)
- FR-002: Represent relation state as edge sets and expose idempotent add-edge and remove-edge intent without scalar replacement. (Stories: US-001; Acceptance: AC-001)
- FR-003: Preserve unrelated edges, reject self-edges, keep relation kinds distinct, and never reverse endpoints implicitly. (Stories: US-001; Acceptance: AC-001)
- FR-004: Reuse fail-closed observation outcomes and preserve lifecycle intent/status independence under relation operations. (Stories: US-001; Acceptance: AC-001)
- FR-005: Consume accepted GS2-02.4 authority, regenerate profile-2 outputs, pass Q1 and pure Q2 controls, and stop before protocol streams. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 42-native-relation-algebra`.
