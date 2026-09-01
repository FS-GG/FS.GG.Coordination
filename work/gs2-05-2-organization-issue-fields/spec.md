---
schemaVersion: 1
workId: gs2-05-2-organization-issue-fields
title: GS2-05.2 organization issue-field contract
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-05.2 organization issue-field contract Specification

Prose status: specified

## User Value
A single minimal and deterministic organization issue-field model removes competing scheduling and protocol authority.

## Scope
- SB-001: Repository-local pure field vocabulary, frozen current-field corpus, migration planner, formal model, validator, tests, and evidence only.
- SB-002: Bind the registered contract `054ee50545c55b314447b7636ee35faf866adba40f6ff9b5ef07effb2009b41f`, command `6311e9ca2c92315c48981983efcb93f2717b6b5273aa6fafa75c3fb8496ebcbd`, and accepted predecessor receipt.

## Non-Goals
- NG-001: No live GitHub or Project mutation, organization schema change, issue conversion, credential, deployment, publication, or stable release.
- NG-002: No GS2-05.3 intake behavior or successor-unit authority.

## User Stories
- US-001 (P1): As an operator, I can distinguish authoritative scheduling intent from derived lifecycle Status without a competing input.
- US-002 (P1): As a migration author, I can map every complete current field row to exactly one canonical target or stable refusal.
- US-003 (P1): As a reviewer, I can reproduce the contract, formal checks, independent inversions, and evidence bytes offline.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001, FR-002]: Given each registered scheduling intent and hold combination, when normalized, then exactly the allowed intent/hold relationship is produced and Status remains derived.
- AC-002 [US-002] [FR-003, FR-004, FR-005]: Given the complete fingerprinted current corpus, when planned, then each row yields one stable migration preserving identity, type, relations, scope, and exemptions.
- AC-003 [US-002] [FR-006, FR-007]: Given malformed, contradictory, lossy, stale, or incomplete input, when planned, then a stable diagnostic is returned and no partial plan exists.
- AC-004 [US-003] [FR-008, FR-009]: Given the exact candidate, when the registered validator and independent controls run, then canonical bytes and every declared Q2 invariant agree.

## Functional Requirements
- FR-001: Scheduling intent is exactly Backlog, Ready, Paused, or Cancelled and is the sole human/policy scheduling input; lifecycle Status is derived only. (Stories: US-001; Acceptance: AC-001)
- FR-002: Hold reason is absent for Ready/Cancelled and exactly one of not-yet-actionable, dependency, decision, external, or operator for Backlog/Paused. (Stories: US-001; Acceptance: AC-001)
- FR-003: Priority, effort, dates, severity, phase, and workstream use only the registered closed vocabularies and canonical date/order rules. (Stories: US-002; Acceptance: AC-002)
- FR-004: Contract references and touch sets are canonical revision-bound projections of authoritative records, never independent ledgers. (Stories: US-001, US-002; Acceptance: AC-002)
- FR-005: The frozen corpus inventories every current field name, type, option, value, issue identity, native type, hierarchy, dependency, repository scope, lifecycle state, and exemption with an exact fingerprint. (Stories: US-002; Acceptance: AC-002)
- FR-006: Planning is total, unique, deterministic, canonically ordered, idempotent, no-op stable, and byte stable while preserving identity, native type, hierarchy, dependencies, repository scope, and exemptions. (Stories: US-002; Acceptance: AC-002, AC-003)
- FR-007: Missing, unknown, contradictory, ambiguous, lossy, duplicate, incomplete, stale, unreadable, invalid-date, reversed-date, unbound-contract, and noncanonical-touch-set inputs fail closed. (Stories: US-002; Acceptance: AC-003)
- FR-008: A separate plain Quint model supplies examples, simulations, invariants, witnesses, and bounded model-checking over the same closed vocabulary. (Stories: US-003; Acceptance: AC-004)
- FR-009: The registered offline validator binds source, corpus, independent expectations, formal results, qualification evidence, and SDD readiness; one inversion per refusal family and one omitted-combination inversion must fail. (Stories: US-003; Acceptance: AC-004)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Add a documented pure Core API for vocabulary validation and deterministic migration planning.
- Add the registered `eng/validate-github-organization-issue-fields.fsx -- .` Q2 command and immutable evidence schema instance.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-05-2-organization-issue-fields`.
