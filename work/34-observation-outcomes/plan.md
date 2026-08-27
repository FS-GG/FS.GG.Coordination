---
schemaVersion: 1
workId: 34-observation-outcomes
title: Implement observation outcomes
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/34-observation-outcomes/spec.md
sourceClarifications: work/34-observation-outcomes/clarifications.md
sourceChecklist: work/34-observation-outcomes/checklist.md
publicOrToolFacingImpact: true
---

# Implement observation outcomes Plan

Prose status: planned

## Source Snapshot
- spec: work/34-observation-outcomes/spec.md sha256:4bc947dcfe0f92b72082a9a4ed00d54a4f5ab6cff0f7be680a42573016541f4f schemaVersion:1
- clarifications: work/34-observation-outcomes/clarifications.md sha256:5f53bd6a526d0f5e43c43930fbc0123f99828c8dd363c08b7c23561931921b6a schemaVersion:1
- checklist: work/34-observation-outcomes/checklist.md sha256:f16e6fd188e3a56b465707f65d3cabb3400ca305ea517e8a1ab2d1c7492e714e schemaVersion:1

## Plan Scope
- Work item 34-observation-outcomes is planned from the current specification, clarification, and checklist facts.
- Requirement count: 5.
- Clarification decision count: 0.
- Checklist result count: 5.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a closed nine-member observation outcome catalogue to the canonical literate Quint source, with stable `OBS-*` identities and no catch-all value.
- PD-002 [AC-001] [FR-002] complete: Replace the GS2-02.2 boolean observation carrier with a typed observation value that binds outcome, authority identity, observed revision, completeness evidence, and retry contract.
- PD-003 [AC-001] [FR-003] complete: Derive knowledge classification from outcome and evidence: observed may qualify positive knowledge, proven absence may qualify negative knowledge only at complete authoritative revision, contradictory remains contradictory, and every failure outcome remains non-knowledge.
- PD-004 [AC-001] [FR-004] complete: Export the outcome catalogue and observation actions through the existing profile-2 compiler and selector manifest while preserving the frozen profile-1 dependency and every accepted protocol identity.
- PD-005 [AC-001] [FR-005] complete: Register GS2-02.3 after the accepted GS2-02.2 receipt, reuse the exact Q1/pure-Q2 gates, add outcome-collapse negative controls, and stop before lifecycle intent.

## Contract Impact
- PC-001 [PD-001] [PD-002] protocol: `Protocol.md` remains the sole authored semantic source; generated Quint, contract, bindings, source map, receipt, and F# projection remain deterministic profile-2 outputs.
- PC-002 [PD-003] qualification: observation qualification is fail closed over the exact outcome, authority, revision, completeness, contradiction, and retry semantics; failures cannot be promoted to proven absence.
- PC-003 [PD-005] roadmap: `eng/github-substrate-v2-units.json` gains GS2-02.3 with accepted GS2-02.2 as its prerequisite, without changing prior unit contracts or receipt bytes.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] generatedProjection: Regenerate twice with the published compiler, inspect all retained profile-2 outputs, and require byte identity with profile 1 untouched.
- VO-002 [PD-003] [PC-002] quintProperties: Run Quint typecheck, examples, bounded simulation, tests, and Apalache safety verification for positive knowledge, proven absence, contradiction, and retry behavior.
- VO-003 [PD-003] [PC-002] negativeControls: Mutate outcome identity, authority/revision/completeness evidence, absence proof, authorization, and retry state; require outcome collapse and rate-limit-as-absence to fail closed.
- VO-004 [PD-005] [PC-003] repositoryGates: Run locked build, unit and architecture suites, exact Q1/pure-Q2 roadmap gates, exact-head CI, independent review, and post-merge default-branch CI.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Preserve all accepted GS2-02.1 and GS2-02.2 identities and the frozen profile-1 payload; the observation algebra is an additive profile-2 refinement.

## Generated View Impact
- GV-001 [PD-004] protocolViews: Generated Quint, compiled contract, bindings, source map, receipt, and F# projection refresh only from the literate source and selector manifest.
- GV-002 [PD-005] roadmapManifest: Candidate and gate evidence stays ignored; only the compact executable unit contract and later accepted receipt enter git.

## Quint Gap Analysis
- GS2-02.2 represents an authority observation as completeness and contradiction booleans plus a binding, so it cannot distinguish absence from failure or retry state.
- The accepted authority catalogue, revision checks, evidence relationships, and catalogue-closure invariant are reusable and must not change meaning.
- GS2-02.3 adds a separate closed outcome catalogue and knowledge classifier; it does not add scheduling, lifecycle intent, external hosting, or mutation authority.
- The existing profile-2 compiler already exports selected pure values, state, actions, and invariants, so extending the literate source and selector manifest remains sufficient without a parallel handwritten protocol model.

## Implementation Gates
1. Extend `Protocol.md` with the outcome algebra, typed observation value, knowledge classifier, actions, invariants, and executable examples.
2. Extend selectors and regenerate retained profile-2 outputs twice; require deterministic bytes and profile-1 compatibility.
3. Extend focused tests and register GS2-02.3; require outcome-collapse controls, formal verification, repository tests, and exact roadmap gates.

Rollback criterion: if any failure outcome can qualify as proven absence, or any accepted GS2-02.1/02.2 identity changes, stop and revise the model rather than weaken the invariant.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 34-observation-outcomes`.
