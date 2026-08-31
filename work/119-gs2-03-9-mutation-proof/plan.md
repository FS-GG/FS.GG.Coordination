---
schemaVersion: 1
workId: 119-gs2-03-9-mutation-proof
title: Prove the qualification harness can fail
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/119-gs2-03-9-mutation-proof/spec.md
sourceClarifications: work/119-gs2-03-9-mutation-proof/clarifications.md
sourceChecklist: work/119-gs2-03-9-mutation-proof/checklist.md
publicOrToolFacingImpact: true
---

# Prove the qualification harness can fail Plan

Prose status: planned

## Source Snapshot
- spec: work/119-gs2-03-9-mutation-proof/spec.md sha256:c51e83a2dd91a5860cd18b95ef4c0b6679836d126ce8764f6f65b7d5ddbc6178 schemaVersion:1
- clarifications: work/119-gs2-03-9-mutation-proof/clarifications.md sha256:16d3df297026829da83453be33f4f57cc97cdbaecec8cc663533a52c164c8278 schemaVersion:1
- checklist: work/119-gs2-03-9-mutation-proof/checklist.md sha256:5b527a991f8945e23e920ce824b12f92a1cd638877ee6182009538a54d8b561c schemaVersion:1

## Plan Scope
- Work item 119-gs2-03-9-mutation-proof is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 0.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Define ten evidence gate classes from the existing qualification inventory fields and six fixed invalid modes; derive the exact 60-cell Cartesian negative inventory plus ten positive controls rather than maintaining a hand-count.
- PD-002 [AC-001] [FR-002] complete: Add a pure mutation-proof runner beside QualificationManifest that creates each isolated mutation from canonical healthy bytes, calls QualificationManifest.validate, and records the actual stable diagnostic; no result can be supplied by the caller.
- PD-003 [AC-002] [FR-003] complete: Add a canonical typed proof contract binding exact candidate commit/tree/unit, validator/baseline/inventory fingerprints, ordered gate/mutation inventories, actual observations, content-set digest, and self digest.
- PD-004 [AC-002] [FR-004] complete: Validate exact shapes, identities, order, Cartesian completeness, fingerprints, controls, required red outcomes, expected diagnostics, content-set digest, and self digest; add independent omission, duplication, substitution, reorder, stale, truncation, forged, generated-only, and asserted-green inversions.
- PD-005 [AC-003] [FR-005] complete: Strengthen QualificationManifest with a generated-producer separation invariant: generated-case producers cannot be the sole provenance of any other content, result, or reviewer class; preserve the accepted GS2-03.1 baseline bytes.
- PD-006 [AC-001] [AC-002] [AC-003] [FR-006] complete: Retain one canonical GS2-03.9 proof and schema, index it through evidence storage, execute it in unit/architecture/Q7 gates, finish SDD, run exact-head hosted qualification, retain five-perspective critique, verify protected main, and accept through one Accountable Delivery Owner.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-005] additive contract: Add HarnessMutationProof public types/module and a versioned mutation-proofs evidence schema/category; QualificationManifest keeps its existing public shape and gains only the generated-provenance refusal.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PC-001] semanticTest: Regenerate the canonical proof from production validation, require 10 passing controls and 60 typed rejections, validate exact bytes, then independently mutate every proof binding and coverage dimension; run warning-free build, unit/architecture, evidence-storage, roadmap-work Q7, SDD, hosted, critique, and protected-main gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveCompatible: Preserve reviews/v1-v2, qualification manifest/inventory v1, accepted receipts, stored baseline bytes, workflow graph, and all earlier gates; mutation-proofs/v1 is a new evidence category with no runtime or migration fallback.

## Generated View Impact
- GV-001 [PD-006] retainedProjection: Refresh only mutation proof/schema/index, documentation, tests, roadmap artifacts, SDD readiness, hosted evidence, critique, and receipt from exact current bytes; generated evidence cannot satisfy its own proof.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 119-gs2-03-9-mutation-proof`.
