---
schemaVersion: 1
workId: 66-gs2-02-11-deterministic-identity
title: Gs2 02 11 Deterministic Identity
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Gs2 02 11 Deterministic Identity Specification

Prose status: specified

## User Value
Reviewers and automation can distinguish authoring-only edits from behavioral changes without trusting prose, raw file bytes, or execution-time discovery of unsupported versions.

## Scope
- SB-001: Repository-local canonical extraction, normalization, version compatibility, behavioral identity, semantic diff, retained generated facts, tests, and Q1/Q2 evidence.
- SB-002: The canonical literate Quint source and accepted profile remain the sole behavioral authority; all identity artifacts are derived projections.

## Non-Goals
- SB-003: No network, GitHub mutation, independent qualification-system semantics, hosted runtime, deployment, publication, or production write authority.
- SB-004: Do not inspect, accept, or implement GS2-03 or any later roadmap unit.

## User Stories
- US-001 (P1): As a reviewer, I can see that equivalent literate authoring forms and prose-only edits preserve one behavioral identity.
- US-002 (P1): As a reviewer, I can see a stable ordered semantic diff when a behavioral contract changes.
- US-003 (P1): As an operator, I receive an explicit refusal before Quint execution when any version dimension is unsupported or substituted.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given two supported literate sources that differ only in named-block partitioning, fence indentation, line endings, or semantically inert Quint trivia, when they are extracted and compiled, then their observed typed-effect SHA-256 behavioral identities are identical.
- AC-002 [US-001] [FR-002]: Given a prose-only edit outside named Quint blocks, when identity is recomputed, then raw source identity changes while behavioral identity and semantic diff remain unchanged.
- AC-003 [US-002] [FR-003]: Given a change to a typed behavioral fact inside the canonical Quint authority, when both contracts are compared, then behavioral identity changes and the semantic diff reports the stable ordered changed paths and before/after digests.
- AC-004 [US-003] [FR-004]: Given any substituted source, extractor, Quint, profile, or compiled-contract schema version, when the compiler gate starts, then it refuses that dimension before invoking Quint or producing an executable candidate.
- AC-005 [US-001] [US-002] [FR-005]: Given an accepted compiler run, when retained artifacts and compiled outputs are inspected, then one typed identity record binds normalized behavior, the five supported version dimensions, and the canonical contract digest.
- AC-006 [US-003] [FR-006]: Given each new identity or compatibility guard is inverted in a bounded fixture, when focused tests run, then the fixture is red and the unmodified candidate remains green under Q1 and the pure portion of Q2.

## Functional Requirements
- FR-001: The compiler MUST normalize supported equivalent literate Quint authoring forms through the observed typed-effect SHA-256 to one behavioral identity without retaining raw typed IR. (Stories: US-001; Acceptance: AC-001)
- FR-002: The compiler MUST exclude prose outside named Quint blocks from behavioral identity while retaining raw source identity for provenance. (Stories: US-001; Acceptance: AC-002)
- FR-003: The compiler MUST derive a deterministic ordered semantic diff whose behavioral-identity row and public-contract path digests expose behavioral changes without reporting prose-only changes or publishing raw typed IR. (Stories: US-002; Acceptance: AC-003)
- FR-004: The compiler MUST validate exact supported source, extractor, Quint, profile, and compiled-contract schema versions and refuse each unsupported or substituted dimension before Quint execution. (Stories: US-003; Acceptance: AC-004)
- FR-005: Retained compiler and compiled-output projections MUST carry a typed identity record binding normalized behavior, the supported version tuple, the canonical contract digest, completeness, and freshness without creating a parallel behavioral model. (Stories: US-001, US-002; Acceptance: AC-005)
- FR-006: Qualification MUST include observed positive equivalence/prose/semantic cases and independent fail-before mutations for normalization, semantic diff, and all five version dimensions under Q1 and the pure portion of Q2. (Stories: US-003; Acceptance: AC-006)

## Ambiguities
- AMB-001: Which authoring differences are admitted as supported equivalence rather than merely tolerated input?
- AMB-002: Which representation is the behavioral identity authority when raw source bytes, generated module bytes, typed effect, and compiled contract digests differ?
- AMB-003: What stable diff shape is reviewable without making the diff projection a second behavioral model?

## Public Or Tool-Facing Impact
- Adds typed deterministic-identity and compatibility facts to the canonical Quint contract and retained generated projections.
- Extends compiled-output manifest and semantic-diff JSON contracts; no new network or production command is added.

## Lifecycle Notes
- Authority order: pinned roadmap and ADR-0077, canonical `Protocol.md` Quint blocks, generated contract, retained identity projections, then tests and evidence.
- The spec change is additive and must preserve every GS2-02.1–GS2-02.10 invariant and witness.
