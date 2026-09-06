---
schemaVersion: 1
workId: 310-gs2-07-4-event-security
title: GS2-07.4 event security
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-07.4 event security Specification

Prose status: specified

## User Value
Operators can trust only authentic, in-scope, fresh, payload/API-agreeing, least-privileged event hints to schedule reconciliation.

## Scope
- SB-001: Repository-local pure event-security contract, qualification controls, and retained evidence; no network, webhook deployment, production queue, derived-state mutation, release, or GS2-07.5 work.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As an operator, I can validate an untrusted GitHub delivery as one sealed, deterministic reconciliation-only decision so forged, cross-scope, stale, contradictory, over-privileged, and replayed hints never reach a writer.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the exact accepted GS2-07.3 receipt, roadmap revision, delivery bytes, configured secret, installation/repository scope, replay window, authoritative API observation, and required permission inventory, when a valid HMAC-SHA256 event is checked, then it produces one canonical reconciliation-scheduling decision and byte-identical replay; every registered malformed, signature, scope, time, duplicate, disagreement, privilege, writer, seal, ordering, network, queue, mutation, Quint, and successor-authority inversion refuses before external effect.

## Functional Requirements
- FR-001: The system MUST bind delivery identity, HMAC-SHA256 algorithm and signature, installation and repository scope, delivery timestamp and inclusive replay bounds, payload subject/revision, authoritative API subject/revision, exact required-and-granted permission inventory, scheduling key, and reconciliation-only disposition into one canonical length-framed seal; derive installation, repository, subject, and revision only by strictly parsing the authenticated payload bytes; accept only authentic, in-scope, replay-fresh, payload/API-agreeing, exactly least-privileged deliveries whose delivery ID and authenticated payload digest are both first-seen; produce only a reconciliation scheduling request; reject every registered missing, malformed, unknown-algorithm, signature, scope, replay, duplicate, disagreement, permission, direct-write, unsealed, altered-seal, ordering, network, queue, mutation, Quint-change, and GS2-07.5 inversion with byte-identical replay. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Add a public F# qualification-contract module, deterministic retained evidence, and its Q3 gate. Existing runtime and canonical Quint wire contracts remain unchanged.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 310-gs2-07-4-event-security`.
