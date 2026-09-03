---
schemaVersion: 1
workId: 270-gs2-06-7-repair-receipt
title: GS2-06.7 Repair Receipt
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-06.7 Repair Receipt Specification

Prose status: specified

## User Value
Maintainers can distinguish the immutable original GS2-06.7 acceptance from the independently requalified repaired implementation.

## Scope
- SB-001: One separate append-only repair-receipt category, strict schema, indexed receipt, validator, tests, SDD, review, and protected delivery.

## Non-Goals
- SB-002: Do not alter the original accepted receipt, enable fleet selection, mutate consumers or settings or workflows, publish packages or releases, or begin GS2-06.8.

## User Stories
- US-001 (P1): As a user, I can maintainers can distinguish the immutable original GS2-06.7 acceptance from the independently requalified repaired implementation.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Schema and index validation plus independent architecture inversions, full SDD and repository gates, independent exact-head review, and protected-main verification all pass.

## Functional Requirements
- FR-001: Bind original receipt exact bytes and self digest, issue 268, PR 269 candidate and protected merge, review and acceptance chain, exact-head and protected-main hosted runs, post-merge build/test/Q3/Q7, fleet observation provenance, and no-mutation boundary. (Stories: US-001; Acceptance: AC-001)
- FR-002: Reject omission, substitution, duplicate index or category, digest mismatch, fabricated identities, and any mutation of the original receipt. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 270-gs2-06-7-repair-receipt`.
