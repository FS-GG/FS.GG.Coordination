---
schemaVersion: 1
workId: gs2-04-8-actions-release-feed-adapter
title: Gs2 04 8 Actions Release Feed Adapter
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/gs2-04-8-actions-release-feed-adapter/spec.md
publicOrToolFacingImpact: true
---

# Gs2 04 8 Actions Release Feed Adapter Clarifications

## Source Specification
- work/gs2-04-8-actions-release-feed-adapter/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] decision: Canonical collections use closed identity keys and ordinal sorting; run chronology is the tuple run number, attempt number, created time, and stable id, never response order.
- CQ-002 [AMB:AMB-002] decision: Merge-group evidence binds repository, head SHA, base SHA, constituent commit SHAs, and observation source; checks remain observations and never grant current merge permission.
- CQ-003 [AMB:AMB-003] decision: Availability is a monotone evidence ladder from upload response through durable metadata and authenticated retrieval to resolved anonymous bytes; no lower rung proves a higher one.
- CQ-004 [AMB:AMB-004] decision: Historical identity and provenance survive deletion or expiry only when independently digest-bound; current availability remains explicitly deleted or expired.

## Answers
- Canonical order is fixed by closed entity kind plus canonical identity; time is retained as data, while run/attempt chronology uses exact tuple fields and never page arrival.
- Merge-group subjects are immutable observation facts. A check outcome can describe that subject but cannot establish branch freshness, policy satisfaction, or merge authorization.
- Upload acceptance proves only request acceptance; durable metadata proves listing; authenticated retrieval proves those retrieved bytes; only a completed redirect chain and hashed anonymous response proves public served availability.
- Deleted and expired artifacts may retain exact historical identity, attestation, and digest evidence, but their current availability state cannot be promoted by that history.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001] [FR-001] [FR-002] [AC-001] [AC-002]: Canonicalize by closed entity kind and stable identity; preserve chronology through exact run/attempt/time/id tuples and reject duplicate identities.
- DEC-002 [CQ-002] [AMB:AMB-002] [FR-001] [AC-001]: Bind merge-group repository/head/base/constituents/source exactly and model checks as non-authoritative observations.
- DEC-003 [CQ-003] [AMB:AMB-003] [FR-005] [FR-006] [AC-005] [AC-006]: Use the explicit acceptance/metadata/authenticated-retrieval/redirect-resolved-anonymous-bytes ladder; each rung proves only itself.
- DEC-004 [CQ-004] [AMB:AMB-004] [FR-003] [FR-007] [AC-003] [AC-007]: Preserve digest-bound historical identity independently from current deleted or expired availability.
- DEC-005 [FR-004] [AC-004]: Attestation and package provenance require exact supported-digest and coordinate agreement; partial or contradictory evidence refuses.
- DEC-006 [FR-008] [AC-008]: Generate positive inventory from closed vocabularies and retain independently authored negative outcomes plus a gate inversion bound to the exact candidate.

## Accepted Deferrals
- None. All registered GS2-04.8 Q3 clauses are implemented in this unit; live mutation and Q4 sandbox proof remain outside this permission ceiling.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work gs2-04-8-actions-release-feed-adapter`.
