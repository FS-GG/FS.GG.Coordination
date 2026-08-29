---
schemaVersion: 1
workId: 96-gs2-03-5-native-quint-formal-tests
title: GS2-03.5 native Quint model, property, and formal tests
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/96-gs2-03-5-native-quint-formal-tests/spec.md
sourceClarifications: work/96-gs2-03-5-native-quint-formal-tests/clarifications.md
sourceChecklist: work/96-gs2-03-5-native-quint-formal-tests/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.5 native Quint model, property, and formal tests Plan

Prose status: planned

## Source Snapshot
- spec: work/96-gs2-03-5-native-quint-formal-tests/spec.md sha256:87c60ada8b20b2fa7625193a2e47e54b8f2d0f4214fa24255aa899212f2ced39 schemaVersion:1
- clarifications: work/96-gs2-03-5-native-quint-formal-tests/clarifications.md sha256:ae4a33e1e9d8b0b8423f5e287165d78ce3bed9aca4699cb728e97a21ee27ff04 schemaVersion:1
- checklist: work/96-gs2-03-5-native-quint-formal-tests/checklist.md sha256:da5ad07d9d89866b879252637b91a50e977da5694e026f7c033f2c4a222339ec schemaVersion:1

## Plan Scope
- Add one closed, content-addressed formal-test catalogue over the six roadmap-named state spaces and generate only executable test/property modules, manifests, and retained evidence from that catalogue.
- Keep `Protocol.md` and its accepted executable-root closure as the sole behavioral authority. Examples, simulations, witnesses, properties, and deliberately invalid subjects import that authority and never restate its transitions.
- Extend the existing pinned canonical qualification path so ordinary changes select the sound affected property closure and protected checkpoints qualify the complete catalogue under existing time, memory, state, and artifact budgets.

## Technical Context
- GS2-03.4 already supplies source-derived executable roots, bounded evaluators, Apalache verification, selection, evidence reuse, metrics, budgets, and future-module admission. GS2-03.5 consumes those contracts instead of adding another runner or verifier service.
- Native Quint examples and properties must be real modules in the executable import closure. A JSON inventory validated only against itself is insufficient; source-import extraction and pinned Quint parsing/typechecking establish topology.
- Temporal claims are finite qualification evidence with explicit fairness and bound assumptions. They complement reachability and safety and never claim an unbounded theorem beyond the configured model-checking semantics.
- Retained counterexamples are paired canonical Quint and normalized ITF projections. Their identity includes source, closure, property, bounds, backend/toolchain, ordered states, and outcome so stale or rebound traces fail closed.

## Constitution Check
- I/II: the accepted roadmap contract, specification, and canonical literate Quint authority precede implementation; generated formal-test views remain projections.
- III/VI: the catalogue and counterexample contracts are versioned, closed, deterministic, content-addressed, and diagnose unknown, duplicate, incomplete, stale, rebound, or unsupported input before execution or reuse.
- VIII: every added property gate has a bounded subject inversion; simulation, reachability, temporal, bounded-checking, normalization, completeness, and budget failure routes are independently exercised.

## Plan Decisions
- PD-001 [AC-001] [AC-004] [FR-001] complete: Define a closed version-one formal-test catalogue with stable entries binding state space, canonical executable root/import closure, test kind, property or witness symbol, seed, finite bounds, backend/toolchain, expected outcome, and measurement budget; independently validate its exact six-domain coverage and reject duplicates or invented symbols.
- PD-002 [AC-001] [FR-002] complete: Author native Quint example and simulation modules that import the canonical roots, use deterministic seeds and bounded runs, expose named reachability witnesses, and retain compact digest-bound result evidence for every state space.
- PD-003 [AC-002] [FR-003] complete: Author reachability and safety properties for every state space and execute a distinct deliberately invalid subject for each property family so the focused gate proves it can observe the targeted violation rather than setup failure.
- PD-004 [AC-003] [FR-004] complete: Author explicit bounded temporal-progress obligations and fairness assumptions for election, relation progress, lifecycle convergence, saga disposition, epoch advancement, and rollback convergence; execute transition-removal counterexamples for each obligation family.
- PD-005 [AC-004] [FR-005] complete: Reuse the pinned Quint evaluator and isolated Apalache path to run the complete finite matrix, bind explored states/traces and elapsed/memory/artifact measurements, and fail on incomplete exploration, timeout, missing metrics, stale backend identity, or accepted-budget overflow.
- PD-006 [AC-005] [AC-006] [FR-006] complete: Normalize the smallest retained counterexample into a canonical Quint trace plus ITF projection, co-bind both artifacts and their ordered states, reproduce them byte-identically, and reject reorder, truncation, substitution, semantic rebinding, or outcome drift.
- PD-007 [AC-006] [AC-007] [FR-007] complete: Extend impact selection, protected-checkpoint full coverage, exact-tree reuse identity, evidence validation, and future-admission rules with the formal-test catalogue, property, bound, normalization, counterexample, and negative-control identities.
- PD-008 [AC-001] [AC-002] [AC-003] [AC-004] [AC-005] [AC-006] [AC-007] complete: Integrate one deterministic repository qualification entry point, tracked architecture/unit coverage, documentation, and evidence-storage contracts without changing accepted receipts, frozen corpus, production commands, or external-write authority.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-004] additive formal-test contract: add closed catalogue and native Quint example/property modules whose symbols and imports are validated against the canonical executable closure.
- PC-002 [PD-005] [PD-006] additive evidence contract: add bounded formal-result and paired Quint/ITF counterexample manifests with deterministic normalization, source/closure/property/bound/toolchain identity, measurements, and expected outcomes.
- PC-003 [PD-007] [PD-008] additive qualification behavior: include property selection, complete protected coverage, stale-reuse invalidation, negative controls, and budget enforcement in the existing repository-local gate; grant no network, GitHub mutation, deployment, publication, or production-write authority.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] executableCoverage: Prove source-derived imports, exact six-domain catalogue coverage, deterministic examples/simulations, reachable witnesses, and refusal of duplicate, missing, unknown, or shadow-model entries.
- VO-002 [PD-003] [PD-004] [PC-001] propertyDetection: Execute every reachability, safety, and temporal property plus its targeted invalid-subject or transition-removal mutation; require the expected property violation and reject vacuous/setup-only failures.
- VO-003 [PD-005] [PD-006] [PC-002] boundedReproduction: Run the complete bounded matrix, enforce metrics/budgets, reproduce paired Quint/ITF traces byte-identically, and reject stale identity, reorder, truncation, substitution, rebinding, or outcome drift.
- VO-004 [PD-007] [PD-008] [PC-003] repositoryQualification: Prove sound affected-property selection and protected full coverage; run deterministic generation twice, pinned Quint/Apalache checks, warning-free build, unit and architecture suites, evidence-storage negatives, exact roadmap Q2/Q7 gates, SDD analyze/verify/ship, hosted exact-head qualification, independent review, and exact-merge verification.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] [PC-003] additiveCompatible: Existing canonical semantics, executable roots, compiled outputs, accepted receipts, frozen corpus, and qualification consumers remain compatible; new version-one formal-test and counterexample inputs diagnose before execution or reuse when incomplete or unsupported.

## Generated View Impact
- GV-001 [PD-001] [PD-002] [PD-003] [PD-004] formalTestViews: deterministically generate and validate catalogue, native modules, source-import closure, example/simulation/witness/property inventory, and deliberately invalid subjects from authored sources.
- GV-002 [PD-005] [PD-006] [PD-007] qualificationViews: deterministically refresh bounded result, metric, budget, selection, normalized trace, paired ITF, and evidence identities; independently reconstructed expectations reject stale views.
- GV-003 [PD-008] lifecycleViews: refresh readiness/96-gs2-03-5-native-quint-formal-tests/work-model.json and authored SDD evidence from the current lifecycle sources.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Temporal evidence is deliberately bounded and assumption-explicit. Independent review must reject prose that generalizes a bounded result into an unbounded correctness claim.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 96-gs2-03-5-native-quint-formal-tests`.
