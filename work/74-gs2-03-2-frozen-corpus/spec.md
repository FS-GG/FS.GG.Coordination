---
schemaVersion: 1
workId: 74-gs2-03-2-frozen-corpus
title: GS2-03.2 Frozen Corpus
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.2 Frozen Corpus Specification

Prose status: specified

## User Value
Reviewers can reproduce the exact 21-case v1 corpus from immutable source Git objects and distinguish expected behavior, ambiguity, and observed v1 outcomes without normalized or invented evidence.

## Scope
- SB-001: Import every Q0 corpus payload byte-for-byte from FS-GG/.github commit 95de1c77674b9dd8d7a9ce568d1ee175a7797e5e; add closed canonical metadata and a pure validator under Q2 and Q7.
- SB-002: Bind source repository, commit, path, ref, media type, byte length, SHA-256, Git blob SHA-1, historical context, expected behavior, ambiguity state, current-v1 result, and evidence provenance for every case.

## Non-Goals
- SB-003: Do not generate structural cases, implement independent oracles, run fault injection, change the canonical behavioral model, publish, deploy, use network access, mutate GitHub, or gain production-write authority.

## User Stories
- US-001 (P1): As a qualification reviewer, I can byte-compare each imported payload with its immutable Q0 Git object and reproduce both digest identities.
- US-002 (P1): As a migration reviewer, I can read expected behavior separately from current-v1 result and preserve indeterminate, failing, or unobserved states.
- US-003 (P1): As a defect investigator, I can see explicit ambiguity and provenance without any whitespace, line-ending, encoding, JSON, YAML, Markdown, shell, Python, or F# normalization.
- US-004 (P1): As an operator, I receive a typed refusal for any omitted, duplicate, extra, malformed, unsafe, unsupported, stale, or tampered case or field.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the frozen Q0 manifest and exact source commit, all 21 imported payloads have byte length, SHA-256, and Git blob SHA-1 identical to the source rows and Git objects.
- AC-002 [US-001] [FR-001]: Given the same corpus in any filesystem enumeration order, validation yields the same ordered identities and aggregate digest.
- AC-003 [US-001] [FR-001]: Given expected behavior and current-v1 observations, they remain distinct fields; an expected pass cannot manufacture an observed pass, and an indeterminate or unobserved result remains explicit.
- AC-004 [US-001] [FR-001]: Given any source byte is normalized or changed, the payload SHA-256 and Git blob identity checks both fail before the case is consumable.
- AC-005 [US-001] [FR-001]: Given any case or bound field is removed, duplicated, substituted, reordered, widened, malformed, or stale, the closed validator returns a distinct red finding.
- AC-006 [US-001] [FR-001]: Given unsafe paths, symlinks, unsupported schemas, unknown fields, noncanonical JSON, mutable source references, or contradictory provenance, validation fails closed.
- AC-007 [US-001] [FR-001]: Given the imported corpus, Q2 and Q7 gates, build, unit, architecture, SDD, hosted exact-head, path, and independent review controls pass without network or production authority.

## Functional Requirements
- FR-001: The corpus MUST contain exactly the 21 Q0 identities in canonical order and no inferred or generated case. (Stories: US-001; Acceptance: AC-001, AC-002, AC-005)
- FR-002: Every payload MUST be stored unchanged and bound to exact source repository, 40-hex commit, relative path, source ref, media type, positive byte length, lowercase SHA-256, and 40-hex Git blob SHA-1. (Stories: US-001, US-003; Acceptance: AC-001, AC-004, AC-006)
- FR-003: Every record MUST bind historical context and the exact structured expected-behavior object retained by Q0. (Stories: US-002; Acceptance: AC-003)
- FR-004: Every record MUST carry an explicit closed ambiguity state and rationale; absence in Q0 MUST remain none-recorded rather than inferred certainty. (Stories: US-002, US-003; Acceptance: AC-003)
- FR-005: Every record MUST carry a current-v1 result state, outcome when observed, exact evidence locator and head when available, and explicit not-atomically-observed when no case-level result exists. (Stories: US-002; Acceptance: AC-003)
- FR-006: Validation MUST compare raw bytes, recompute SHA-256 and Git blob SHA-1, reject path traversal or symlinks, require canonical metadata, and enforce the closed inventory and aggregate digest. (Stories: US-001, US-004; Acceptance: AC-002, AC-004, AC-005, AC-006)
- FR-007: Negative controls MUST independently invert payload bytes, length, both digests, source identity, expected behavior, ambiguity, v1 result, evidence, order, completeness, uniqueness, schema, and path safety. (Stories: US-004; Acceptance: AC-004, AC-005, AC-006)
- FR-008: Existing accepted receipts and evidence meanings MUST remain byte-identical; the unit MUST stop before GS2-03.3 behavior and all network, publication, deployment, or production mutations. (Stories: US-004; Acceptance: AC-007)

## Ambiguities
- AMB-001 open: Q0 records expected behavior but does not record an atomic runtime result for every multi-case source artifact; the import must preserve that evidentiary absence.
- AMB-002 open: Some exact source payloads exceed the generic 65536-byte JSON evidence envelope; raw corpus artifacts need a byte-preserving storage rule without weakening compact indexed metadata.
- AMB-003 open: Only some Q0 source artifacts have exact-head hosted check evidence; others require an explicit unobserved result rather than a guessed green classification.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 74-gs2-03-2-frozen-corpus`.
