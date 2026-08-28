---
schemaVersion: 1
workId: 66-gs2-02-11-deterministic-identity
title: Gs2 02 11 Deterministic Identity
stage: clarify
changeTier: tier1
status: needsAnswers
sourceSpec: work/66-gs2-02-11-deterministic-identity/spec.md
publicOrToolFacingImpact: true
---

# Gs2 02 11 Deterministic Identity Clarifications

## Source Specification
- work/66-gs2-02-11-deterministic-identity/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001]: Which authoring transformations are supported equivalences?
- CQ-002 [AMB:AMB-002]: Which existing compiler artifact owns normalized behavioral identity?
- CQ-003 [AMB:AMB-003]: How can semantic diff remain stable and reviewable without becoming a second model?

## Answers
- CQ-001 → named-block partitioning, fence indentation, LF/CRLF, prose outside named Quint blocks, and Quint whitespace/comments are equivalent only when the accepted extractor and profile produce the same canonical typed behavioral document.
- CQ-002 → the accepted compiler receipt's `typedEffectSha256` is behavioral normalization authority; raw typed IR remains private and unretained, while raw Markdown, extracted module, public contract, and toolchain digests remain separately bound provenance.
- CQ-003 → flatten the canonical public compiled-contract JSON into ordinal JSON-pointer/value-digest rows sorted by pointer and include the behavioral identity as its own row; git compares retained current rows across revisions, so the projection derives facts and stores no independent transition logic.

## Decisions
- **DEC-001** [CQ-001] [AMB:AMB-001] [FR-001] [FR-002]: Equivalence is accepted only after the pinned extractor/profile compile both forms to the same observed typed-effect SHA-256; prose and raw source differences stay visible solely as provenance.
- **DEC-002** [CQ-002] [AMB:AMB-002] [FR-004] [FR-005]: The identity tuple binds source grammar `fsgg.quint.literate-source/1`, extractor `quint-specification-v1` from `FS.GG.SDD.Artifacts/1.5.0`, the pinned Quint/toolchain digest, profile `fsgg-quint-profile/2`, and compiled-contract schema `fsgg.quint.compiled-contract/v2`; the typed-effect SHA-256 is behavioral identity.
- **DEC-003** [CQ-003] [AMB:AMB-003] [FR-003] [FR-006]: Semantic diff is a deterministic current-state projection of the behavioral identity plus sorted public-contract JSON-pointer/value-digest rows; review uses ordinary git diff between retained projections, and mutation tests prove prose-only stability and semantic sensitivity without publishing raw typed IR.

## Accepted Deferrals
- None.

## Remaining Ambiguity
- None.

## Lifecycle Notes
- All decisions stay inside the ratified repository-local permission ceiling and preserve raw-source provenance alongside behavioral identity.
