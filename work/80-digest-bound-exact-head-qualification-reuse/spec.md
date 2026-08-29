---
schemaVersion: 1
workId: 80-digest-bound-exact-head-qualification-reuse
title: Digest Bound Exact Head Qualification Reuse
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Digest Bound Exact Head Qualification Reuse Specification

Prose status: specified

## User Value
Maintainers can avoid repeating an expensive protected qualification when—and only when—an independently reviewable receipt proves that the current exact head has the same complete qualification subject as a retained successful run.

## Scope
- SB-001: Define a canonical qualification-subject identity, a content-addressed reuse-receipt schema, deterministic generation and strict validation, a read-only CLI decision boundary, workflow selection, artifact continuity, exact-head acceptance, negative controls, and avoided-minute metrics.
- SB-002: Bind the complete closed subject: every tracked byte and mode in the repository Git tree; the qualification plan, workflow, gate, toolchain, dependency, model, compiler, fixture, environment, and command component identities; retained result and artifact bytes; prior terminal run identity; and the current independent-review policy identity.
- SB-003: Keep the prior qualified head and the current candidate head distinct: prior results remain immutable evidence, while a successful reuse decision explicitly authorizes only the named current exact head.

## Non-Goals
- SB-004: Do not weaken, delete, or partially reuse a required gate; treat failed, cancelled, timed-out, incomplete, superseded, malformed, unsupported, or unavailable prior evidence as a cache miss or refusal.
- SB-005: Do not implement Quint Q1/Q2 batching, solver tuning, process reuse, or general CI topology changes owned by #79 and #78.
- SB-006: Do not trust mutable cache keys, broad restore prefixes, branch names, PR numbers, commit messages, or GitHub cache presence as qualification authority.

## User Stories
- US-001 (P1): As a maintainer, I can reuse a retained successful qualification for a new exact head whose complete qualification subject is byte-identical.
- US-002 (P1): As an independent reviewer, I can audit which prior run, artifacts, review, contract inputs, and digests authorized reuse for the current head.
- US-003 (P1): As an operator, I receive an explicit execute or refuse decision whenever any bound input is changed, missing, stale, malformed, unsupported, or unverifiable.
- US-004 (P2): As a process owner, I can measure reuse hits, misses, refusals, and avoided runner minutes without those metrics influencing authorization.

## Acceptance Scenarios
- AC-001 [US-001] [US-002] [FR-001] [FR-002] [FR-003]: Given a retained terminal-success run and byte-identical closed subject inputs, results, artifacts, environment contract, and review-policy identity, when a receipt is generated and validated for a different current head, then the decision is `reuse`, canonical receipt bytes are deterministic, and the authorization names that current head; the ordinary exact-head independent review remains required before delivery.
- AC-002 [US-003] [FR-002] [FR-004] [FR-005]: Given any independently mutated source, workflow, gate, command, model, toolchain, dependency, fixture, environment, result, artifact, run outcome, reviewer, review freshness, schema, or digest input, when validation runs, then it cannot return `reuse` and identifies the exact failed binding.
- AC-003 [US-003] [FR-004] [FR-006]: Given evidence is absent, malformed, partial, failed, cancelled, timed out, superseded, unsupported, unavailable, duplicated, stale, non-canonical, or bound to another prior/current head, when the decision is requested, then it fails closed as `execute` when a safe full run remains available or `refuse` when authority itself is contradictory or unreadable.
- AC-004 [US-001] [US-002] [FR-003] [FR-007]: Given a valid reuse decision, when exact-head acceptance runs, then all retained artifacts are downloaded and re-hashed, the accepted reviewer decision remains current, the prior result is not relabelled as if executed on the current head, and the final evidence binds the current head to the prior run and receipt.
- AC-005 [US-003] [FR-008]: Given two candidates race, a prior run is deleted, or a candidate head moves during selection, when the workflow reaches acceptance, then stale selection cannot authorize the moved head and execution or red is required.
- AC-006 [US-004] [FR-009]: Given hosted qualification samples before and after the change, when telemetry is compared, then reuse hit/miss/refusal counts and avoided minutes are reported separately from authorization, while unchanged full-execution latency remains visible.

## Functional Requirements
- FR-001: The system MUST deterministically construct one canonical qualification-subject document from an explicitly versioned, duplicate-free, ordered closed set of semantic inputs and calculate its SHA-256 over canonical bytes. Commit identity and review metadata MUST remain separate from the semantic subject so provenance-only head movement can be represented without hiding semantic drift. (Stories: US-001, US-002; Acceptance: AC-001)
- FR-002: The subject MUST bind exact immutable identities for the complete tracked Git tree and separately expose audit identities for the qualification plan, generated workflow, gate entry points, executed command contract, pinned toolchain and dependency manifests, Quint/model/compiler inputs, fixtures, runner environment contract, retained result set, artifact bytes, and independent-review policy. Unknown fields, mutable references, missing categories, duplicate identities, unsupported versions, or non-canonical digest forms MUST fail closed. (Stories: US-001, US-002, US-003; Acceptance: AC-001, AC-002)
- FR-003: A reuse receipt MUST bind schema version, prior head and terminal-success run/attempt, current exact head, subject digest, every component digest, prior evidence-manifest digest, retained artifact identities and byte digests, review-policy identity, creation instant, live artifact-retention boundary, decision, reason, and self-digest. Generation MUST be canonical and deterministic. The separately recorded current-head independent-review decision completes delivery authority and MUST cite or execute the reuse route; it is not retroactively embedded in an already completed workflow artifact. (Stories: US-001, US-002; Acceptance: AC-001, AC-004)
- FR-004: Validation MUST expose exactly three typed outcomes: `reuse` only for a complete valid equivalence proof, `execute` for a safe cache miss where fresh qualification can restore authority, and `refuse` for contradictory, malformed, unsupported, or unverifiable authority. No error, timeout, lookup failure, or unknown state may be coerced to `reuse`. (Stories: US-003; Acceptance: AC-002, AC-003)
- FR-005: Every bound input class MUST have an independent inversion proving that omission, substitution, mutation, duplication, reordering where order is semantic, stale review, changed result, lost artifact, altered environment, or digest tampering forces `execute` or `refuse`. (Stories: US-002, US-003; Acceptance: AC-002)
- FR-006: Reusable prior evidence MUST be terminal success from the protected workflow and exact run attempt, must not be failed, cancelled, timed out, skipped, superseded, or partial, and MUST remain within the declared freshness and artifact-retention boundary at acceptance time. (Stories: US-003; Acceptance: AC-003)
- FR-007: Reuse MUST preserve provenance: retained artifacts are retrieved and re-hashed; prior results retain their original head/run identity; the reuse receipt explains the equivalence; the current independent review remains bound to the current review generation and records the route it reviewed; and final acceptance evidence names the actual candidate head. (Stories: US-001, US-002; Acceptance: AC-004)
- FR-008: Selection and acceptance MUST re-read the current candidate head and the retained run/artifact facts so candidate movement, deletion, expiry, replacement, concurrent execution, or stale selection cannot create a false hit. Repeated processing of the same valid receipt MUST be idempotent. (Stories: US-003; Acceptance: AC-005)
- FR-009: Telemetry MUST record decision reason, hit/miss/refusal, prior and current run identities, subject digest, full-execution and reuse-path duration, and avoided runner minutes. Telemetry is observational only and MUST NOT participate in or override the authorization decision. (Stories: US-004; Acceptance: AC-006)

## Ambiguities
- AMB-001: Which repository paths and generated contracts form the closed semantic subject, and which provenance-only files may differ without forcing execution?
- AMB-002: What freshness boundary applies to retained run results, artifacts, and independent review when artifact retention can expire independently?
- AMB-003: Which mismatches are safe `execute` misses and which contradictory or unreadable facts require `refuse`?
- AMB-004: How does the workflow discover a prior candidate without granting mutable GitHub cache keys or search ordering authority?
- AMB-005: Which runner environment facts must match exactly across GitHub-hosted executions, given ephemeral host details can differ without changing gate semantics?

## Public Or Tool-Facing Impact
- Adds a versioned qualification-subject and reuse-receipt JSON contract, typed generator/validator and CLI decision surface, and workflow-visible `reuse|execute|refuse` result.
- Changes protected qualification orchestration but not the required gate set, branch protection, or independent review requirement.

## Lifecycle Notes
- The content-addressed receipt is authorization evidence, not a general-purpose build cache.
- Next lifecycle action: `fsgg-sdd clarify --work 80-digest-bound-exact-head-qualification-reuse`.
