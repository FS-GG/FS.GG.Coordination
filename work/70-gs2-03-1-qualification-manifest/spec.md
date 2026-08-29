---
schemaVersion: 1
workId: 70-gs2-03-1-qualification-manifest
title: GS2-03.1 Qualification Manifest
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.1 Qualification Manifest Specification

Prose status: specified

## User Value
Reviewers can prove that one exact candidate was qualified from complete, fresh, independent, content-addressed evidence.

## Scope
- SB-001: Define one repository-local canonical qualification-manifest schema, deterministic generator, strict validator, retained example, CLI boundary, and Q1/Q2/Q7 qualification evidence.
- SB-002: Bind exact candidate identity plus complete closed sets for source, model, compiler, dependencies, generated cases, independent cases, external fixtures, package bytes, environment, results, and reviewers against a separately supplied, content-addressed expected-inventory contract.
- SB-003: Preserve the canonical literate Quint protocol as the sole behavioral authority; manifest vocabulary and validation facts are typed protocol projections, not an independent behavioral model.

## Non-Goals
- SB-004: Do not import the frozen corpus, implement independent oracle behavior, run fault injection, publish candidate artifacts, deploy, mutate GitHub, or perform production writes.
- SB-005: Do not claim complete Q6 recovery or Q7 supply-chain qualification; this unit only establishes manifest and evidence-storage capability consumed by later GS2-03 units.

## User Stories
- US-001 (P1): As a qualification reviewer, I can identify the exact candidate and every input, environment, result, and review that supports its qualification.
- US-002 (P1): As an independent reviewer, I can distinguish generated evidence from independent evidence and reject a generated-only or self-reviewed roll-up.
- US-003 (P1): As an operator, I receive a typed refusal when any required evidence is absent, duplicated, stale, substituted, malformed, unsupported, or bound to another candidate.
- US-004 (P1): As a maintainer, I can regenerate byte-identical manifest bytes from the same canonical inputs and obtain a different content address for any semantic input change.

## Acceptance Scenarios
- AC-001 [US-001] [US-004] [FR-001] [FR-002]: Given the same complete inputs in different discovery orders, when the manifest is generated, then canonical bytes, self-digest, candidate binding, and ordered entry identities are identical.
- AC-002 [US-001] [FR-003]: Given one complete manifest and its independently supplied expected inventory, when they are validated, then the manifest binds the exact inventory bytes, every required category has its exact closed identity set, and all digests, sizes, media types, provenance, timestamps, and candidate bindings agree.
- AC-003 [US-002] [FR-004]: Given generated cases and results but no independently authored cases or a reviewer who is also the producer, when validation runs, then it refuses generated-only or self-reviewed evidence with distinct findings.
- AC-004 [US-003] [FR-005]: Given any required category is missing, duplicated, stale, substituted, truncated, malformed, unsupported, bound to another candidate, or removed together with its manifest-embedded expected ID, when validation runs against the unchanged independent inventory, then it fails closed with the category and exact path identified.
- AC-005 [US-004] [FR-006]: Given one semantic manifest input changes, when generation is repeated, then the self-digest changes; given only discovery order or JSON presentation changes before canonicalization, the digest does not change.
- AC-006 [US-001] [US-003] [FR-007]: Given the retained example and every validator class inversion, when Q1, pure Q2, and Q7 controls run, then the example is green and each inversion is red without network or production authority.

## Functional Requirements
- FR-001: The manifest MUST bind schema version; source/model/compiler identities; exact candidate commit, tree, and compiled-contract identities; and one canonical UTC creation instant. (Stories: US-001, US-004; Acceptance: AC-001)
- FR-002: Every category MUST be a duplicate-free, ordinally sorted closed set whose entries carry stable IDs, role/kind, immutable SHA-256, byte length where bytes exist, media type where applicable, producer/provenance identity, and candidate relationship. Its expected ID set MUST come from separately supplied canonical inventory bytes, and the manifest MUST bind those exact bytes by SHA-256. (Stories: US-001, US-004; Acceptance: AC-001, AC-002)
- FR-003: The validator MUST require the exact category vocabulary: sources, model, compiler, dependencies, generated cases, independent cases, external fixtures, packages, environment, results, and reviewers; unknown properties, categories, roles, schema versions, digest forms, or mutable references MUST fail closed. (Stories: US-001, US-003; Acceptance: AC-002, AC-004)
- FR-004: Generated and independent evidence MUST carry distinct authorship/provenance roles; at least one independent case and one independent reviewer MUST exist; a reviewer identity MUST differ from the producer identities of the candidate and reviewed results. (Stories: US-002; Acceptance: AC-003)
- FR-005: Results and reviews MUST bind the exact candidate and the exact input-set digest, name their Q gate or review role, record a terminal accepted outcome, and use canonical timestamps that are not earlier than their inputs or later than manifest creation. (Stories: US-001, US-003; Acceptance: AC-002, AC-004)
- FR-006: Generation MUST serialize one canonical property order and sorted entry order, calculate the manifest digest over canonical bytes omitting only the digest field, and validate that self-digest before any qualification claim is consumed. (Stories: US-004; Acceptance: AC-001, AC-005)
- FR-007: The retained example, CLI validation surface, generated projections, evidence index, and tests MUST cover positive completeness plus independent omissions/substitutions for every category, duplicate IDs, stale candidate/input bindings, generated-only cases, self-review, malformed canonical forms, unsupported versions, and self-digest tampering. (Stories: US-001, US-003; Acceptance: AC-006)

## Ambiguities
- AMB-001: Which identity separates an independent reviewer from candidate and result producers without introducing organization-directory authority?
- AMB-002: Which timestamps are freshness authority, and what ordering constraints are valid without trusting local wall-clock duration?
- AMB-003: Should environment be a single closed record or an ordered set of observed platform/tool facts?
- AMB-004: How should package bytes be represented before GS2-03.7 publishes or attests them?

## Public Or Tool-Facing Impact
- Adds a versioned qualification-manifest JSON contract, canonical generator/validator API, CLI validation command, retained example/projection, and typed finding vocabulary.
- Extends the canonical Quint compiled contract with qualification-manifest vocabulary and invariants; no network or production command is added.

## Lifecycle Notes
- Authority order: accepted GS2-03.1 unit contract, canonical `Protocol.md` Quint blocks, generated compiled contract, qualification-manifest schema/projection, retained example, tests, and evidence.
- Any proposed behavior beyond manifest construction and validation returns to scope review rather than being silently pulled forward from GS2-03.2–GS2-03.9.
