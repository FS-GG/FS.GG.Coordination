---
schemaVersion: 1
workId: 30-authority-bindings
title: Implement authority bindings
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/30-authority-bindings/spec.md
sourceClarifications: work/30-authority-bindings/clarifications.md
sourceChecklist: work/30-authority-bindings/checklist.md
publicOrToolFacingImpact: true
---

# Implement authority bindings Plan

Prose status: planned

## Source Snapshot
- spec: work/30-authority-bindings/spec.md sha256:3a390b4532f409f9ff3874df31ff60933e81ad1c211b59e8785805c5146f1d33 schemaVersion:1
- clarifications: work/30-authority-bindings/clarifications.md sha256:90f43c6fe836a8336b5bee9f50b913e734c0c95bb2a70fb9946fbdeb281ba3a2 schemaVersion:1
- checklist: work/30-authority-bindings/checklist.md sha256:e9d5d2f8b4cbb67cb21bb6cc21328b169cc0bf31cb243ba1e7949f9b8409a984 schemaVersion:1

## Plan Scope
- Work item 30-authority-bindings is planned from the current specification, clarification, and checklist facts.
- Requirement count: 4.
- Clarification decision count: 0.
- Checklist result count: 4.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Extend `Protocol.md` with a closed seven-row authority catalogue while retaining the existing vocabulary catalogue and every GS2-02.1 identity unchanged.
- PD-002 [AC-001] [FR-002] complete: Model each binding as stable identity, family, revision kind/value, completeness contract, and evidence relationship, then export it through the existing profile-2 compiler.
- PD-003 [AC-001] [FR-003] complete: Add observation state, qualification action, safety invariants, and witnesses that reject incomplete, stale, contradictory, wrong-authority, and incomplete-catalogue inputs.
- PD-004 [AC-001] [FR-004] complete: Register GS2-02.2 with the accepted GS2-02.1 receipt as its only prerequisite and reuse the exact Q1/Q2 protocol gates after extending their focused tests.

## Contract Impact
- PC-001 [PD-001] [PD-002] protocol: `Protocol.md` remains the sole authored semantic source; `Protocol.bindings.json` and `Generated/*` remain deterministic profile-2 projections.
- PC-002 [PD-003] qualification: GS2-02.2 accepts an authority only when its identity, family, revision, completeness, evidence relation, and catalogue closure agree exactly with the canonical binding.
- PC-003 [PD-004] roadmap: `eng/github-substrate-v2-units.json` gains GS2-02.2 after GS2-02.1 without changing the historical roadmap or earlier receipts.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] generatedProjection: Regenerate twice from the published 1.5.0 compiler, inspect the result, and require byte-identical profile-2 projections with profile 1 untouched.
- VO-002 [PD-003] [PC-002] quintProperties: Run Quint typecheck, bounded simulation, tests, and Apalache verification; require positive reachability and safety for authority qualification.
- VO-003 [PD-003] [PC-002] negativeControls: Mutate completeness, revision kind/value, authority identity/family, contradiction state, and catalogue membership and require fail-closed results.
- VO-004 [PD-004] [PC-003] repositoryGates: Run locked build, unit and architecture suites, exact Q1/pure-Q2 roadmap gates, exact-head CI, and post-merge default-branch CI.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Preserve the frozen profile-1 payload and all GS2-02.1 identifiers; authority bindings are an additive profile-2 refinement.

## Generated View Impact
- GV-001 [PD-002] typedAuthority: The generated Quint, compiled contract, bindings, source map, receipt, and F# projection refresh from the literate source and selector manifest.
- GV-002 [PD-004] roadmapManifest: Candidate and gate evidence stays ignored; only the compact executable unit contract and later accepted receipt enter git.

## Quint Gap Analysis
- Existing state `evidenceObserved` and `acceptedVocabulary` covers only generic vocabulary acceptance; no concrete authority observation is represented.
- Existing actions map cleanly to GS2-02.1 and stay intact. GS2-02.2 adds separate authority observation and qualification actions rather than changing their meaning.
- The generic `AuthorityVocabulary` seam exists, but no concrete binding rows, revision checks, completeness rule, contradiction rule, or catalogue-closure invariant exists.
- Generated artifacts already consume the literate Quint source through `Protocol.bindings.json`; extending selectors and regenerating is sufficient, with no parallel handwritten protocol model.
- Affected authored files are the literate protocol, selector manifest, roadmap unit index, focused tests/validator, and this work package. Generated files change only through the compiler.

## Implementation Gates
1. Extend `Protocol.md` and `Protocol.bindings.json`; gate with Quint typecheck and authority safety invariants.
2. Regenerate profile-2 projections; gate with deterministic compiler inspection and frozen profile-1 checks.
3. Extend focused tests and register GS2-02.2; gate with negative controls, locked build, repository tests, and exact roadmap Q1/Q2.

Rollback criterion: if any authority safety invariant or GS2-02.1 compatibility assertion fails, stop and revise the implementation plan rather than weakening the invariant.

## Implementation Status
- Steps 1–3: done.
- Quint typecheck, examples, simulation, and Apalache verification: green.
- Deterministic profile-2 regeneration and inspection: green; profile-1 dependency unchanged.
- Negative controls: incomplete, stale revision, wrong revision kind, wrong authority, contradiction, and omitted family all red as required.
- Repository build/tests: green (17 unit, 141 architecture).
- Current: exact-candidate qualification and protected merge.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 30-authority-bindings`.
