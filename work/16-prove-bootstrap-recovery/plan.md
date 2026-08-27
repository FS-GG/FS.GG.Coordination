---
schemaVersion: 1
workId: 16-prove-bootstrap-recovery
title: Prove Bootstrap Recovery
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/16-prove-bootstrap-recovery/spec.md
sourceClarifications: work/16-prove-bootstrap-recovery/clarifications.md
sourceChecklist: work/16-prove-bootstrap-recovery/checklist.md
publicOrToolFacingImpact: true
---

# Prove Bootstrap Recovery Plan

Prose status: planned

## Source Snapshot
- spec: work/16-prove-bootstrap-recovery/spec.md sha256:940afa6af6b314f06308b14527f86cd8df6fe66f03f9b628c7f441214388afbf schemaVersion:1
- clarifications: work/16-prove-bootstrap-recovery/clarifications.md sha256:d2993962baa8b58e19d902c1370d7eeaec989d2e4baa4e354102401293f95bc0 schemaVersion:1
- checklist: work/16-prove-bootstrap-recovery/checklist.md sha256:d0984859311337fc2f0b1627e941cb394cd979b10923ec728f671a90d905b750 schemaVersion:1

## Plan Scope
- Work item 16-prove-bootstrap-recovery is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 0.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Implement a dependency-free F# runner that verifies a clean source HEAD, creates private scratch storage, performs a non-local clone, checks out the exact revision detached, and rejects any source/clone revision or cleanliness mismatch before invoking .NET.
- PD-002 [AC-002] [FR-002] complete: Generate a scratch NuGet configuration containing only NuGet.org, isolate all CLI/cache homes, reject feed-related override variables, and restore the solution in locked mode with the explicit configuration and source.
- PD-003 [AC-003] [FR-003] complete: Run one Release warnings-as-errors build followed by unit and architecture tests with no restore or rebuild; preserve ordered process receipts and fail on the first nonzero exit.
- PD-004 [AC-004] [FR-004] complete: Pack the Protocol project at an inert bootstrap version, then reuse the clean package-consumer fixture with only the candidate feed and NuGet.org to restore, build, and execute the installed package without publishing.
- PD-005 [AC-005] [FR-005] complete: Write one canonical compact recovery receipt beneath ignored artifacts, bind its exact candidate and package digest, upload it from a dedicated read-only workflow job, and extend the existing bootstrap manifest contract so tampering or omission fails closed.
- PD-006 [AC-006] [FR-006] complete: Register literal `dotnet fsi eng/bootstrap-recovery.fsx -- .`, independently pin its command hash and GS2-01.8 unit-contract digest, and add static/adversarial architecture tests for permissions, feeds, overrides, command identity, evidence shape, and workflow binding.

## Contract Impact
- PC-001 [PD-005] repository evidence contract: Add `fsgg.coordination.bootstrap-recovery/1` and a required `bootstrap-recovery/result.json` input to the existing exact-head bootstrap evidence manifest.
- PC-002 [PD-006] roadmap gate contract: Add the closed Q7 `bootstrap-recovery` catalog entry, command digest, and recomputed GS2-01.8 unit-contract digest; unsupported or substituted commands fail before execution.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] semanticTest: Run the complete recovery command from an exact clean candidate with isolated caches and require clone, restore, build, both test suites, pack, install, and execute receipts.
- VO-002 [PD-005] [PC-001] semanticTest: Require the hosted bootstrap contract and evidence collector to reject a missing or tampered recovery receipt and stale workflow hash.
- VO-003 [PD-006] [PC-002] semanticTest: Require static permission/feed/override controls, the exact roadmap manifest/gate execution, prerequisite receipt validation, and `stoppedAtUnitBoundary=true`.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Existing five bootstrap jobs remain compatible; the evidence manifest gains one required recovery artifact only after the producer job exists in the same commit.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh the generated work model, analysis, verification, ship verdict, and governance handoff from the final authored lifecycle sources; never hand-edit generated readiness data or treat an earlier source snapshot as current evidence.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 16-prove-bootstrap-recovery`.
