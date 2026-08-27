---
schemaVersion: 1
workId: 38-lifecycle-intent
title: Implement lifecycle intent
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Implement lifecycle intent Specification

Prose status: specified

## User Value
Human scheduling intent remains authoritative while claims, blockers, pull requests, reviews, delivery, and derived lifecycle status remain independently observed facts.

## Scope
- SB-001: Implement GS2-02.4 only in the canonical literate Quint protocol, deterministic profile-2 projections, executable unit contract, and focused verification.

## Non-Goals
- SB-002: No relation algebra, protocol streams, external write authority, hosted runtime, deployment, publication, or production mutation.

## User Stories
- US-001 (P1): As a user, I can human scheduling intent remains authoritative while claims, blockers, pull requests, reviews, delivery, and derived lifecycle status remain independently observed facts.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Implement lifecycle intent is available, when the user exercises it, then they can human scheduling intent remains authoritative while claims, blockers, pull requests, reviews, delivery, and derived lifecycle status remain independently observed facts.

## Functional Requirements
- FR-001: Catalogue exactly Backlog, Ready, Paused, and Cancelled human intents with stable identities. (Stories: US-001; Acceptance: AC-001)
- FR-002: Represent claim, blocker, pull-request, review, and delivery facts separately and accept only Observed or ProvenAbsent fact outcomes as lifecycle knowledge. (Stories: US-001; Acceptance: AC-001)
- FR-003: Derive lifecycle status fail closed without rewriting human intent; changing a claim, blocker, or derived status cannot silently change intent. (Stories: US-001; Acceptance: AC-001)
- FR-004: Regenerate and expose the lifecycle catalogue and three lifecycle actions through profile 2 while preserving profile 1 and all earlier accepted identities. (Stories: US-001; Acceptance: AC-001)
- FR-005: Consume the accepted GS2-02.3 receipt, pass exact Q1 and pure Q2 plus claim-to-intent collapse controls, and stop before GS2-02.5 relation algebra. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 38-lifecycle-intent`.
