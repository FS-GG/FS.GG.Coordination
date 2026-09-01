---
schemaVersion: 1
workId: 208-claim-touch-sets
title: GS2-05.5 claims and touch sets
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-05.5 claims and touch sets Specification

Prose status: specified

## User Value
Maintainers can claim one or several work conflict domains without relying on comment order or lease clocks for authority, and every later effect can prove that its exact fenced grant is still current.

## Scope
- SB-001: Register and implement the offline GS2-05.5 claim and touch-set contract over the existing protected sharded-journal adapter.
- SB-002: Add a public qualification contract and controlled-fixture GitHub adapter for canonical claims, touch conflicts, successor acquisition, multi-touch sagas, and effect-time reproof.
- SB-003: Preserve the existing Quint protocol and ShardedJournalAdapter contracts as formal and storage authority while registering one exact Q3 qualification gate.

## Non-Goals
- SB-004: No production GitHub write, credential, journal App or ruleset administration, Project-field authority, deployment, publication, or stable release.
- SB-005: Do not implement review/delivery, lifecycle projection, fleet shadowing, cutover, or any successor unit.
- SB-006: Do not change the Quint specification or treat lease expiry, clocks, Project/native fields, comments, labels, webhooks, API success, or object existence as claim authority.

## User Stories
- US-001 (P1): As a worker, I can acquire one canonical claim by expected-parent CAS and receive a monotonically fenced grant.
- US-002 (P1): As a successor, I can become eligible after expiry but cannot own or act until my successor CAS is authoritative.
- US-003 (P1): As an operation owner, I can acquire multiple touch domains deterministically, prove every grant before effects, and recover conflicts through fenced compensation.
- US-004 (P1): As a reviewer, I can prove that projection hints and clocks never authorize effects and that all claim decisions satisfy the existing Quint model.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given a canonical subject, owner, touch set, complete current journal, and valid proposal, claim planning emits one deterministic expected-parent CAS and a grant bound to the resulting commit and next generation; sibling proposals cannot both authorize.
- AC-002 [US-002] [FR-003]: Given an active lease, a successor is ineligible; given an expired lease, the successor is eligible to attempt CAS but still cannot act until the authoritative head records its owner, commit, generation, and touch set.
- AC-003 [US-003] [FR-004] [FR-005]: Given several touch domains, planning canonicalizes conflicts, rejects duplicates/overlap with active foreign claims, sorts acquisitions, persists the entire plan before effects, and on conflict releases the unconsumed suffix and compensates applied grants in reverse order.
- AC-004 [US-003] [US-004] [FR-006] [FR-007]: Given an external effect, complete authoritative reread authorizes only the exact current nonterminal owner/commit/generation/touch grant; stale, incomplete, divergent, terminal, wrong-owner, wrong-touch, or projection-only evidence refuses.
- AC-005 [US-004] [FR-008] [FR-009]: Generated and independently authored sibling-CAS, generation, expiry, successor, projection, overlap, ordering, persistence, stale-fence, compensation, replay, bounded-cost, Quint, and accepted-prerequisite inversions all pass without production writes.

## Functional Requirements
- FR-001: Claim identity shall canonicalize the qualified subject and every touch path, reject empty, duplicate, noncanonical, traversal, or root-wide touches, and derive deterministic claim and conflict-domain journal addresses. (Stories: US-001, US-003; Acceptance: AC-001, AC-003)
- FR-002: Acquisition shall consume a complete validated claim-journal observation and emit one stable operation identity and expected-parent CAS whose next generation is exactly current plus one; only the authoritative reread of that exact proposal yields a fenced claim grant. (Stories: US-001; Acceptance: AC-001)
- FR-003: Lease state shall be a journal payload fact. An unexpired current claim blocks a rival; expiry makes a rival eligible to attempt a successor CAS but does not revoke, transfer, or authorize before that CAS is accepted and reread. (Stories: US-002; Acceptance: AC-002)
- FR-004: Touch conflict shall be decided from canonical authority-bearing touch sets using equal or ancestor/descendant path overlap within one repository, never from Project fields, issue fields, comments, labels, webhooks, or copied body metadata. (Stories: US-003, US-004; Acceptance: AC-003, AC-004)
- FR-005: Multi-touch planning shall reject duplicate domains and active foreign overlaps, sort by the existing `(journal-kind, shard, aggregate-digest)` total order, and persist the complete touch set and expected generations in the operation journal before any effect. Conflict recovery shall release the unconsumed suffix and append fenced compensation for applied grants in reverse order while retaining original results. (Stories: US-003; Acceptance: AC-003)
- FR-006: Effect authorization shall reread each complete authoritative claim journal immediately before the effect and match subject, owner, complete touch set, journal commit, generation, and nonterminal head. Any unavailable, incomplete, stale, divergent, terminal, wrong-owner, or wrong-touch fact shall refuse. (Stories: US-003, US-004; Acceptance: AC-004)
- FR-007: Pure planning and controlled-fixture application shall be deterministic, byte-stable, idempotent on exact replay, explicit about indeterminate responses, and bounded by declared touch cardinality; unrelated Project, Backlog, comment, or webhook cardinality shall not change plan bytes, decisions, or cost. (Stories: US-001, US-003; Acceptance: AC-001, AC-003, AC-004)
- FR-008: The public surface shall be additive in the qualification-contract and GitHub-adapter assemblies, remain offline/pure or controlled-fixture only, and preserve all existing `ShardedJournalAdapter` and compiled Quint behavior. (Stories: US-004; Acceptance: AC-005)
- FR-009: The unit index shall register GS2-05.5 with accepted GS2-05.4 as its sole prerequisite and one exact Q3 command. Generated and independently authored controls shall bind every acceptance boundary, canonical Quint compiler/pure-model results, and roadmap prerequisite inversion. (Stories: US-001, US-002, US-003, US-004; Acceptance: AC-005)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds public F# claim intent, observation, grant, successor, multi-touch plan, effect-proof, diagnostic, and controlled-fixture result types/functions without removing or changing existing members.
- Adds one exact Q3 `github-claim-touch-set-contract` command and registers GS2-05.5 after accepted GS2-05.4.
- The new surface is offline and additive; it defines controlled-fixture target semantics, not a production writer or v2 cutover.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 208-claim-touch-sets`.
