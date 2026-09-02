---
schemaVersion: 1
workId: 220-fleet-shadow
title: Fleet Shadow
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Fleet Shadow Specification

Prose status: specified

## User Value
A sealed read-only comparison of v1 and v2 decisions over the complete live fleet.

## Scope
- SB-001: Coordination fleet-shadow adapter, qualification contracts, tests, registries, live observation evidence, and GS2-05.8 readiness.

## Non-Goals
- SB-002: No production mutation, no cutover, no permanent second authority, and no GS2-06 implementation.

## User Stories
- US-001 (P1): As the accountable delivery owner, I can prove every rostered live item has a preserved v1 and v2 decision and every difference is explained.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Complete roster and item coverage, zero unexplained divergence, strict read-only permission manifest, no mutation attempts, byte-stable replay, generated and independent Q4 controls.

## Functional Requirements
- FR-001: Bind roster, item, source revisions, observation time, permission manifest, completeness proof, raw v1 decision, raw v2 decision, and classification evidence in one canonical seal. (Stories: US-001; Acceptance: AC-001)
- FR-002: Accept only equal decisions or evidence-backed v1-defect, v2-defect, or intentional-versioned-change classifications; preserve raw decisions and reject all missing, duplicate, stale, cross-subject, unsupported, unclassified, altered, partial, unreadable, unauthorized, mutation-capable, mutation-attempted, or indeterminate inputs. (Stories: US-001; Acceptance: AC-001)
- FR-003: Consume the accepted GS2-05.7 receipt and exact accepted roadmap revision while preserving canonical Quint source bytes. (Stories: US-001; Acceptance: AC-001)
- FR-004: Capture a fresh complete live-fleet read-only observation without credentials or production writes. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 220-fleet-shadow`.
