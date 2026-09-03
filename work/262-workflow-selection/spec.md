---
schemaVersion: 1
workId: 262-workflow-selection
title: Workflow Selection
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Workflow Selection Specification

Prose status: specified

## User Value
Maintainers can inspect one sealed workflow-selection decision that safely minimizes CI work while preserving every required obligation and fleet safety control.

## Scope
- SB-001: Exact GS2-06.7 repository-local Q3/Q7 qualification contracts and retained evidence; no production mutation or successor work.

## Non-Goals
- SB-002: Do not mutate production GitHub settings, workflows, rulesets, checks, merge queues, repositories, releases, packages, feeds, environments, deployments, or fleet state.
- SB-003: Do not author the GS2-06.7 acceptance receipt and do not inspect, prepare, or implement a successor unit.

## User Stories
- US-001 (P1): As a maintainer, I can inspect one deterministic selection report that explains exactly which obligations run, which are not applicable, and why no required work was skipped.
- US-002 (P1): As a fleet operator, I can compare measured baselines to accepted targets and deterministically disable selection after any sentinel-observed missed obligation.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the exact roadmap, accepted prerequisite, and complete typed workflow inventory, when the Q3 compiler runs, then every policy job, composite step, reusable job contract, and required aggregate is bound in one canonical seal.
- AC-002 [US-001] [FR-002]: Given representative file and non-file changes, when selection runs, then it returns the smallest sound transitive build, test, policy, coordination, packaging, and release closure plus every unconditional obligation.
- AC-003 [US-001] [FR-003]: Given an unselected expensive child or merge-group event, when selection runs, then the child reports typed NotApplicable without provisioning and merge-group impact is recomputed from the queued head and current base/settings.
- AC-004 [US-002] [FR-004]: Given per-repository baseline and target metrics, when the Q7 compiler evaluates a selection, then every fan-out, billed-minute, queue-time, p50, and p95 target passes without a missed obligation.
- AC-005 [US-002] [FR-005]: Given a scheduled full-suite sentinel, when an actual failure lies outside the selected closure, then the report records the missed obligation and deterministically disables selection fleet-wide while preserving the complete removal ledger.
- AC-006 [US-001] [FR-006]: Given unknown, ambiguous, stale, incomplete, unsupported, reordered, altered, or unsealed evidence, when either gate runs, then qualification fails closed and exposes no production mutation path.

## Functional Requirements
- FR-001: The Q3 contract MUST bind a complete typed inventory of workflows, policy jobs, composite steps, reusable job contracts, and stable aggregate outputs to the exact repository, source, roadmap, and prerequisite. (Stories: US-001; Acceptance: AC-001)
- FR-002: The selector MUST compile a versioned dependency graph over changed subjects and non-file inputs into the smallest sound transitive closure across build, test, policy, coordination, packaging, and release obligations while retaining unconditional policy and core obligations. (Stories: US-001; Acceptance: AC-002)
- FR-003: Every required aggregate MUST resolve, and every unselected expensive child MUST return a typed NotApplicable reason without provisioning; merge-group selection MUST recompute against the queued head and current base/settings. (Stories: US-001; Acceptance: AC-003)
- FR-004: The Q7 contract MUST compare per-repository workflow/job fan-out, billed minutes, queue time, and p50/p95 completion baselines to accepted targets and reject any target breach or missed obligation. (Stories: US-002; Acceptance: AC-004)
- FR-005: Scheduled full-suite sentinels MUST compare selected closure with actual failures, deterministically disable selection fleet-wide after any missed obligation, and retain every removed workflow and obligation. (Stories: US-002; Acceptance: AC-005)
- FR-006: Both gates MUST fail closed on unknown, ambiguous, stale, incomplete, unsupported, reordered, altered, unsealed, or non-replayable evidence and MUST expose no production mutation, acceptance, or successor authority. (Stories: US-001; Acceptance: AC-006)

## Ambiguities
- AMB-001: Whether NotApplicable children may be represented as absent jobs or require explicit outcome rows.
- AMB-002: Whether the Q7 gate performs live fleet optimization or qualifies retained measurements and deterministic fleet-disable decisions.
- AMB-003: Whether mixed changes may union independent closures without recomputing transitive and unconditional obligations.

## Public Or Tool-Facing Impact
- Adds a public F# qualification signature plus distinct exact Q3 and Q7 roadmap validator commands.
- Adds versioned retained corpus and independent expectation artifacts; all changes are additive.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 262-workflow-selection`.
