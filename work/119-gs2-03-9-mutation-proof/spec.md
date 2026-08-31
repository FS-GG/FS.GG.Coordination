---
schemaVersion: 1
workId: 119-gs2-03-9-mutation-proof
title: Prove the qualification harness can fail
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Prove the qualification harness can fail Specification

Prose status: specified

## User Value
The Accountable Delivery Owner can trust that every qualification evidence gate demonstrably rejects all roadmap-mandated invalid evidence classes.

## Scope
- SB-001: Repository-local closed gate inventory, executable production-validator mutations, typed diagnostics, retained observations, and Q7 evidence; no network, production mutation, publication, deployment, extra authorization, or successor implementation.

## Non-Goals
- SB-002: Do not add network, live GitHub mutation, publication, deployment, production-write, gate-bypass, invariant-weakening, extra-approval, or GS2-04 implementation authority.

## User Stories
- US-001 (P1): As the Accountable Delivery Owner, I can inspect one closed coverage record and reproduce every claimed negative result through the production validation boundary.
- US-002 (P1): As a maintainer, I can add or remove a gate class only by changing an independently validated inventory whose Cartesian coverage is derived rather than asserted.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given the exact healthy baseline and closed gate inventory, when the mutation harness runs, then every gate has one passing control and all six required invalid evidence modes are rejected with the expected production diagnostic.
- AC-002 [US-002] [FR-003] [FR-004]: Given a missing gate, mutation, observation, or unexpected green result, when the retained proof is validated, then deterministic coverage diagnostics refuse it before Q7 acceptance.
- AC-003 [US-001] [FR-005]: Given a generated-case producer attempts to stand in for independent, source, model, compiler, dependency, fixture, package, result, or review evidence, when the manifest is validated, then generated-only evidence is red even if its JSON and digest are otherwise well formed.

## Functional Requirements
- FR-001: Every closed gate class must reject vacuous, absent, stale, truncated, forged, and generated-only evidence while its unmutated control passes. (Stories: US-001; Acceptance: AC-001)
- FR-002: Each negative observation MUST execute the production qualification-manifest validator against exact baseline and independent inventory bytes and retain the actual diagnostic code; self-attested or prose-only results MUST be red. (Stories: US-001; Acceptance: AC-001)
- FR-003: A canonical typed proof MUST bind the exact candidate revision, tracked tree digest, unit contract digest, validator digest, baseline/inventory digests, closed gate and mutation inventories, and the derived Cartesian observation set. (Stories: US-002; Acceptance: AC-002)
- FR-004: Validation MUST reject missing, duplicate, unexpected, reordered, stale, malformed, self-digest-mismatched, non-Cartesian, or unexpectedly green controls and observations with stable diagnostics. (Stories: US-002; Acceptance: AC-002)
- FR-005: The production qualification validator MUST distinguish generated-case provenance and reject evidence sets where generated producers are the sole support for any non-generated gate class. (Stories: US-001; Acceptance: AC-003)
- FR-006: Unit and architecture tests, the evidence-storage self-test, roadmap-work Q7 gates, SDD evidence, exact-head hosted qualification, retained critique evidence, and protected-merge verification MUST all pass without adding a CI lane or approval authority. (Stories: US-001; Acceptance: AC-001, AC-002, AC-003)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 119-gs2-03-9-mutation-proof`.
