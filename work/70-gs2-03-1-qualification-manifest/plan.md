---
schemaVersion: 1
workId: 70-gs2-03-1-qualification-manifest
title: GS2-03.1 Qualification Manifest
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/70-gs2-03-1-qualification-manifest/spec.md
sourceClarifications: work/70-gs2-03-1-qualification-manifest/clarifications.md
sourceChecklist: work/70-gs2-03-1-qualification-manifest/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.1 Qualification Manifest Plan

Prose status: planned

## Source Snapshot
- spec: work/70-gs2-03-1-qualification-manifest/spec.md sha256:554d5f987ecbd20a1b0b5e725efcecce560933709d648929154832066978f34e schemaVersion:1
- clarifications: work/70-gs2-03-1-qualification-manifest/clarifications.md sha256:eb91062eec42b799981c86252da818efd7916cb808121581eb5b8217baa54952 schemaVersion:1
- checklist: work/70-gs2-03-1-qualification-manifest/checklist.md sha256:20694e3bc6e25e8aada4bbc9053b163e373996af8665315ec087eacd8bde03e8 schemaVersion:1

## Plan Scope
- Extend the canonical Quint protocol with qualification-manifest vocabulary, completeness/independence/freshness predicates, examples, and invariants; regenerate the existing compiled-contract projections.
- Add a pure `QualificationManifest` contract module with typed inputs, canonical JSON generation, self-digesting, and strict validation; keep all filesystem/process concerns at the CLI/test edge.
- Add one repository-local CLI validation surface and one retained complete example indexed under the bounded evidence store.
- Preserve the GS2-03.1 permission ceiling: no corpus import, independent-oracle implementation, network, GitHub mutation, publication, deployment, or production write.

## Technical Context
- `src/FS.GG.Coordination.Protocol/Protocol.md` and its generated profile-2 contract are the sole behavioral authority and already expose deterministic identity and compiled-output facts.
- `FS.GG.Coordination.Qualification.Contracts` contains pure published-kernel and roadmap-work validators and is the correct inward dependency boundary for a pure manifest contract.
- `FS.GG.Coordination.Cli` currently exposes only local `roadmap-work`; the additive `qualification-manifest validate` command will read one caller-named manifest and one independently supplied inventory file, then emit deterministic JSON/text without network effects.
- `evidence/github-substrate-v2` already provides a strict closed index, schemas, size ceilings, and negative-control harness for durable retained evidence.

## Constitution Check
- I/II: The accepted unit contract, this specification, and canonical Quint source precede implementation; generated artifacts remain projections.
- III: Additive JSON and CLI contracts are versioned explicitly and changed together with validators, examples, tests, and docs.
- VI/VIII: Unknown fields and every missing/duplicate/stale/substituted/generated-only/self-review class fail closed with distinct typed findings and bounded inversions.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-003] complete: Add closed Quint `qualificationManifest*` records for candidate, content entries, environment, results, and reviews plus completeness, candidate-binding, independence, ordering, and self-binding predicates; run focused examples/invariants without copying product behavior into F#.
- PD-002 [AC-001] [AC-002] [FR-002] [FR-006] [DEC-003] [DEC-004] complete: Introduce immutable F# input records and one canonical writer that emits fixed property order, ordinally sorted unique entries, one closed environment record, canonical UTC timestamps, and SHA-256 over canonical bytes omitting only `digest`.
- PD-003 [AC-002] [AC-004] [FR-003] complete: Parse strict JSON with exact object members, schema `fsgg.coordination.qualification-manifest/1`, lowercase SHA-256, positive lengths, closed role/kind/Q-gate/outcome vocabularies, canonical serialization equality, and no URI or mutable-reference fields; report stable code/path/expected/actual findings.
- PD-004 [AC-003] [AC-004] [FR-004] [FR-005] [DEC-001] [DEC-002] complete: Validate exact candidate and input-set binding on results/reviews, distinct generated/independent case roles, at least one independent case/reviewer, producer-reviewer inequality, terminal pass/accepted outcomes, and monotonic input-result-review-manifest timestamps without live identity or clock lookup.
- PD-005 [AC-002] [AC-006] [FR-007] complete: Add `qualification-manifest validate --file FILE --inventory FILE` as a read-only deterministic CLI projection, retain one complete example plus its independently content-addressed inventory and schemas in evidence storage, and update the canonical evidence index without widening runtime authority.
- PD-006 [AC-001] [AC-003] [AC-004] [AC-005] [AC-006] [FR-007] complete: Add unit property/order/digest tests and table-driven architecture mutations for every category, duplicate IDs, candidate/input substitution, generated-only, self-review, timestamp inversion, malformed canonical forms, unsupported version, unknown fields, and digest tampering; each mutation must red independently.
- PD-007 [AC-006] [FR-007] complete: Regenerate typed Quint/compiled outputs, bind the retained example and validator tests into exact Q1, pure Q2, Q7 evidence-storage, hosted `compiler-and-tests`, SDD analyze/evidence/verify/ship, and exact-head independent review.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] additive schema: canonical compiled-contract v2 gains qualification-manifest vocabulary/invariants and `fsgg.coordination.qualification-manifest/1` defines the retained tool-facing JSON contract.
- PC-002 [PD-005] additive command: `qualification-manifest validate --file FILE --inventory FILE` returns deterministic pass/finding JSON and has no write, process, filesystem-discovery, or network authority beyond reading the two named files.
- PC-003 [PD-005] additive evidence category: retain the schema and one bounded example under the existing evidence index and tracked-size policy; existing receipt bytes and category meanings remain unchanged.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PC-001] [PC-002] [PC-003] semanticTest: Observe canonical Quint tests/invariants, byte-identical generation under input reordering, semantic digest sensitivity, complete example validation, every table-driven red mutation, evidence-store controls, warning-free build, unit/architecture suites, exact Q1/Q2/Q7 gates, SDD readiness, hosted exact-head checks, paths, and independent review.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] [PC-003] additiveCompatible: Add schema/command/evidence identities without reinterpreting existing roadmap-work or bootstrap evidence manifests; unsupported or partial qualification manifests diagnose before consumption.

## Generated View Impact
- GV-001 [PD-001] [PD-007] retainedProjection: regenerate typed authority, compiled contract/bindings/source map/receipt, compiled-output manifest/views/diff/inventory, qualification schema/example/index, and SDD readiness views; stale bytes block acceptance.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- No existing F# public record changes; all F# and CLI surfaces are additive under Tier 1.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 70-gs2-03-1-qualification-manifest`.
