---
schemaVersion: 1
workId: gs2-05-2-organization-issue-fields
title: GS2-05.2 organization issue-field contract
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-05-2-organization-issue-fields/spec.md
sourceClarifications: work/gs2-05-2-organization-issue-fields/clarifications.md
sourceChecklist: work/gs2-05-2-organization-issue-fields/checklist.md
publicOrToolFacingImpact: true
---

# GS2-05.2 organization issue-field contract Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-05-2-organization-issue-fields/spec.md sha256:84ccbe1d52121c10edca7c496a4a74d28c046785664db2f04f1066be8ac878fc schemaVersion:1
- clarifications: work/gs2-05-2-organization-issue-fields/clarifications.md sha256:d6069e585a727197ddb4fca058eacfa942cf03f167aac55c0faac57885db2c44 schemaVersion:1
- checklist: work/gs2-05-2-organization-issue-fields/checklist.md sha256:7cc9db952d6559ab41e3096d2ff31d9093e220fbe2937f50a6b8135cf96ecb9d schemaVersion:1

## Plan Scope
- Work item gs2-05-2-organization-issue-fields is planned from the current specification, clarification, and checklist facts.
- Requirement count: 9.
- Clarification decision count: 4.
- Checklist result count: 9.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add closed SchedulingIntent and derived LifecycleStatus types plus one pure derivation function; Status never enters planner authority.
- PD-002 [AC-001] [FR-002] complete: Validate the intent/hold matrix through a closed HoldReason type and stable FIELD-HOLD diagnostics.
- PD-003 [AC-002] [FR-003] complete: Represent every registered metadata vocabulary with closed discriminated unions and canonical ISO date strings checked for order.
- PD-004 [AC-002] [FR-004] complete: Require lowercase digest-bound contract and touch-set projection records; normalize path separators, ordering, and duplicates before comparison.
- PD-005 [AC-002] [FR-005] complete: Freeze current rows in canonical JSON with declared count and corpus digest; every combination has an independent expectation.
- PD-006 [AC-002] [AC-003] [FR-006] complete: Implement pure row classification and corpus-wide all-or-nothing planning in Core with canonical stable-row ordering and UTF-8 JSON evidence.
- PD-007 [AC-003] [FR-007] complete: Use stable diagnostic codes for every refusal family and prevent a partial plan when any diagnostic exists.
- PD-008 [AC-004] [FR-008] complete: Add a plain Quint module/test pair whose planned/refused actions, invariants, examples, simulations, and bounded verification use the same closed vocabulary.
- PD-009 [AC-004] [FR-009] complete: Add the exact registered FSI gate, independent expectations, qualification receipt, focused unit/architecture suites, and one mutation per refusal family.

## Contract Impact
- PC-001 [PD-001] [PD-006] [PD-009] publicApiAndCommand: Add `OrganizationIssueFields.fsi`, the registered validator command, and versioned evidence shapes. This is additive and repository-local; no live adapter surface changes.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PD-008] [PD-009] [PC-001] semanticTest: Run the registered gate, focused unit and architecture tests, warning-free build, Quint typecheck/run/test/verify, canonical-byte replay, every independent inversion, and omission mutation.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] explicitPurePlan: This unit emits migration dispositions and diagnostics only. No GitHub or Project apply path exists; GS2-05.3 owns intake and later cutover units own mutation.

## Generated View Impact
- GV-001 [PD-001] [PD-009] workModel: Refresh the work model and analysis only from the final authored SDD sources; retain the exact candidate's verify and ship verdicts under the same work id, and fail if any source digest moves.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-05-2-organization-issue-fields`.
