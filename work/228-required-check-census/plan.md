---
schemaVersion: 1
workId: 228-required-check-census
title: Required Check Census
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/228-required-check-census/spec.md
sourceClarifications: work/228-required-check-census/clarifications.md
sourceChecklist: work/228-required-check-census/checklist.md
publicOrToolFacingImpact: true
---

# Required Check Census Plan

Prose status: planned

## Source Snapshot
- spec: work/228-required-check-census/spec.md sha256:b986880a2568567edff5c2606d5b887de3f7e99f289abe2ea6ef6302b1315d5b schemaVersion:1
- clarifications: work/228-required-check-census/clarifications.md sha256:5b5a3467467c0a0cec5013eb623e3863df212abb1b8bb19350c5e6f10466f15d schemaVersion:1
- checklist: work/228-required-check-census/checklist.md sha256:23ce856dff00138ee1386ab71742fa0fa72983f1e75cdc1a779fe5e73f25de38 schemaVersion:1

## Plan Scope
- Work item 228-required-check-census is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 4.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a pure `RequiredCheckCensusAdapter` whose canonical length-framed seal binds the accepted GS2-06.1 receipt, exact repository profile, and complete source revisions to classic-protection requirements, ruleset requirements, and producer observations. Normalize exact context plus optional positive integration identity, retain both authority legs, order deterministically, and refuse ambiguous or contradictory identity combinations.
- PD-002 [AC-001] [FR-001] [DEC-002] [DEC-005] complete: Model unconditional production as an explicit closed proof for `pull_request` and `merge_group`: the event is admitted, event filters and activity restrictions are absent, and every job/dependency fact is complete with no condition or continue-on-error escape. Unknown, partial, renamed, cross-repository, duplicate, and stale observations refuse; complete filtered, conditional, or missing-event observations compile to a sealed not-ready classification. Retain the measured FS.GG.Coordination authority and workflow baseline rather than a synthetic green fixture.
- PD-003 [AC-001] [FR-001] complete: Retain exact contexts, authorities, ruleset ids, integration ids, producer workflow/job facts, and event proofs inside the sealed census. Publish only stable per-repository counts, readiness booleans, and the seal. Register GS2-06.2 behind the accepted GS2-06.1 receipt with generated corpus, independently authored expectations, Q3 qualification, offline validator, unit tests, and architecture gates.

## Contract Impact
- PC-001 [PD-001] command report: Add `RequiredCheckCensusAdapter` and its public signature, the required-check-census qualification contract, GS2-06.2 registry entry, retained corpus and independent expectations, and an exact offline validator. Existing repository profile/settings adapters and the canonical Quint protocol remain unchanged; no plan or apply operation is exposed.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Run the exact census validator against digest-bound authority evidence and exact workflow bytes; generated and independently authored classic/ruleset membership, authority digest, source binding, identity ambiguity, duplicate, missing producer, PR event, merge-group event, branch/path/activity filter, conditional job, continue-on-error, dependency completeness, rename, cross-repository, stale source, aggregate leakage, ordering, replay, seal, prerequisite, Quint, and no-apply inversions; warning-free build; focused and full unit/architecture suites; evidence self-test; and receipt-bound SDD analyze, verify, and ship gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: This is an additive read-only compiler over retained evidence. It changes no protection or workflow and grants no production permission; GS2-06.3 may later consume the sealed census to plan rulesets, while GS2-12 alone may apply settings under explicit administrative authority.

## Generated View Impact
- GV-001 [PD-001] workModel: Regenerate the SDD work model and agent projections from the authored source set after lifecycle changes, and bind final verify/ship reports to observed required-check-census test receipts.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 228-required-check-census`.
