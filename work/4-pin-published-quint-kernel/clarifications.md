---
schemaVersion: 1
workId: 4-pin-published-quint-kernel
title: Pin Published Quint Kernel
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/4-pin-published-quint-kernel/spec.md
publicOrToolFacingImpact: true
---

# Pin Published Quint Kernel Clarifications

## Source Specification
- work/4-pin-published-quint-kernel/spec.md

## Clarification Questions
- **CQ-001** [FR-001] [FR-002]: Which published artifact and identity are authoritative for this consumer?
- **CQ-002** [FR-002] [FR-003]: Which digest is stable across signed feed copies while still binding the accepted bundle?
- **CQ-003** [FR-003] [FR-004]: How is the producer/consumer boundary enforced without copying the producer implementation?

## Answers
- CQ-001 → Consume `FS.GG.SDD.Artifacts` exactly at version `1.4.0`; its packaged `quint/q1-identity-manifest.json` has schema `fsgg.quint.q2-toolchain-identity/1`, profile `fsgg-quint-profile/1`, and accepted producer/consumer merge bindings.
- CQ-002 → Verify the exact SHA-256 of the packaged identity-manifest payload (`abd9c18e8146ac3855be58ce88f1efbf5e74a4b1e42c8bc35927478cc74393b2`) and its typed identity fields. Do not bind to a signed `.nupkg` container hash because feed signing may change container bytes while preserving payload bytes.
- CQ-003 → Reference the package only from `Qualification.Contracts`, validate the resolved assets and MSBuild graph, and use repository scanners plus mutation fixtures to reject project references, local package sources, and producer-named files or implementations.

## Decisions
- **DEC-001** [CQ-001] [FR-001] [FR-002] [AC-001]: Pin `FS.GG.SDD.Artifacts` `1.4.0` centrally and reference it only from `FS.GG.Coordination.Qualification.Contracts`.
- **DEC-002** [CQ-002] [FR-002] [FR-003] [AC-001] [AC-002]: Bind acceptance to the embedded identity-manifest payload digest and required identity fields, while treating feed-specific signed container hashes as transport evidence rather than semantic identity.
- **DEC-003** [CQ-003] [FR-003] [FR-004] [AC-002] [AC-003]: Enforce consumer-only use with a closed project/package/source policy and independent mutation fixtures; do not copy or expose producer implementation.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 4-pin-published-quint-kernel`.
