---
schemaVersion: 1
workId: 90-gs2-03-4-decompose-quint-model-independent-oracles
title: GS2-03.4 Quint decomposition and independent oracles
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.4 Quint decomposition and independent oracles Specification

Prose status: specified

## User Value
Maintainers can qualify bounded Quint roots efficiently while independent observers detect abstraction, concurrency, omission, stale-reuse, and anti-vacuity failures.

## Scope
- SB-001: Execute ordered packages 03.4a canonical-model decomposition and behavior preservation; 03.4b independent black-box observable-behavior oracles; 03.4c impact selection, budgets, bounded artifacts, backend policy, and future-module admission.
- SB-002: Keep the literate Quint protocol as the single behavioral authority while deriving content-addressed module, root, closure, budget, admission, and evidence projections.
- SB-003: Rebind the executable roadmap index to the latest canonical roadmap bytes without changing any accepted predecessor receipt or the GS2-03.4 unit contract.

## Non-Goals
- SB-004: Do not implement GS2-03.5, external fault injection, network or GitHub mutation, deployment, publication, production writes, a second behavioral model, blanket inductive invariants, or a persistent Apalache server.
- SB-005: Do not claim that file splitting alone improves qualification; only independently executable roots with measured state, time, memory, and artifact reductions receive that credit.
- SB-006: Do not replace formal checks with simulation or duplicate every invariant across every backend; backend diversity remains bounded evidence selected per registered root.

## User Stories
- US-001 (P1): As a protocol maintainer, I can change one bounded Quint domain and qualify every affected root without rerunning unrelated roots on ordinary pull requests.
- US-002 (P1): As an independent reviewer, I can expose a behavior, abstraction, atomicity, selection, or anti-vacuity defect without consuming the model's generated expectations as my oracle.
- US-003 (P1): As a qualification operator, I can run the complete registered root inventory at protected checkpoints and compare source-bound cost and artifact measurements with explicit budgets.
- US-004 (P2): As a future Quint author, I receive a fail-closed admission verdict before new behavior can enter without ownership, bounds, witnesses, or independent coverage.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given the current canonical protocol, when decomposition is generated and validated, then every state variable and action has one classification, every module import is acyclic, every root has the exact intended transitive closure, and behavior-preservation evidence binds the before/after identities.
- AC-002 [US-002] [FR-003]: Given each registered root, when its witness suite runs, then positive and adversarial behaviors are reachable and one deliberately invalid parameterization exposes the named invariant violation; deleting or weakening any witness is red.
- AC-003 [US-002] [FR-004]: Given the ten required observable behaviors and an abstraction-sensitive race, when independently authored black-box cases execute, then correct outcomes pass and subject mutations for each oracle fail without importing generated model expectations.
- AC-004 [US-001] [US-003] [FR-005]: Given a change to any module, bound, toolchain, or oracle registration, when qualification selection runs, then it selects the changed root and every reverse dependant, rejects unknown or incomplete closure, and refuses evidence from a different closure identity.
- AC-005 [US-003] [FR-005] [FR-006]: Given pull-request and protected-checkpoint modes, when qualification runs, then pull requests execute the smallest sound affected closure while protected main, acceptance, freeze, and release modes execute the complete inventory and record dependency depth, states, samples, elapsed time, peak memory, and artifact volume.
- AC-006 [US-003] [FR-006]: Given runner-calibrated per-root budgets and before/after samples, when a metric exceeds its accepted ceiling or disappears, then qualification refuses with a reviewable disposition rather than hiding the regression behind exact-tree reuse.
- AC-007 [US-004] [FR-007]: Given a proposed future Quint behavior, when any owning module, permitted import, invariant, oracle, smallest root, bound, anti-vacuity witness, projection, CI impact, or budget effect is missing or stale, then admission is red before canonical source generation.

## Functional Requirements
- FR-001: Preserve one behavioral authority and classify every state variable and action as essential, derived, or bookkeeping. (Stories: US-001; Acceptance: AC-001)
- FR-002: Validate an acyclic module graph and the smallest useful independently executable roots with semantic-equivalence or intentional-delta receipts. (Stories: US-001; Acceptance: AC-001)
- FR-003: Require positive reachability, adversarial reachability, and deliberately-invalid anti-vacuity witnesses for every root. (Stories: US-001; Acceptance: AC-001)
- FR-004: Independently cover claim exclusion, stale projections, dependency concurrency, partial operations, old-client fencing, ledger rewind or tamper, exact-head review, post-merge verification, dual-feed recovery, and abstraction races. (Stories: US-001; Acceptance: AC-001)
- FR-005: Select all reverse-dependent roots, invalidate stale evidence, and run the complete root inventory at protected checkpoints. (Stories: US-001; Acceptance: AC-001)
- FR-006: Record dependency depth, states, samples, elapsed time, peak memory, and artifact volume against explicit per-root budgets. (Stories: US-001; Acceptance: AC-001)
- FR-007: Reject future Quint behavior without an owning module, imports, invariants, independent oracles, root, bounds, witnesses, projections, CI impact, and budget effect. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
- AMB-001: Whether decomposition changes observable semantics. Resolution target: no intentional behavior change in GS2-03.4; any unavoidable delta must be explicit, content-addressed, independently reviewed, and reflected in every affected oracle.
- AMB-002: Which backend qualifies each root. Resolution target: pinned Rust evaluator for fast simulation and tests, Apalache for bounded symbolic checks, and selected TLC cross-checks only for finite high-risk roots.
- AMB-003: Whether inductive invariants or a persistent Apalache process enter the default gate. Resolution target: neither; inductive checking is time-boxed experimental evidence with a bounded fallback, and each Apalache invocation remains isolated.
- AMB-004: How ordinary pull-request selection differs from protected checkpoints. Resolution target: PRs use complete reverse-dependency closure; protected main, roadmap acceptance, freeze, and release use the full registered root inventory.

## Public Or Tool-Facing Impact
- Adds repository-local module/root inventory, qualification-selection, independent-oracle, budget, admission, and evidence contracts.
- Changes qualification behavior and evidence shape but adds no production command, external writer, network permission, deployment, or package publication.

## Constraints
- Preserve all accepted unit receipts and frozen-corpus bytes exactly.
- Keep model traces deterministic, bounded, chunked, and content-addressed; retain the smallest useful counterexample rather than an unbounded MBT payload.
- A source, module graph, root closure, bound, toolchain, backend, oracle inventory, or candidate identity change invalidates reusable evidence.
- Existing generated structural tests remain projections of the canonical source and never become independent behavioral evidence.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 90-gs2-03-4-decompose-quint-model-independent-oracles`.
