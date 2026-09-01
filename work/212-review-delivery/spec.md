---
schemaVersion: 1
workId: 212-review-delivery
title: GS2-05.6 review and delivery
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-05.6 review and delivery Specification

Prose status: specified

## User Value
Maintainers can prove that accountable critique authorizes only its immutable full-snapshot review epoch and that completion exists only after protected-main verification.

## Scope
- SB-001: Add an offline additive review/delivery adapter, qualification contract, Q3 gate, corpus, independent expectations, tests, and accepted-prerequisite binding over the existing sharded journal.

## Non-Goals
- SB-002: No production GitHub write, credential, reviewer dispatch, merge execution, branch administration, deployment, publication, or stable release.
- SB-003: Do not implement lifecycle projection, fleet shadowing, cutover, or any successor unit.
- SB-004: Do not modify the Quint source or accept mutable PR metadata, comments, checks, API responses, historical verdicts, merge state, or object existence as current authority.

## User Stories
- US-001 (P1): As a maintainer, I can bind critique to an immutable full-snapshot epoch and know exactly when a pass becomes stale.
- US-002 (P1): As an accountable review authority, I can retain responsibility across snapshot changes while assigning a fresh phase seat for each epoch or succession.
- US-003 (P1): As a delivery owner, I can distinguish merge acceptance from protected-main verification and produce journal-bound delivery and done receipts.
- US-004 (P1): As a reviewer, I can prove all authorization and refusal boundaries offline against generated, independent, and formal evidence.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-003]: Given a canonical full snapshot and current passing seat, effect authorization succeeds only for its exact stable chain, epoch key, snapshot digest, journal commit, and generation; a snapshot change preserves the chain, creates a different epoch, and makes the historical pass non-authorizing.
- AC-002 [US-002] [FR-002] [FR-003]: Given one accountable authority, same-epoch succession preserves chain and epoch but requires a fresh seat, while a new snapshot opens a new epoch with a fresh seat without requiring another accountable authority; prior seats and verdicts remain historical only.
- AC-003 [US-003] [FR-004] [FR-005] [FR-006]: Given exact review authority and merge evidence, Merged can produce a delivery receipt but cannot produce done; only a successful protected-main run for the exact merge commit can seal a done receipt, and stale or divergent replay refuses.
- AC-004 [US-004] [FR-007] [FR-008] [FR-009]: Generated and independently authored epoch, seat, verdict, merge/run, replay, bounded-cost, Quint, and accepted-prerequisite inversions all pass without production writes.

## Functional Requirements
- FR-001: Derive a stable chain identity independently from a canonical complete snapshot digest and immutable ReviewEpochKey. (Stories: US-001; Acceptance: AC-001)
- FR-002: Require one accountable authority while every epoch and succession uses a fresh phase seat; succession is valid only inside one unchanged epoch. (Stories: US-002; Acceptance: AC-002)
- FR-003: Retain historical verdicts but authorize effects only from a complete current Review-journal reread matching chain, snapshot, epoch, current seat, pass verdict, commit, and generation. (Stories: US-001, US-002; Acceptance: AC-001, AC-002)
- FR-004: Distinguish NotMerged, Merged, and ProtectedVerified; require the exact merge commit and a successful protected-main run bound to it before done. (Stories: US-003; Acceptance: AC-003)
- FR-005: Seal journal-bound delivery and done receipts in the Operation journal; exact replay is idempotent and divergent replay refuses. (Stories: US-003; Acceptance: AC-003)
- FR-006: Fail closed on incomplete, stale, divergent, terminal, wrong-chain, wrong-epoch, wrong-seat, non-pass, merge/run mismatch, or unavailable authority. (Stories: US-001, US-003; Acceptance: AC-001, AC-003)
- FR-007: Keep planning deterministic, bounded by the declared review/delivery facts, offline, and free of production GitHub writes or credential paths. (Stories: US-004; Acceptance: AC-004)
- FR-008: Preserve the existing Quint source unchanged and re-run its canonical compiler, witnesses, and mutation qualification. (Stories: US-004; Acceptance: AC-004)
- FR-009: Register GS2-05.6 after accepted GS2-05.5 as its sole prerequisite with exactly one Q3 gate and generated plus independent controls. (Stories: US-004; Acceptance: AC-004)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds public F# review-chain, snapshot, epoch, seat, verdict, delivery state, receipt, cost, and typed-refusal contracts without changing existing members.
- Adds one exact Q3 `github-review-delivery-contract` command and registers GS2-05.6 after accepted GS2-05.5.
- The new surface is offline and additive; it defines controlled-fixture target semantics, not a production writer or v2 cutover.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 212-review-delivery`.
