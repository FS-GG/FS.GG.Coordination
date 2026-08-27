---
schemaVersion: 1
workId: 34-observation-outcomes
title: Implement observation outcomes
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Implement observation outcomes Specification

Prose status: specified

## User Value
Profile 2 distinguishes observation knowledge and failures without unsafe collapse.

## Scope
- SB-001: Implement GS2-02.3 only in the canonical literate Quint protocol, generated profile-2 projections, executable unit contract, and focused verification while preserving accepted GS2-02.1 and GS2-02.2 identities.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can profile 2 distinguishes observation knowledge and failures without unsafe collapse.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Implement observation outcomes is available, when the user exercises it, then they can profile 2 distinguishes observation knowledge and failures without unsafe collapse.

## Functional Requirements
- FR-001: Catalogue exactly observed, proven absent, contradictory, unreadable, unsupported, unauthorized, incomplete, stale, and rate-limited outcomes with stable identities. (Stories: US-001; Acceptance: AC-001)
- FR-002: Bind every observation outcome to an authority identity, observed revision, completeness evidence, and optional retry contract. (Stories: US-001; Acceptance: AC-001)
- FR-003: Qualification treats only observed and valid proven-absence evidence as knowledge, preserves contradiction, and never treats unreadable, unsupported, unauthorized, incomplete, stale, or rate-limited as absence. (Stories: US-001; Acceptance: AC-001)
- FR-004: Generated profile-2 contract and bindings expose the closed outcome algebra without changing frozen profile 1. (Stories: US-001; Acceptance: AC-001)
- FR-005: The exact GS2-02.3 unit consumes the accepted GS2-02.2 receipt, passes Q1 and pure Q2 formal gates, and stops before GS2-02.4 lifecycle intent. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 34-observation-outcomes`.
