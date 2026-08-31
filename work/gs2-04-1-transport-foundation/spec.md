---
schemaVersion: 1
workId: gs2-04-1-transport-foundation
title: GS2-04.1 typed GitHub transport foundation
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.1 typed GitHub transport foundation Specification

Prose status: specified

## User Value
preserve GitHub REST and GraphQL transport meaning through typed repository-local contracts and deterministic offline evidence

## Scope
- SB-001: src/FS.GG.Coordination.GitHub, src/FS.GG.Coordination.Qualification.Contracts, unit and architecture tests, fixtures, eng validation, evidence, and SDD work artifacts

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a user, I can preserve GitHub REST and GraphQL transport meaning through typed repository-local contracts and deterministic offline evidence.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given a REST or GraphQL operation, when it crosses the transport boundary, then its method, URI or document, API version, headers, body or variables, idempotency class, and typed response envelope remain explicit.
- AC-002 [US-001] [FR-002]: Given a transient response, when retry is considered, then replay occurs only for an explicitly replay-safe request and otherwise fails closed.
- AC-003 [US-001] [FR-003]: Given an absent, mismatched, or unreadable revision, when a conditional operation is evaluated, then the distinct revision outcome is preserved without collapsing it into success.
- AC-004 [US-001] [FR-004]: Given missing or exhausted authoritative rate-budget facts, when another request is scheduled, then scheduling is refused with a typed reason.
- AC-005 [US-001] [FR-005]: Given multi-page REST or GraphQL fixtures, when traversal runs, then every page is consumed exactly once and a missing continuation is an incomplete result rather than partial success.
- AC-006 [US-001] [FR-006]: Given a captured request or response containing credentials and unstable identifiers, when the fixture is serialized, then the committed projection is deterministic and contains none of the sensitive values.
- AC-007 [US-001] [FR-007]: Given each registered transport fault class, when its generated or independent mutation is applied, then validation turns red with the expected typed diagnostic while the unmutated control remains green.
- AC-008 [US-001] [FR-008]: Given the repository at an exact candidate commit, when the registered Q3 command runs offline, then it passes without a live GitHub write, production credential, or Q4 correspondence claim.

## Functional Requirements
- FR-001: The transport MUST represent every REST request with the required GitHub API version and typed method, URI, headers, body, idempotency class, and response envelope, and MUST represent GraphQL documents, variables, and response envelopes explicitly. (Stories: US-001; Acceptance: AC-001)
- FR-002: Retry MUST occur only for explicitly transient outcomes and only when the request's declared idempotency class permits replay; exhausted or unsafe replay MUST fail closed. (Stories: US-001; Acceptance: AC-002)
- FR-003: ETag and revision preconditions MUST preserve stale-write meaning and distinguish absent, mismatched, and unreadable revisions. (Stories: US-001; Acceptance: AC-003)
- FR-004: Rate-budget state MUST account for limit, remaining, reset, and cost and MUST refuse scheduling when authoritative budget facts are absent or exhausted. (Stories: US-001; Acceptance: AC-004)
- FR-005: REST pagination MUST follow every Link next relation to completion, GraphQL traversal MUST follow pageInfo endCursor while hasNextPage is true, and either traversal MUST report incomplete evidence instead of returning partial success. (Stories: US-001; Acceptance: AC-005)
- FR-006: Deterministic fixture capture MUST remove tokens, cookies, authorization, unstable request identifiers, and declared private payload fields before evidence can be committed. (Stories: US-001; Acceptance: AC-006)
- FR-007: Generated and independent controls MUST turn red for truncation, unsafe replay, stale revision, rate exhaustion, incomplete pagination, redaction leakage, and ambiguous mapping while their unmutated controls remain green. (Stories: US-001; Acceptance: AC-007)
- FR-008: `dotnet fsi eng/validate-github-transport.fsx -- .` MUST pass Q3 without live GitHub writes, production credentials, or Q4 sandbox claims. (Stories: US-001; Acceptance: AC-008)

## Ambiguities
- AMB-001: Does retry safety derive from the HTTP method or from an explicit request idempotency classification?
- AMB-002: Does the Q3 transport own repository or organization authority decisions, or only preserve typed wire meaning for later Q4 adapters?
- AMB-003: May a pagination traversal return accumulated items when the continuation chain is malformed or truncated?
- AMB-004: Is redaction a deny-list of known secret names or a deterministic allow-listed evidence projection that rejects undisclosed sensitive fields?
- AMB-005: May Q3 validation use GitHub credentials or external GitHub endpoints when collecting fixtures?

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-1-transport-foundation`.
