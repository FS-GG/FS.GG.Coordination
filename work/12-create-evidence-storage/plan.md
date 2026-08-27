---
schemaVersion: 1
workId: 12-create-evidence-storage
title: Create Evidence Storage
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/12-create-evidence-storage/spec.md
sourceClarifications: work/12-create-evidence-storage/clarifications.md
sourceChecklist: work/12-create-evidence-storage/checklist.md
publicOrToolFacingImpact: true
---

# Create Evidence Storage Plan

Prose status: planned

## Source Snapshot
- spec: work/12-create-evidence-storage/spec.md sha256:2bf5afa730df365635e913e95d64c3b09c4cdac45d2cfa47c54b0e4d37578c17 schemaVersion:1
- clarifications: work/12-create-evidence-storage/clarifications.md sha256:02f7374fb58f32c9ed710a0cb462c959e8b66c88f4cce0bfdd6902387f0e5213 schemaVersion:1
- checklist: work/12-create-evidence-storage/checklist.md sha256:32d2a9d963d9295f68042a84a9ef16ea92aa1402382787cb5e0efbcbc02e7197 schemaVersion:1

## Plan Scope
- Work item 12-create-evidence-storage is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 0.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Declare one version-one policy with exactly eight ordered category records, category directories, and category-specific JSON schemas under `evidence/github-substrate-v2`.
- PD-002 [AC-002] [AC-004] [FR-002] complete: Keep a compact canonical index sorted by stable identifier and bind every tracked JSON payload to its normalized relative path, category, media type, byte length, and lowercase SHA-256.
- PD-003 [AC-003] [FR-003] complete: Cap tracked payloads at 65,536 bytes; represent larger generated payloads only with digest-bound `github-actions-artifact` or `github-release-asset` manifests containing positive numeric repository, run or release, and artifact or asset IDs.
- PD-004 [AC-004] [FR-004] complete: Implement a dependency-free offline F# gate that validates every indexed record against its category contract, with a positive artifact-manifest case and isolated mutations for versions, digests, paths, duplicates, ordering, schemas, categories, mutable locators, sizes, and symbolic links.
- PD-005 [AC-005] [FR-005] complete: Index every existing accepted receipt by exact bytes and validate it read-only under `accepted/`; creation of the GS2-01.7 receipt remains a post-merge operation outside this candidate.
- PD-006 [AC-004] [FR-006] complete: Admit only literal local `dotnet fsi` execution; the implementation contains no settings administration, deployment, credentials, network access, remote writer, or successor command.

## Contract Impact
- PC-001 [PD-001] repository contract: Add schemas `fsgg.coordination.evidence-storage-policy/1`, `fsgg.coordination.evidence-index/1`, the eight category schemas, and exact Q7 command `evidence-storage-contract`; unsupported versions fail closed.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Require the positive storage validation, all independent negative self-tests, locked Release build, unit tests, architecture tests, and roadmap exact-candidate gate before review.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: Existing accepted receipt bytes are indexed without migration or rewrite; future evidence adopts version one, and unsupported storage versions diagnose without mutation.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh `readiness/12-create-evidence-storage/work-model.json` and analysis from the authored lifecycle sources; generated readiness data is never hand-edited.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 12-create-evidence-storage`.
