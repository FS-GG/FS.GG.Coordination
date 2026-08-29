---
schemaVersion: 1
workId: 86-gs2-03-3-generated-structural-tests
title: GS2-03.3 generated structural tests
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.3 Generated Structural Tests Specification

Prose status: specified

## User Value
Maintainers can prove that every structural element exported by the qualified Quint protocol is represented by a deterministic executable case, while reviewers can distinguish this source-derived coverage from the independent behavioral oracles owned by GS2-03.4.

## Scope
- SB-001: Generate a canonical structural-test manifest from the qualified compiled contract and its registered compiled outputs.
- SB-002: Cover vocabulary catalogue entries, action/transition effects, command registrations, mutation registrations, required permissions, record-schema round trips, and projection freshness.
- SB-003: Bind every case to immutable qualified source, behavioral, and compiled-contract identities and to the exact source output from which the case was derived.
- SB-004: Provide a production validator, deterministic regeneration check, architecture-test integration, negative controls, and content-addressed evidence.

## Non-Goals
- SB-005: Do not hand-author expected protocol behavior or create a second state-transition model; generated expectations must be direct projections of the qualified contract.
- SB-006: Do not implement GS2-03.4 independent black-box oracles, GS2-03.5 model/property runs, GS2-03.6 fault injection, network or GitHub mutations, deployment, publication, or production writes.
- SB-007: Do not normalize or rewrite the GS2-03.2 frozen corpus or any accepted evidence receipt.

## User Stories
- US-001 (P1): As a qualification maintainer, I can regenerate a byte-identical structural suite from the exact qualified contract.
- US-002 (P1): As an independent reviewer, I can account for every registered vocabulary, transition, command, mutation, permission, schema, and projection through a source-bound case.
- US-003 (P1): As a cutover operator, I receive a named red result when structural coverage is missing, duplicated, stale, substituted, unregistered, malformed, or bound to different qualified identities.
- US-004 (P2): As a future oracle author, I can tell mechanically that these cases are generated structural evidence and never mistake them for independently authored behavioral evidence.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given the accepted qualified source and compiled outputs, when generation runs twice, then it emits canonical byte-identical manifest bytes with complete category counts and a valid self-digest.
- AC-002 [US-002] [FR-003] [FR-004]: Given the generated manifest and live qualified artifacts, when validation runs, then every live vocabulary item, action effect, command, mutation, permission, record shape, and registered projection output maps to exactly one deterministic case and no case lacks a live source.
- AC-003 [US-003] [FR-005] [FR-006]: Given a case or live source is removed, duplicated, reordered where canonical order is required, changed, substituted, made stale, or bound to another identity, when validation runs, then the relevant named structural gate is red.
- AC-004 [US-002] [US-004] [FR-007]: Given a generated case, when its provenance is inspected, then it identifies its category, source artifact, source key, qualified identities, derivation rule, and generated-only evidence class without asserting independent behavioral correctness.
- AC-005 [US-001] [US-003] [FR-008]: Given existing accepted receipts and frozen corpus bytes, when GS2-03.3 is implemented and verified, then all prior immutable evidence and corpus bytes remain unchanged and Q2/Q7 accept only the new source-bound structural evidence.

## Functional Requirements
- FR-001: Generation MUST consume only `contract.json` and the compiled-output manifest plus its registered JSON outputs, reject unsupported/incomplete/stale identity envelopes, and emit one canonical `fsgg.quint.generated-structural-tests/1` document. (Stories: US-001, US-004; Acceptance: AC-001)
- FR-002: The generated document MUST bind source SHA-256, behavioral SHA-256, compiled-contract SHA-256, the qualified output-manifest identity, ordered category descriptors, ordered cases, category counts, total count, and a SHA-256 self-digest over canonical bytes. Ordering and JSON serialization MUST be deterministic and duplicate-free. (Stories: US-001; Acceptance: AC-001)
- FR-003: Vocabulary cases MUST derive from every contract catalogue identity; transition cases from every action-effect identity and its read/write/subject sets; command and mutation cases from their complete registered compiled outputs; permission cases from every required permission; schema cases from every record shape and its exact ordered fields; and projection cases from every compiled-output manifest entry plus the projection view. (Stories: US-002; Acceptance: AC-002)
- FR-004: Validation MUST independently rebuild the expected case keys and source digests from the live qualified artifacts and compare complete sets, counts, identities, ordering, category registration, canonical bytes, and self-digest. It MUST NOT accept producer-declared totals or a second copy of hand-authored expectations as authority. (Stories: US-002, US-003; Acceptance: AC-002, AC-003)
- FR-005: Every category MUST have focused inversions for a missing case and source substitution. Across the complete generated suite, focused inversions MUST also cover duplicate case/key, identity drift, stale or incomplete output, incorrect count, non-canonical order, malformed envelope, digest tampering, and omission from each independently readable producer inventory. Each inversion MUST reach the production validator and produce a stable named refusal. (Stories: US-003; Acceptance: AC-003)
- FR-006: The committed generated document MUST be byte-identical to fresh generation. Unregistered new vocabulary, action, command, mutation, permission, schema, or projection entries MUST make freshness validation red until regeneration records them. (Stories: US-001, US-003; Acceptance: AC-003)
- FR-007: Every case MUST declare `evidenceClass=generated-structural`, a derivation rule, source artifact and source key. The validator MUST reject any generated case labelled independent, behavioral, black-box, or fault-injection evidence. (Stories: US-004; Acceptance: AC-004)
- FR-008: Implementation MUST preserve all GS2-03.2 frozen-corpus files and every pre-existing accepted evidence receipt byte-for-byte, register the new generated artifact under the existing qualification/evidence storage controls, and pass the pure portions of Q2 and Q7. (Stories: US-001, US-003; Acceptance: AC-005)

## Ambiguities
- AMB-001: Whether transition coverage should enumerate concrete state combinations. Resolution target: no; GS2-03.3 covers the qualified action-effect registration surface, while concrete behavioral reachability belongs to GS2-03.5.
- AMB-002: Whether schemas should be executed against invented sample values. Resolution target: no; structural round trip operates over canonical schema descriptors and exact field order, avoiding fabricated domain semantics.
- AMB-003: Whether each compiled output becomes a projection case even when a more specific category also consumes it. Resolution target: yes; projection freshness accounts for the complete registered output set, while category cases account for its typed content.

## Public Or Tool-Facing Impact
- Adds a committed generated structural-test manifest and repository-local generator/validator entry points used by architecture and evidence-storage qualification.
- Does not add network authority or change production coordination behavior.

## Lifecycle Notes
- The generated suite is exhaustive structural evidence but intentionally not an independent correctness oracle.
- Next lifecycle action: `fsgg-sdd clarify --work 86-gs2-03-3-generated-structural-tests`.
