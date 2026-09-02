---
schemaVersion: 1
workId: 232-ruleset-plans
title: Ruleset Plans
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Ruleset Plans Specification

Prose status: specified

## User Value
One deterministic, reviewable desired-state plan per repository that joins accepted profile and required-check authority before any administrative cutover.

## Scope
- SB-001: GS2-06.3 pure ruleset-plan contracts, qualification, tests, unit registration, evidence, and readiness; no GitHub mutation or apply path.

## Non-Goals
- SB-002: No production GitHub write, workflow rewrite, merge-queue cutover, permission compilation, release hardening, or GS2-06.4 and later implementation.

## User Stories
- US-001 (P1): As the accountable delivery owner, I can inspect an exact sealed branch, tag, and repository merge-policy target per reviewed profile before any administrative cutover.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the accepted GS2-06.2 authority, an exact accepted repository profile, its exact sealed required-check census, complete current-policy evidence, the approved bypass registry, and the bounded exception registry, compilation emits one stable ordered target plan with retained provenance and an exact replay seal; incomplete, stale, cross-repository, lossy, ambiguous, unauthorized-bypass, unbounded, expired, altered, or unsealed inputs refuse.

## Functional Requirements
- FR-001: Bind the accepted GS2-06.2 receipt, exact accepted repository-profile report and profile seal, exact required-check-census report and seal, complete current-policy observation, approved bypass-principal registry, and bounded exception registry. Compile deterministic default-branch and release-tag rulesets, required checks, pull-request review and conversation-resolution policy, allowed merge methods, auto-merge and merge-queue posture, delete-branch-on-merge behavior, bypass principals, and only currently active exceptions whose approval window is positive and no longer than 30 days. Exact repository identities and source revisions must agree; unsupported or observe-only profiles produce a retained non-mutable disposition rather than a writable target; missing, duplicate, partial, stale, cross-repository, lossy, contradictory, unauthorized-bypass, non-expiring, not-yet-active, expired, overlong, or altered inputs refuse. Preserve complete provenance, stable ordering, exact replay, and a deterministic seal. The retained `FS-GG/FS.GG.Coordination` target must preserve all six effective required checks, squash-only merge, auto-merge and branch deletion, protected default branch, immutable signed `v*` release tags, no merge queue until unconditional merge-group production is proven, and no bypass or exception. Pass generated and independently authored Q3 controls without changing the canonical Quint protocol or adding an apply surface. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 232-ruleset-plans`.
