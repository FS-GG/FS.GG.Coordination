---
schemaVersion: 1
workId: 254-release-hardening
title: Release Hardening
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/254-release-hardening/spec.md
sourceClarifications: work/254-release-hardening/clarifications.md
sourceChecklist: work/254-release-hardening/checklist.md
publicOrToolFacingImpact: true
---

# Release Hardening Plan

Prose status: planned

## Source Snapshot
- spec: work/254-release-hardening/spec.md sha256:91a5452ac2ba5976f6194c365ef37689539dc7413ce6f10eea916f840992898a schemaVersion:1
- clarifications: work/254-release-hardening/clarifications.md sha256:49d82bb957021d036f9802926abe73748c0803043e48e37c8c81c028e8051a39 schemaVersion:1
- checklist: work/254-release-hardening/checklist.md sha256:cead50f8e5aa79a13c0aa363840e483e398c0824c05b07339085402e4e63dbb6 schemaVersion:1

## Plan Scope
- Work item 254-release-hardening is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 1.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a signature-first pure compiler whose exact input snapshot contains every release-hardening fact and whose output carries a canonical length-framed seal.
- PD-002 [AC-001] [FR-001] complete: Encode release progression as one exact ordered stage inventory so dependency review and attestation precede both feed publications and public verification follows immutable release creation.
- PD-003 [AC-001] [FR-001] complete: Retain one repository corpus plus an independently authored expectation inventory and invert every required control in the exact Q3 validator.

## Contract Impact
- PC-001 [PD-001] public API: Add `GitHubReleaseHardeningQualification.fsi` with snapshot, report, finding, control, compile, verify, and validation contracts.
- PC-002 [PD-002] roadmap gate: Register `github-release-hardening-contract` at Q3 and pin its exact executable-plus-argument digest.
- PC-003 [PD-003] retained evidence: Bind `corpus.json`, `independent-expectations.json`, and the exact validator to the accepted prerequisite and roadmap bytes.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Prove the valid baseline, exact replay, and fail-closed malformed, stale, unprotected, mutable, reordered, repacked, divergent, and altered-subject inputs.
- VO-002 [PD-002] [PC-002] architectureTest: Prove catalog/index command agreement and run the exact Q3 validator through the architecture suite.
- VO-003 [PD-003] [PC-003] integrationTest: Run the full warning-as-error build and full unit and architecture suites before manifesting the clean candidate.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The public contract and gate are additive; no existing schema or consumer changes.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh normalized SDD sources and commit the compact ship verdict; regenerable readiness views remain ignored and must report their exact source digests.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 254-release-hardening`.
