---
schemaVersion: 1
workId: 62-compiled-contract-outputs
title: GS2-02.10 compiled-contract outputs
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-02.10 compiled-contract outputs Specification

Prose status: specified

## User Value
A complete deterministic compiled contract that qualification and maintainers can inspect without inventing a second model.

## Scope
- SB-001: Repository-local canonical compiler outputs for schemas, command metadata, permission and mutation censuses, settings plans, Markdown/JSON views, semantic diff, diagrams, and model-test inventory.

## Non-Goals
- SB-002: Do not implement GS2-02.11 deterministic-identity semantics, an external writer, network or GitHub mutation, hosted runtime, deployment, publication, or production authority.

## User Stories
- US-001 (P1): As a protocol maintainer, I can inspect one complete derived contract and know which canonical source, profile, and contract identity produced every output.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given a qualified canonical source, when profile 2 compiles it, then all nine output families appear exactly once in deterministic order with typed identity and content bindings.
- AC-002 [US-001] [FR-001] [FR-003]: Given a missing, duplicate, substituted, unsupported, incomplete, reordered, or stale output, when the output set is qualified, then the pure model refuses it and a focused negative control proves the guard is live.

## Functional Requirements
- FR-001: The compiler emits exactly one typed entry for every required output family, binds each entry to source/profile/contract identity and deterministic order/content, and refuses missing, duplicate, substituted, unsupported, incomplete, or stale derivations under focused Q1/Q2 tests and negative-control mutations. (Stories: US-001; Acceptance: AC-001)
- FR-002: The closed nine-family output catalogue covers schemas, command metadata, permission census, mutation census, settings plans, Markdown/JSON projection views, semantic diff, diagrams, and model-test inventory without hand-authored rival projections. (Stories: US-001; Acceptance: AC-001)
- FR-003: Preserve accepted GS2-02.1 through GS2-02.9 semantics and stop before GS2-02.11 deterministic-identity behavior or any external writer. (Stories: US-001; Acceptance: AC-002)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 62-compiled-contract-outputs`.
