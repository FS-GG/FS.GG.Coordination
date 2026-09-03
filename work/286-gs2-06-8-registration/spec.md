---
schemaVersion: 1
workId: 286-gs2-06-8-registration
title: GS2-06.8 Fleet Dry-Plan Frontier Registration
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-06.8 Fleet Dry-Plan Frontier Registration Specification

Prose status: specified

## User Value
The next worker can inspect GS2-06.8 and prove its accepted GS2-06.7 prerequisite from exact reviewed authority.

## Scope
- SB-001: Register exact roadmap bytes, one comprehensive-closure unit contract, the ordered GS2-06 Q3/Q7 gates plus one Q5 fleet-dry-plan gate identity, focused refusal tests, and bounded docs only.

## Non-Goals
- SB-002: Do not inspect the live fleet, implement or execute fleet dry plans, create candidate evidence or acceptance, inspect GS2-07.1, apply settings, or perform production mutations.

## User Stories
- US-001 (P1): As the GS2-06.8 implementer, I can inspect one exact unit contract and prove that its sole accepted prerequisite is ready before authoring implementation bytes.
- US-002 (P1): As a reviewer, I can distinguish roadmap drift, index drift, and gate-catalog drift through independent fail-closed controls.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002] [FR-003]: Given roadmap commit `ac05985f0d60c33fb40a5dccecb271a3e00bec4b`, its exact bytes, and the tracked accepted-receipt directory, when `roadmap-work inspect` and `roadmap-work prerequisites` select GS2-06.8, then the reported owner, permission ceiling, comprehensive exit contract, Q3/Q5/Q7 lanes, ordered gate identities, contract digest, accepted GS2-06.7 receipt digest, and `ready: true` state all match the registration.
- AC-002 [US-002] [FR-004]: Given separate mutations to roadmap bytes, the index roadmap pin, or the selected catalog command, when the corresponding architecture controls run, then each mutation fails for its own mismatch before any gate process can start.
- AC-003 [US-001] [FR-005]: Given the complete diff, when scope and authority are audited, then it contains no fleet observation, plan implementation, settings application, acceptance receipt, GS2-07.1 work, or production mutation.

## Functional Requirements
- FR-001: Pin roadmap revision `ac05985f0d60c33fb40a5dccecb271a3e00bec4b`, path `docs/github-substrate-v2-roadmap.md`, and SHA-256 `888d1c3307ba119f6c7075b0d8963f7fa14d1e357ce1f97fdb7c803f1aa5465f`. (Stories: US-001; Acceptance: AC-001)
- FR-002: Register only `GS2-06.8 — Fleet dry plans` with owner `FS.GG.Coordination` and sole prerequisite `GS2-06.7`; preserve all earlier unit-contract and accepted-receipt bytes. (Stories: US-001; Acceptance: AC-001)
- FR-003: Bind the complete read-only, no-apply permission ceiling and roadmap-derived comprehensive closure contract to the ordered GS2-06.1–GS2-06.7 Q3/Q7 gates followed by Q5 gate `github-fleet-dry-plans-contract` (`dotnet fsi eng/validate-github-fleet-dry-plans.fsx -- .`). (Stories: US-001; Acceptance: AC-001)
- FR-004: Add separately executed refusal controls for stale roadmap content, a stale index revision/digest pair, and a gate catalog whose selected command identity differs. (Stories: US-002; Acceptance: AC-002)
- FR-005: Stop at registration: do not execute the absent Q5 gate, inspect the live fleet, implement dry-plan behavior, create candidate or acceptance evidence, inspect GS2-07.1, apply settings, or mutate production state. (Stories: US-001; Acceptance: AC-003)

## Ambiguities
- AMB-001 open: Whether registration authorizes executing the not-yet-implemented fleet-dry-plan gate or any earlier comprehensive gate.

## Public Or Tool-Facing Impact
- Extends the versioned roadmap-work unit index and gate catalog; runtime and production behavior are unchanged.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 286-gs2-06-8-registration`.
