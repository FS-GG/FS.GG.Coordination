---
schemaVersion: 1
workId: gs2-04-5-comment-projection-adapter
title: Gs2 04 5 Comment Projection Adapter
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.5 typed GitHub comment/projection adapter Specification

Prose status: specified

## User Value
Operators can inspect trustworthy human-facing comment projections whose relationship to durable journal authority is explicit and tamper-evident.

## Scope
- SB-001: repository-local typed comment observations, marker parsing and binding validation, tamper classification, deterministic durable-authority projection regeneration plans, tests, fixtures, Q3 validation, evidence, and SDD artifacts

## Non-Goals
- SB-002: no live GitHub writes, comment-order concurrency authority, sharded Git journal CAS implementation, Q4 sandbox correspondence, deployment, publication, or successor-unit work

## User Stories
- US-001 (P1): As an operator, I can distinguish trustworthy, stale, damaged, and unreadable human projections and regenerate them deterministically from durable authority without granting comments transition authority.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given complete paginated comment observations, when the adapter reads them, then every server-issued database identity, node identity, creation timestamp, update timestamp, author identity, body bytes, page count, node count, and terminal-page proof is preserved in stable server order.
- AC-002 [US-001] [FR-002]: Given a recognized projection marker, when marker JSON is parsed, then schema, subject, journal kind, shard, generation, journal commit, authority digest, and projection digest are validated canonically before the marker can be treated as bound evidence.
- AC-003 [US-001] [FR-003]: Given duplicate identities, reordered pages, edits, deletions, malformed JSON, marker tampering, journal-digest mismatch, unsupported schema, unauthorized observation, incomplete pagination, or unreadable transport, when classification runs, then each outcome remains distinct and no absence, authority, or trusted projection is invented.
- AC-004 [US-001] [FR-004]: Given a valid marker and a matching durable journal authority snapshot, when projection trust is evaluated, then equality is based on canonical subject, generation, commit, authority digest, and rendered projection digest rather than comment position or recency.
- AC-005 [US-001] [FR-005]: Given one durable authority snapshot and a rendering policy, when projection regeneration is requested, then the adapter returns deterministic UTF-8 body bytes and a guarded create, replace, or no-op plan bound to expected comment revision and a stable idempotency identity.
- AC-006 [US-001] [FR-006]: Given a regeneration plan whose comment identity, update timestamp, body digest, marker binding, or authority snapshot differs from a mandatory re-read, when readiness is checked, then the adapter requires re-read and replan and grants no effect authority.
- AC-007 [US-001] [FR-007]: Given a complete observed post-state for a projection plan, when verification runs, then success requires the exact intended marker/body binding and byte-equivalent preservation of unrelated comments; concurrent, extra, missing, or indeterminate changes are refused.
- AC-008 [US-001] [FR-008]: Given each registered comment/projection fault class, when its generated or independently authored mutation is applied, then validation turns red with the expected diagnostic while the unmutated control remains green and the registered Q3 validator performs no live write.

## Functional Requirements
- FR-001: The adapter MUST preserve complete paginated comment observations with server-issued database/node identities, creation/update timestamps, author identity, body bytes, stable server order, and terminal completeness evidence. (Stories: US-001; Acceptance: AC-001)
- FR-002: The adapter MUST parse recognized marker JSON canonically and validate schema, subject, journal kind, shard, fencing generation, journal commit, authority digest, and rendered projection digest. (Stories: US-001; Acceptance: AC-002)
- FR-003: The adapter MUST preserve duplicate identity, reordered page, edited, deleted, malformed, tampered, journal-digest mismatch, unsupported, unauthorized, incomplete, and unreadable outcomes without inventing absence, authority, or trust. (Stories: US-001; Acceptance: AC-003)
- FR-004: The adapter MUST establish projection trust only from an exact canonical binding to a supplied durable journal authority snapshot and MUST never treat comment position, order, timestamp, or recency as transition authority. (Stories: US-001; Acceptance: AC-004)
- FR-005: The adapter MUST derive deterministic UTF-8 projection bytes and guarded create, replace, or no-op plans bound to expected comment revision, durable authority identity, rendering policy, causation, and stable idempotency identity. (Stories: US-001; Acceptance: AC-005)
- FR-006: The adapter MUST require any stale comment identity, update timestamp, body digest, marker binding, or authority snapshot to be re-read and replanned before an effect can be attempted. (Stories: US-001; Acceptance: AC-006)
- FR-007: The adapter MUST verify the exact intended post-state marker/body binding and unchanged unrelated comments while refusing concurrent, extra, missing, partial, or indeterminate outcomes. (Stories: US-001; Acceptance: AC-007)
- FR-008: Generated cases and independently authored pagination, duplicate-identity, reordered-page, edit, delete, tamper, malformed-JSON, journal-digest mismatch, incomplete-observation, stale-revision, concurrent-change, and no-op mutations MUST turn red, and `dotnet fsi eng/validate-github-comment-projection.fsx -- .` MUST pass Q3 offline without live writes, production credentials, Q4 claims, or production-to-qualification inversion. (Stories: US-001; Acceptance: AC-008)

## Ambiguities
- AMB-001 open: which server-issued comment identity and ordering facts are mandatory for completeness and duplicate detection
- AMB-002 open: the canonical JSON and digest rules for projection markers and referenced journal authority
- AMB-003 open: how edited, deleted, missing, malformed, and tampered comments remain distinguishable when only current GitHub observations are available
- AMB-004 open: whether comment order, update time, or latest matching marker may ever select concurrency authority
- AMB-005 open: which authority facts and rendering inputs determine byte-stable projection output and idempotency identity
- AMB-006 open: which stale pre-state and post-state differences force re-read, replan, or indeterminate refusal
- AMB-007 open: whether Q3 fixtures may call live GitHub endpoints or implement journal CAS behavior

## Public Or Tool-Facing Impact
- Adds a public typed F# comment/projection adapter surface and a public qualification contract; no existing public surface is removed.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-5-comment-projection-adapter`.
