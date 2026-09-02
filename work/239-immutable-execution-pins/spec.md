---
schemaVersion: 1
workId: 239-immutable-execution-pins
title: GS2-06.4 immutable execution pins
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-06.4 immutable execution pins Specification

Prose status: specified

## User Value
Maintainers can prove every executable workflow dependency is immutable and that only Renovate proposes pin updates.

## Scope
- SB-001: Bind the accepted GS2-06.3 receipt, exact accepted roadmap revision, complete sealed workflow corpus, immutable reusable-workflow publication identities, and bounded Renovate update policy; add only offline contracts, qualification, evidence, and lifecycle artifacts.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can maintainers can prove every executable workflow dependency is immutable and that only Renovate proposes pin updates.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given GS2-06.4 immutable execution pins is available, when the user exercises it, then they can maintainers can prove every executable workflow dependency is immutable and that only Renovate proposes pin updates.

## Functional Requirements
- FR-001: Every third-party Action and reusable workflow reference uses an exact 40-hex commit SHA; immutable publication binds repository, path, commit and content digest; exactly Renovate is automated update authority; missing, duplicate, stale, cross-repository, altered, incomplete, conflicting, mutable, or forbidden mutation/publication inputs fail; generated adversarial controls and independent Q3 oracles pass with deterministic sealing and exact replay. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 239-immutable-execution-pins`.
