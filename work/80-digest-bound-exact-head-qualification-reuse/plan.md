---
schemaVersion: 1
workId: 80-digest-bound-exact-head-qualification-reuse
title: Digest Bound Exact Head Qualification Reuse
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/80-digest-bound-exact-head-qualification-reuse/spec.md
sourceClarifications: work/80-digest-bound-exact-head-qualification-reuse/clarifications.md
sourceChecklist: work/80-digest-bound-exact-head-qualification-reuse/checklist.md
publicOrToolFacingImpact: true
---

# Digest Bound Exact Head Qualification Reuse Plan

Prose status: planned

## Source Snapshot
- spec: work/80-digest-bound-exact-head-qualification-reuse/spec.md sha256:4c9e514eb812a9837b77a6168b31df4cae2e096609854cf895f08b65f450e0c4 schemaVersion:1
- clarifications: work/80-digest-bound-exact-head-qualification-reuse/clarifications.md sha256:371067962ee235a92410dc4dd1aa0051fcd9c1ea0005a9cef06b7fd951bafc65 schemaVersion:1
- checklist: work/80-digest-bound-exact-head-qualification-reuse/checklist.md sha256:6414c017247b235a68165fe5e62c4a51a7ebbdac3ec9dfcee21744f17ac7987a schemaVersion:1

## Plan Scope
- Work item 80-digest-bound-exact-head-qualification-reuse is planned from the current specification, clarification, and checklist facts.
- Requirement count: 9.
- Clarification decision count: 7.
- Checklist result count: 9.

## Plan Decisions
- PD-001 [AC-001] [AC-002] [FR-001] [FR-002] [DEC-001] [DEC-005] complete: Advance the qualification plan to a versioned reuse-aware contract that declares one runner/environment authority, the bounded discovery policy, decision artifact, terminal behavior, and exact immutable action pins beside the existing gate declarations.
- PD-002 [AC-001] [AC-002] [AC-003] [FR-001] [FR-002] [FR-004] [DEC-001] [DEC-003] complete: Add a compiled pure qualification-reuse domain that canonically hashes the complete tracked tree, writes strict compact subject/decision/receipt bytes, rejects unknown or duplicate fields, and returns exactly `reuse`, `execute`, or `refuse` with a named reason.
- PD-003 [AC-001] [AC-003] [AC-005] [FR-004] [FR-006] [FR-008] [DEC-002] [DEC-003] [DEC-004] complete: Keep GitHub discovery and download in the stable gate adapter. Search only the bounded immutable `bootstrap-evidence-manifest` artifact census; verify each owning run is the same workflow path, exact attempt, completed success, non-current, and live before the compiled core can select it.
- PD-004 [AC-001] [AC-003] [AC-005] [FR-004] [FR-008] [DEC-006] complete: Generate one unconditional `reuse-decision` job before the six execution gates. Execution gates run only for `execute`; the terminal `evidence-manifest` job uses `always()`, depends on the decision and all execution gates, and is the stable fail-closed authority for both routes.
- PD-005 [AC-001] [AC-004] [AC-005] [FR-003] [FR-006] [FR-007] [FR-008] [DEC-002] [DEC-006] complete: On reuse, terminal acceptance re-reads the current candidate head, downloads the selected prior run's complete artifact set with `actions:read`, re-hashes every artifact against the prior terminal manifest and receipt, preserves the prior run/head identities, and emits a new exact-current-head terminal manifest. Any loss or movement is red.
- PD-006 [AC-001] [AC-002] [AC-004] [FR-003] [FR-005] [FR-007] complete: Add versioned schemas for the qualification subject and reuse receipt, extend the terminal evidence schema without accepting legacy manifests for reuse, and make canonical serialization plus self-digest validation part of the compiled contract.
- PD-007 [AC-002] [AC-003] [AC-005] [FR-005] [FR-008] complete: Add a focused in-process inversion matrix for every subject component, tree byte/mode/path, run fact, artifact identity/digest/liveness, candidate/prior head, outcome, schema, self-digest, ordering, duplicate, unknown field, and movement boundary; retain bounded production-adapter parity and workflow-shape tests.
- PD-008 [AC-001] [AC-004] [AC-006] [FR-007] [FR-009] [DEC-007] complete: Prove full execution on the implementation head, then push a new provenance-only commit with the identical tree and require the hosted workflow to choose reuse, recover/re-hash prior artifacts, produce current-head evidence, and keep all required checks green.
- PD-009 [AC-006] [FR-009] complete: Emit observational reason, hit/miss/refusal, source run/attempt/head, candidate head, subject digest, route duration, source runner minutes, and avoided runner minutes in the receipt and performance report; none of these telemetry fields influence the semantic decision.

## Contract Impact
- PC-001 [PD-001] qualification plan: the typed plan remains the sole workflow/topology authority and gains one reuse policy rather than duplicating conditions in YAML and scripts.
- PC-002 [PD-002] [PD-006] compiled contracts: `QualificationReuse` owns canonical subject, decision, receipt, validation, and outcome semantics; the FSI layer remains an adapter.
- PC-003 [PD-003] discovery adapter: GitHub Actions REST reads and immutable artifact downloads are bounded, read-only, and cannot directly authorize reuse without the compiled validation decision.
- PC-004 [PD-004] [PD-005] workflow: `reuse-decision` selects a route, existing gate names remain diagnostic/required contexts, and `evidence-manifest` is the one terminal authority across execution and reuse.
- PC-005 [PD-005] [PD-006] terminal evidence: versioned exact-head evidence preserves source run provenance and artifact bytes while authorizing only the current candidate.

## Verification Obligations
- VO-001 [PD-001] [PD-004] [PC-001] [PC-004] planProjection: Mutate reuse topology, permissions, conditions, outputs, needs, action pins, and terminal `always()` behavior; each drift is named red and the generated workflow remains byte-current.
- VO-002 [PD-002] [PD-006] [PC-002] canonicalContract: Generate the same subject/receipt repeatedly and require byte identity; independently invert every closed field, ordering, duplicate, unknown-field, digest, and tree mutation.
- VO-003 [PD-003] [PC-003] discoveryBoundary: Exercise empty, transient-error, ineligible, failed, wrong-workflow, current-run, expired, missing, malformed, and valid artifact fixtures; only a fully validated candidate can select reuse.
- VO-004 [PD-004] [PD-005] [PC-004] routeParity: Prove `execute` runs every stable gate and current artifacts, `reuse` skips expensive execution but revalidates every prior artifact, and `refuse` makes the terminal required check red.
- VO-005 [PD-005] [PD-006] [PC-005] provenance: Mutate prior/current head, run/attempt, plan/subject digest, artifact set/bytes, result, liveness, review-policy identity, and self-digest; no mutation can yield current-head green evidence.
- VO-006 [PD-007] gateInversions: For every new or changed gate predicate, execute the bounded subject mutation and record the observed named red before restoring the green fixture.
- VO-007 [PD-008] hostedHit: Source-link one full-execution run and one later byte-identical-tree/new-head run; verify all required contexts, exact current-head evidence, prior artifact provenance, and route decision.
- VO-008 [PD-009] performanceComparison: Report settled wall time, runner minutes, decision/setup/transfer/validation time, avoided minutes, and hit/miss/refusal reason without using telemetry as authority.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additiveProof: Add subject/receipt validation and a decision artifact while legacy full execution remains the only selectable route; prove all inversions before enabling conditional execution.
- PM-002 [PC-004] guardedCutover: Enable reuse only after a hosted full-execution run has emitted the new terminal schema and retained complete artifacts; legacy terminal manifests remain valid historical evidence but are never reusable authority.
- PM-003 [PC-004] fallback: Discovery absence or preselection lookup failure executes every gate; selected-candidate contradiction refuses and requires a fresh run rather than silently relabelling stale evidence.

## Generated View Impact
- GV-001 [PD-001] workflowProjection: `.github/workflows/bootstrap-qualification.yml` regenerates from the one typed plan and must be current.
- GV-002 [PD-006] schemas: subject, receipt, and terminal evidence examples validate against the versioned schema directory.
- GV-003 [PD-008] performanceEvidence: the work package records source-linked hosted execute/reuse timings and runner-minute accounting.
- GV-004 [PD-001] workModel: readiness/80-digest-bound-exact-head-qualification-reuse/work-model.json refreshes from current lifecycle sources and must be current before ship.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- GitHub documents that conditionally skipped jobs conclude success, so the unchanged required gate contexts alone are not reuse authority; the required terminal `evidence-manifest` job must always run and validate the decision plus artifact route.
- Cross-run artifact access requires `actions:read`; no write permission, cache restore prefix, or mutable reference is introduced.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 80-digest-bound-exact-head-qualification-reuse`.
