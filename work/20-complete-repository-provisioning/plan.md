---
schemaVersion: 1
workId: 20-complete-repository-provisioning
title: Complete Repository Provisioning
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/20-complete-repository-provisioning/spec.md
sourceClarifications: work/20-complete-repository-provisioning/clarifications.md
sourceChecklist: work/20-complete-repository-provisioning/checklist.md
publicOrToolFacingImpact: true
---

# Complete Repository Provisioning Plan

Prose status: planned

## Source Snapshot
- spec: work/20-complete-repository-provisioning/spec.md sha256:89fa96b659039dc325b9ae5d0d43ebaf51f9a21e1d9748f245c11dd2b8ff0fed schemaVersion:1
- clarifications: work/20-complete-repository-provisioning/clarifications.md sha256:a4db191bb58015ddd7d56d2b15844918ab933c94b6b2bdab64ac954042f6d2c1 schemaVersion:1
- checklist: work/20-complete-repository-provisioning/checklist.md sha256:2d152308433384fc185051da6a2afeda68c8b8c60381468aaa365910a675ae29 schemaVersion:1

## Plan Scope
- Work item 20-complete-repository-provisioning is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 0.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add repository-wide CODEOWNERS rooted in `@FS-GG/coordination-maintainers`, explicitly covering workflows, protocol, qualification contracts, evidence, and settings/release contracts. Validate the live team grant is exactly Maintain and the repository has no unexpected team grant.
- PD-002 [AC-002, AC-005] [FR-002] complete: Record the authoritative pre-state, attach organization code-security configuration 17, bind its exact association and policy projection, verify CodeQL default setup and private vulnerability reporting, then apply supported repository security and replace `allowed_actions=all` with selected GitHub-owned Actions. Preserve the already-correct merge, repository-feature, workflow-token, and self-approval values. Bind the effective disabled validity-check and generic/non-provider projections as license-unsupported, and treat organization SHA-policy 403 as an explicit unsupported result, never success.
- PD-003 [AC-003] [FR-003] complete: After CODEOWNERS and the settings verifier merge, create one active `~DEFAULT_BRANCH` ruleset from reviewed JSON. Bind the six exact GitHub Actions contexts to App id 15368, require strict current-head checks and one independent approval, dismiss stale reviews, require last-push and CODEOWNERS approval plus resolved threads, block deletion/non-fast-forward changes, and define no bypass actor.
- PD-004 [AC-004] [FR-004] complete: Create one active `refs/tags/v*` ruleset from reviewed JSON with deletion/non-fast-forward protection and required signatures, no bypass, and no release/runtime side effect.
- PD-005 [AC-005] [FR-005] complete: Add a dependency-free F# validator over a versioned desired-state contract and compact receipt. Capture complete paginated REST responses, normalize only declared fields in fixed order, hash raw bytes, record operation outcomes and rollback guidance, and publish the exact post-state receipt on the accountable issue for independent acceptance.
- PD-006 [AC-006] [FR-006] complete: Split source merge from protected setting apply. No setting write begins before `implementationReady`; no unit acceptance occurs until authoritative post-write rereads and independent review agree. A lost/403/partial/stale response stops the batch, with organization-only SHA policy retained as unsupported and no runtime/successor work started.

## Contract Impact
- PC-001 [PD-001, PD-002, PD-003, PD-004, PD-005] repository provisioning contract: `.github/CODEOWNERS`, `eng/repository-settings/desired.json`, branch/tag ruleset request JSON, `eng/repository-settings/verify.fsx`, and `fsgg.coordination.repository-settings-receipt/2` become the reviewed source and evidence boundary. The unaccepted `/1` shape is superseded before any live receipt is accepted; `/2` is the sole accepted GS2-01.1 receipt shape.

## Verification Obligations
- VO-001 [PD-001, PD-002, PD-003, PD-004, PD-005, PD-006] [PC-001] semanticTest: Run static contract tests, strict receipt positive validation, isolated mutations for repository identity, merge/security/Actions/team/check/ruleset/bypass/signature/unsupported/canonical fields, the full locked build and test suites, exact live post-state verification, independent settings review, and ordinary protected-branch qualification.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additiveThenProtect: Supersede the unaccepted receipt `/1` contract with `/2` before acceptance, merge the validator and CODEOWNERS before enabling enforcement, apply repository settings in small reread-verified batches, and create branch protection last. Roll forward by reviewed contract correction because removing protection to recover would weaken the accepted boundary.

## Generated View Impact
- GV-001 [PD-005] workModel: readiness/20-complete-repository-provisioning/work-model.json and the ignored live receipt refresh from current lifecycle and authoritative API sources; no generated live response is silently committed as source.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 20-complete-repository-provisioning`.
