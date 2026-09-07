---
schemaVersion: 1
workId: 320-gs2-07-6-queue-sandbox-pilot
title: GS2-07.6 queue sandbox/pilot
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-07.6 queue sandbox/pilot Specification

Prose status: specified

## User Value
Operators can qualify one bounded hosted merge-queue pilot and its complete recovery without enabling the fleet.

## Scope
- SB-001: Add deterministic queue pilot and recovery qualification contracts, tests, documentation, SDD records, retained exact-revision evidence, and execute reversible hosted mutations only against FS-GG/FS.GG.GitHub.Substrate.Sandbox id 1353050537.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can operators can qualify one bounded hosted merge-queue pilot and its complete recovery without enabling the fleet.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given GS2-07.6 queue sandbox/pilot is available, when the user exercises it, then they can operators can qualify one bounded hosted merge-queue pilot and its complete recovery without enabling the fleet.

## Functional Requirements
- FR-001: Bind exact repository id, candidate and merge-group heads, full base ref SHA and observation revision, complete required checks and results, claim review dependency release and settings authority, admission and expiry, provider capability, exact prestate and no-secrets proof into deterministic sealed evidence; prove forward-base re-evaluation, required-check growth and execution, expiry, interruption, failed-step recovery from sealed state, deterministic retry without duplicate effects, reverse compensation, cleanup, private rollback and authoritative readback. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 320-gs2-07-6-queue-sandbox-pilot`.
