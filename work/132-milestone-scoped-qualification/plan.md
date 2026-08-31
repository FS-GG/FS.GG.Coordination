---
schemaVersion: 1
workId: 132-milestone-scoped-qualification
title: Milestone Scoped Qualification
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/132-milestone-scoped-qualification/spec.md
sourceClarifications: work/132-milestone-scoped-qualification/clarifications.md
sourceChecklist: work/132-milestone-scoped-qualification/checklist.md
publicOrToolFacingImpact: true
---

# Milestone Scoped Qualification Plan

Prose status: planned

## Source Snapshot
- spec: work/132-milestone-scoped-qualification/spec.md sha256:e20efd00ee0f62d64329b0a9a82b2700419d16d657ed8df27ef3e24281f78bb1 schemaVersion:1
- clarifications: work/132-milestone-scoped-qualification/clarifications.md sha256:2c13550977325ffbc7ce67c2b9f5452e149843ddd6e21135541ddb06e83d78a8 schemaVersion:1
- checklist: work/132-milestone-scoped-qualification/checklist.md sha256:c9456165e99386684a75cda9f27c9507bb50cd81cb205dc52b55e58ecf7d3d92 schemaVersion:1

## Plan Scope
- Work item 132-milestone-scoped-qualification is planned from the current specification, clarification, and checklist facts.
- Extend the existing qualification contracts rather than introduce a parallel workflow authority.
- Preserve the current complete-tree reuse route as an exact-tree optimization while adding an independent canonical-formal route for changed trees.
- Make the generated workflow exercise one of three event shapes: ordinary qualification, comprehensive qualification, or scheduled economics review.

## Plan Decisions
- PD-001 [AC-001] [AC-003] [FR-001] [DEC-002] complete: Extend plan schema v4 with a milestone-state path, default scoped mode, comprehensive boundary kinds, and event-specific topology. The renderer reads these facts once; YAML contains projections only.
- PD-002 [AC-001] [AC-002] [FR-002] [DEC-001] complete: Replace the complete-tree formal subject with plan-declared exact/prefix selectors and canonical selected-file framing. Selectors cover all inputs consumed by canonical execution; duplicate overlap, required no-match, unsafe paths, or a missing file refuse.
- PD-003 [AC-001] [FR-003] [DEC-003] complete: Split whole-run and formal-gate decisions. Whole-run exact-tree reuse remains the first fast path. On its miss, formal decision selects an original `canonical-quint-<subject>` execution artifact; non-formal current-tree jobs still execute. Terminal validation joins both routes without transitive relabelling.
- PD-004 [AC-002] [FR-004] [DEC-001] complete: Remove the all-source parallel-AST scan from the canonical validator and add an equivalent current-tree architecture test. Mutate every selector class and prove formal subject drift; mutate unrelated adapter bytes and prove the formal subject remains stable while architecture/current-tree tests still run.
- PD-005 [AC-003] [FR-005] [DEC-002] complete: Comprehensive mode overrides both reuse routes, emits `forced-comprehensive` reasons, requires every execution job success, and refuses prior/deferred artifacts. The terminal job remains `always()` and fails red on any missing cold result.
- PD-006 [AC-003] [AC-004] [FR-006] [DEC-002] complete: Add strict milestone state parsing/validation against the unit index and accepted receipt bytes. Scoped mode permits an incomplete accepted prefix; comprehensive mode requires the complete ordered set and current unit-contract/receipt agreement.
- PD-007 [AC-003] [AC-004] [FR-007] complete: Upgrade terminal evidence schema to bind full tree, formal subject, whole/formal route decisions, mode, milestone subject, per-gate artifact/source identities, policy, and environment. Add a closure receipt codec whose creation requires comprehensive terminal evidence.
- PD-008 [AC-005] [FR-008] [DEC-005] complete: Add canonical gate-observation and defect-attribution codecs. Observations bind exact run/attempt/job/artifact identities and measurements; attribution records are canonical digest-bound tracked inputs, and unclassified failures stay explicit.
- PD-009 [AC-005] [AC-006] [FR-009] [DEC-004] [DEC-007] complete: Add a pure cadence evaluator and CLI projection using plan thresholds. The generated 24-hour schedule queries a bounded 14-day Actions window, joins tracked attribution, uploads one immutable recommendation artifact, and does not run qualification jobs.
- PD-010 [AC-005] [AC-006] [FR-010] [DEC-006] complete: Encode minimum-cadence and protected-boundary refusal before economics scoring. Recommendations never rewrite authority; closure misses, unattributed failures, stale telemetry, high yield, or high blast radius prevent reduction and may recommend increase.

## Contract Impact
- PC-001 [PD-001] [PD-002] qualification plan: schema v4 declares milestone state, formal selectors, formal artifact indexing, economics schedule/window/thresholds, and event topology as the sole workflow source.
- PC-002 [PD-003] [PD-005] route decisions: the current reuse receipt evolves to distinguish whole-run and formal-gate route authority and comprehensive override without treating skipped jobs as evidence.
- PC-003 [PD-006] [PD-007] milestone evidence: strict milestone-state, closure-subject, terminal-manifest v4, and closure-receipt schemas bind ordered children and exact protected candidate evidence.
- PC-004 [PD-008] [PD-009] economics evidence: gate observations, defect attributions, and cadence recommendations are canonical versioned documents with deterministic codecs and validation.
- PC-005 [PD-001] [PD-009] workflow: the generated workflow adds schedule authority and an economics job while preserving stable qualification job ids and current required contexts.

## Verification Obligations
- VO-001 [PD-001] [PD-005] [PC-001] [PC-005] planProjection: Mutate plan mode/state/economics fields, job conditions, schedule, permissions, outputs, artifact names, and terminal `always()` behavior; generated workflow drift or weakened closure is named red.
- VO-002 [PD-002] [PD-004] [PC-001] formalSubject: Reorder enumeration and require canonical equality; independently mutate every selector class and require drift; mutate an unrelated adapter file and require stability; reject overlap, no-match, unsafe, missing, and undeclared consumed inputs.
- VO-003 [PD-003] [PC-002] formalRoute: Exercise whole hit, changed-tree/formal hit, formal miss, discovery failure, selected artifact loss, changed bytes, transitive prior reuse, stale head, expiry, and comprehensive override; only original matching formal execution may skip canonical work.
- VO-004 [PD-005] [PD-006] [PD-007] [PC-003] closure: Prove cold execution and exact terminal evidence for a valid closure; invert mode, parent, order, child id/contract/receipt digest, acceptance state, artifact source, and exact head/tree; no inversion yields a closure receipt.
- VO-005 [PD-004] [PC-001] parallelAst: Mutate unrelated adapter source with a rival formal AST and require current-tree architecture red while the formal subject remains stable, proving the guard moved rather than disappeared.
- VO-006 [PD-008] [PD-009] [PD-010] [PC-004] economics: Table-test insufficient sample, stale telemetry, zero-yield/high-cost, high-yield, unattributed failure, infrastructure noise, closure miss, long detection delay, missing equivalence, and protected minimums; canonical output is deterministic and no recommendation mutates policy.
- VO-007 [PD-009] [PC-005] scheduledRoute: Prove schedule executes only economics, PR/push execute only qualification, API failures produce explicit unavailable/insufficient evidence, and least-privilege `actions:read`/`contents:read` remains sufficient.
- VO-008 [PD-003] [PD-005] hostedRoutes: Observe one changed-tree/formal-subject hit with current-tree gates, one formal-subject drift cold run, and one forced-comprehensive fixture or hosted candidate; bind exact heads, source artifact bytes, route reasons, and durations.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveColdFirst: Land codecs, state, selector inversions, and renderer with formal reuse disabled; this implementation PR changes its own formal subject and therefore executes cold under the old and new contracts.
- PM-002 [PC-002] guardedFormalEnablement: Enable formal reuse only after a protected successful run emits the new canonical artifact and terminal schema; absence of that epoch remains a cold execute.
- PM-003 [PC-003] closureActivation: Seed GS2-04 state as scoped with the known accepted prefix. Comprehensive validation is dormant until the final child explicitly changes mode; it refuses stale accepted receipts at that boundary.
- PM-004 [PC-004] observationalEconomics: Begin scheduled recommendations with insufficient data and no automatic change. The first reviewed cadence adjustment requires live observations under the new schema.

## Generated View Impact
- GV-001 [PD-001] [PD-009] workflowProjection: `.github/workflows/bootstrap-qualification.yml` regenerates byte-identically from plan v4 with event-specific qualification/economics topology.
- GV-002 [PD-006] milestoneState: `eng/milestone-qualification.json` is authored state validated against the unit index and acceptance receipts, not duplicated in workflow YAML.
- GV-003 [PD-007] [PD-008] schemas: new evidence examples and JSON schemas validate against the contract implementation and exact property sets.
- GV-004 [PD-001] workModel: readiness/132-milestone-scoped-qualification/work-model.json refreshes from current lifecycle sources and must be current before ship.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- The current GS2-04.2 acceptance receipt appears contract-drifted from the latest unit index; scoped mode records it without blocking ordinary work, while comprehensive closure will require a refreshed accepted receipt.
- A recommendation is deliberately conservative about missing attribution: no recorded actionable defect is not evidence of zero defects.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 132-milestone-scoped-qualification`.
