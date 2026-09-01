---
schemaVersion: 1
workId: gs2-05-3-intake
title: GS2-05.3 intake contract
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-05.3 intake contract Specification

Prose status: specified

## User Value
Operators can admit work through one deterministic v2 intake boundary whose decisions are inspectable, replayable, and fail closed.

## Scope
- SB-001: Repository-local validate, plan, controlled-fixture apply, live-inspection abstraction, protocol initialization, qualification, and immutable evidence for issues, native types and fields, Project membership, hierarchy, and dependencies.

## Non-Goals
- SB-002: Do not perform production GitHub writes, organization issue-type or field mutation, live Project or issue mutation, deployment, publication, or stable release.
- SB-003: Do not compile roadmaps or implement GS2-05.4 or any later lifecycle behavior.

## User Stories
- US-001 (P1): As an operator, I can validate and plan intake without mutation and inspect the exact canonical decision before authorizing effects.
- US-002 (P1): As an executor, I can apply only the exact sealed plan against controlled state, recover deterministically from interruption, and refuse drift before further mutation.
- US-003 (P1): As a reviewer, I can inspect a complete observation and immutable evidence that preserves every issue, field, Project, hierarchy, dependency, and protocol-initialization meaning.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given a valid request and complete revision-bound observation, when validate and plan run repeatedly, then they emit identical canonical intent or diagnostics and an identical sealed plan without mutation.
- AC-002 [US-002] [FR-003] [FR-004]: Given an exact sealed plan and controlled fixture state, when apply runs, is interrupted, resumes, replays, rolls forward, or compensates, then every step is fenced, ordered, durably recorded, post-state verified, and idempotent.
- AC-003 [US-003] [FR-005] [FR-006]: Given paginated observations of all intake surfaces, when inspect and protocol initialization run, then completeness is proved, identities and relations are preserved, outcome uncertainty stays explicit, and only revision-bound initialization intents are produced.
- AC-004 [US-003] [FR-007] [FR-008]: Given the exact candidate and registered Q3 gate, when qualification runs, then canonical fixtures and independent inversions prove correspondence without a production write or successor behavior.

## Functional Requirements
- FR-001: Validation shall be pure, total, deterministic, mutation-free, and return canonical normalized intent or stable diagnostics for missing, malformed, ambiguous, contradictory, duplicate, cyclic, unauthorized, unsupported, partial, stale, and indeterminate inputs. (Stories: US-001; Acceptance: AC-001)
- FR-002: Planning shall require a complete revision-bound observation and emit a sealed byte-stable ordered immutable plan with stable operation identities, exact expected revisions and preconditions, postconditions, compensations, integrity digest, and canonical no-op. (Stories: US-001; Acceptance: AC-001)
- FR-003: Application shall accept only the exact sealed plan, re-observe every precondition and fencing revision before each controlled-fixture effect, refuse drift before mutation, execute in dependency order, verify authoritative post-state rather than response inference, and record durable outcomes. (Stories: US-002; Acceptance: AC-002)
- FR-004: Replay, partial-failure resume, roll-forward, reverse-order compensation, and repeated satisfied application shall be deterministic and idempotent, with no applied effect repeated under the same operation identity. (Stories: US-002; Acceptance: AC-002)
- FR-005: Inspection shall exhaust every page, reject missing/repeated/cyclic cursors and incomplete collections, prove terminal completeness, preserve issue identity, native type, fields, Project membership, hierarchy, dependencies, and repository scope, and distinguish absent, unauthorized, unsupported, partial, stale, and indeterminate state. (Stories: US-003; Acceptance: AC-003)
- FR-006: Protocol initialization shall emit only revision-bound initial journal, scheduling-intent, contract, touch-set, and projection intents required to admit work, with Project fields remaining projections rather than an execution ledger. (Stories: US-003; Acceptance: AC-003)
- FR-007: Canonical fixtures and independently authored expectations shall cover every registered positive and inversion family, and the Q3 validator shall bind exact source, corpus, result, and command identities into byte-stable immutable evidence. (Stories: US-003; Acceptance: AC-004)
- FR-008: The implementation shall perform no production GitHub write, require no production credential, and expose no roadmap compilation or GS2-05.4 successor behavior. (Stories: US-003; Acceptance: AC-004)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds repository-local public F# intake types and pure/effect-separated operations to the GitHub product boundary, plus the registered `eng/validate-github-intake.fsx` Q3 command.
- Does not expose a production CLI mutation command or grant a credentialed writer.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-05-3-intake`.
