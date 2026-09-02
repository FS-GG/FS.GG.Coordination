---
schemaVersion: 1
workId: 239-immutable-execution-pins
title: GS2-06.4 immutable execution pins
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/239-immutable-execution-pins/spec.md
sourceClarifications: work/239-immutable-execution-pins/clarifications.md
sourceChecklist: work/239-immutable-execution-pins/checklist.md
publicOrToolFacingImpact: true
---

# GS2-06.4 immutable execution pins Plan

Prose status: planned

## Source Snapshot
- spec: work/239-immutable-execution-pins/spec.md sha256:0ba34a60d98d6975490008be0911784631e9960d5dbf5acacc1fa6dd14d2b778 schemaVersion:1
- clarifications: work/239-immutable-execution-pins/clarifications.md sha256:dfb7bcc24628ba31fd771304f3bacc1f9e0228c0e45a7a97d19527a7ea736295 schemaVersion:1
- checklist: work/239-immutable-execution-pins/checklist.md sha256:03c1e75ce8e47b2895176101a084cf65e389c8f9bd242fed7db6eb700a1e3556 schemaVersion:1

## Plan Scope
- Work item 239-immutable-execution-pins is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 0.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a pure qualification contract that compiles a complete, sealed repository workflow inventory into deterministic execution-pin evidence. Parse action and reusable-workflow references as structured identities, require exact 40-hex revisions, bind reusable publication to owner-qualified repository, path, revision, and content SHA-256, and make completeness and source revision explicit inputs rather than filesystem assumptions.
- PD-002 [AC-001] [FR-001] complete: Model updater authority as an exact, closed registry. Accept one active automated authority only when it is Renovate, pull-request-only, and explicitly owns the workflow dependency families; reject Dependabot/custom-bot overlap, missing ownership, duplicate authority, direct-push permission, or unbounded update scope. Manual reviewed authorship is outside automated authority and remains allowed.
- PD-003 [AC-001] [FR-001] complete: Register GS2-06.4 with accepted GS2-06.3 as its sole prerequisite, advance the accepted roadmap pin to `.github@7ab43852609563265291eec2b4010a829582d447`, add one Q3 gate whose executable and arguments are independently digest-bound, and qualify the exact committed candidate through roadmap-work manifest and gates.

## Contract Impact
- PC-001 [PD-001] [PD-002] additive: Add `GitHubImmutableExecutionPinsQualification` as an offline qualification-only contract and retain its corpus plus independent expectations under `evidence/github-substrate-v2/gs2-06-4`; no GitHub transport, settings mutation, publication, deployment, or apply surface is introduced.
- PC-002 [PD-003] additive: Add `github-immutable-execution-pins-contract` to the tracked gate catalog and bootstrap qualification plan, with deterministic generated workflow projection and exact command digest in the unit index.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] semanticTest: Generated and independently authored controls cover prerequisite, corpus completeness, source binding, exact action pins, exact reusable-workflow pins, publication repository/path/revision/content binding, stable ordering, Renovate-only authority, PR-only updates, ownership completeness, exact seal, replay, and no mutation/publication surface.
- VO-002 [PD-003] [PC-002] integrationTest: Run the exact FSI gate directly and through roadmap-work manifest/gates against a clean committed candidate, plus focused architecture tests and a warning-free build.
- VO-003 [PD-001] [PD-002] mutationTest: For every added refusal gate, invert its declared subject once and require an observed red result; independently derive baseline expectations from retained corpus bytes rather than reusing compiler output.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveOnly: Existing workflows remain executable and production authority remains unchanged. This unit adds offline proof and tracked qualification only; immutable workflow publication is defined as a content-addressed contract, not performed, and all production GitHub writes remain reserved for successor units.

## Generated View Impact
- GV-001 [PD-001] [PD-003] workModel: Refresh `readiness/239-immutable-execution-pins/work-model.json`, `analysis.json`, `verify.json`, and `ship.json` from the authored lifecycle sources; generated views must bind their exact source digests and refuse stale evidence.
- GV-002 [PD-003] workflowProjection: Regenerate `.github/workflows/bootstrap-qualification.yml` from `eng/bootstrap-qualification-plan.json`; the projection gate must remain byte-exact.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 239-immutable-execution-pins`.
