---
schemaVersion: 1
workId: 216-lifecycle-projection
title: Lifecycle Projection
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Lifecycle Projection Specification

Prose status: specified

## User Value
A deterministic typed lifecycle projection for GitHub substrate v2.

## Scope
- SB-001: The Coordination repository lifecycle adapter, qualification contracts, tests, registries, and evidence for GS2-05.7.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can A deterministic typed lifecycle projection for GitHub substrate v2.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Generated and independent qualification cover exactly the declared 18 Q3 controls.

## Functional Requirements
- FR-001: Derive Backlog, Ready, Paused, Blocked, Claimed, InReview, Accepted, Cancelled, and Delivered using Protocol.md formal precedence. (Stories: US-001; Acceptance: AC-001)
- FR-002: Bind scheduling intent, holds, dependencies, claim journal, pull request, review journal, delivery journal, issue state, and current Project Status observations to one subject and revision. (Stories: US-001; Acceptance: AC-001)
- FR-003: Fail closed on incomplete, unauthorized, unreadable, stale, contradictory, historical, or unprotected delivery evidence. (Stories: US-001; Acceptance: AC-001)
- FR-004: Map derived lifecycle to the existing Project Status option without treating Status as lifecycle intent. (Stories: US-001; Acceptance: AC-001)
- FR-005: Produce exact revision-bound plans, reject stale authorization, verify authoritative poststate, and make replay a zero-write no-op. (Stories: US-001; Acceptance: AC-001)
- FR-006: Preserve the canonical Quint source byte-for-byte and perform no production writes during qualification. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 216-lifecycle-projection`.
