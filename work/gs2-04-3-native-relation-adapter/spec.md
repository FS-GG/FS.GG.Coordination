---
schemaVersion: 1
workId: gs2-04-3-native-relation-adapter
title: GS2-04.3 typed GitHub native relation adapter
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.3 typed GitHub native relation adapter Specification

Prose status: specified

## User Value
read complete native hierarchy and dependency relations and derive safe, verifiable edge changes

## Scope
- SB-001: repository-local typed relation observations, guarded add/remove plans, stale re-read and exact post-state verification, unit and architecture tests, deterministic fixtures, Q3 validation, evidence, and SDD artifacts

## Non-Goals
- SB-002: no live GitHub writes, Q4 sandbox correspondence, Project adapter, or successor-unit work

## User Stories
- US-001 (P1): As a user, I can read complete native hierarchy and dependency relations and derive safe, verifiable edge changes.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given complete paginated hierarchy and dependency observations, when native relations are read, then the result contains every typed directed edge, its exact relation kind, endpoint direction, revision, page count, node count, and terminal-page proof.
- AC-002 [US-001] [FR-002]: Given an edge-local add or remove request, when current state is complete and current, then the plan changes only that exact typed edge and preserves every unrelated edge.
- AC-003 [US-001] [FR-003]: Given incomplete, unauthorized, unsupported, indeterminate, duplicate, reversed, stale, or concurrently changed observations, when reading, planning, or verification runs, then the condition remains distinguishable and no absence, effect, or success is invented.
- AC-004 [US-001] [FR-004]: Given complete current state, an expected revision, and a causation identity, when add or remove planning runs, then it returns a deterministic guarded operation with a stable idempotency identity; an already-present add or already-absent remove returns a typed no-op.
- AC-005 [US-001] [FR-005]: Given pre-state whose revision differs from the plan guard, when execution readiness is checked, then the adapter returns a typed re-read-and-replan requirement and emits no effect authority.
- AC-006 [US-001] [FR-006]: Given an observed post-state for a planned add or remove, when verification runs, then success requires the exact intended edge delta, the expected resulting revision, and byte-equivalent preservation of unrelated edges; concurrent or extra changes are refused.
- AC-007 [US-001] [FR-007]: Given each registered relation fault class, when its generated or independently authored mutation is applied, then validation turns red with the expected diagnostic while the unmutated control remains green.
- AC-008 [US-001] [FR-008]: Given an exact committed candidate, when the registered Q3 validator runs offline, then it passes without a live GitHub write, production credential, Q4 sandbox claim, or production-to-qualification dependency inversion.

## Functional Requirements
- FR-001: read complete paginated hierarchy and dependency observations as typed directed edge sets with revision and terminal completeness evidence (Stories: US-001; Acceptance: AC-001)
- FR-002: preserve relation kind, endpoint direction, and every unrelated edge across an edge-local add or remove (Stories: US-001; Acceptance: AC-002)
- FR-003: preserve incomplete, unauthorized, unsupported, indeterminate, duplicate, reversed, stale, and concurrent outcomes without inventing absence, effect, or success (Stories: US-001; Acceptance: AC-003)
- FR-004: derive deterministic guarded add and remove plans bound to expected revisions, causation, and stable idempotency identities, with typed mutation-free no-op decisions (Stories: US-001; Acceptance: AC-004)
- FR-005: require a stale pre-state to be re-read and replanned before any effect becomes authorized (Stories: US-001; Acceptance: AC-005)
- FR-006: verify the exact intended post-state edge delta, expected resulting revision, and unchanged unrelated edges while refusing concurrent or extra changes (Stories: US-001; Acceptance: AC-006)
- FR-007: generated cases and independently authored pagination, duplicate-edge, reversed-endpoint, relation-kind, stale-revision, incomplete-observation, concurrent-change, and no-op mutations must turn red (Stories: US-001; Acceptance: AC-007)
- FR-008: dotnet fsi eng/validate-github-native-relation.fsx -- . must pass Q3 offline without live writes, production credentials, or Q4 sandbox claims (Stories: US-001; Acceptance: AC-008)

## Ambiguities
- AMB-001 open: which endpoint ordering canonically represents parent/child and blocking relations
- AMB-002 open: whether duplicate identical observed edges are normalized as a set or refused as malformed observation evidence
- AMB-003 open: what completeness evidence is required before absence or a remove plan is authoritative
- AMB-004 open: what exact result represents a plan whose pre-state revision has become stale
- AMB-005 open: whether post-state verification tolerates unrelated concurrent changes when the intended edge delta is present
- AMB-006 open: whether Q3 relation fixtures may call live GitHub endpoints

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-3-native-relation-adapter`.
