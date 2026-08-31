---
schemaVersion: 1
workId: 115-gs2-03-8-critique-evidence
title: GS2-03.8 critique evidence gates
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/115-gs2-03-8-critique-evidence/spec.md
sourceClarifications: work/115-gs2-03-8-critique-evidence/clarifications.md
sourceChecklist: work/115-gs2-03-8-critique-evidence/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.8 critique evidence gates Plan

Prose status: planned

## Source Snapshot
- spec: work/115-gs2-03-8-critique-evidence/spec.md sha256:4a1854de1a58d66a167f45eca4c9b7ebbe48e7d84c1698a4b6abd56fe0ac9f09 schemaVersion:1
- clarifications: work/115-gs2-03-8-critique-evidence/clarifications.md sha256:db4ce26c7e557323d90829c810c51edaedbe5f9953a7ca2cb88ebdf1d608c807 schemaVersion:1
- checklist: work/115-gs2-03-8-critique-evidence/checklist.md sha256:42c6290db6e8fe9d66e535503b96180e2562bd1532950b594cf84b6e6fabde56 schemaVersion:1

## Plan Scope
- Work item 115-gs2-03-8-critique-evidence is planned from the current specification, clarification, and checklist facts.
- Requirement count: 8.
- Clarification decision count: 0.
- Checklist result count: 8.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [FR-002] complete: Add one typed, canonical `critique-evidence/1` bundle that binds an exact candidate revision, tracked-tree digest, unit-contract digest, Accountable Delivery Owner, and closed evidence inventory.
- PD-002 [AC-001] [FR-003] [FR-004] complete: Represent architecture, security, adapter, migration, and cutover as a closed discriminated union; require one finding per perspective, a distinct phase identity per finding, and the same Accountable Delivery Owner as author without creating five authorization identities.
- PD-003 [AC-001] [FR-005] complete: Derive the roll-up from the canonical findings and evidence inventory; never accept a caller-asserted green result or prose summary as authority.
- PD-004 [AC-001] [FR-006] complete: Regenerate the complete expected canonical bundle during validation and require exact byte equality so omissions, additions, stale bindings, substitutions, digest forgery, truncation, noncanonical ordering, and hidden red findings fail closed.
- PD-005 [AC-001] [FR-007] complete: Preserve the immutable reviews/v1 schema, add a closed Draft 2020-12 reviews/v2 schema, select it through storage policy, and execute generic schema validation before review-specific semantic validation.
- PD-006 [AC-001] [FR-007] complete: Keep frozen-corpus records on their stronger specialized validator because generic schema validation would otherwise pre-empt the domain-specific failure diagnostics; all other ordinary categories use the generic schema path.
- PD-007 [AC-001] [FR-008] complete: Change the qualification plan review-policy identity from `structured-decisions/1` to `critique-evidence/1`, making policy adoption part of the hashed qualification subject without adding workflow ordering or another CI job.
- PD-008 [AC-001] [FR-008] complete: Prove the design with typed unit inversions, schema/storage self-test inversions, architecture-boundary tests, warning-free build, exact-head hosted qualification, and a post-merge critique bundle bound to the immutable implementation merge.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] typedContract: `FS.GG.Coordination.Qualification.Contracts.CritiqueEvidence` is the additive public generation and validation boundary for canonical critique bundles.
- PC-002 [PD-005] [PD-006] storageContract: `evidence/github-substrate-v2/schemas/v2/reviews.schema.json` is executable and selected by storage policy while reviews/v1 remains byte-preserved.
- PC-003 [PD-007] qualificationIdentity: `critique-evidence/1` participates in the qualification-subject digest, so evidence produced under the prior review policy cannot be reused for this candidate.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] typedInversions: Generate one canonical green and one canonical red bundle, then invert every identity, inventory, perspective, phase, authority, time, binding, content, digest, shape, and roll-up predicate.
- VO-002 [PD-005] [PD-006] [PC-002] schemaExecution: Run the evidence-storage positive bundle plus missing, oversized, externally authored, and open-shape negatives; preserve frozen-corpus specialized diagnostics.
- VO-003 [PD-007] [PC-003] policyIdentity: Validate the reviewed bootstrap plan and generated workflow and prove that the review-policy change changes the qualification subject.
- VO-004 [PD-008] [PC-001] [PC-002] [PC-003] exactCandidate: Run warning-free build, full unit and architecture suites, declared roadmap gates, exact-head hosted qualification, post-merge validation, and retained five-perspective evidence.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additiveVersion: Add the typed contract and reviews/v2 beside reviews/v1; do not reinterpret or rewrite accepted v1 evidence.
- PM-002 [PC-003] atomicPolicyCutover: Switch the hashed review-policy identity in the same candidate that introduces executable v2 validation and its inversion evidence.
- PM-003 [PC-001] successorBoundary: Defer runtime review/delivery interpretation to GS2-05.6; this item establishes evidence semantics without authorizing mutation or adding another approval dependency.

## Generated View Impact
- GV-001 [PD-005] storageProjection: storage-policy schema selection and the reviews documentation must agree on reviews/v2 while retaining reviews/v1.
- GV-002 [PD-007] workflowProjection: the bootstrap workflow remains a deterministic projection of the changed qualification plan and must remain current.
- GV-003 [PD-001] workModel: readiness/115-gs2-03-8-critique-evidence/work-model.json refreshes from current lifecycle sources and must be current before ship.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.
- Five perspectives are five independently digestible evidence phases, not five people, accounts, agents, votes, or GitHub approvals.
- The post-merge acceptance bundle is a follow-up evidence record so it can bind the exact immutable implementation merge without a self-referential commit digest.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 115-gs2-03-8-critique-evidence`.
