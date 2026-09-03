---
schemaVersion: 1
workId: 270-gs2-06-7-repair-receipt
title: GS2-06.7 Repair Receipt
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/270-gs2-06-7-repair-receipt/spec.md
sourceClarifications: work/270-gs2-06-7-repair-receipt/clarifications.md
sourceChecklist: work/270-gs2-06-7-repair-receipt/checklist.md
publicOrToolFacingImpact: true
---

# GS2-06.7 Repair Receipt Plan

Prose status: planned

## Source Snapshot
- spec: work/270-gs2-06-7-repair-receipt/spec.md sha256:d63e7427adcc0ddbae53594edb51903b9a99ea7b9fec1dffeef870b85e14f9bd schemaVersion:1
- clarifications: work/270-gs2-06-7-repair-receipt/clarifications.md sha256:6b750beac05ce267ab0da54064ee5c8a16df75bf69bf609f63a34e0b3ac88414 schemaVersion:1
- checklist: work/270-gs2-06-7-repair-receipt/checklist.md sha256:bca87ba12fe4f3b6548ed32c91b0270140e883c7868012eb5d269875a268b561 schemaVersion:1

## Plan Scope
- Work item 270-gs2-06-7-repair-receipt is planned from the current specification, clarification, and checklist facts.
- Requirement count: 2.
- Clarification decision count: 0.
- Checklist result count: 2.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a dedicated `repair-receipts` storage-policy category and strict schema so the original accepted unit receipt remains the sole `accepted` identity while the repair gains an append-only identity of its own.
- PD-002 [AC-001] [FR-002] complete: Bind the repair receipt to immutable original bytes, implementation and protected merge identities, structured independent review, hosted executions, detached post-merge Q3/Q7 and full-suite results, observed fleet provenance, and the no-mutation boundary; validate every binding and inversion independently.

## Contract Impact
- PC-001 [PD-001] evidence schema: Extend the evidence index/storage policy with a separately named repair category and JSON schema without changing existing accepted receipt semantics.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PC-001] architectureTest: Prove canonical receipt validation plus omission, substitution, digest, duplicate-index/category, original-mutation, and fabricated-identity inversions; then run the full repository, Q3/Q7, SDD, review, hosted, and protected-main gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Existing `accepted` receipts and readers remain unchanged; the repair category is additive and separately indexed.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh deterministic SDD work-model, verify, and ship views for the receipt-only change and retain them for clean-checkout fixed-point verification.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 270-gs2-06-7-repair-receipt`.
