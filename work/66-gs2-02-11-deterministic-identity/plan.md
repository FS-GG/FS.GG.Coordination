---
schemaVersion: 1
workId: 66-gs2-02-11-deterministic-identity
title: Gs2 02 11 Deterministic Identity
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/66-gs2-02-11-deterministic-identity/spec.md
sourceClarifications: work/66-gs2-02-11-deterministic-identity/clarifications.md
sourceChecklist: work/66-gs2-02-11-deterministic-identity/checklist.md
publicOrToolFacingImpact: true
---

# Gs2 02 11 Deterministic Identity Plan

Prose status: planned

## Source Snapshot
- spec: work/66-gs2-02-11-deterministic-identity/spec.md sha256:e5491ee927fb6bff2415582c30ca3254200f0d5e05b9449801ee2adc8de7e513 schemaVersion:1
- clarifications: work/66-gs2-02-11-deterministic-identity/clarifications.md sha256:ac4793c7fbfeec998e3d302443968ef25e5199b53b22e1bbb2544fce9ba55524 schemaVersion:1
- checklist: work/66-gs2-02-11-deterministic-identity/checklist.md sha256:005fd569138ac2b70ad011cea4d49336b6f7f277b8d550d75fffaa0301cffae6 schemaVersion:1

## Plan Scope
- Extend the canonical Quint authority and its existing repository-local compiler/compiled-output pipeline only.
- Preserve raw Markdown provenance, every GS2-02.1–GS2-02.10 contract fact, and the no-network permission ceiling.
- Treat the accepted receipt's typed-effect SHA-256 as normalization authority, keep raw typed IR private, and derive review rows mechanically from the public compiled contract.

## Technical Context
- `fsgg-sdd typed-sdd author` extracts and typechecks named Quint blocks, emitting raw-source, generated-module, typed-effect, contract, toolchain, and compilation identities.
- `eng/validate-canonical-quint-protocol.fsx` owns exact version pins, regeneration, Quint execution, and negative controls.
- `eng/generate-compiled-contract-outputs.fsx` owns deterministic retained output content and manifests.

## Constitution Check
- I/II: the SDD spec and canonical Quint source precede implementation; typed generated artifacts remain the machine contract.
- III: additive compiled-contract and manifest fields remain explicit and are covered by generated artifacts, tests, and docs together.
- VI/VIII: every guard has an observed red mutation; malformed, unsupported, stale, and substituted identities fail with distinct diagnostics before execution.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] complete: Add a closed Quint deterministic-identity specification and equivalence predicate over the supported five-part version tuple and non-empty behavioral identity; run Quint typecheck plus focused `testDeterministicIdentity*` cases after the spec delta.
- PD-002 [AC-002] [FR-002] [DEC-001] complete: Retain raw source SHA separately while deriving behavioral identity exclusively from the observed receipt's typed-effect SHA-256; compile prose-only and equivalent-form scratch variants and require identical behavioral identities before any Q2 execution without retaining raw IR.
- PD-003 [AC-003] [FR-003] [DEC-003] complete: Flatten public compiled-contract JSON into stable ordinal JSON-pointer/value-digest rows, add the behavioral identity row, and require a semantic mutant to change identity and the expected ordered row.
- PD-004 [AC-004] [FR-004] [DEC-002] complete: Validate source grammar, extractor package/backend, Quint/toolchain, profile, and contract schema against exact supported values before invoking Quint; invert each comparison and assert a distinct red diagnostic.
- PD-005 [AC-005] [FR-005] [DEC-002] complete: Add deterministic-identity facts to the canonical compiled contract and carry raw source, behavioral digest, version tuple, public contract, completeness, and freshness bindings through retained outputs and manifests without retaining raw IR, adding a rival AST, or adding a public runtime command.
- PD-006 [AC-006] [FR-006] [DEC-003] complete: Extend unit and architecture coverage, regenerate all projections from `Protocol.md`, run existing witnesses/invariants plus new Quint tests, then execute exact `canonical-quint-compiler` Q1 and `canonical-quint-pure-model` Q2 catalog commands.

## Contract Impact
- PC-001 [PD-001] [PD-003] [PD-004] [PD-005] additive schema: `fsgg.quint.compiled-contract/v2`, `fsgg.quint.compiled-output/1`, and `fsgg.quint.compiled-output-manifest/1` gain derived deterministic-identity/version/diff facts; existing fields keep their meanings and raw-source bindings.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PC-001] semanticTest: Observe Quint typecheck/tests/invariants, equivalent/prose/semantic scratch compilation, five version-refusal mutations, retained regeneration, warning-free build, unit/architecture suites, and catalog-bound Q1/Q2 gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveCompatible: Preserve compiled-contract v2 and compiled-output v1 schema identifiers because fields are additive; stale retained artifacts and consumers missing the new required identity facts fail closed and regenerate from the canonical source.

## Generated View Impact
- GV-001 [PD-002] [PD-003] [PD-005] retainedProjection: regenerate typed authority, contract, bindings, source map, receipt, compiled-output files, semantic-diff rows, manifest, roadmap-work artifacts, and SDD readiness views; any byte drift without regeneration is blocking.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- No F# public signature changes are planned; the tool-facing schema/generated-view contract is Tier 1.

## Lifecycle Notes
- Roll back to planning if any prior Quint invariant/witness, generated projection, exact version pin, or raw-source provenance binding regresses.
