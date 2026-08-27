---
schemaVersion: 1
workId: 12-create-evidence-storage
title: Create Evidence Storage
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Create Evidence Storage Specification

Prose status: specified

## User Value
Maintainers and independent reviewers can verify every qualification claim from compact, canonical, digest-bound evidence retained in git while large generated payloads remain immutable outside git.

## Scope
- SB-001: Version the evidence storage contract, schemas, category directories, compact indexes, artifact manifests, validation gates, and negative fixtures for corpus inputs, external observations, independent oracles, generated cases, test results, reviews, and accepted receipts.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a maintainer or independent reviewer, I can resolve every qualification claim to a versioned category, canonical compact record, immutable payload identity, and verified digest.
- US-002 (P1): As a repository maintainer, I can run one deterministic local gate that rejects malformed, unsafe, stale, oversized, or mutable evidence without network access.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the repository evidence root, when the storage gate runs, then all eight required evidence categories and their versioned schemas are present and exactly described by the storage policy.
- AC-002 [US-001] [FR-002]: Given a compact tracked record, when the gate resolves it, then its relative path, category, media type, byte length, and lowercase SHA-256 agree with the canonical index.
- AC-003 [US-001] [FR-003]: Given a bulky generated payload, when it is indexed, then only an immutable GitHub Actions artifact or GitHub release asset locator plus its digest is retained in git.
- AC-004 [US-002] [FR-004]: Given any indexed record that violates its category schema, or path traversal, an absolute path, a symbolic link, a duplicate identity or path, a stale digest, a missing schema/category, noncanonical JSON/order, a mutable external locator, or an oversized tracked payload, when validation runs, then it fails closed with a stable finding.
- AC-005 [US-002] [FR-005]: Given an accepted receipt, when validation runs, then it must be canonical, compact, digest-bound, and located only in the append-only accepted category; the validator never rewrites it.

## Functional Requirements
- FR-001: The version-one policy MUST enumerate exactly `corpus-inputs`, `external-observations`, `independent-oracles`, `generated-cases`, `test-results`, `artifact-manifests`, `reviews`, and `accepted-receipts`, with one tracked directory and schema for each. (Stories: US-001; Acceptance: AC-001)
- FR-002: The tracked index MUST be canonical UTF-8 JSON with LF termination, entries sorted by identifier, unique identifiers and paths, repository-relative normalized paths, lowercase SHA-256 digests, declared byte lengths, and a 65,536-byte maximum per tracked payload. (Stories: US-001, US-002; Acceptance: AC-002, AC-004)
- FR-003: Generated payloads larger than the tracked limit MUST use an immutable `github-actions-artifact` or `github-release-asset` locator. The git record MUST retain the payload SHA-256, byte length, numeric repository, producer run or release, and artifact or asset IDs, plus the artifact name. Mutable tags and names alone are forbidden. (Stories: US-001; Acceptance: AC-003)
- FR-004: A deterministic offline gate MUST exhaustively enforce the declared shape, types, required values, formats, patterns, and enumerations for every indexed record in all eight category schemas. It MUST also reject unsupported policy or record versions, unknown/missing categories or schemas, unsafe paths or symbolic links, category/path mismatch, duplicate identities or paths, stale digests or lengths, noncanonical JSON or ordering, oversized tracked payloads, and mutable external locators. Canonical timestamps MUST use second-precision UTC `YYYY-MM-DDTHH:MM:SSZ`. (Stories: US-002; Acceptance: AC-004)
- FR-005: Accepted receipts MUST remain compact tracked records under `accepted/`, conform to their existing receipt contract, and be covered by an index digest. Validation is read-only and MUST NOT manufacture, replace, or rewrite a receipt. (Stories: US-001, US-002; Acceptance: AC-005)
- FR-006: The gate MUST require no organization settings, deployments, credentials, network access, GitHub mutation, or successor-unit behavior. (Stories: US-002; Acceptance: AC-004)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds the `fsgg.coordination.evidence-storage-policy/1`, `fsgg.coordination.evidence-index/1`, and `fsgg.coordination.evidence-record/1` repository contracts.
- Adds the closed `evidence-storage-contract` Q7 gate.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 12-create-evidence-storage`.
