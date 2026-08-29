---
schemaVersion: 1
workId: 74-gs2-03-2-frozen-corpus
title: GS2-03.2 Frozen Corpus
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/74-gs2-03-2-frozen-corpus/spec.md
sourceClarifications: work/74-gs2-03-2-frozen-corpus/clarifications.md
sourceChecklist: work/74-gs2-03-2-frozen-corpus/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.2 Frozen Corpus Plan

Prose status: planned

## Source Snapshot
- spec: work/74-gs2-03-2-frozen-corpus/spec.md sha256:a64fd7ff8978bf96a511662ac20a2ca1b7ad9ec5128179286c3a9377a62fb8a6 schemaVersion:1
- clarifications: work/74-gs2-03-2-frozen-corpus/clarifications.md sha256:72161a7a280f59036eb4d5c901688c426ecb81ebe0c7080bed853a65fc29b72b schemaVersion:1
- checklist: work/74-gs2-03-2-frozen-corpus/checklist.md sha256:2244271381035efb81133ac4cd3376f7a597edcf75cfd27efec085254e19ebec schemaVersion:1

## Plan Scope
- Import the closed 21-case Q0 inventory and exact source blobs from `.github` commit `95de1c77674b9dd8d7a9ce568d1ee175a7797e5e` into repository-local corpus evidence.
- Add one canonical metadata contract and pure validator that preserve source provenance, expected behavior, ambiguity, and current-v1 result as separate facts.
- Keep the existing compact JSON evidence index authoritative for metadata while validating the larger raw source blobs through their content-addressed metadata records.
- Preserve the GS2-03.2 permission ceiling and stop before generated cases, independent oracles, fault injection, network, publication, deployment, or production writes.

## Plan Decisions
- PD-001 [AC-001] [AC-002] [FR-001] complete: Define the exact ordered identity set from Q0's immutable corpus-originals artifact and require all 21 identities with no omission, duplicate, extra, inferred, or generated row.
- PD-002 [AC-001] [AC-004] [AC-006] [FR-002] [FR-006] [DEC-002] complete: Retain each original as a non-JSON Git-tracked raw payload below `corpus/originals/`; bind it from a compact indexed JSON record by safe relative path, byte length, SHA-256, and Git blob SHA-1, without changing the 65536-byte metadata ceiling.
- PD-003 [AC-003] [AC-005] [FR-003] [FR-004] [FR-005] [DEC-001] [DEC-003] complete: Give each record exact structured expected behavior, a closed ambiguity state and rationale, and a separately typed `observed` or `not-atomically-observed` current-v1 result with immutable evidence provenance; never derive one field from another.
- PD-004 [AC-003] [FR-005] [DEC-001] [DEC-003] complete: Classify a result as observed only when an immutable exact-source-head run directly executes the artifact; otherwise retain the Q0 gap as `not-atomically-observed`. Preserve Q0 `Indeterminate` expected decisions independently of this evidence state.
- PD-005 [AC-002] [AC-004] [AC-005] [AC-006] [FR-006] complete: Implement a deterministic pure corpus validator with exact JSON shape/order, schema/vocabulary checks, safe paths and no symlinks, closed order/coverage, source-ref coherence, raw SHA-256 and Git blob recomputation, and one aggregate digest over the ordered records.
- PD-006 [AC-001] [AC-003] [FR-003] [FR-004] [FR-005] [FR-008] complete: Add the 21 canonical metadata records to the existing evidence index and extend its corpus-record validation without changing any accepted receipt bytes or another evidence category's meaning.
- PD-007 [AC-004] [AC-005] [AC-006] [FR-007] complete: Add independent mutations for payload bytes, length, both digests, source repository/commit/path/ref, expected behavior, ambiguity, v1 result/evidence, order, omission, duplicate, extra, canonical form, schema, traversal, and symlink refusal.
- PD-008 [AC-007] [FR-008] complete: Bind the validator and retained corpus into unit/architecture tests, evidence-storage Q7, canonical pure-model Q2, roadmap-work candidate/gates, SDD analyze/evidence/verify/ship, hosted exact-head checks, path verification, and fresh independent review.

## Contract Impact
- PC-001 [PD-002] [PD-003] [PD-005] additive schema: `fsgg.coordination.corpus-input/1` keeps its outer storage envelope while its `input` value gains one closed `fsgg.coordination.frozen-corpus-case/1` contract binding payload and semantic provenance.
- PC-002 [PD-005] additive validation: a repository-local validator reads only the named evidence root and returns deterministic findings; it has no discovery outside that root, network, GitHub, or write authority.
- PC-003 [PD-006] additive evidence: 21 metadata records and 21 exact raw payloads enter the existing corpus category; every pre-existing indexed payload and accepted receipt remains unchanged.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PD-008] [PC-001] [PC-002] [PC-003] semanticTest: Reproduce the Q0 manifest and all source object identities, byte-compare every import, run every independent inversion, preserve observed/indeterminate/unobserved distinctions, and pass warning-free build, unit/architecture, Q2/Q7, roadmap-work, SDD, hosted exact-head, path, and fresh-review gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] [PC-003] additiveCompatible: add immutable qualification evidence and pure validation without changing the v1 implementation, canonical Quint behavior, existing evidence meanings, or any production route.

## Generated View Impact
- GV-001 [PD-008] retainedProjection: refresh SDD readiness, evidence-index metadata, roadmap-work candidate/results, and hosted evidence from exact source and candidate bytes; stale or synthetic projections remain red.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Raw payload size is intentionally governed by its canonical metadata record and dedicated validator; the existing 65536-byte cap remains unchanged for every indexed JSON record.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 74-gs2-03-2-frozen-corpus`.
