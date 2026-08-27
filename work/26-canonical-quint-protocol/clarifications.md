---
schemaVersion: 1
workId: 26-canonical-quint-protocol
title: Canonical Quint Protocol
stage: clarify
changeTier: tier1
status: needsAnswers
sourceSpec: work/26-canonical-quint-protocol/spec.md
publicOrToolFacingImpact: true
---

# Canonical Quint Protocol Clarifications

## Source Specification
- work/26-canonical-quint-protocol/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001]: Which path and API are authoritative for the canonical literate source and extraction?
- CQ-002 [AMB:AMB-002]: How much executable semantics belongs in GS2-02.1 before the specialized GS2-02.2 through GS2-02.11 units?
- CQ-003 [AMB:AMB-003]: Which generated outputs are committed, and which remain disposable verification products?

## Answers
- CQ-001 → use the published FS.GG.SDD 1.5.0 manifest-v2 `quint-specification-v1` backend with explicit `--profile fsgg-quint-profile/2`, `--source src/FS.GG.Coordination.Protocol/Protocol.md`, and `--bindings src/FS.GG.Coordination.Protocol/Protocol.bindings.json`. The exact offline Q1 tool objects remain unchanged. Published 1.4.0/profile 1 was probed and rejected because it intentionally admits only frozen Q1 whole-program digests; FS.GG.SDD#932 is therefore an explicit prerequisite repair, not an optional upgrade.
- CQ-002 → GS2-02.1 declares the complete stable catalogue vocabulary and a minimal bounded, typecheckable baseline with named actions/invariants; detailed authority, observation, lifecycle, relation, stream, mutation, plan, desired-state, output, and identity-proof semantics remain explicit successor seams.
- CQ-003 → commit the canonical Markdown plus the manifest-v2 authority inventory, catalogue, compiled contract, bindings, source map, and receipt produced atomically by the published backend. Generated `.qnt` modules are disposable and may not be edited or committed as independent authority.

## Decisions
- DEC-001 [CQ-001] [AMB:AMB-001] [FR-002] [FR-003]: Adopt the published 1.5.0 profile-2 source/binding inputs and `quint-specification-v1` author/inspect boundary exactly; preserve frozen profile 1 and create no product-local extractor or profile.
- DEC-002 [CQ-002] [AMB:AMB-002] [FR-002] [FR-007]: Establish every GS2-02.1 vocabulary identity and its minimal executable relationships now, while representing each later semantic family as a typed, inert successor seam rather than approximating its future behavior.
- DEC-003 [CQ-003] [AMB:AMB-003] [FR-004] [FR-005]: Treat canonical Markdown and the backend's authenticated manifest-v2 inventory as authority; extracted `.qnt` is disposable generated output and any independent edit or committed rival source is a qualification failure.

## Accepted Deferrals
- None. Successor semantics are excluded by the named unit boundary, not silently deferred work within GS2-02.1.

## Remaining Ambiguity
- None. All blocking ambiguities are resolved by DEC-001 through DEC-003.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 26-canonical-quint-protocol`.
