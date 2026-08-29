---
schemaVersion: 1
workId: 78-shorten-qualification-critical-path
title: Shorten qualification critical path
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Shorten qualification critical path Specification

Prose status: specified

## User Value
One lean, typed, fail-closed qualification architecture with lower exact-head latency and lower change amplification.

## Scope
- SB-001: Bootstrap qualification semantic plan, workflow projection, stable gate entry points, compiled validation core, evidence join, first-party action pins, measured cache adoption decision, architecture performance tests, and hosted timing evidence.

## Non-Goals
- SB-002: Do not implement #80 cross-run qualification reuse, relax a gate, remove an artifact, or change the canonical Quint model/tool pins.
- SB-003: Do not treat runner queue delay as executable regression or optimize only a single favorable hosted sample.
- SB-004: Do not introduce a generic workflow framework; the plan is closed over this qualification product and its seven known gate identities.

## User Stories
- US-001 (P1): As a maintainer, I can change one gate through one semantic declaration and one stable implementation entry point without synchronizing command strings across YAML, JSON, and tests.
- US-002 (P1): As a reviewer, I can prove every gate, dependency edge, artifact, permission, pin, and exact-head relationship from typed structure and independent inversions rather than presentation bytes.
- US-003 (P1): As a contributor, I receive materially faster owned qualification routes and lower runner usage without weaker coverage; unchanged formal-solver variance is reported separately.
- US-004 (P2): As an operator, I can distinguish queue delay, setup, execution, fan-in, and total workflow time from retained machine evidence.

## Acceptance Scenarios
- AC-001 [US-001] [US-002] [FR-001, FR-002, FR-007]: Given the reviewed plan and projected workflow, when a gate is added or changed, then one semantic declaration and one entry point change; workflow whitespace and step labels do not change authority, and a stale projection is red.
- AC-002 [US-002] [FR-001, FR-008]: Given each gate or dependency is removed, bypassed, substituted, reordered, or made conditional, when validation runs, then the named structural or evidence inversion is red and the terminal qualification cannot pass.
- AC-003 [US-001] [US-003] [FR-003]: Given the bootstrap validation mutation corpus, when architecture tests run, then pure cases execute in-process against the same compiled core used by the thin production adapter, while an adapter smoke proves command parity.
- AC-004 [US-002] [FR-004]: Given the projected workflow, when action manifests are resolved, then all first-party actions are exact immutable Node 24 pins and a hosted run produces no Node 20 deprecation annotation.
- AC-005 [US-003] [FR-005]: Given comparable successful hosted samples, when baseline and candidate are compared, then compiler/tests and recovery improve by at least 30%, aggregate runner minutes improve by at least 10%, and unchanged canonical Quint plus queue variance are reported separately from owned execution.
- AC-006 [US-003] [FR-006]: Given an exact-key NuGet cache miss and hit on the same head, when locked restore and all gates execute, then adoption occurs only if measured runner time materially improves; otherwise the cache action and its configuration are absent from the final plan and projection.
- AC-007 [US-004] [FR-005]: Given a hosted run, when timing evidence is retained, then queue, setup, subject execution, artifact fan-in, and settled total are separate, source-linked measurements.
- AC-008 [US-002] [FR-009]: Given the final full-execution evidence, when #80 later evaluates reuse, then both paths target the same versioned terminal evidence contract without #78 authorizing reuse.

## Functional Requirements
- FR-001: Preserve the six independently failable execution gates and one terminal exact-head evidence join with exact candidate, `contents: read`, immutable pins, compact artifacts, cancellation, and named negative controls. (Stories: US-001, US-002; Acceptance: AC-001, AC-002)
- FR-002: Replace mirrored YAML/JSON command inventories and workflow-byte authority with one versioned typed plan plus deterministic structural projection; workflow whitespace and step labels MUST NOT affect authority. (Stories: US-001, US-002; Acceptance: AC-001)
- FR-003: Compile pure bootstrap validation into `FS.GG.Coordination.Qualification.Contracts`, execute its mutation matrix in-process, and retain thin production-adapter parity tests. (Stories: US-001, US-003; Acceptance: AC-003)
- FR-004: Pin upload-artifact v7.0.1 (`043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`) and download-artifact v8.0.1 (`3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c`), verify their official manifests use Node 24, and produce no hosted Node 20 annotation. (Stories: US-002; Acceptance: AC-004)
- FR-005: Record comparable successful hosted samples, separate queue/setup/execution/fan-in timing, and meet the AC-005 owned-route/runner-minute thresholds without attributing unchanged solver variance to this item. (Stories: US-003, US-004; Acceptance: AC-005, AC-007)
- FR-006: Evaluate an exact OS/SDK/all-lockfile NuGet cache on miss and hit routes, preserve locked restore and identical evidence semantics, and remove the cache if it does not materially improve measured runner time. (Stories: US-002, US-003; Acceptance: AC-006)
- FR-007: Enforce a complexity budget: one semantic gate declaration, one implementation entry point, no mirrored exact command inventory, and no more than two required edit locations for a representative gate change excluding focused tests. (Stories: US-001; Acceptance: AC-001)
- FR-008: Preserve bootstrap recovery, canonical Quint, deterministic build, dependency/security, package smoke, compiler/unit/architecture coverage, and artifact-digest validation unchanged in meaning and independently red. (Stories: US-002, US-003; Acceptance: AC-002)
- FR-009: Keep #80 reuse outside this item while exposing the same versioned terminal evidence contract to full execution and later digest-bound reuse. (Stories: US-002; Acceptance: AC-008)

## Ambiguities
- AMB-001: Is the plan JSON, compiled F# data, or another format, and what bytes carry semantic authority?
- AMB-002: Is the workflow parsed structurally or generated, and how can presentation remain non-authoritative while projection drift still fails?
- AMB-003: Which validation logic moves into the compiled core, and how is the production FSI adapter kept equivalent?
- AMB-004: Which cache boundary is safe for deterministic, recovery, security, and ordinary test gates?
- AMB-005: Which optimization is expected to move the critical path now that restore/build consumes only 29 seconds but architecture tests consume 275 seconds?
- AMB-006: What hosted cohort and quantitative thresholds decide adoption without conflating runner queue time?

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 78-shorten-qualification-critical-path`.
