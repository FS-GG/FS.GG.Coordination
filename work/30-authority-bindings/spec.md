---
schemaVersion: 1
workId: 30-authority-bindings
title: Implement authority bindings
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Implement authority bindings Specification

Prose status: specified

## User Value
Profile 2 can qualify observations against a closed, revision-aware authority catalogue.

## Scope
- SB-001: Extend the canonical literate Quint protocol and generated profile-2 projections for GS2-02.2 only; preserve GS2-02.1 identities and frozen profile 1.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can profile 2 can qualify observations against a closed, revision-aware authority catalogue.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Implement authority bindings is available, when the user exercises it, then they can profile 2 can qualify observations against a closed, revision-aware authority catalogue.

## Functional Requirements
- FR-001: Catalogue exactly seven authority families: native GitHub, repository registry, protocol stream, git ledger, Actions, package feed, and classified external authority. (Stories: US-001; Acceptance: AC-001)
- FR-002: Each binding exposes a stable identity, revision kind, revision value, completeness contract, and evidence relationship. (Stories: US-001; Acceptance: AC-001)
- FR-003: Qualification rejects incomplete, stale, contradictory, wrong-authority, and omitted-family observations. (Stories: US-001; Acceptance: AC-001)
- FR-004: The exact GS2-02.2 roadmap unit consumes the accepted GS2-02.1 receipt and passes Quint typecheck, simulation, tests, model checking, build, and repository tests. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 30-authority-bindings`.
