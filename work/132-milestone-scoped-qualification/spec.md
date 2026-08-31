---
schemaVersion: 1
workId: 132-milestone-scoped-qualification
title: Milestone Scoped Qualification
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Milestone Scoped Qualification Specification

Prose status: specified

## User Value
Maintainers receive fast current-tree child feedback, reuse canonical formal evidence only when its semantic subject is unchanged, and close every parent milestone with a cold comprehensive qualification.

## Scope
- SB-001: Define a versioned generic milestone frontier whose ordinary mode is `scoped` and whose explicit parent-closure candidate is `comprehensive`.
- SB-002: Define a canonical formal semantic subject, reusable canonical artifact receipt, route selection, current-tree integration execution, and exact-head terminal evidence.
- SB-003: Define immutable gate observations and an approximately daily evaluator that recommends cadence changes from actual cost, unique actionable-defect yield, detection delay, closure equivalence, confidence, and blast radius.

## Non-Goals
- SB-004: Do not automatically enact a cadence recommendation, trust labels/path filters/caller skip flags, or infer low risk from missing or sparse observations.
- SB-005: Do not weaken milestone closure, freeze, release, cutover, `OpenV2`, rollback-authority, formal-input drift, or other declared high-blast-radius minimums.
- SB-006: Do not special-case GS2-04 in workflow logic or change the canonical Quint corpus and formal obligations themselves.

## User Stories
- US-001 (P1): As a maintainer, I receive current-tree build, test, security, package, and recovery feedback without repeating unchanged canonical formal work.
- US-002 (P1): As a milestone owner, I can close a parent only after every accepted child is bound and every declared qualification gate executes cold.
- US-003 (P1): As a reviewer, I can audit the exact candidate, mode, semantic subjects, current or reused artifacts, source runs, closure contract, and policy identities.
- US-004 (P2): As a process owner, I receive a daily evidence-based recommendation to move expensive low-yield checks outward or valuable checks inward without granting that recommendation execution authority.

## Acceptance Scenarios
- AC-001 [US-001] [US-003] [FR-001] [FR-002] [FR-003]: Given an ordinary child candidate whose formal semantic subject matches one live successful protected artifact, when qualification runs, then current-tree non-formal gates execute, canonical formal execution is skipped, retained bytes are downloaded and re-hashed, and terminal evidence names the prior source and current exact head.
- AC-002 [US-001] [FR-002] [FR-004]: Given any canonical model, compiler, validator, fixture, tool pin, budget, formal gate, plan policy, or subject-membership input changes, when qualification runs, then the formal subject changes and canonical formal execution is mandatory.
- AC-003 [US-002] [US-003] [FR-005] [FR-006] [FR-007]: Given an explicit parent-closure candidate with the complete accepted ordered child set, when qualification runs, then every gate executes cold, reused or deferred formal evidence is refused, and the exact-head terminal closure manifest binds all child receipt digests.
- AC-004 [US-002] [FR-006] [FR-007]: Given a closure candidate has a missing, duplicate, stale, unaccepted, reordered, contract-drifted, or forged child receipt, when closure is evaluated, then qualification refuses before a closure receipt can be produced.
- AC-005 [US-004] [FR-008] [FR-009] [FR-010]: Given a rolling observation window, when the daily evaluator runs, then it deterministically emits `retain`, `increase`, `reduce`, or `insufficient-data` with sample, cost, unique yield, expected detection delay, closure equivalence, confidence, blast radius, and policy version, but does not alter workflow authority.
- AC-006 [US-004] [FR-009] [FR-010]: Given stale or sparse telemetry, a high-yield/high-impact gate, a closure-discovered miss, or a protected boundary, when evaluation runs, then it remains inconclusive or recommends increased cadence and cannot recommend a reduction below declared minimums.

## Functional Requirements
- FR-001: The qualification plan MUST declare one generic `scoped|comprehensive` mode contract. `scoped` MUST be the default for ordinary child work; comprehensive mode MUST derive only from a tracked versioned parent-closure or production-authority contract, never labels, branches, prose, path diffs, or caller skip flags. (Stories: US-001, US-002; Acceptance: AC-001, AC-003)
- FR-002: The plan MUST declare one canonical formal semantic subject containing every model, compiler/extractor, validator, formal fixture, tool pin, budget, command, gate contract, workflow projection, and qualification-policy input capable of changing the canonical result. Subject generation MUST be deterministic, ordered, duplicate-free, content-addressed, and shared by renderer and validators. (Stories: US-001, US-003; Acceptance: AC-001, AC-002)
- FR-003: Scoped mode MAY reuse exactly one immutable unexpired independently validated protected canonical artifact only when gate, subject, toolchain, environment, policy, result, and artifact byte identities match. Selection and terminal acceptance MUST re-read and re-hash evidence; absence before selection executes, while contradiction or loss after selection refuses. (Stories: US-001, US-003; Acceptance: AC-001)
- FR-004: Any formal subject drift MUST force canonical execution in scoped mode. Deterministic inversions MUST prove each subject member changes the digest and prevents reuse. (Stories: US-001; Acceptance: AC-002)
- FR-005: A comprehensive candidate MUST force all declared gates cold, reject every reused or deferred gate artifact, and keep the terminal evidence job fail-closed under all prior job outcomes. (Stories: US-002, US-003; Acceptance: AC-003)
- FR-006: The generic milestone frontier and closure candidate MUST bind parent ID, ordered child contract set, accepted child receipt digests, closure policy version, and current protected candidate. Missing, duplicate, stale, unaccepted, reordered, or contract-drifted children MUST refuse closure. (Stories: US-002, US-003; Acceptance: AC-003, AC-004)
- FR-007: The terminal manifest MUST bind exact head and tree, selected mode, every gate subject, each current or reused artifact digest and source execution, policy and environment identity, and closure subject when present. A protected post-merge receipt MUST retain comprehensive closure provenance. (Stories: US-002, US-003; Acceptance: AC-003, AC-004)
- FR-008: Every gate execution and reuse MUST emit immutable observations for duration and runner cost, reuse status, outcome class, unique actionable-defect attribution, infrastructure/noise attribution, detection instant, and linkage to a later comprehensive result without allowing telemetry to influence the running decision. (Stories: US-004; Acceptance: AC-005)
- FR-009: An approximately daily evaluator MUST process a declared rolling window and deterministically emit `retain`, `increase`, `reduce`, or `insufficient-data`, including counts, confidence state, cost saved, expected detection delay, closure equivalence, blast-radius class, rationale codes, and policy version. Missing, stale, or sparse data MUST remain explicit and MUST NOT mean zero risk. (Stories: US-004; Acceptance: AC-005, AC-006)
- FR-010: A recommendation MUST be observational and immutable; cadence changes require reviewed versioned policy. The evaluator MUST refuse `reduce` below a declared minimum for comprehensive/production-authority boundaries, formal drift, security, corruption, irreversible mutation, or other high-blast-radius controls, and MUST treat closure-discovered misses as evidence favoring increased cadence. (Stories: US-004; Acceptance: AC-005, AC-006)

## Ambiguities
- AMB-001: Which existing plan components and tracked paths form the first canonical formal subject without accidentally retaining the complete-tree subject?
- AMB-002: What generic tracked contract selects comprehensive mode and binds the ordered accepted child set without remaining permanently active after closure?
- AMB-003: How is a canonical artifact discovered and transported independently of complete-tree reuse while preserving fail-closed post-selection semantics?
- AMB-004: What rolling window and minimum sample distinguish `insufficient-data` from a `reduce` recommendation without pretending to estimate rare-event risk precisely?
- AMB-005: How are unique actionable defects and closure-discovered misses attributed without making mutable human labels authority?

## Public Or Tool-Facing Impact
- Adds versioned formal-subject, milestone-frontier, closure-candidate/receipt, gate-observation, and cadence-recommendation contracts.
- Changes generated protected-workflow topology and adds an approximately daily scheduled control point.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 132-milestone-scoped-qualification`.
