---
schemaVersion: 1
workId: 79-optimize-canonical-quint-q1-q2
title: Optimize canonical Quint Q1/Q2 execution
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/79-optimize-canonical-quint-q1-q2/spec.md
sourceClarifications: work/79-optimize-canonical-quint-q1-q2/clarifications.md
sourceChecklist: work/79-optimize-canonical-quint-q1-q2/checklist.md
publicOrToolFacingImpact: true
---

# Optimize canonical Quint Q1/Q2 execution Plan

Prose status: planned

## Source Snapshot
- spec: work/79-optimize-canonical-quint-q1-q2/spec.md sha256:9a08bdc225120130f4e2d07c6ac12faa89059c85f310f2b0cc7aa4ab101c000e schemaVersion:1
- clarifications: work/79-optimize-canonical-quint-q1-q2/clarifications.md sha256:445de0058bd200c1694a41d5fa73af9bee27eabb3eecec99edf549e282c796a8 schemaVersion:1
- checklist: work/79-optimize-canonical-quint-q1-q2/checklist.md sha256:5ca29c587b0df00cf5de0942703a8865c8f480d881481707c7b5f8f5ac568c91 schemaVersion:1

## Plan Scope
- Work item 79-optimize-canonical-quint-q1-q2 is planned from the current specification, clarification, and checklist facts.
- Requirement count: 11.
- Clarification decision count: 6.
- Checklist result count: 11.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-004] complete: Refactor the formal validator so extraction, compilation, generated-module validation, and typechecking form one timed preparation that produces a deterministic digest reused by Q1 and Q2.
- PD-002 [AC-001] [AC-003] [FR-002] [FR-005] [DEC-004] complete: Emit Q1 after preparation and Q2 after the full suite, retaining stable human markers plus explicit fail-closed fields in one final receipt.
- PD-003 [AC-002] [FR-003] [DEC-001] complete: Replace eight separate positive verifier launches with one pinned Quint multi-invariant invocation only after proving equivalent pass/fail behavior.
- PD-004 [AC-002] [FR-004] [DEC-002] complete: Derive and enforce the complete 56-rejection Quint mutation inventory and exact retained 85-external / 61-Quint / 14-Apalache process inventory; prove a one-route near-miss fails closed; evaluate explicit bounded concurrency at 1, 2, and 4 without changing expected diagnostics or cardinality.
- PD-005 [AC-003] [FR-005] complete: Write the receipt atomically only after required data is available and treat missing, malformed, incomplete, or failed phase evidence as qualification failure.
- PD-006 [AC-004] [FR-006] complete: Move canonical Quint into a sibling hosted job with no dependency on compiler-and-tests; make evidence-manifest depend on and authenticate artifacts from both jobs.
- PD-007 [AC-003] [AC-005] [FR-007] [DEC-005] complete: Add a v1 JSON schema and validator coverage for phase durations, exact process counts, tool identities, input/preparation/result digests, and Q1/Q2 outcomes; bind the process inventory into the result digest.
- PD-008 [AC-006] [FR-008] [DEC-002] [DEC-003] complete: Keep server reuse and mutation parallelism behind measured adoption gates; prefer the least complex candidate that meets equivalence, stability, and timing thresholds.
- PD-009 [AC-007] [FR-009] [DEC-006] complete: Delete the hard-coded `70-gs2-03-1-qualification-manifest` SDD command chain from the reusable wrapper; run this item's SDD lifecycle separately.
- PD-010 [AC-002] [AC-004] [AC-005] [FR-010] complete: Update the workflow contract, F# validator, architecture tests, gate catalog, schema inventory, and architecture documentation as one closed topology change.
- PD-011 [AC-006] [FR-011] complete: Capture a baseline and at least five hosted post-change samples when feasible, calculate median/p95 and process counts, and label smaller preliminary sets explicitly.

## Contract Impact
- PC-001 [PD-001] [PD-002] formal runner: `eng/validate-canonical-quint-protocol.fsx` performs one preparation, preserves stable Q1/Q2 markers, and emits one canonical receipt.
- PC-002 [PD-006] [PD-010] workflow topology: `.github/workflows/bootstrap-qualification.yml` adds an independently scheduled canonical-Quint job and the evidence-manifest dependency closes both qualification paths.
- PC-003 [PD-007] [PD-010] evidence receipt: `fsgg.coordination.canonical-quint-qualification/1` is a closed, versioned JSON contract validated before retention.
- PC-004 [PD-003] [PD-004] qualification inventory: the eight positive invariants, 56 observed rejected Quint mutation processes, and exact 85/61/14 retained process tuple form the executable acceptance inventory.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] [PC-004] semanticEquivalence: Run the pinned full formal suite, prove one preparation, eight equivalent positive properties, exactly 56 execution-derived mutation rejections, and separate green Q1/Q2 outcomes.
- VO-002 [PD-005] [PD-007] [PC-003] receiptIntegrity: Validate a positive receipt and mutations for missing phase, false success, wrong digest, each wrong exact process count, malformed duration, unknown field, and incomplete result inventory; execute a producer near-miss at 84/60/14.
- VO-003 [PD-006] [PD-010] [PC-002] workflowClosure: Architecture tests prove the sibling-job topology, exact pins/timeouts/permissions, closed dependencies, retained receipt artifact, and fail-closed evidence join.
- VO-004 [PD-008] [PD-011] performanceComparison: Compare hosted baseline and candidate samples, recording median, p95, phase durations, process counts, and any rejected optimization with its measured reason.
- VO-005 [PD-009] [PC-001] lifecycleSeparation: Static and execution evidence prove the formal runner contains no hard-coded SDD work identity while this work item's lifecycle reaches verified ship readiness.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] atomicCutover: Land the runner, receipt contract, workflow topology, validator, tests, and documentation together; retain the existing human Q1/Q2 markers for operator compatibility.

## Generated View Impact
- GV-001 [PD-007] [PC-003] qualificationReceipt: Each hosted attempt generates a dynamic exact-candidate receipt; git retains its schema and tests, not a fabricated runtime result.
- GV-002 [PD-001] workModel: readiness/79-optimize-canonical-quint-q1-q2/work-model.json refreshes from current lifecycle sources and must be current before ship.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Acquisition caching is not a primary optimization in this item because measured tool acquisition is a small fraction of formal execution.
- If five hosted repetitions are impractical before merge, exact-head samples are provisional and the remaining sample obligation is recorded in the epic rather than weakening semantic acceptance.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 79-optimize-canonical-quint-q1-q2`.
