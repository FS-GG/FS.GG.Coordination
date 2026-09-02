---
schemaVersion: 1
workId: 246-permission-compilation
title: GS2-06.5 permission compilation
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-06.5 permission compilation Specification

Prose status: specified

## User Value
Maintainers can compile least-privilege GitHub App and workflow permissions from the complete registered interpreter inventory with strict principal and environment separation.

## Scope
- SB-001: Bind accepted GS2-06.4, the exact accepted roadmap revision, a sealed complete registered-interpreter inventory, and pure permission compilation evidence.

## Non-Goals
- SB-002: Do not mutate production settings or permissions, deploy, publish, release, author acceptance, or inspect successor work.

## User Stories
- US-001 (P1): As a user, I can maintainers can compile least-privilege GitHub App and workflow permissions from the complete registered interpreter inventory with strict principal and environment separation.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given GS2-06.5 permission compilation is available, when the user exercises it, then they can maintainers can compile least-privilege GitHub App and workflow permissions from the complete registered interpreter inventory with strict principal and environment separation.

## Functional Requirements
- FR-001: Every registered interpreter maps to exactly the minimum declared App and workflow permissions for one principal/environment class; undeclared interpreters or permissions, wildcard or escalation, missing or conflicting mappings, stale inputs, and normal/admin-release crossover fail closed; deterministic seal, replay, and independent Q3 controls pass. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 246-permission-compilation`.
