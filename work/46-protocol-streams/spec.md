---
schemaVersion: 1
workId: 46-protocol-streams
title: Implement protocol streams
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Implement protocol streams Specification

Prose status: specified

## User Value
Coordination stream facts are typed, ordered, and retained according to explicit durability policy.

## Scope
- SB-001: Implement only GS2-02.6 in the canonical literate Quint protocol, deterministic profile-2 projections, unit index, and focused verification.

## Non-Goals
- SB-002: Do not implement GS2-02.7 generalized mutation algebra, network or GitHub mutation, hosted runtime, deployment, publication, or production-write authority.

## User Stories
- US-001 (P1): As a user, I can coordination stream facts are typed, ordered, and retained according to explicit durability policy.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Implement protocol streams is available, when the user exercises it, then they can coordination stream facts are typed, ordered, and retained according to explicit durability policy.

## Functional Requirements
- FR-001: Define a closed stream-kind catalogue for claim/lease/touch-set, operation-lock/election, review, delivery, and operation-receipt envelopes. (Stories: US-001; Acceptance: AC-001)
- FR-002: Bind every envelope to stable stream identity, subject, generation/revision, event identity, predecessor, and payload kind; reject cross-stream substitution, duplicate identity conflicts, gaps, and invalid ordering. (Stories: US-001; Acceptance: AC-001)
- FR-003: Classify lease/lock liveness material as ephemeral and accepted review, delivery, election, and operation receipts as durable checkpoints; compaction must preserve the latest required checkpoint and cannot turn missing or unreadable history into absence. (Stories: US-001; Acceptance: AC-001)
- FR-004: Preserve GS2-02.1 through GS2-02.5 authority, observation, lifecycle, and relation semantics and consume only the accepted GS2-02.5 receipt. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 46-protocol-streams`.
