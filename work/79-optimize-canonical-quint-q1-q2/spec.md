---
schemaVersion: 1
workId: 79-optimize-canonical-quint-q1-q2
title: Optimize canonical Quint Q1/Q2 execution
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Optimize canonical Quint Q1/Q2 execution Specification

Prose status: specified

## User Value
Protected Q1/Q2 qualification completes faster and reports failures more precisely, while preserving every positive result and negative-control inversion.

## Scope
- SB-001: One deterministic compiler/generator preparation shared by Q1 and Q2 within a qualification attempt.
- SB-002: Separately attributable Q1 compiler/generation and Q2 model-checking outcomes.
- SB-003: A dedicated hosted Quint execution path that can begin independently of the build and architecture-test sequence.
- SB-004: Semantics-preserving consolidation of positive verification and bounded evaluation of process reuse or parallel mutation execution.
- SB-005: Versioned retained telemetry covering phase durations, process counts, tool/input/result digests, and outcomes.
- SB-006: Documentation, contract validation, architecture tests, and hosted before/after evidence for the optimized path.

## Non-Goals
- NG-001: Do not enable Quint inductive-invariant mode.
- NG-002: Do not remove, weaken, sample, or silently skip any positive property or any of the execution-derived 56 negative-control rejections.
- NG-003: Do not redesign the complete bootstrap orchestration or stable terminal check; that is owned by issue #78.
- NG-004: Do not reuse qualification across workflow runs or commits; that is owned by issue #80.
- NG-005: Do not make the reusable Quint runner execute a hard-coded SDD lifecycle for an unrelated roadmap item.

## User Stories
- US-001 (P1): As a maintainer, I want Q1 and Q2 to share deterministic preparation so qualification avoids duplicate compiler and generation work.
- US-002 (P1): As a reviewer, I want every former positive and negative result preserved and separately attributable so a speedup cannot hide semantic regression.
- US-003 (P1): As a CI operator, I want formal qualification to begin independently and publish phase telemetry so the critical path and failures are observable.
- US-004 (P2): As a maintainer, I want measured process consolidation or bounded parallelism adopted only when it improves hosted execution without instability.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001, FR-002]: Given an unchanged canonical input set, when Q1 and Q2 execute in one qualification attempt, then compiler/generator preparation occurs once and both outcomes refer to the same preparation digest.
- AC-002 [US-002] [FR-003, FR-004]: Given the optimized runner, when the canonical positive suite and mutation suite execute, then all eight positive invariants pass, all 56 Quint mutation processes are rejected, and the completed retained route inventory is exactly 85 external processes / 61 Quint CLI processes / 14 Apalache verify invocations; all cardinalities are derived from completed execution and a near-miss fails closed.
- AC-003 [US-002] [FR-002, FR-005]: Given either Q1 or Q2 fails, when the runner emits its receipt and exits, then the failed phase is explicit and the overall result is fail-closed.
- AC-004 [US-003] [FR-006]: Given a hosted bootstrap run, when the workflow starts, then canonical Quint does not wait for the architecture-test sequence and retained evidence depends on both paths.
- AC-005 [US-003] [FR-007]: Given a completed qualification, when evidence is retained, then its versioned JSON records phase durations, relevant process counts, pinned tool identities, input/result digests, and separate Q1/Q2 outcomes.
- AC-006 [US-004] [FR-008]: Given candidate concurrency levels 1, 2, and 4 or a server-reuse candidate, when five hosted samples are compared, then adoption requires equivalent results, stable resource use, and at least a 10% median improvement for server reuse.
- AC-007 [US-003] [FR-009]: Given the reusable formal runner is invoked, when it completes, then it has not invoked a hard-coded `fsgg-sdd` lifecycle for another work item.

## Functional Requirements
- FR-001: The runner MUST perform compiler/generator preparation exactly once per qualification attempt and reuse the prepared outputs for Q1 and Q2. (Stories: US-001; Acceptance: AC-001)
- FR-002: The runner MUST emit separate fail-closed Q1 and Q2 outcomes tied to one deterministic preparation digest. (Stories: US-001, US-002; Acceptance: AC-001, AC-003)
- FR-003: The optimized suite MUST preserve all eight positive invariant checks with equivalent pinned Quint/Apalache semantics. (Stories: US-002; Acceptance: AC-002)
- FR-004: The optimized suite MUST derive and enforce all 56 negative-control rejections from completed Quint executions; a removed or unexpectedly green control MUST prevent Q2 success. (Stories: US-002; Acceptance: AC-002)
- FR-005: Any preparation, Q1, Q2, telemetry, or evidence failure MUST fail the qualification attempt without converting an unknown or missing result into success. (Stories: US-002; Acceptance: AC-003)
- FR-006: Hosted canonical Quint MUST run in a job that has no dependency on the compiler-and-architecture-test job, while retained qualification evidence MUST depend on both. (Stories: US-003; Acceptance: AC-004)
- FR-007: The runner MUST emit a schema-versioned machine-readable receipt on success and failure containing phase-aware Q1/Q2 outcomes, derived completed inventories, phase durations, direct external/Quint and Apalache-verify invocation counts, pinned tool identities, canonical input digests, optional preparation digest, hashed failure detail, and a result digest that binds the process counts as well as the outcome inventories. (Stories: US-002, US-003; Acceptance: AC-003, AC-005)
- FR-008: Positive multi-invariant verification, bounded mutation parallelism, or Quint server reuse MAY be adopted only after result-equivalence checks and hosted measurements; concurrency MUST be explicitly capped. (Stories: US-004; Acceptance: AC-006)
- FR-009: The reusable formal runner MUST NOT invoke SDD lifecycle commands for a hard-coded work item; lifecycle evidence remains the responsibility of the active roadmap item. (Stories: US-003; Acceptance: AC-007)
- FR-010: Documentation and executable contract tests MUST describe and enforce the new job topology, receipt schema, exact pins, timeouts, result cardinalities, and retained evidence relationships. (Stories: US-002, US-003; Acceptance: AC-002, AC-004, AC-005)
- FR-011: Before/after hosted evidence MUST report at least five samples when feasible, including median and p95 wall time, phase durations, and process counts, with any smaller sample explicitly identified as provisional. (Stories: US-003, US-004; Acceptance: AC-006)

## Ambiguities
- AMB-001: Which positive invariants are semantically safe to verify in one multi-invariant invocation rather than eight separate invocations?
- AMB-002: What bounded mutation concurrency provides the best hosted tradeoff among wall time, memory, and reliability?
- AMB-003: Should Quint server mode be adopted, and what evidence threshold justifies the added lifecycle complexity?
- AMB-004: At what point should Q1 be emitted when preparation is shared with the full Q2 suite?
- AMB-005: What receipt schema and artifact boundary provide durable evidence without coupling this item to the broader orchestration redesign?
- AMB-006: How should SDD lifecycle compliance be preserved after removing the unrelated hard-coded SDD commands from the formal runner?

## Public Or Tool-Facing Impact
- The bootstrap workflow gains an independently scheduled canonical-Quint job.
- The formal runner emits a versioned JSON qualification receipt in addition to stable human-readable markers.
- The bootstrap CI contract, retained evidence manifest, architecture tests, and qualification documentation change coherently.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 79-optimize-canonical-quint-q1-q2`.
