---
schemaVersion: 1
workId: 224-repository-profiles
title: Repository Profiles
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Repository Profiles Specification

Prose status: specified

## User Value
One deterministic desired repository profile for every reviewed roster row without losing rich authority or mutating an external repository.

## Scope
- SB-001: Coordination repository-profile compiler, qualification contracts, tests, registries, reviewed roster evidence, and GS2-06.1 readiness; no production mutation, required-check census, ruleset plan, custom-property application, or successor-unit implementation.

## Non-Goals
- SB-002: No production mutation, required-check census, ruleset plan, custom-property application, or GS2-06.2 and later implementation.

## User Stories
- US-001 (P1): As the accountable delivery owner, I can derive one deterministic desired profile for every reviewed roster row without losing rich authority or mutating an external repository.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given a sealed reviewed roster, compilation emits one stable profile per row, bounded native properties only for organization-administered repositories, observe-only external dispositions, an exact replay seal, and passing generated and independent Q3 controls.

## Functional Requirements
- FR-001: Bind an exact roster revision and digest; compile every unique owner-qualified row in stable order; retain role, complete capabilities, delivery metadata, reason and ownership; emit bounded controlled-vocabulary fsgg_role, fsgg_owner_scope, and fsgg_coordination_mode properties only for organization-administered repositories; make external rows observe-only; reject missing, duplicate, malformed, stale, unsupported, cross-owner, lossy, overflow, altered-seal, and incomplete inputs; prove exact replay with generated and independent Q3 controls behind accepted terminal GS2-02 through GS2-05 evidence. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 224-repository-profiles`.
