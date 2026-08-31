---
schemaVersion: 1
workId: gs2-04-4-project-adapter
title: Gs2 04 4 Project Adapter
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.4 typed GitHub Project adapter Specification

Prose status: specified

## User Value
observe complete Project membership and Status projections without mistaking either projection for durable authority

## Scope
- SB-001: repository-local typed Project, item, membership, and Status observations; guarded projection plans; unit and architecture tests; deterministic fixtures; Q3 validation; evidence; and SDD artifacts

## Non-Goals
- SB-002: no live GitHub writes, concurrency-transition authority, Q4 sandbox correspondence, comment/projection adapter, or successor-unit work

## User Stories
- US-001 (P1): As a user, I can observe complete Project membership and Status projections and derive safe, deterministic repair proposals without promoting those projections into durable authority.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given complete paginated Project observations, when membership is resolved for a repository subject, then every matching Project item is preserved with its stable item identity, content kind, content identity, archive state, revision, page count, node count, and terminal-page proof.
- AC-002 [US-001] [FR-002]: Given a resolved Project item and complete field observations, when Status is read, then the adapter preserves field identity, option identity, display value, and missing-value state without treating the value as claim, review, operation, or completion authority.
- AC-003 [US-001] [FR-003]: Given archived, duplicated, external-repository, draft, missing, unsupported, unauthorized, incomplete, or unreadable observations, when membership or Status is resolved, then the exact outcome remains distinguishable and no absence, uniqueness, authority, or successful repair is invented.
- AC-004 [US-001] [FR-004]: Given one current eligible Project item, a complete current Status observation, an expected revision, and a causation identity, when a membership or Status projection change is requested, then the adapter returns a deterministic guarded proposal with a stable idempotency identity; an already-satisfied request returns a typed no-op.
- AC-005 [US-001] [FR-005]: Given a proposal whose membership, item identity, field identity, option set, archive state, or revision differs from a mandatory re-read, when readiness is checked, then the adapter returns a typed re-read-and-replan requirement and emits no effect authority.
- AC-006 [US-001] [FR-006]: Given a complete observed post-state for a proposal, when verification runs, then success requires the exact intended membership or Status delta, an advanced resulting revision, and byte-equivalent preservation of unrelated items and fields; concurrent or extra changes are refused.
- AC-007 [US-001] [FR-007]: Given each registered Project fault class, when its generated or independently authored mutation is applied, then validation turns red with the expected diagnostic while the unmutated control remains green.
- AC-008 [US-001] [FR-008]: Given an exact committed candidate, when the registered Q3 validator runs offline, then it passes without a live GitHub write, production credential, Q4 sandbox claim, or dependency from production code to qualification fixtures.

## Functional Requirements
- FR-001: read complete paginated Project membership observations as typed Project items with stable identities, content kinds, content identities, archive state, revision, and terminal completeness evidence (Stories: US-001; Acceptance: AC-001)
- FR-002: read complete Status field and option projections while explicitly preventing those projections from authorizing claim, review, operation, or completion transitions (Stories: US-001; Acceptance: AC-002)
- FR-003: preserve archived, duplicated, external-repository, draft, missing, unsupported, unauthorized, incomplete, and unreadable outcomes without inventing absence, uniqueness, authority, or repair success (Stories: US-001; Acceptance: AC-003)
- FR-004: derive deterministic guarded membership and Status projection proposals bound to expected revisions, stable item/field/option identities, causation, and stable idempotency identities, with typed mutation-free no-op decisions (Stories: US-001; Acceptance: AC-004)
- FR-005: require any stale membership, item, field, option, archive, or revision pre-state to be re-read and replanned before an effect can be attempted (Stories: US-001; Acceptance: AC-005)
- FR-006: verify the exact intended post-state projection delta, advanced resulting revision, and unchanged unrelated items and fields while refusing concurrent or extra changes (Stories: US-001; Acceptance: AC-006)
- FR-007: generated cases and independently authored pagination, archived-item, duplicate-item, external-item, draft-item, missing-item, unreadable-observation, stale-revision, concurrent-change, and no-op mutations MUST turn red (Stories: US-001; Acceptance: AC-007)
- FR-008: `dotnet fsi eng/validate-github-project-adapter.fsx -- .` MUST pass Q3 offline without live writes, production credentials, Q4 sandbox claims, or production-to-qualification dependency inversion (Stories: US-001; Acceptance: AC-008)

## Ambiguities
- AMB-001 open: which Project item content kinds and identity fields are canonical for repository issues, pull requests, external content, and draft issues
- AMB-002 open: whether duplicate items for one content identity are normalized, selected, or refused as ambiguous projection evidence
- AMB-003 open: whether archived matching items satisfy membership, block mutation planning, or remain a distinct observation outcome
- AMB-004 open: how missing Project membership differs from an unreadable or incomplete Project observation
- AMB-005 open: which field and option identities are required before Status absence or a Status change proposal is authoritative
- AMB-006 open: whether post-state verification tolerates unrelated concurrent Project changes when the intended projection delta is present
- AMB-007 open: whether Q3 Project fixtures may call live GitHub endpoints

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-4-project-adapter`.
