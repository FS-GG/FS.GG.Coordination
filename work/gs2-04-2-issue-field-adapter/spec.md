---
schemaVersion: 1
workId: gs2-04-2-issue-field-adapter
title: GS2-04.2 typed GitHub issue and field adapter
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.2 typed GitHub issue and field adapter Specification

Prose status: specified

## User Value
resolve semantic issue schema identities and plan guarded issue and field mutations from complete typed GitHub observations

## Scope
- SB-001: src/FS.GG.Coordination.GitHub, src/FS.GG.Coordination.Qualification.Contracts, unit and architecture tests, deterministic fixtures, eng validation, v2 evidence, and SDD work artifacts

## Non-Goals
- SB-002: Do not perform live GitHub mutations, claim Q4 sandbox correspondence, implement native relation or Project adapters, or add hosted lifecycle commands.

## User Stories
- US-001 (P1): As a user, I can resolve semantic issue schema identities and plan guarded issue and field mutations from complete typed GitHub observations.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given complete repository, issue-type, field, and option observations, when semantic declarations are resolved, then exactly one stable live ID is returned for each identity and zero or multiple matches are refused distinctly.
- AC-002 [US-001] [FR-002]: Given expected field data types and closed option declarations, when live schema is checked, then type drift, missing options, extra options, duplicate names, and duplicate IDs are reported before values or mutation plans are authoritative.
- AC-003 [US-001] [FR-003]: Given unsupported, unauthorized, incomplete, stale, missing, or ambiguous observations, when resolution or planning runs, then the original outcome remains distinguishable and no absence or mutation authority is invented.
- AC-004 [US-001] [FR-004]: Given paginated current-value observations with terminal-page and revision evidence, when values are read, then the complete typed value set and revision are preserved; a broken page chain is incomplete rather than partial success.
- AC-005 [US-001] [FR-005]: Given complete current state, desired issue or field state, an expected revision, and a causation identity, when planning runs, then it derives deterministic create, update, or clear steps with stable idempotency identities and explicit revision guards.
- AC-006 [US-001] [FR-006]: Given current state already equal to desired state, when planning runs, then it returns a typed no-op with no mutation step; incomplete, stale, or ambiguous observations are refused rather than converted to a plan.
- AC-007 [US-001] [FR-007]: Given each registered issue-schema fault class, when its generated or independently authored mutation is applied, then validation turns red with the expected diagnostic while the unmutated control remains green.
- AC-008 [US-001] [FR-008]: Given an exact committed candidate, when the registered Q3 validator runs offline, then it passes without a live GitHub write, production credential, Q4 sandbox claim, or production-to-qualification dependency inversion.

## Functional Requirements
- FR-001: resolve repository, issue, issue-type, field, and option semantic identities to unique live IDs only from complete typed observations (Stories: US-001; Acceptance: AC-001)
- FR-002: verify expected field data types and exact closed option sets before producing authoritative values or plans (Stories: US-001; Acceptance: AC-002)
- FR-003: preserve unknown, unsupported, unauthorized, incomplete, stale, duplicate, and missing outcomes without inventing absence (Stories: US-001; Acceptance: AC-003)
- FR-004: read complete current values with revision and pagination evidence (Stories: US-001; Acceptance: AC-004)
- FR-005: derive deterministic guarded create, update, and clear plans bound to expected revisions and stable idempotency identities (Stories: US-001; Acceptance: AC-005)
- FR-006: keep no-op desired state mutation-free and refuse plans derived from incomplete or ambiguous observations (Stories: US-001; Acceptance: AC-006)
- FR-007: generated cases and independently authored pagination, duplicate-identity, type-drift, option-drift, stale-revision, incomplete-observation, and no-op mutations must turn red (Stories: US-001; Acceptance: AC-007)
- FR-008: dotnet fsi eng/validate-github-issue-field.fsx -- . must pass Q3 offline without live writes, production credentials, or Q4 sandbox claims (Stories: US-001; Acceptance: AC-008)

## Ambiguities
- AMB-001 open: whether semantic identities are defined by stable names alone or by repository-owned expected type and option declarations
- AMB-002 open: whether extra live options are tolerated or make a closed option set drifted
- AMB-003 open: whether create plans may be emitted when repository or issue observations have no revision
- AMB-004 open: whether identical current and desired field values emit a no-op receipt or no mutation step
- AMB-005 open: whether fixtures may call GitHub or must remain deterministic and offline

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-2-issue-field-adapter`.
