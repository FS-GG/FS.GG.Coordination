---
schemaVersion: 1
workId: 260-gs2-06-7-registration
title: GS2-06.7 Workflow-Selection Frontier Registration
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-06.7 Workflow-Selection Frontier Registration Specification

Prose status: specified

## User Value
The next worker can inspect GS2-06.7 and prove its accepted GS2-06.6 prerequisite from exact reviewed authority.

## Scope
- SB-001: Register exact roadmap bytes, one unit contract, distinct Q3 and Q7 gate identities, focused refusal tests, and bounded docs only.

## Non-Goals
- SB-002: Do not implement workflow consolidation, selection, candidate evidence, acceptance, GS2-06.8, or production mutations.

## User Stories
- US-001 (P1): As the GS2-06.7 implementer, I can inspect one exact unit contract and prove that its sole accepted prerequisite is ready before authoring implementation bytes.
- US-002 (P1): As a reviewer, I can distinguish roadmap drift, index drift, and gate-catalog drift through independent fail-closed controls.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002] [FR-003]: Given roadmap commit `b6d4b60493d1f0b99daf73b98f4e8ad9bbbc0ed9`, its exact bytes, and the tracked accepted-receipt directory, when `roadmap-work inspect` and `roadmap-work prerequisites` select GS2-06.7, then the reported owner, permission ceiling, exit contract, Q3/Q7 lanes, ordered gate identity, contract digest, accepted GS2-06.6 receipt digest, and `ready: true` state all match the registration.
- AC-002 [US-002] [FR-004]: Given separate mutations to roadmap bytes, the index roadmap pin, or the selected catalog command, when the corresponding architecture controls run, then each mutation fails for its own mismatch before any gate process can start.
- AC-003 [US-001] [FR-005]: Given the complete diff, when scope and authority are audited, then it contains no selector implementation, workflow change, acceptance receipt, GS2-06.8 work, or production mutation.

## Functional Requirements
- FR-001: Pin roadmap revision `b6d4b60493d1f0b99daf73b98f4e8ad9bbbc0ed9`, path `docs/github-substrate-v2-roadmap.md`, and SHA-256 `590d019dba1f7ce72338d8ca940e66e89d2e9f47d0454495938256c912a35b57`. (Stories: US-001; Acceptance: AC-001)
- FR-002: Register only `GS2-06.7 — Workflow consolidation and change-impact selection` with owner `FS.GG.Coordination` and sole prerequisite `GS2-06.6`; preserve all earlier unit-contract and accepted-receipt bytes. (Stories: US-001; Acceptance: AC-001)
- FR-003: Bind the complete repository-local, no-production permission ceiling and roadmap-derived exit contract to ordered Q3 gate `github-workflow-selection-contract` (`dotnet fsi eng/validate-github-workflow-selection.fsx -- .`) and Q7 gate `github-workflow-selection-supply-chain-contract` (`dotnet fsi eng/validate-github-workflow-selection-supply-chain.fsx -- .`). (Stories: US-001; Acceptance: AC-001)
- FR-004: Add separately executed refusal controls for stale roadmap content, a stale index revision/digest pair, and a gate catalog whose selected command identity differs. (Stories: US-002; Acceptance: AC-002)
- FR-005: Stop at registration: do not execute the absent gate, implement selection/consolidation behavior, create candidate or acceptance evidence, inspect GS2-06.8, or mutate production state. (Stories: US-001; Acceptance: AC-003)

## Ambiguities
- AMB-001 open: Whether registration authorizes executing the not-yet-implemented workflow-selection gate.

## Public Or Tool-Facing Impact
- Extends the versioned roadmap-work unit index and gate catalog; runtime and production behavior are unchanged.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 260-gs2-06-7-registration`.
