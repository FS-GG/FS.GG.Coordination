---
schemaVersion: 1
workId: 54-durable-plans
title: Implement durable plans
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Implement durable plans Specification

Prose status: specified

## User Value
Coordination decisions compile into ordered resumable durable steps with explicit causation, correlation, receipt re-read, compensation boundaries, and roll-forward classification without executing a network write.

## Scope
- SB-001: Implement only GS2-02.8 in the canonical literate Quint protocol, deterministic profile-2 projections, roadmap unit pin, and focused verification.

## Non-Goals
- SB-002: Do not implement GS2-02.9 desired-state schemas, a plan executor, network or GitHub mutation, hosted runtime, deployment, publication, or production-write authority.

## User Stories
- US-001 (P1): As a user, I can coordination decisions compile into ordered resumable durable steps with explicit causation, correlation, receipt re-read, compensation boundaries, and roll-forward classification without executing a network write.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Implement durable plans is available, when the user exercises it, then they can coordination decisions compile into ordered resumable durable steps with explicit causation, correlation, receipt re-read, compensation boundaries, and roll-forward classification without executing a network write.

## Functional Requirements
- FR-001: Define ordered durable plan steps with stable plan, step, predecessor, sequence, causation, correlation, mutation-intent, and compensation-boundary bindings. (Stories: US-001; Acceptance: AC-001)
- FR-002: Resume only from an exact durable operation receipt bound to the step; uncertain results require receipt re-read or exact replay and never authorize advancement. (Stories: US-001; Acceptance: AC-001)
- FR-003: Keep compensation within one explicit boundary, name an applied predecessor, and reject cross-boundary, forward-order, duplicate, unknown, or uncertain compensation. (Stories: US-001; Acceptance: AC-001)
- FR-004: Classify advance, receipt-reread, replan, and compensate dispositions from exact outcomes and boundary history; substitutions fail closed. (Stories: US-001; Acceptance: AC-001)
- FR-005: Preserve accepted GS2-02.1 through GS2-02.7 semantics and consume only the accepted GS2-02.7 receipt. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 54-durable-plans`.
