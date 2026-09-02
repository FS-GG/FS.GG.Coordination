---
schemaVersion: 1
workId: 224-repository-profiles
title: Repository Profiles
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/224-repository-profiles/spec.md
sourceClarifications: work/224-repository-profiles/clarifications.md
sourceChecklist: work/224-repository-profiles/checklist.md
publicOrToolFacingImpact: true
---

# Repository Profiles Plan

Prose status: planned

## Source Snapshot
- spec: work/224-repository-profiles/spec.md sha256:0eba9bf2421e2c561be7d011f4440112dfa1606b5dfb25ccf1540b327cb57f47 schemaVersion:1
- clarifications: work/224-repository-profiles/clarifications.md sha256:930df8018624d9a9e3e387f91b18d78e2011d9ccc6f595c77bbb9a46f10c07ab schemaVersion:1
- checklist: work/224-repository-profiles/checklist.md sha256:db508436c1bd7462d6c665f085bfb851bedcb4d55621aa2801f76e15e2281b53 schemaVersion:1

## Plan Scope
- Work item 224-repository-profiles is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 0.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a pure RepositoryProfileAdapter whose canonical length-framed
  seal binds the reviewed roster source revision and digest to every owner-qualified row in stable order.
  Retain complete rich row facts in the profile; derive a closed profile class and administration boundary;
  project only the three selected controlled-vocabulary native properties for FS-GG-owned rows.
- PD-002 [AC-001] [FR-001] complete: Treat non-FS-GG rows as first-class observe-only profiles with no
  property mutation plan. Reject ownership substitution, duplicates, malformed identities, unsupported
  roles/capabilities, incomplete or stale sources, property overflow, lossy capability retention, and altered seals.
- PD-003 [AC-001] [FR-001] complete: Register GS2-06.1 behind the accepted terminal receipts for GS2-02,
  GS2-03, GS2-04, GS2-05.8, and GS2-05.9. Add one Q3 contract with a retained exact roster snapshot,
  generated corpus, independently authored expectations, offline validator, unit tests, and architecture gates.

## Contract Impact
- PC-001 [PD-001] command report: Add RepositoryProfileAdapter and its public signature, the repository-profile
  qualification contract, GS2-06.1 registry entry, retained roster/corpus/expectations, and exact validator.
  Existing repository/settings adapter and canonical Quint contracts remain unchanged; no apply operation is exposed.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Run the exact repository-profile validator, generated and independent
  ownership/duplicate/malformed/stale/unsupported/loss/overflow/seal inversions, warning-free build, focused and
  full unit/architecture suites, evidence self-test, unchanged canonical Quint verification, and receipt-bound
  SDD analyze, verify, and ship gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: This is an additive desired-state compiler over retained evidence. It creates
  no GitHub properties and grants no production permission; later GS2-06 plans may consume the sealed profiles,
  and GS2-12 alone may apply settings under an explicit administrative plan.

## Generated View Impact
- GV-001 [PD-001] workModel: Regenerate the SDD work model and agent projections from the authored source set
  after lifecycle changes, and bind final verify/ship reports to observed repository-profile test receipts.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 224-repository-profiles`.
