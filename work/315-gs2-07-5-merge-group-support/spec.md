---
schemaVersion: 1
workId: 315-gs2-07-5-merge-group-support
title: Gs2 07 5 Merge Group Support
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-07.5 merge-group support Specification

Prose status: specified

## User Value
Operators can qualify merge-group readiness from one fresh, sealed aggregate decision instead of trusting event-time or partial check state.

## Scope
- SB-001: Repository-local pure merge-group contract, tests, retained evidence, documentation, and SDD readiness only; no network or production mutations.

## Non-Goals
- SB-002: No production merge queue, settings, workflow, release, package, stable-channel, or successor-unit mutation.

## User Stories
- US-001 (P1): As an operator, I can receive a deterministic fail-closed merge-group readiness decision bound to current base, claim, review, head, dependency, release, settings, and aggregate check authority.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the exact accepted GS2-07.4 receipt and roadmap revision, canonical merge-group event, current base repository/ref/SHA observation, complete aggregate required-check census, and current claim/review/head/dependency/release/settings observations, valid fresh facts authorize one sealed merge-group decision with byte-identical replay; every registered missing, malformed, unknown-event, stale, changed, conflicting, incomplete, pending, failed, direct-merge, seal, ordering, network, mutation, Quint, and successor-authority inversion refuses before external effect.

## Functional Requirements
- FR-001: The system MUST bind canonical repository, merge-group identity and head SHA, freshly re-observed base repository identity, full base ref and exact base SHA, base observation revision, observed-at and freshness deadline, configured-expected and observed aggregate required-check inventories plus every full check-result row, and current claim generation, review, candidate head, dependency, release, and settings authority into one canonical length-framed seal; accept only `merge_group` checks-requested facts whose expected and observed complete unique inventories agree, whose results ran successfully for that merge group, and whose every authority was freshly re-observed unchanged; make public parse/verify independently reject semantically malformed plans even when their seals are correctly recomputed; explicitly reject every registered missing, malformed, unknown-event, stale/changed-base identity/ref/SHA/revision, incomplete/missing/pending/failed-check, stale claim/review/head/dependency, unmet release, changed settings, conflicting group, direct merge, unsealed/altered seal, ordering, replay, network, production mutation, Quint-change, and GS2-07.6 inversion; and reproduce byte-identical results on exact replay. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Add a public F# qualification-contract module, deterministic retained evidence, and its registered Q3 gate. Existing runtime and canonical Quint wire contracts remain unchanged.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 315-gs2-07-5-merge-group-support`.
