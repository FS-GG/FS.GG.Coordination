---
schemaVersion: 1
workId: 50-mutation-algebra
title: Implement mutation algebra
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Implement mutation algebra Specification

Prose status: specified

## User Value
Coordination mutations are typed, revision-bound, idempotent, and classify every terminal or uncertain outcome without executing any network write.

## Scope
- SB-001: Implement only GS2-02.7 in the canonical literate Quint protocol, deterministic profile-2 projections, unit index, and focused verification.

## Non-Goals
- SB-002: Do not implement GS2-02.8 durable plan sequencing, network or GitHub mutation, hosted runtime, deployment, publication, or production-write authority.

## User Stories
- US-001 (P1): As a user, I can coordination mutations are typed, revision-bound, idempotent, and classify every terminal or uncertain outcome without executing any network write.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Implement mutation algebra is available, when the user exercises it, then they can coordination mutations are typed, revision-bound, idempotent, and classify every terminal or uncertain outcome without executing any network write.

## Functional Requirements
- FR-001: Define a closed mutation-kind catalogue for create, append, add-edge, remove-edge, set, clear, transition, and compensate, with each kind carrying an explicit payload shape and target subject. (Stories: US-001; Acceptance: AC-001)
- FR-002: Bind every mutation intent to a stable operation identity, subject, expected revision, idempotency key, and canonical intent digest. Exact replay of one key and intent is a no-op result; key reuse across a different kind, subject, revision, or payload is rejected. (Stories: US-001; Acceptance: AC-001)
- FR-003: Define a closed result catalogue that separates terminal applied, idempotent, rejected, and revision-conflict results from uncertain rate-limited, unavailable, timed-out, and incomplete-observation results. Uncertain results authorize observation or exact replay, never an assumed success, assumed failure, or changed idempotency identity. (Stories: US-001; Acceptance: AC-001)
- FR-004: Permit compensation only for a terminal applied mutation, bind it to that mutation's operation identity and resulting revision, and reject self-compensation, compensation chains, unknown operations, and uncertain predecessors. (Stories: US-001; Acceptance: AC-001)
- FR-005: Preserve GS2-02.1 through GS2-02.6 authority, observation, lifecycle, relation, and protocol-stream semantics; consume only the accepted GS2-02.6 receipt and stop before durable-plan semantics. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 50-mutation-algebra`.
