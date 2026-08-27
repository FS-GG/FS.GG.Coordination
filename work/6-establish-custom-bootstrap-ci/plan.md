---
schemaVersion: 1
workId: 6-establish-custom-bootstrap-ci
title: Establish Custom Bootstrap CI
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/6-establish-custom-bootstrap-ci/spec.md
sourceClarifications: work/6-establish-custom-bootstrap-ci/clarifications.md
sourceChecklist: work/6-establish-custom-bootstrap-ci/checklist.md
publicOrToolFacingImpact: true
---

# Establish Custom Bootstrap CI Plan

Prose status: planned

## Source Snapshot
- spec: work/6-establish-custom-bootstrap-ci/spec.md sha256:0cbc07a423d280d17246329bb255b0ba9a2f488e4de4dd984aff61a79c8fce97 schemaVersion:1
- clarifications: work/6-establish-custom-bootstrap-ci/clarifications.md sha256:2d57f00313c58eb549b27747a400fc9b5e3a400094d2ed512e7732c0ce976937 schemaVersion:1
- checklist: work/6-establish-custom-bootstrap-ci/checklist.md sha256:a49067ee806c8c0e6368641c1d6090ab0e5f59f77d305b355149f7a122b05ff5 schemaVersion:1

## Plan Scope
- Work item 6-establish-custom-bootstrap-ci is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 5.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] complete: Replace the monolithic bootstrap job with the exact five-job contract and give the locked build its own empty-cache job.
- PD-002 [AC-001] [FR-002] [DEC-001] complete: Run compiler/build plus unit and architecture suites in a dedicated job and upload exact-run TRX evidence.
- PD-003 [AC-002] [AC-004] [FR-003] [DEC-002] complete: Extend the evaluated dependency policy with a fail-closed NuGet vulnerability report validator and independent unsafe-input mutations.
- PD-004 [AC-002] [FR-004] [DEC-003] complete: Pack the inert Protocol project at a CI-only version and compile a fresh consumer against staged bytes and declared read feeds.
- PD-005 [AC-003] [AC-004] [FR-005] [DEC-004] complete: Define a compact bootstrap evidence schema and validator; assemble it only after producing jobs pass and bind exact candidate, commands, and SHA-256 artifacts.
- PD-006 [AC-004] [FR-006] [DEC-005] complete: Validate workflow job inventory, read-only permissions, immutable action pins, and absence of v1 completion, publishing, deployment, or production-write routes.

## Contract Impact
- PC-001 [PD-001] [PD-002] workflow contract: `.github/workflows/bootstrap-qualification.yml` exposes exactly five required bootstrap job names on pull requests and main pushes with `contents: read` only.
- PC-002 [PD-003] [PD-006] validation commands: repository-owned scripts return zero only for a complete safe dependency/security and workflow-policy observation.
- PC-003 [PD-004] package smoke: a repository-owned command produces only temporary CI package/consumer state and never publishes.
- PC-004 [PD-005] evidence contract: `fsgg.coordination.bootstrap-evidence/1` binds a 40-hex candidate, exact required gate set, reviewed command identities, and exact-byte SHA-256 artifact receipts.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] cleanBootstrap: From an empty package cache and output tree, locked restore, warnings-as-errors Release build, unit tests, and the full architecture suite pass.
- VO-002 [PD-003] [PC-002] dependencySecurity: The evaluated dependency verifier and a complete JSON vulnerability report are green; prohibited dependency/source and vulnerable/malformed report mutations are red.
- VO-003 [PD-004] [PC-003] cleanConsumer: Protocol packs at the fixed CI-only version and a newly created consumer restores and compiles solely from the staged package plus supported read feed; absent or tampered staged bytes fail.
- VO-004 [PD-005] [PC-004] manifestIntegrity: Positive exact-head evidence validates; missing gate, duplicate gate, stale candidate, malformed digest, wrong artifact digest, and unknown gate controls fail.
- VO-005 [PD-006] [PC-001] workflowCeiling: A structure-aware workflow validator proves exact jobs, dependencies, read-only permissions, immutable action pins, and absence of v1, release, deployment, and write authority; one mutation per rule is red.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveBootstrap: Replace the initial single job only after the five-job workflow passes on its own pull request; no repository setting, required check, release, or runtime authority is changed by this unit.

## Generated View Impact
- GV-001 [PD-005] evidenceManifest: CI uploads dynamic exact-candidate evidence while git retains only its schema, validator, gate contract, tests, and compact qualification receipt.
- GV-002 [PD-001] workModel: The SDD work model records the five-job bootstrap boundary and remains a prerequisite for the exact-candidate evidence validator; any stale source snapshot blocks ship readiness.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 6-establish-custom-bootstrap-ci`.
