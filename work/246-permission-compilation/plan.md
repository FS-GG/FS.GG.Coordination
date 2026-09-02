---
schemaVersion: 1
workId: 246-permission-compilation
title: GS2-06.5 permission compilation
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/246-permission-compilation/spec.md
sourceClarifications: work/246-permission-compilation/clarifications.md
sourceChecklist: work/246-permission-compilation/checklist.md
publicOrToolFacingImpact: true
---

# GS2-06.5 permission compilation Plan

Prose status: planned

## Source Snapshot
- spec: work/246-permission-compilation/spec.md sha256:fa434e68f41ffa806ba9310a096f56f8455613183ba0bb185b5434fbad9f4efb schemaVersion:1
- clarifications: work/246-permission-compilation/clarifications.md sha256:f9a5da610892a28d7fb6be79afc3a9a6c25008756329f2b3ccde69bcc578c57e schemaVersion:1
- checklist: work/246-permission-compilation/checklist.md sha256:d93089056fb39d541f92966e5259083d7d3a7a22653bb8912c67afe512cfa387 schemaVersion:1

## Plan Scope
- Work item 246-permission-compilation is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 0.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Implement a pure F# permission compiler over a retained, complete registered-interpreter inventory. Normalize each interpreter operation to one operation class, one principal/environment class, and the exact App plus workflow permission maps it requires; reject omissions, duplicates, conflicts, undeclared outputs, wildcard levels, cross-class grants, and any permission above the class ceiling before producing a deterministic seal.

## Contract Impact
- PC-001 [PD-001] command report: Add `GitHubPermissionCompilationQualification` as a deterministic offline contract plus a tracked GS2-06.5 corpus, independent expectations, Q3 validator, ordered gate-catalog entry, roadmap-unit index entry, focused unit/architecture coverage, and bootstrap workflow invocation. Existing production GitHub clients and mutation paths remain unchanged.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Prove the baseline corpus compiles exactly the least permissions for all registered interpreters and deterministically replays; invert undeclared interpreter and permission, missing/conflicting mapping, wildcard and elevated access, normal/admin/release principal or environment crossover, incomplete inventory, changed source binding, and forbidden production-mutation surfaces. Run the direct Q3 validator, roadmap-work manifest/gates, focused and full unit/architecture suites, warning-free Release build, and SDD evidence/verify/ship.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: GS2-06.5 adds only candidate-state qualification and retained evidence. It performs no App installation or permission update, workflow permission mutation, environment creation or protection change, deployment, publication, release, acceptance-receipt authoring, or successor-unit work; every such surface remains outside the permission ceiling.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh `readiness/246-permission-compilation/work-model.json` and analysis/evidence/verification/ship artifacts from the exact authored lifecycle sources; retain the final ship verdict, while ignored roadmap-work candidate and gate results bind the clean committed candidate at execution time.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 246-permission-compilation`.
