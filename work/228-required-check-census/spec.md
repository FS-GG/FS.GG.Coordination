---
schemaVersion: 1
workId: 228-required-check-census
title: Required Check Census
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Required Check Census Specification

Prose status: specified

## User Value
One deterministic, provenance-preserving required-check census per reviewed repository, with guaranteed pull-request and merge-group production and no production mutation.

## Scope
- SB-001: GS2-06.2 pure census contracts, qualification, tests, unit registration, evidence, and readiness; no settings apply path, ruleset plan, or successor-unit implementation.

## Non-Goals
- SB-002: No production GitHub mutation, branch or tag ruleset planning, workflow rewrite, merge-queue cutover, or GS2-06.3 and later implementation.

## User Stories
- US-001 (P1): As the accountable delivery owner, I can inspect one deterministic, provenance-preserving required-check census per reviewed repository and know whether every effective requirement is produced unconditionally for pull requests and merge groups.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the accepted GS2-06.1 authority and sealed complete classic-protection, ruleset, workflow-trigger, job, and producer observations, compilation emits one stable ordered census with retained internal provenance, unconditional pull-request and merge-group production proofs, stable external aggregates, and an exact replay seal; every incomplete, ambiguous, stale, filtered, conditional, renamed, cross-repository, contradictory, or altered input is refused.

## Functional Requirements
- FR-001: Bind the accepted GS2-06.1 receipt and exact repository profiles to sealed complete classic-protection, ruleset, workflow-trigger, job, and producer observations; union equivalent requirements with both provenance legs; reject missing, duplicate, ambiguous, stale, partial, conditionally skipped, branch-filtered, path-filtered, event-restricted, renamed, unsupported, cross-repository, and contradictory identities or producers; prove unconditional `pull_request` and `merge_group` production for every effective context; retain the complete internal census while exposing only stable deterministic aggregates; preserve stable ordering and exact replay; pass generated and independently authored Q3 controls without changing the canonical Quint protocol or adding an apply surface. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 228-required-check-census`.
