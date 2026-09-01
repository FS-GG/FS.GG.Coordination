---
schemaVersion: 1
workId: 200-staged-intake-admission
title: GS2-05.9 staged intake admission
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-05.9 staged intake admission Specification

Prose status: specified

## User Value
Operators can capture incomplete discoveries cheaply and promote them only when scheduling facts are complete.

## Scope
- SB-001: Extend the receiver-owned intake adapter with a staged capture contract, an item-local operation/cost model, and a separate Ready-promotion decision while retaining the existing sealed transaction executor.
- SB-002: Register the exact GS2-05.9 unit and prerequisite chain, update GS2-05.4 sequencing, and qualify the contract through focused unit and roadmap-architecture tests.

## Non-Goals
- SB-003: Do not perform production GitHub writes, organization-wide reconciliation, full-Backlog traversal or retriage, live Project administration, deployment, publication, or stable release.
- SB-004: Do not implement roadmap compilation or any GS2-05.4 behavior beyond its prerequisite edge.
- SB-005: Do not require claim or pull-request evidence for Ready promotion; those are causally later lifecycle outputs.

## User Stories
- US-001 (P1): Capture a new item with unknown root cause, deferred verification, and no touch set without losing transaction safety.
- US-002 (P1): Promote a captured item to Ready only when all schedulability inputs are complete, with diagnostics for every omission.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002] [FR-003]: Given an item-local observation and a discovery with explicitly unknown root cause, deferred verification, and no touch set, when staged capture is planned repeatedly, then it emits the same sealed `fsgg.coord.intake/v1`-compatible decision, uses no more than six authority reads and six mutations, and performs no operation over unrelated work.
- AC-002 [US-001] [FR-001] [FR-005]: Given a staged capture plan, when controlled application, interruption, replay, resume, roll-forward, compensation, and authoritative readback run, then the unchanged GS2-05.3 transaction fences and outcomes govern every effect.
- AC-003 [US-002] [FR-004]: Given one missing Ready-promotion fact at a time, when promotion is evaluated, then it refuses and names every missing fact; given all phase-local facts, it produces canonical Ready intent without claim or pull-request evidence.
- AC-004 [US-001] [US-002] [FR-002] [FR-003] [FR-006]: Given unrelated Project and Backlog cardinality growth and forbidden global-operation inversions, when qualification runs, then capture cost and output remain identical, every inversion is red, GS2-05.9 binds accepted GS2-05.3, and GS2-05.4 binds the accepted GS2-05.9 receipt.

## Functional Requirements
- FR-001: Staged capture shall accept canonical known, explicitly unknown, deferred, and unspecified discovery details, including unknown root cause, deferred verification, and no touch set, while preserving explicit create-or-reuse identity and canonical deterministic planning. (Stories: US-001; Acceptance: AC-001, AC-002)
- FR-002: A capture plan shall declare at most six distinct item-local authority-read operations and contain at most six mutations; both bounds and the resulting plan shall be independent of unrelated Project and Backlog cardinality. (Stories: US-001; Acceptance: AC-001, AC-004)
- FR-003: The capture operation vocabulary shall contain no organization-wide reconcile, full-board traversal, Backlog traversal, or unrelated retriage operation; event-targeted, scheduled, or explicitly requested maintenance owns those behaviors. (Stories: US-001; Acceptance: AC-001, AC-004)
- FR-004: Ready promotion shall require a complete non-duplicated root cause, touch set, verification contract, dependency declaration, route decision, native issue type, organization fields, repository scope, and work classification, and shall return stable diagnostics for every missing, duplicate, or invalid fact. Claim and pull-request evidence shall not be inputs to this decision. (Stories: US-002; Acceptance: AC-003)
- FR-005: Capture and promotion shall produce canonical intents consumable by the existing `fsgg.coord.intake/v1` validate/plan/apply path, preserving altered-plan, reorder, stale revision, precondition drift, partial apply, replay, resume, roll-forward, reverse compensation, unauthorized, unsupported, indeterminate, pagination-completeness, and authoritative-readback guarantees. (Stories: US-001; Acceptance: AC-002)
- FR-006: The unit index shall register GS2-05.9 with accepted GS2-05.3 as its prerequisite, update GS2-05.4 to depend on accepted GS2-05.9, bind the exact canonical roadmap bytes, and require qualification evidence for fixed cost, staged causality, missing-fact diagnostics, and compatibility. (Stories: US-001, US-002; Acceptance: AC-004)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds public F# staged-capture, bounded-cost, and Ready-promotion types/functions beside the existing intake API without removing or weakening existing members.
- Extends the registered GitHub Substrate v2 unit index with GS2-05.9 and a new prerequisite edge for GS2-05.4.
- Existing `fsgg.coord.intake/v1` callers remain source- and behavior-compatible; the new staged surface produces inputs for that contract rather than replacing it.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 200-staged-intake-admission`.
