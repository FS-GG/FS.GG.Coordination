---
schemaVersion: 1
workId: 74-gs2-03-2-frozen-corpus
title: GS2-03.2 Frozen Corpus
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/74-gs2-03-2-frozen-corpus/spec.md
publicOrToolFacingImpact: true
---

# GS2-03.2 Frozen Corpus Clarifications

## Source Specification
- work/74-gs2-03-2-frozen-corpus/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking open: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking open: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking open: Resolve source ambiguity AMB-003 before checklist.

## Answers
- CQ-001 [AMB:AMB-001] decision: CQ-001: decision: Use current-v1 result state observed only when immutable exact-head evidence names a terminal outcome for the source artifact; otherwise record not-atomically-observed with the Q0 evidence gap as provenance. Never copy expected behavior into result.
- CQ-002 [AMB:AMB-002] decision: CQ-002: decision: Keep compact canonical JSON metadata under the existing 65536-byte indexed envelope and retain raw original payloads as non-JSON, Git-tracked content-addressed children of the corpus category. A dedicated validator closes their inventory and digests; the generic JSON index binds each metadata record, not a normalized payload copy.
- CQ-003 [AMB:AMB-003] decision: CQ-003: decision: Record observed only for a source artifact with an exact source-head check or immutable Q0 result that directly executed it; bind run/check URL, head, conclusion and observation time. Record not-atomically-observed for every other source and preserve that state as required evidence, not as a waiver.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001]: CQ-001: decision: Use current-v1 result state observed only when immutable exact-head evidence names a terminal outcome for the source artifact; otherwise record not-atomically-observed with the Q0 evidence gap as provenance. Never copy expected behavior into result.
- DEC-002 [CQ-002] [AMB:AMB-002]: CQ-002: decision: Keep compact canonical JSON metadata under the existing 65536-byte indexed envelope and retain raw original payloads as non-JSON, Git-tracked content-addressed children of the corpus category. A dedicated validator closes their inventory and digests; the generic JSON index binds each metadata record, not a normalized payload copy.
- DEC-003 [CQ-003] [AMB:AMB-003]: CQ-003: decision: Record observed only for a source artifact with an exact source-head check or immutable Q0 result that directly executed it; bind run/check URL, head, conclusion and observation time. Record not-atomically-observed for every other source and preserve that state as required evidence, not as a waiver.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 74-gs2-03-2-frozen-corpus`.
