---
schemaVersion: 1
workId: gs2-04-1-transport-foundation
title: GS2-04.1 typed GitHub transport foundation
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-04-1-transport-foundation/spec.md
publicOrToolFacingImpact: true
---

# GS2-04.1 typed GitHub transport foundation Clarifications

## Source Specification
- work/gs2-04-1-transport-foundation/spec.md

## Clarification Questions
- **CQ-001** (AMB-001): What is the authoritative source of replay safety?
- **CQ-002** (AMB-002): Where is the boundary between transport semantics and GitHub authority policy?
- **CQ-003** (AMB-003): Is a truncated traversal a successful partial result?
- **CQ-004** (AMB-004): What redaction posture makes committed fixtures safe and deterministic?
- **CQ-005** (AMB-005): What execution environment is permitted for Q3 evidence?

## Answers
- CQ-001 → replay safety is a required typed property of the request; the HTTP method may inform construction but cannot silently grant replay authority.
- CQ-002 → Q3 preserves wire and protocol meaning only; repository and organization authority belongs to later Q4 adapters.
- CQ-003 → no; a missing, cyclic, malformed, or prematurely absent continuation makes the traversal incomplete and fail-closed.
- CQ-004 → fixture evidence is an allow-listed stable projection with explicit redactors; an unclassified sensitive field is a validation failure.
- CQ-005 → Q3 is hermetic: scripted or loopback fixtures only, without production credentials or external GitHub writes.

## Decisions
- **DEC-001** [CQ-001] [AMB:AMB-001] [FR-002] [AC-002]: Every request declares an idempotency class, and retry policy consumes that class plus a typed transient outcome; method-only inference cannot authorize replay.
- **DEC-002** [CQ-002] [AMB:AMB-002] [FR-001] [FR-003] [FR-004]: The transport exposes typed protocol facts and outcomes but does not decide whether a GitHub actor may mutate a repository or organization; that authority seam remains outside Q3.
- **DEC-003** [CQ-003] [AMB:AMB-003] [FR-005] [AC-005]: Traversal returns complete results only after a terminal continuation state; malformed, cyclic, or truncated continuation evidence is a typed failure and never partial success.
- **DEC-004** [CQ-004] [AMB:AMB-004] [FR-006] [AC-006]: Committed fixtures are deterministic allow-listed projections; known secret carriers are redacted and unclassified sensitive material causes validation to fail.
- **DEC-005** [CQ-005] [AMB:AMB-005] [FR-008] [AC-008]: Q3 evidence runs against scripted or local-loopback fixtures with no production credential and makes no live-write or Q4 correspondence claim.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
- None.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-04-1-transport-foundation`.
