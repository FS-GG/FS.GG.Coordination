---
schemaVersion: 1
workId: 70-gs2-03-1-qualification-manifest
title: GS2-03.1 Qualification Manifest
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/70-gs2-03-1-qualification-manifest/spec.md
publicOrToolFacingImpact: true
---

# GS2-03.1 Qualification Manifest Clarifications

## Source Specification
- work/70-gs2-03-1-qualification-manifest/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking open: Resolve source ambiguity AMB-004 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: Use stable producer and reviewer principal IDs carried in the manifest; require every accepted reviewer principal to differ from candidate and result producer principals. Authentication and organization membership are later adapter evidence, not manifest semantics.
- CQ-002 [AMB:AMB-002] decision: Freshness is exact candidate and input-set binding plus canonical second-precision UTC monotonic ordering: inputs no later than results, results no later than reviews, and every entry no later than manifest creation; no elapsed-time expiry is introduced.
- CQ-003 [AMB:AMB-003] decision: Environment is one closed record binding OS, architecture, runtime, locale, timezone, and network mode so one candidate has exactly one execution context.
- CQ-004 [AMB:AMB-004] decision: Packages are ordered local byte artifacts carrying stable ID, name, media type, byte length, SHA-256, producer and candidate binding; publication and attestation claims belong exclusively to GS2-03.7.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001] [FR-004]: Use stable producer and reviewer principal IDs carried in the manifest; require every accepted reviewer principal to differ from candidate and result producer principals. Authentication and organization membership are later adapter evidence, not manifest semantics.
- DEC-002 [CQ-002] [AMB:AMB-002] [FR-005]: Freshness is exact candidate and input-set binding plus canonical second-precision UTC monotonic ordering: inputs no later than results, results no later than reviews, and every entry no later than manifest creation; no elapsed-time expiry is introduced.
- DEC-003 [CQ-003] [AMB:AMB-003] [FR-003]: Environment is one closed record binding OS, architecture, runtime, locale, timezone, and network mode so one candidate has exactly one execution context.
- DEC-004 [CQ-004] [AMB:AMB-004] [FR-002]: Packages are ordered local byte artifacts carrying stable ID, name, media type, byte length, SHA-256, producer and candidate binding; publication and attestation claims belong exclusively to GS2-03.7.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 70-gs2-03-1-qualification-manifest`.
