---
schemaVersion: 1
workId: gs2-04-9-sandbox-qualification-closure
title: Gs2 04 9 Sandbox Qualification Closure
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.9 Sandbox Qualification Closure Specification

Prose status: specified

## User Value
Operators can prove comprehensive GitHub adapter correspondence and cleanup in an isolated sandbox without conferring production authority.

## Scope
- SB-001: Add repository-local typed sandbox plan, execution result, compensation, cleanup, and closure-evidence contracts plus a literal `github-sandbox-closure-contract` Q4 validator.
- SB-002: Run the eight registered GS2-04 Q3 validators cold, then Q4, in one exact-candidate comprehensive route.
- SB-003: Provide synthetic contract coverage and a protected live route that consumes only separately configured non-production identity and isolated sandbox coordinates.

## Non-Goals
- SB-004: No production-capable human token, production repository, production organization, production Project, stable package/feed, or durable release may be mutated or admitted as evidence.
- SB-005: Do not implement GS2-05 or later roadmap units, weaken a prior Q3 contract, or treat synthetic evidence as live closure.

## User Stories
- US-001 (P1): As an operator, I can execute every GitHub substrate adapter against bounded disposable state and retain proof that correspondence and cleanup both completed.
- US-002 (P1): As a security reviewer, I can prove that a production-capable identity, credential, target, stale fence, ambiguous response, or exceeded quota is refused before authority is granted.
- US-003 (P1): As a roadmap auditor, I can replay one immutable exact-candidate closure receipt binding all eight cold Q3 results and the Q4 live result.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given an explicitly isolated test repository and Project, when a sandbox plan is normalized, then identity, credential class, targets, quotas, pre-state, expected revisions, operations, compensations, cleanup policy, expiry, and evidence coordinates are complete and canonically bound.
- AC-002 [US-001] [FR-002]: Given a qualified plan, when effects execute, then each operation is fenced by expected pre-state/revision, its response is classified, and an authoritative reread proves the observed post-state before the next step.
- AC-003 [US-001] [FR-003]: Given successful or partially failed effects, when closure runs, then reverse-order compensation executes, every target is authoritatively reread, and residue or ambiguous cleanup makes the result red.
- AC-004 [US-002] [FR-004]: Given identity, credential, target, quota, fence, response, or receipt substitutions, when independently validated, then each mutant is red without relying on another mutation.
- AC-005 [US-003] [FR-005]: Given the exact candidate, when comprehensive qualification runs, then all eight registered Q3 commands start cold and their distinct result identities are present before Q4 can pass.
- AC-006 [US-003] [FR-006]: Given execution and cleanup observations, when closure evidence is emitted, then it binds contract, candidate, workflow/run, plan/result/cleanup digests, expiry, and immutable evidence coordinates without secrets.
- AC-007 [US-002] [FR-007]: Given no separately configured non-production credential or a production-capable identity, when live mode starts, then it fails closed before any GitHub write while synthetic contract tests remain runnable.
- AC-008 [US-001] [US-002] [US-003] [FR-008]: Given the protected workflow, when dispatched with authorized sandbox configuration, then live Q4 uses only the isolated identity, enforces quotas and expiry, performs cleanup, uploads evidence, and refuses warm reuse.

## Functional Requirements
- FR-001: Public typed contracts MUST represent sandbox identity, credential classification, repository and Project targets, per-surface quotas, pre-state, expected revision, operations, compensations, cleanup policy, expiry, and immutable evidence bindings with canonical validation. (covers AC-001)
- FR-002: The executor MUST reject stale pre-state or revision before mutation, classify ambiguous transport outcomes without inventing success, and require authoritative post-effect rereads for every operation. (covers AC-002)
- FR-003: Closure MUST compensate in reverse order, reread every created or changed target, distinguish absent/restored/residual/ambiguous cleanup, and refuse a pass until zero residue is proven. (covers AC-003)
- FR-004: Independent tests MUST invert production identity, production target, production-capable credential, stale fence, ambiguous response, quota overflow, partial cleanup, substituted receipt, warm reuse, and omitted adapter, with each mutation alone making the validator red. (covers AC-004)
- FR-005: Comprehensive orchestration MUST invoke the registered issue-field, native-relation, Project, comment-projection, sharded-journal, repository-settings, and actions/release/feed Q3 commands plus the transport Q3 command in cold processes before the literal Q4 command. (covers AC-005)
- FR-006: The `github-sandbox-closure-contract` Q4 validator MUST accept only an exact-candidate immutable receipt binding the registered contract, all Q3 result identities, plan/result/cleanup digests, workflow/run identity, expiry, and evidence coordinates, and MUST never serialize secrets. (covers AC-006)
- FR-007: Live execution MUST require a separately configured non-production identity and isolated targets, reject the current production-capable human token and missing/ambiguous classification, and perform no write before readiness passes. (covers AC-007)
- FR-008: A protected workflow MUST expose explicit synthetic and live modes, prevent warm evidence reuse, enforce bounded quotas and expiry, always attempt cleanup, retain artifacts, and make unsuccessful cleanup or unavailable live authority non-green. (covers AC-008)

## Ambiguities
- AMB-001: The identity attributes that are sufficient to prove non-production status without trusting a caller-supplied label must be fixed.
- AMB-002: The minimum sandbox target set and per-adapter destructive operations needed for correspondence must be bounded.
- AMB-003: Ambiguous GitHub responses and cleanup after partial execution need a deterministic evidence classification.
- AMB-004: Cold-process proof and immutable evidence binding must prevent prior-run or cross-candidate reuse.

## Public Or Tool-Facing Impact
- Add a public `.fsi` sandbox-closure contract surface in `FS.GG.Coordination.GitHub`.
- Add the repository-owned `github-sandbox-closure-contract` Q4 command and protected comprehensive qualification workflow route without changing its registered command identity.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-9-sandbox-qualification-closure`.
