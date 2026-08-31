---
schemaVersion: 1
workId: gs2-04-5-comment-projection-adapter
title: GS2-04.5 typed GitHub comment/projection adapter
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-5-comment-projection-adapter/spec.md
sourceClarifications: work/gs2-04-5-comment-projection-adapter/clarifications.md
sourceChecklist: work/gs2-04-5-comment-projection-adapter/checklist.md
publicOrToolFacingImpact: true
---

# GS2-04.5 typed GitHub comment/projection adapter Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-5-comment-projection-adapter/spec.md sha256:f7509c2e554d84164e6adf3bbcbf04425ee6d2c7040df0c917ac5b30bc48ffbc schemaVersion:1
- clarifications: work/gs2-04-5-comment-projection-adapter/clarifications.md sha256:ad8bcda142cfcc347bbff74abbd17899022247147605938105c265c0b1779921 schemaVersion:1
- checklist: work/gs2-04-5-comment-projection-adapter/checklist.md sha256:8d0b7dc632ebafa83b65aaac0279cd8849ddc9dba5ec7f7ea2db13a9edb047d1 schemaVersion:1

## Plan Scope
- Add a pure comment/projection adapter to `FS.GG.Coordination.GitHub`; this unit performs no HTTP, journal CAS, or hosted writes.
- Add an independent, closed qualification contract to `FS.GG.Coordination.Qualification.Contracts`.
- Prove the contract with focused unit and architecture tests, a committed synthetic fixture, and the registered offline validator.
- Record exact SDD lifecycle evidence for the implementation and its focused gates.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] complete: Define `CommentProjectionAdapter.fsi` before its implementation with typed server identities, exact UTF-8 comment bodies, page coordinates, and terminal pagination evidence. Normalize only complete observations in stable `(createdAt,databaseId)` server order and refuse duplicate database/node identities or reordered input.
- PD-002 [AC-002] [FR-002] [DEC-002] complete: Parse only the exact `fsgg:projection/v1` marker and canonical schema, validating qualified subject, journal kind/shard, positive fencing generation, 40-hex commit, lowercase SHA-256 authority digest, and the digest of the exact human-body suffix.
- PD-003 [AC-003] [FR-003] [DEC-001] [DEC-002] [DEC-003] complete: Preserve duplicate identity, reordered page, Edited, Deleted, Missing, Malformed, Tampered, authority-digest mismatch, unsupported schema, Unauthorized, Incomplete, and Unreadable outcomes as distinct typed refusals. No outcome may invent absence, authority, or trust.
- PD-004 [AC-004] [FR-004] [DEC-003] [DEC-004] complete: Evaluate trust against exactly one expected server identity and an exact canonical durable-journal binding. Comment position, identifier magnitude, timestamps, and recency are observational only and never select transition authority.
- PD-005 [AC-005] [FR-005] [DEC-005] complete: Render deterministic LF-normalized UTF-8 without BOM and with one final LF. Produce guarded create, replace, or no-op plans whose length-framed SHA-256 identity binds action, subject, expected identity/revision, journal identity, policy, desired digest, and causation.
- PD-006 [AC-006] [FR-006] [DEC-006] complete: Compare every plan guard with a mandatory complete re-read and current durable authority. Any identity, update instant, body digest, marker binding, expected absence, or authority drift yields a typed re-read/replan refusal and no effect authority.
- PD-007 [AC-007] [FR-007] [DEC-006] complete: Verify an exact intended marker/body post-state, require an advanced revision for replacement, and compare all unrelated comment identities and body digests byte-for-byte. Concurrent, extra, missing, partial, or indeterminate changes refuse success.
- PD-008 [AC-008] [FR-008] [DEC-007] complete: Define a closed qualification inventory covering pagination, duplicate identity, reordered page, edit, delete, tamper, malformed JSON, authority mismatch, incomplete observation, stale revision, concurrent change, and no-op. Generated and independent producers must enumerate it exactly and every mutation must fail while both controls pass.
- PD-009 [AC-008] [FR-008] [DEC-007] complete: Implement `eng/validate-github-comment-projection.fsx` as the literal registered Q3 command loading production signatures and the independent qualification contract against a committed canonical synthetic fixture, without HTTP, credentials, live writes, Q4 claims, or dependency inversion.

## Contract Impact
- PC-001 [PD-001] [PD-002] publicSurface: `src/FS.GG.Coordination.GitHub/CommentProjectionAdapter.fsi` is the signature-first public adapter contract.
- PC-002 [PD-008] qualificationContract: `src/FS.GG.Coordination.Qualification.Contracts/GitHubCommentProjectionQualification.fsi` is an independent public qualification contract and does not reference the production project.
- PC-003 [PD-009] gateCommand: `eng/validate-github-comment-projection.fsx` is the exact offline validator registered by the roadmap gate catalog.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PC-001] semanticTest: Run focused unit tests for complete reads, canonical marker parsing, every typed refusal, exact trust binding, deterministic plans and idempotency, no-op behavior, stale re-read refusal, and exact post-state verification.
- VO-002 [PD-008] [PC-002] mutationControl: Prove generated and independent producers emit the exact closed inventory, both controls pass, and every named mutation is rejected.
- VO-003 [PD-009] [PC-003] registeredGate: Run the literal registered gate, focused solution tests, architecture checks, SDD analyze/verify, and hosted exact-head qualification before acceptance.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Add the adapter after the existing typed GitHub adapters. No persisted-data migration, remote mutation, or protected-ref journal operation is part of GS2-04.5.

## Generated View Impact
- GV-001 [PD-008] generatedControls: The fixture and generated producer must match the independent producer's exact closed inventory while retaining separate implementations.
- GV-002 [PD-009] lifecycleViews: SDD readiness and evidence views regenerate from current sources; stale or missing generated views cannot satisfy verification.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- MVU is not applicable because this unit is a pure adapter and projection-planning algebra; stateful hosted execution belongs to later reconciliation work.
- The committed fixture is deliberately synthetic and credential-free. Live destructive Q4 proof is deferred by the roadmap to GS2-04.9, not claimed here.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-5-comment-projection-adapter`.
