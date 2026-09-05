---
schemaVersion: 1
workId: 304-gs2-07-3-audit-repair
title: GS2-07.3 audit repair
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-07.3 audit repair Specification

Prose status: specified

## User Value
Pure deterministic scheduled audit repair reconciles missed GitHub change delivery through the exclusive shared reconciler.

## Scope
- SB-001: Repository-local additive audit-repair contract, tests, validator, retained evidence, and terminal roadmap/catalog acceptance only; no network, production queue, production GitHub mutation, deployment, publication, or successor-unit authority.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As an operator, I can turn a complete scheduled GitHub audit into a deterministic sealed repair plan so missed deliveries converge through the existing shared reconciler without granting the audit a second write path.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given an accepted GS2-07.2 baseline, exact source revision, sorted repository scope, cursor, complete pages, event history, and all mandatory audit classifications, when the operator compiles and replays the audit, then the sealed plan is canonical and byte-identical, missed subjects remain schedulable, overlapping event and audit subjects converge at the newest revision, and every altered or incomplete input fails closed before the writer boundary.

## Functional Requirements
- FR-001: Given exact accepted GS2-07.2 and roadmap authority, compile complete scheduled-audit observations for dropped deliveries, preview gaps, external repositories, and schema drift into deterministic newest-revision repair queue entries that converge byte-identically with event-derived entries; reject every registered completeness, scope, cursor, revision, routing, writer-boundary, seal, replay, network, queue, mutation, Quint-preservation, and successor-authority inversion. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- The change adds a public qualification-contract module and retained deterministic evidence; it does not change an existing runtime or wire API.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 304-gs2-07-3-audit-repair`.
