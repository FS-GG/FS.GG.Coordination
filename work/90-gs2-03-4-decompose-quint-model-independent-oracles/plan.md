---
schemaVersion: 1
workId: 90-gs2-03-4-decompose-quint-model-independent-oracles
title: GS2-03.4 Quint decomposition and independent oracles
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/90-gs2-03-4-decompose-quint-model-independent-oracles/spec.md
sourceClarifications: work/90-gs2-03-4-decompose-quint-model-independent-oracles/clarifications.md
sourceChecklist: work/90-gs2-03-4-decompose-quint-model-independent-oracles/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.4 Quint decomposition and independent oracles Plan

Prose status: planned

## Source Snapshot
- spec: work/90-gs2-03-4-decompose-quint-model-independent-oracles/spec.md sha256:882f51dae130414c4111fe4ceaad5625cd361f594f51b1c975bc4a79976b9632 schemaVersion:1
- clarifications: work/90-gs2-03-4-decompose-quint-model-independent-oracles/clarifications.md sha256:9b007752315ff99d07afc2b0d34b56a138d497f37254bfd1572803c544e09d3f schemaVersion:1
- checklist: work/90-gs2-03-4-decompose-quint-model-independent-oracles/checklist.md sha256:0e1efb7c7be3c04ff9f080627adf4e58f3187d0c40453fbad830c747c366941d schemaVersion:1

## Plan Scope
- Execute the accepted GS2-03.4 packages in order: 03.4a decomposes the canonical model into registered bounded roots and proves behavior preservation; 03.4b adds independently authored observable-behavior oracles; 03.4c adds sound impact selection, protected-checkpoint full qualification, budgets, and future-module admission.
- Preserve `Protocol.md` as the sole behavioral authority and preserve every accepted receipt and frozen-corpus byte. Module/root/oracle/budget/admission documents are strict content-addressed qualification contracts, not parallel models.
- Rebind the executable unit index to the latest canonical roadmap revision and digest while preserving the accepted GS2-03.3 receipt and the already registered GS2-03.4 contract digest.

## Technical Context
- The canonical literate protocol currently compiles into one profile-2 Quint projection plus typed contract, bindings, source-map, receipt, and other registered outputs. Existing structural tests are generated from those outputs and therefore cannot independently validate observable behavior.
- The qualification manifest already binds candidate, inputs, results, environment, and independent review. GS2-03.4 extends the repository-local qualification layer with module/root closure identities, oracle cases, measured costs, and mode/selection evidence rather than weakening that manifest.
- Ordinary pull requests require a complete reverse-dependency closure. Protected main, roadmap acceptance, freeze, and release checkpoints require the entire registered root inventory; exact-tree reuse remains valid only when every closure, bound, backend, toolchain, oracle, and budget identity matches.
- Fast deterministic tests use the pinned Quint evaluator, bounded symbolic checks use isolated Apalache invocations, and selected finite high-risk roots may receive a TLC cross-check. Neither blanket inductive invariants nor a persistent verifier process enters the default gate.

## Constitution Check
- I/II: accepted roadmap unit, this specification, and the canonical literate source precede implementation; generated modules and views remain projections.
- III/VI: every additive schema and command is versioned, closed, content-addressed, deterministic, and fail-closed for unknown, duplicate, incomplete, cyclic, stale, or over-budget input.
- VIII: positive, adversarial, deliberately-invalid, mutation, stale-reuse, and selection-omission inversions remain bounded and identify the exact violated obligation.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Inventory every canonical state variable and action exactly once as essential, derived, or bookkeeping, with owning module and rationale; validate completeness directly against the compiled canonical contract so omissions, duplicates, and invented members fail.
- PD-002 [AC-001] [FR-002] complete: Define a closed acyclic module graph and the smallest useful independently executable roots. Generate each root and exact transitive closure deterministically from the canonical source, bind source/module/closure identities, and retain a before/after behavior-preservation receipt or an explicit reviewed intentional-delta receipt.
- PD-003 [AC-002] [FR-003] complete: Register per-root positive reachability, adversarial reachability, and deliberately-invalid anti-vacuity witnesses; execute them through bounded evaluator and verifier paths and require the invalid configuration to expose its named invariant rather than merely fail setup.
- PD-004 [AC-003] [FR-004] complete: Add an independently authored black-box oracle inventory for the ten required observable behaviors plus an abstraction-sensitive race. Oracle inputs and expected observations are handwritten at the public behavior boundary and never import generated model expectations; mutation tests prove each oracle detects its targeted defect.
- PD-005 [AC-004] [AC-005] [FR-005] complete: Add a pure impact selector over changed module, root, bound, backend, toolchain, oracle, and budget identities. Select the complete reverse-dependency closure for pull requests, the full inventory for protected checkpoints, and reject unknown, incomplete, cyclic, or differently identified closure evidence.
- PD-006 [AC-005] [AC-006] [FR-006] complete: Record per-root dependency depth, states, samples, elapsed milliseconds, peak memory, and artifact bytes in bounded evidence, compare them with explicit runner-calibrated ceilings, and fail on missing or exceeded metrics with a reviewable disposition that exact-tree reuse cannot conceal.
- PD-007 [AC-007] [FR-007] complete: Add a fail-closed admission validator requiring each future behavior to name its owner, permitted imports, invariants, independent oracles, smallest root, bounds, witnesses, projections, CI impact, and budget effect before generation.
- PD-008 [AC-001] [AC-003] [AC-004] [AC-005] [AC-006] [AC-007] complete: Implement one deterministic validation/qualification entry point and independent negative controls for graph cycles, closure omission, behavior drift, weak or missing witness, oracle mutation survival, reverse-dependency omission, stale reuse, missing/over-budget metric, and incomplete future admission.
- PD-009 [AC-001] [FR-002] [FR-005] complete: Re-pin `eng/github-substrate-v2-units.json` to the latest canonical roadmap bytes without changing the GS2-03.4 unit contract or any accepted predecessor receipt, then bind exact Q1/Q2/Q7, SDD, hosted exact-head, and post-merge evidence.

## Contract Impact
- PC-001 [PD-001] [PD-002] additive model topology: add closed version-one classification, module graph, root, closure, and behavior-preservation contracts generated from the canonical source; no second behavioral authority or existing compiled-contract reinterpretation.
- PC-002 [PD-003] [PD-004] additive qualification evidence: add registered witness and independent black-box oracle contracts with stable case identities, observable inputs/outcomes, target mutations, and source bindings.
- PC-003 [PD-005] [PD-006] additive qualification command: add deterministic selection/full-checkpoint modes and bounded measurement/budget evidence; filesystem/process concerns remain at the repository script edge and grant no network or production-write authority.
- PC-004 [PD-007] additive admission contract: add a version-one future-behavior admission document and validator that diagnoses before canonical generation.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] behaviorPreservation: Prove inventory completeness, graph acyclicity, exact closures, deterministic generation, bounded before/after trace equivalence, and unchanged accepted/frozen identities; any semantic delta requires an explicit content-addressed reviewed disposition.
- VO-002 [PD-003] [PD-004] [PC-002] independentDetection: Run every positive/adversarial/invalid witness and all eleven black-box oracle families; require each targeted subject mutation to turn its independent case red without consuming generated expectations.
- VO-003 [PD-005] [PD-006] [PC-003] selectionAndBudgets: Prove reverse-dependency closure under every registered change kind, full-inventory protected modes, stale-identity refusal, exact metric presence, and over-budget refusal with bounded deterministic artifacts.
- VO-004 [PD-007] [PC-004] admission: Independently omit or stale every required future-behavior field and require generation to refuse with its named diagnostic.
- VO-005 [PD-008] [PD-009] [PC-001] [PC-002] [PC-003] [PC-004] repositoryQualification: Run deterministic generation twice, focused evaluator/Apalache and selected TLC evidence, warning-free locked build, unit and architecture suites, evidence-storage negatives, exact roadmap Q1/Q2/Q7 gates, SDD evidence/verify/ship, hosted exact-head checks, independent review, and exact-merge verification.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] [PC-003] [PC-004] additiveCompatible: Existing profile-2 semantics, compiled outputs, generated structural tests, accepted receipts, and frozen corpus remain byte-compatible; new topology/oracle/selection/budget/admission consumers diagnose unsupported or incomplete version-one inputs before use.

## Generated View Impact
- GV-001 [PD-001] [PD-002] [PD-009] canonicalProjections: deterministically refresh registered module/root projections, closure identities, behavior-preservation evidence, compiled-output inventory as required, roadmap binding, and SDD readiness from their authored sources.
- GV-002 [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] qualificationViews: deterministically refresh bounded witness, oracle, selection, measurement/budget, and admission examples/evidence; validators independently reconstruct expectations and reject stale views.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Protected checkpoint policy is intentionally broader than pull-request selection; cost control comes from bounded independent roots, not from skipping full acceptance/freeze/release coverage.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 90-gs2-03-4-decompose-quint-model-independent-oracles`.
