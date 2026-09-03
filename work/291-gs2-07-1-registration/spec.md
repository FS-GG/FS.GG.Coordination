---
schemaVersion: 1
workId: 291-gs2-07-1-registration
title: GS2-07.1 Event-Envelope Frontier Registration
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-07.1 Event-Envelope Frontier Registration Specification

Prose status: specified

## User Value
The next worker can inspect GS2-07.1 and prove its accepted GS2-06.8 prerequisite from exact reviewed authority.

## Scope
- SB-001: Register exact roadmap bytes, one event-envelope unit contract, one Q3 event-envelope gate identity, focused refusal tests, and bounded docs only.

## Non-Goals
- SB-002: Do not implement or execute event-envelope behavior, create candidate evidence or acceptance, inspect a successor unit, or perform production mutations.

## User Stories
- US-001 (P1): As the GS2-07.1 implementer, I can inspect one exact unit contract and prove that its sole accepted prerequisite is ready before authoring implementation bytes.
- US-002 (P1): As a reviewer, I can distinguish roadmap drift, index drift, and gate-catalog drift through independent fail-closed controls.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002] [FR-003]: Given roadmap commit `d0267c02c59de75571f6ee9086f924e8c924da08`, its exact bytes, and the tracked accepted-receipt directory, when `roadmap-work inspect` and `roadmap-work prerequisites` select GS2-07.1, then the reported owner, permission ceiling, exit contract, Q3 lane, ordered gate identity, contract digest, accepted GS2-06.8 receipt digest, and `ready: true` state all match the registration.
- AC-002 [US-002] [FR-004]: Given separate mutations to roadmap bytes, the index roadmap pin, or the selected catalog command, when the corresponding architecture controls run, then each mutation fails for its own mismatch before any gate process can start.
- AC-003 [US-001] [FR-005]: Given the complete diff, when scope and authority are audited, then it contains no event-envelope implementation, acceptance receipt, successor-unit work, or production mutation.

## Functional Requirements
- FR-001: Pin roadmap revision `d0267c02c59de75571f6ee9086f924e8c924da08`, path `docs/github-substrate-v2-roadmap.md`, and SHA-256 `152956bff4f264d7a6e034c0d8553d3df2cd44ac6773b03e83f85ff52dfb4655`. (Stories: US-001; Acceptance: AC-001)
- FR-002: Register only `GS2-07.1 — Event envelope and cursor` with owner `FS.GG.Coordination` and sole prerequisite `GS2-06.8`; preserve all earlier unit-contract and accepted-receipt bytes. (Stories: US-001; Acceptance: AC-001)
- FR-003: Bind a repository-local, no-production permission ceiling and roadmap-derived event normalization/idempotency exit contract to Q3 gate `github-event-envelope-contract` (`dotnet fsi eng/validate-github-event-envelope.fsx -- .`). (Stories: US-001; Acceptance: AC-001)
- FR-004: Add separately executed refusal controls for stale roadmap content, a stale index revision/digest pair, and a gate catalog whose selected command identity differs. (Stories: US-002; Acceptance: AC-002)
- FR-005: Stop at registration: do not execute the absent Q3 gate, implement event-envelope behavior, create candidate or acceptance evidence, inspect a successor unit, or mutate production state. (Stories: US-001; Acceptance: AC-003)

## Ambiguities
- AMB-001 open: Whether registration authorizes executing the not-yet-implemented event-envelope gate.

## Public Or Tool-Facing Impact
- Extends the versioned roadmap-work unit index and gate catalog; runtime and production behavior are unchanged.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 291-gs2-07-1-registration`.
