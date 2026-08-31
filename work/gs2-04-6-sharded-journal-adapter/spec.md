---
schemaVersion: 1
workId: gs2-04-6-sharded-journal-adapter
title: Gs2 04 6 Sharded Journal Adapter
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.6 Sharded Git Journal Adapter Specification

Prose status: specified

## User Value
FS.GG.Coordination can deterministically validate, plan, and reconcile protected sharded Git-journal authority without treating comments or ambiguous transport outcomes as success.

## Scope
- SB-001: Repository-local Q3 typed adapter, canonical journal codec, exact expected-parent CAS planning, outcome reconciliation, fencing, protected ruleset observation validation, multi-aggregate saga planning, tests, and retained evidence.
- SB-002: Consume the already-protected authority repository/ruleset qualification as a bound observation fixture; do not mint credentials or mutate GitHub.

## Non-Goals
- SB-003: No live GitHub writes, repository/ruleset settings mutation, deployment, publication, stable release, runtime cutover, destructive compaction, or Q4 sandbox claim.
- SB-004: Do not implement GS2-04.7 repository/settings behavior or any successor unit.

## User Stories
- US-001 (P1): As a coordination engine, I can encode and validate an append-only journal state so replay and audit agree on the same authority.
- US-002 (P1): As a journal writer, I can plan an exact-parent CAS and reconcile every transport outcome without inventing success.
- US-003 (P1): As an effect executor or auditor, I can reject stale fencing tokens, drifted protection, and nondeterministic multi-aggregate work before authority escapes.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given a canonical aggregate id, when its journal location is derived, then the adapter emits the length-prefixed SHA-256 digest, its first two lowercase hex characters as shard, and only `refs/heads/fsgg/v2/journal/<kind>/<shard>`.
- AC-002 [US-001] [FR-002]: Given head, event, snapshot, commit, and ancestry observations, when they are validated, then only canonical bytes, matching digests, one-parent append-only ancestry, monotonic generations, correct shards, and supported schemas contribute authority.
- AC-003 [US-002] [FR-003]: Given an observed branch parent and proposed event, when a mutation is planned and reconciled, then the plan binds an exact old-OID lease and stable operation identity and reports accepted, parent conflict, definite refusal, or unknown requiring authoritative reread.
- AC-004 [US-003] [FR-004]: Given an effect grant, when execution is checked, then its aggregate, journal commit, and generation must equal the complete current head and terminal heads refuse ordinary effects.
- AC-005 [US-003] [FR-005]: Given multiple aggregates, when acquisition or compensation is planned, then keys are globally sorted, the full touch set is journaled before effects, and compensations run idempotently in reverse applied order without erasing history.
- AC-006 [US-003] [FR-006]: Given repository, ruleset, and effective-rule observations, when protection is validated, then the exact repository/id/name/target/rule split and writer-App-only bypass are required and integrity has no bypass.
- AC-007 [US-001] [FR-007]: Given comments, webhooks, incomplete observations, branch divergence, or object existence, when authority is evaluated, then none can substitute for a complete current journal observation and each failure remains typed.
- AC-008 [US-001] [FR-008]: Given the registered Q3 gate and independent fixture suite, when every required mutant is applied, then each mutant is red while the unmutated candidate remains green and evidence binds the exact candidate.

## Functional Requirements
- FR-001: The adapter MUST normalize non-empty aggregate ids as lowercase UTF-8, length-prefix before SHA-256 hashing, derive the first two lowercase hex characters as shard, validate journal kinds, and emit only the exact protected journal branch family. (covers AC-001)
- FR-002: The adapter MUST produce and validate recursively key-sorted UTF-8 LF canonical head, event, and checkpoint snapshot bytes; bind all declared digests; require correct aggregate/shard/schema, unique monotonic generations, exactly one parent after the root, append-only ancestry, terminal semantics, and replay-equivalent compaction. (covers AC-002)
- FR-003: The adapter MUST derive a stable-operation one-parent fast-forward proposal and exact `--force-with-lease=<ref>:<observed-object-id>` receive-pack plan, and MUST reconcile outcomes as accepted, parent-conflict, definite-refusal, or response-unknown-requires-reread using exact operation/commit/tree/head/generation evidence. (covers AC-003)
- FR-004: The adapter MUST authorize an externally visible effect only when a complete fresh current head exactly matches the grant aggregate, journal commit, and monotonically increasing generation; stale, terminal, incomplete, divergent, deleted, or unreadable authority MUST refuse. (covers AC-004)
- FR-005: The adapter MUST sort multi-aggregate acquisition by `(journal-kind, shard, aggregate-digest)`, persist the complete touch set before effects, release unconsumed grants on conflict, and derive idempotent reverse-order fenced compensation for applied effects without deleting original history. (covers AC-005)
- FR-006: The adapter MUST validate the dedicated authority repository id `1351660651`, active `v2-journal-writer` ruleset `21872113`, active `v2-journal-integrity` ruleset `21872115`, exact `refs/heads/fsgg/v2/journal/**/*` target, writer App `4166418` as the only writer bypass, no integrity bypass, and all required effective branch rules from complete observations. (covers AC-006)
- FR-007: The adapter MUST treat comments and webhooks only as projections or wake-up hints, MUST never infer success from object existence, and MUST preserve incomplete, unsupported, unauthorized, unreadable, unknown-schema, ambiguous, rewind, deletion, divergence, and drift outcomes as typed non-authority. (covers AC-007)
- FR-008: The registered `github-sharded-journal-contract` Q3 validator and independent tests MUST cover positive cases plus wrong-shard, missing-parent, duplicate-generation, digest-mismatch, unknown-schema, stale-parent, ambiguous-response, rewind, deletion, divergence, stale-fence, terminal-append, compaction, ruleset-drift, target-pattern, bypass, acquisition-order, and compensation mutations with exact-candidate evidence. (covers AC-008)

## Ambiguities
- None. The accepted GS2-03.10 protocol, registered GS2-04.6 unit contract, and protected-administration evidence close the required design choices.

## Public Or Tool-Facing Impact
- Add a public `.fsi` journal-adapter surface in `FS.GG.Coordination.GitHub`.
- Add one repository-owned Q3 validator and retained typed result artifact; do not change the registered command identity.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-6-sharded-journal-adapter`.
