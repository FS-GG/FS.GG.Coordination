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
Maintainers can invoke one sealed workflow selector on arbitrary changed paths and non-file inputs against the exact current protected checkout and separately reviewed settings authority, consume stable aggregate outcomes, and reproduce real fleet baselines without enabling selection across the fleet.

## Scope
- SB-001: Repaired GS2-06.7 Core/CLI selection, callable repository-owned workflow contracts, exact Q3/Q7 qualification, read-only observations, sentinel/deletion evidence, and a post-merge superseding repair receipt; no fleet mutation or successor work.

## Non-Goals
- SB-002: Do not mutate production GitHub settings, workflows, rulesets, checks, merge queues, repositories, releases, packages, feeds, environments, deployments, or fleet state.
- SB-003: Do not rewrite the immutable original GS2-06.7 receipt, pre-author its superseding repair receipt, or inspect, prepare, or implement GS2-06.8.

## User Stories
- US-001 (P1): As a maintainer, I can inspect one deterministic selection report that explains exactly which obligations run, which are not applicable, and why no required work was skipped.
- US-002 (P1): As a fleet operator, I can compare measured baselines to accepted targets and deterministically disable selection after any sentinel-observed missed obligation.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the exact roadmap, accepted prerequisite, complete typed inventory, and repository-owned workflow files, when the Q3 compiler runs, then every actual policy job, composite step, reusable job contract, and required aggregate is bound in one canonical seal.
- AC-002 [US-001] [FR-002]: Given arbitrary supported file and non-file inputs outside the retained case corpus plus separately reviewed content authority and runtime authority derived from the exact checkout and live event, when the public Core API or CLI runs at any descendant protected revision, then it derives roots and returns the smallest sound transitive build, test, policy, coordination, packaging, and release closure plus every unconditional obligation.
- AC-003 [US-001] [FR-003]: Given an unselected expensive child or merge-group event, when selection runs, then the child reports typed NotApplicable without provisioning and merge-group impact is recomputed from the queued head and current base/settings.
- AC-004 [US-002] [FR-004]: Given complete per-repository GitHub Actions run/job observations and reviewed targets, when Q7 independently recomputes them, then exact fan-out, billed-minute, queue-time, p50, and p95 baselines match and missing, stale, duplicate, substituted, uniform, or forged provenance is refused.
- AC-005 [US-002] [FR-005]: Given a scheduled full-suite sentinel, when an actual failure lies outside the selected closure, then the report records the missed obligation and deterministically disables selection fleet-wide while preserving the complete removal ledger.
- AC-006 [US-001] [FR-006]: Given unknown, ambiguous, stale, incomplete, unsupported, reordered, altered, unsealed, stale-paired, content-authority drift, forged checkout identity, unavailable authority, stale live settings, or a mismatched queued head/base, when either gate or the scheduled sentinel runs, then qualification fails closed, retains a typed disabled decision, and exposes no production mutation path; unrelated protected advances remain runnable.

## Functional Requirements
- FR-001: The Q3 contract MUST bind a complete typed inventory to actual repository-owned callable workflows, policy jobs, composite steps, reusable job contracts, stable aggregate outputs, exact source, roadmap, and prerequisite. (Stories: US-001; Acceptance: AC-001)
- FR-002: A public pure Core API and CLI MUST validate sealed inventory/version/base/settings against reviewed immutable content digests, while runtime authority records the exact checked-out Actions revision and live event independently of commit distance; it then derives roots from arbitrary changed paths and non-file inputs and computes the smallest sound transitive closure across build, test, policy, coordination, packaging, and release while retaining unconditional policy and core. For merge-group execution it MUST derive changed paths from the live event base and queued head and bind both plus current reviewed settings. (Stories: US-001; Acceptance: AC-002)
- FR-003: Every required aggregate MUST resolve, and every unselected expensive child MUST return a typed NotApplicable reason without provisioning; merge-group selection MUST recompute against the queued head and current base/settings. (Stories: US-001; Acceptance: AC-003)
- FR-004: The Q7 contract MUST independently reproduce each registered repository's workflow/job fan-out, billed minutes, queue time, and p50/p95 completion baselines from read-only source run/job IDs, observation window, revision, query, sample, aggregation, completeness, and review provenance; it MUST reject stale, missing, duplicate, substituted, uniform, or forged evidence and any target breach. (Stories: US-002; Acceptance: AC-004)
- FR-005: Scheduled full-suite sentinels MUST compare selected closure with actual failures, deterministically disable selection fleet-wide after any missed obligation, and retain every removed workflow and obligation. (Stories: US-002; Acceptance: AC-005)
- FR-006: Both gates and the scheduled sentinel MUST fail closed on unknown, ambiguous, stale, incomplete, unsupported, reordered, altered, unsealed, non-replayable, unavailable-authority, content-authority drift, forged runtime identity, stale-paired inputs, stale settings, or mismatched merge-group evidence and MUST expose no production mutation, acceptance, or successor authority. The scheduled sentinel MUST retain a typed non-mutating disabled decision on refusal and MUST reach the full-suite oracle across arbitrary unrelated protected-main advances. (Stories: US-001; Acceptance: AC-006)

## Ambiguities
- AMB-001: Whether NotApplicable children may be represented as absent jobs or require explicit outcome rows.
- AMB-002: Whether the Q7 gate performs live fleet optimization or qualifies retained measurements and deterministic fleet-disable decisions.
- AMB-003: Whether mixed changes may union independent closures without recomputing transitive and unconditional obligations.

## Public Or Tool-Facing Impact
- Adds a public F# Core signature and `workflow-select` CLI plus distinct exact Q3 and Q7 roadmap validator commands.
- Adds callable reusable/composite/sentinel workflow contracts and versioned retained corpus, observation, deletion-ledger, runtime-fixture, and independent expectation artifacts.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 262-workflow-selection`.
