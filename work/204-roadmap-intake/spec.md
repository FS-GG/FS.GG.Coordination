---
schemaVersion: 1
workId: 204-roadmap-intake
title: GS2-05.4 roadmap intake
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-05.4 roadmap intake Specification

Prose status: specified

## User Value
Maintainers can deterministically project one approved typed roadmap into native GitHub work without hand-building its hierarchy and dependency graph or turning Project fields into a second execution ledger.

## Scope
- SB-001: Register and implement the offline GS2-05.4 contract for typed roadmap validation, bounded create-or-reuse planning, sealed controlled-fixture application, and complete owned-projection drift inspection.
- SB-002: Add a public qualification contract and GitHub adapter for one Epic, bounded native work issues, hierarchy, dependencies, dates, organization fields, and derived Project membership.
- SB-003: Register one Q3 gate bound to generated cases, independently authored expectations, accepted GS2-05.9, and roadmap prerequisite sequencing.

## Non-Goals
- SB-004: Do not perform production GitHub writes, full-Project scans, unrelated Backlog traversal/retriage, live Project administration, deployment, publication, or stable release.
- SB-005: Do not implement claims/touch sets, review/delivery, fleet reconciliation, or any successor unit.
- SB-006: Do not treat Markdown, Project status or fields, body metadata, copied blocker lines, or issue comments as semantic roadmap or execution authority.

## User Stories
- US-001 (P1): As a maintainer, I can validate and compile an approved typed roadmap into a deterministic sealed native-work plan.
- US-002 (P1): As an operator, I can apply or recover that plan over a controlled fixture and inspect all owned drift without scanning unrelated work.
- US-003 (P1): As a reviewer, I can prove that native hierarchy and dependencies are authoritative while Project data remains a non-authorizing projection.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given a valid typed roadmap with one root and bounded nodes and edges, repeated validation and planning emit byte-identical canonical plans for exactly one Epic, create-or-reuse issues, native parent/dependency edges, dates, fields, and derived Project membership.
- AC-002 [US-001] [FR-003]: Given duplicate/ambiguous keys, identity collisions, dangling parents or dependencies, hierarchy or dependency cycles, unsupported types/fields, invalid dates, stale/partial observations, or an altered sealed plan, the operation refuses with stable diagnostics before mutation.
- AC-003 [US-002] [FR-004] [FR-005]: Given controlled interruption, replay, resume, roll-forward, reverse compensation, and authoritative readback, each effect is idempotent and every partial/indeterminate outcome retains enough evidence for a deterministic next action.
- AC-004 [US-002] [US-003] [FR-006] [FR-007]: Given missing, extra, and mismatched owned projections plus unrelated Project and Backlog growth, inspection reports all owned drift, ignores unrelated work, and produces identical cost and plan bytes at each unrelated cardinality.
- AC-005 [US-003] [FR-008] [FR-009]: Given inverted Project status/body blocker facts and unchanged native identity/relationship facts, execution readiness and graph meaning remain unchanged; generated and independent Q3 fixtures, source-vocabulary checks, and accepted-receipt prerequisite inversions fail closed.

## Functional Requirements
- FR-001: The input shall be a versioned typed roadmap with one root Epic and bounded work nodes carrying stable source keys, repository, native issue type, title/body intent, optional parent, dependencies, dates, and accepted organization field values. Markdown and Project rows shall not be semantic input. (Stories: US-001; Acceptance: AC-001)
- FR-002: Planning shall canonicalize node and edge order, resolve each stable key to exactly one create-or-reuse identity, and seal exactly one Epic plus bounded issue, hierarchy, dependency, date, field, and Project-membership projection effects into byte-identical plan bytes. (Stories: US-001; Acceptance: AC-001)
- FR-003: Validation shall report all duplicate or ambiguous keys, identity collisions, missing roots, dangling parents/dependencies, self-edges, hierarchy/dependency cycles, unsupported issue types/fields, invalid values/date order, incomplete pagination, stale observations, and altered plan bytes before any effect. (Stories: US-001; Acceptance: AC-002)
- FR-004: Controlled-fixture application shall use exact plan identity and preconditions; create/reuse/update/link/unlink operations shall be idempotent, and replay, resume, roll-forward, reverse compensation, unauthorized, unsupported, partial, and indeterminate outcomes shall preserve authoritative readback and recovery evidence. (Stories: US-002; Acceptance: AC-003)
- FR-005: Authority-read and mutation ceilings shall be closed formulas over the roadmap's node, hierarchy-edge, dependency-edge, and projection counts. Unrelated Project or Backlog cardinality shall be observation metadata only and shall not alter cost, diagnostics, or sealed plan bytes. (Stories: US-002; Acceptance: AC-003, AC-004)
- FR-006: Inspection shall compare only projections carrying the roadmap ownership identity, report every missing, extra, or mismatched owned issue/edge/date/field/membership fact in stable order, and exclude unrelated work from drift. (Stories: US-002; Acceptance: AC-004)
- FR-007: Native issue identity, native parent/sub-issue edges, and native dependency edges shall be authoritative. Project membership, status and fields are derived projections and shall not decide execution, graph meaning, claims, reviews, or delivery; copied body/comment metadata shall be non-authorizing. (Stories: US-003; Acceptance: AC-004, AC-005)
- FR-008: The public surface shall be additive in the qualification-contract and GitHub-adapter assemblies, remain offline/pure or controlled-fixture only, and contain no production transport, credential, organization-wide reconcile, full-Project scan, or unrelated Backlog traversal operation. (Stories: US-003; Acceptance: AC-005)
- FR-009: The unit index shall replace the GS2-05.4 future stub with accepted GS2-05.9 as its sole prerequisite, one Q3 gate command and exact contract digests, without granting successor-unit authority. Generated and independently authored fixtures plus inversion controls shall bind every acceptance boundary. (Stories: US-001, US-002, US-003; Acceptance: AC-005)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds public F# roadmap definition, observation, plan, effect, result, drift, and diagnostic types/functions in the qualification-contract and GitHub-adapter assemblies without removing existing members.
- Replaces the registered GS2-05.4 future stub with one exact Q3 contract while preserving its accepted GS2-05.9 prerequisite.
- The new surface is offline and additive; it defines controlled-fixture semantics and qualification evidence, not a production GitHub writer or CLI cutover.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 204-roadmap-intake`.
