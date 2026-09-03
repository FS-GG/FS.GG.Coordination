---
schemaVersion: 1
workId: 288-gs2-06-8-fleet-dry-plans
title: Gs2 06 8 Fleet Dry Plans
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/288-gs2-06-8-fleet-dry-plans/spec.md
sourceClarifications: work/288-gs2-06-8-fleet-dry-plans/clarifications.md
sourceChecklist: work/288-gs2-06-8-fleet-dry-plans/checklist.md
publicOrToolFacingImpact: true
---

# Gs2 06 8 Fleet Dry Plans Plan

Prose status: planned

## Source Snapshot
- spec: work/288-gs2-06-8-fleet-dry-plans/spec.md sha256:b01799dd66b55645fdbb4535119370dc3b254f025eb6c014e4b668e431f9f80b schemaVersion:1
- clarifications: work/288-gs2-06-8-fleet-dry-plans/clarifications.md sha256:71c57d1b7b5e5bc01e3a3524d23b0ef4c64a100648acae72be09aad106652fbc schemaVersion:1
- checklist: work/288-gs2-06-8-fleet-dry-plans/checklist.md sha256:b48716fce81d91acc6df0f38c33c96bcffde6140bcfd985ca1fbd856f41c47cb schemaVersion:1

## Plan Scope
- Work item 288-gs2-06-8-fleet-dry-plans is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 6.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add an additive `GitHubFleetDryPlanQualification` signature/implementation whose top-level input binds roadmap/unit/source revisions, accepted receipt digests, and the exact typed repository roster into one length-framed SHA-256 seal.
- PD-002 [AC-002] [FR-002] complete: Model endpoint observations and pagination proofs as discriminated unions, validate exhaustive repository/endpoint coverage, and retain one explicit disposition per setting; all missing, duplicate, extra, malformed, stale, and permission-indeterminate inputs return typed errors.
- PD-003 [AC-003] [FR-003] complete: Derive operations only for supported desired differences, sort by repository/setting/action, derive ids and digests from framed semantic fields, use strict canonical JSON, and prove parse-serialize equality plus repeated compilation byte identity.
- PD-004 [AC-004] [FR-004] complete: Review is a separate typed digest over exact plan bytes and reviewer identity. Re-inspection recomputes relevant fingerprints from a second observation set and yields confirmed or stale without consuming first-read output.
- PD-005 [AC-005] [FR-005] complete: Add unit and architecture tests, a standalone Q5 FSI validator, checked-in live observation/desired/reinspection/review/corpus/independent-expectation evidence, generated mutation enumeration, and exact-candidate roadmap manifest/gate results.
- PD-006 [AC-006] [FR-006] complete: Keep all production integration read-only and evidence-only. The first PR omits a closing keyword; after its verified protected merge, rotate the claim through markerless In progress and append the acceptance receipt in a second reviewed PR.

## Contract Impact
- PC-001 [PD-001] qualificationContract: Add `GitHubFleetDryPlanQualification.fsi/.fs` and include them in the Qualification.Contracts project without changing existing signatures.
- PC-002 [PD-002] evidenceSchema: Add strict schema `fsgg.github-fleet-dry-plan/1` and provider artifacts under `evidence/github-substrate-v2/gs2-06-8`; unknown/missing/duplicate fields are refused.
- PC-003 [PD-005] gateCommand: Implement the already registered command `dotnet fsi eng/validate-github-fleet-dry-plans.fsx -- .` without changing its catalog identity.
- PC-004 [PD-006] acceptanceReceipt: Append schema-compatible immutable `evidence/github-substrate-v2/accepted/GS2-06.8.json` only after the implementation protected merge.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Prove exact authority and ten-repository roster binding; mutate each authority identity and omit/duplicate/extra one roster member.
- VO-002 [PD-002] [PC-002] semanticTest: Prove every endpoint is terminal and every disposition is explicit; independently mutate pagination, permission, identity, time, completeness, and each disposition.
- VO-003 [PD-003] [PC-002] semanticTest: Prove minimality, deterministic ordering, stable ids, least permissions, desired/pre-state binding, unrelated preservation, strict reparse, and byte-identical replay; invert each control.
- VO-004 [PD-004] [PC-002] semanticTest: Prove independent review and second-read confirmation; relevant drift stales and unrelated drift remains confirmed.
- VO-005 [PD-005] [PC-003] integrationTest: Run warning-free Release build, full unit/architecture suites, the Q5 validator directly, then all nine registered commands through roadmap-work gates from a clean exact candidate.
- VO-006 [PD-006] [PC-004] protectedBoundary: Bind exact-head review and hosted checks, implementation protected merge and exact-merge run, markerless handoff, receipt review/merge, receipt immutability, and the single terminal completion receipt.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Existing GS2-06 contracts and accepted receipts remain byte-identical; the new module, evidence schema, and Q5 gate are additive.

## Generated View Impact
- GV-001 [PD-001] workModel: Regenerate analysis, work-model, verify, ship, governance handoff, and ship verdict from exact authored sources; retain the focused/unit/architecture reports referenced by evidence and require two consecutive no-change verify/ship reads from the clean candidate.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 288-gs2-06-8-fleet-dry-plans`.
