---
schemaVersion: 1
workId: 86-gs2-03-3-generated-structural-tests
title: GS2-03.3 generated structural tests
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/86-gs2-03-3-generated-structural-tests/spec.md
sourceClarifications: work/86-gs2-03-3-generated-structural-tests/clarifications.md
sourceChecklist: work/86-gs2-03-3-generated-structural-tests/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.3 generated structural tests Plan

Prose status: planned

## Source Snapshot
- spec: work/86-gs2-03-3-generated-structural-tests/spec.md sha256:cb2668e4046f31f2b172d55d9c7665a5c64ac4da80e0eadf2b494e96f54e5976 schemaVersion:1
- clarifications: work/86-gs2-03-3-generated-structural-tests/clarifications.md sha256:05f8c509a198442748adc9ae224aacb953fb48c404741158e639f05dd1fa942d schemaVersion:1
- checklist: work/86-gs2-03-3-generated-structural-tests/checklist.md sha256:fd137d9608ff70956cbcf298cd6d2d647233afa53a88aea5f6ecbb49d9fd5285 schemaVersion:1

## Plan Scope
- Work item 86-gs2-03-3-generated-structural-tests is planned from the current specification, clarification, and checklist facts.
- Requirement count: 8.
- Clarification decision count: 3.
- Checklist result count: 8.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add one F# generator entry point that reads the qualified compiled contract, registered output manifest, and registered output JSON documents, validates their common supported/complete/fresh identity envelope, and emits the versioned structural-test document.
- PD-002 [AC-001] [FR-002] complete: Canonicalize category order and case keys, serialize compact UTF-8 JSON with a final newline, frame all self-digest fields explicitly, and reject duplicate keys before writing so two runs over identical inputs are byte-identical.
- PD-003 [AC-002] [FR-003] complete: Project cases directly from live artifacts: catalogue ids; action-effect ids and set digests; command action ids; mutation ids; permission strings; schema kind plus ordered-field digest; and every registered compiled-output family/path identity for projection freshness.
- PD-004 [AC-002] [AC-003] [FR-004] complete: Implement a separate validator code path that re-reads live qualified artifacts, independently reconstructs expected keys and source digests, and compares exact ordered sets rather than trusting generated counts, producer completeness flags, or copied expectations.
- PD-005 [AC-003] [FR-005] complete: Add architecture tests that invoke the production generator and validator against isolated fixture copies and invert missing, duplicate, reordered, substituted, identity-drifted, stale, malformed, mislabeled, and digest-tampered cases with stable refusal codes.
- PD-006 [AC-003] [FR-006] complete: Commit `Generated/generated-structural-tests.json`; generator check mode compares exact bytes, and the architecture suite regenerates into a temporary path so any qualified source/output change requires an explicit refreshed artifact.
- PD-007 [AC-004] [FR-007] complete: Give every case an immutable `generated-structural` evidence class, derivation id, source artifact, source key, and source-value digest; reject independent, behavioral, black-box, or fault-injection labels.
- PD-008 [AC-005] [FR-008] complete: Extend evidence-storage controls to hash the new artifact and prove the frozen corpus plus every pre-existing accepted receipt is unchanged; run the pure canonical Quint gate and architecture/evidence gates without adding runtime authority.

## Contract Impact
- PC-001 [PD-001] additiveArtifact: Add schema `fsgg.quint.generated-structural-tests/1`, committed `src/FS.GG.Coordination.Protocol/Generated/generated-structural-tests.json`, generator/check command `eng/generate-generated-structural-tests.fsx`, validator command `eng/validate-generated-structural-tests.fsx`, and stable named refusal codes; no existing production API or evidence schema changes.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PD-008] [PC-001] semanticTest: Record exact generation identity/counts, byte-identical double generation, production validator green, every named inversion red, immutable frozen-corpus/accepted-receipt hashes, warning-free build, unit and architecture results, pure Q2 and Q7 evidence, exact-head hosted qualification, and exact-merge verification.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Existing qualified outputs, frozen corpus, and accepted receipts remain byte-identical; consumers that do not know the new additive generated artifact remain unaffected, while qualification freshness requires its registered validator once this unit lands.

## Generated View Impact
- GV-001 [PD-001] [PD-006] generatedStructuralManifest: Regeneration deterministically refreshes only `src/FS.GG.Coordination.Protocol/Generated/generated-structural-tests.json`; SDD refresh separately maintains readiness views and both freshness checks fail closed on drift.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 86-gs2-03-3-generated-structural-tests`.
