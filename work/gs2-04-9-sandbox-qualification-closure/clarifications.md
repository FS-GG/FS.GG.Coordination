---
schemaVersion: 1
workId: gs2-04-9-sandbox-qualification-closure
title: Gs2 04 9 Sandbox Qualification Closure
stage: clarify
changeTier: tier1
status: needsAnswers
sourceSpec: work/gs2-04-9-sandbox-qualification-closure/spec.md
publicOrToolFacingImpact: true
---

# GS2-04.9 Sandbox Qualification Closure Clarifications

## Source Specification
- work/gs2-04-9-sandbox-qualification-closure/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001]: Which independently observed facts establish a non-production execution identity?
- CQ-002 [AMB:AMB-002]: Which destructive operations and quotas are sufficient for all adapter correspondence?
- CQ-003 [AMB:AMB-003]: How are ambiguous effects and partial cleanup classified and recovered?
- CQ-004 [AMB:AMB-004]: How are cold execution and cross-run evidence substitution prevented?

## Answers
- CQ-001 → Require a separately supplied credential plus authoritative actor/App identity, explicit allowlisted sandbox owner/repository/Project coordinates, a non-production purpose marker, bounded permissions, and rejection of the current human actor or any missing/contradictory fact.
- CQ-002 → Use one disposable test repository and one disposable test Project; allow at most one bounded create/update/delete correspondence operation per registered adapter surface, with per-surface and total quotas fixed in the signed plan.
- CQ-003 → An uncertain response is `ambiguous`, never success. Re-read by stable identity; compensate every possibly applied operation in reverse order; any unreadable or residual target keeps cleanup red.
- CQ-004 → Each child command runs in a new process with a per-run nonce and exact candidate digest. The closure receipt binds child result digests, workflow/run identity, creation and expiry times, and append-only artifact coordinates; any prior-run coordinate is rejected.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001] [FR-001] [FR-007] [AC-001] [AC-007]: Non-production status is a conjunction of authoritative actor/App identity, credential classification, allowlisted isolated coordinates, bounded permissions, and purpose marker; labels alone confer no authority.
- DEC-002 [CQ-002] [AMB:AMB-002] [FR-001] [FR-005] [FR-008] [AC-001] [AC-005] [AC-008]: The live sandbox is one disposable repository plus one disposable Project and a plan-fixed quota of bounded operations sufficient to exercise each adapter, never an organization-wide or production target.
- DEC-003 [CQ-003] [AMB:AMB-003] [FR-002] [FR-003] [AC-002] [AC-003]: Effects use expected-state/revision fences; ambiguous results trigger authoritative discovery and reverse compensation; only proven absence or exact restoration closes cleanup.
- DEC-004 [CQ-004] [AMB:AMB-004] [FR-004] [FR-005] [FR-006] [FR-008] [AC-004] [AC-005] [AC-006] [AC-008]: Comprehensive qualification uses fresh processes, a run nonce, exact candidate and registration digests, time bounds, distinct child result identities, and immutable run coordinates to prohibit warm or substituted evidence.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
- None. All registered ambiguities are resolved by DEC-001 through DEC-004.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-04-9-sandbox-qualification-closure`.
