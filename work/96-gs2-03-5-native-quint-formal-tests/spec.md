---
schemaVersion: 1
workId: 96-gs2-03-5-native-quint-formal-tests
title: "GS2-03.5 native Quint model, property, and formal tests"
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.5 native Quint model, property, and formal tests Specification

Prose status: specified

## User Value
Maintainers can execute, prove, inspect, and reproduce the canonical coordination protocol's formal behaviors and counterexamples directly in native Quint.

## Scope
- SB-001: Add repository-local native Quint examples, simulation runs, reachability witnesses, safety properties, temporal liveness checks, bounded model checking, and reproducible Quint/ITF counterexamples.
- SB-002: Cover claim/election, relation mutation, lifecycle, operation saga, epoch, and rollback with a complete, machine-validated property catalogue.
- SB-003: Import the accepted canonical protocol and independently executable roots; properties and scenarios may parameterize that authority but may not restate it as a shadow transition model.

## Non-Goals
- SB-004: Do not implement GS2-03.6 fault injection, external fault orchestration, network or GitHub mutation, deployment, publication, or production writes.
- SB-005: Do not replace model checking with simulation, claim unbounded correctness, or retain nondeterministic counterexamples whose identity cannot be reproduced.
- SB-006: Do not change accepted predecessor receipts, frozen-corpus bytes, or the GS2-03.5 contract.

## User Stories
- US-001 (P1): As a protocol maintainer, I can run executable examples and simulations that demonstrate the expected behaviors of every named coordination state space.
- US-002 (P1): As a formal-methods reviewer, I can evaluate reachability, safety, temporal liveness, and bounded-state properties against the exact canonical Quint authority.
- US-003 (P1): As an incident investigator, I can reproduce a retained failing Quint/ITF trace from its content identity and observe the same property violation.
- US-004 (P2): As a qualification operator, I can prove the formal-test catalogue is complete, bounded, deterministic, and covered by negative controls within the accepted qualification budgets.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given the canonical protocol and its registered verification roots, when native examples and simulations run, then every named state space has a reachable success witness whose observable terminal state and trace identity are deterministic.
- AC-002 [US-002] [FR-003]: Given the complete catalogue, when reachability and safety checks run, then each named state space has at least one non-vacuous reachability target and one safety property, and corrupting each property's subject makes its focused control red.
- AC-003 [US-002] [FR-004]: Given progress is continuously enabled under the catalogue's explicit fairness assumptions, when temporal checks run, then the named lifecycle, operation-saga, epoch, rollback, election, and relation progress obligations are satisfied within registered bounds; removing a progress transition exposes the expected violation.
- AC-004 [US-002] [US-004] [FR-005]: Given the finite registered bounds and pinned toolchain, when bounded model checking runs, then the complete state-space/property matrix is selected, explored, measured, and refused on missing coverage, missing bounds, stale tool identity, timeout, or budget overflow.
- AC-005 [US-003] [FR-006]: Given a deliberately violated property, when its retained counterexample is emitted and replayed, then canonical Quint and normalized ITF identities bind the same source, root, property, bound, toolchain, ordered states, and violated outcome and reproduce byte-identically.
- AC-006 [US-003] [US-004] [FR-006] [FR-007]: Given a counterexample or catalogue artifact is reordered, truncated, substituted, rebound, or otherwise corrupted, when qualification runs, then it fails closed before the artifact can be reused or reported as evidence.
- AC-007 [US-004] [FR-007]: Given an ordinary pull request or protected checkpoint, when qualification selects GS2-03.5 evidence, then it executes the smallest sound affected formal-test closure for the pull request and the complete catalogue at protected checkpoints while remaining within the accepted 03.4 performance and artifact budgets.

## Functional Requirements
- FR-001: Define a canonical formal-test catalogue whose entries bind one of the six named state spaces, the imported canonical root, test kind, property or witness name, finite bounds, pinned backend, and expected outcome. (Stories: US-001, US-004; Acceptance: AC-001, AC-004)
- FR-002: Execute native Quint examples and seeded simulations for every named state space and retain deterministic reachability witnesses rather than prose-only examples. (Stories: US-001; Acceptance: AC-001)
- FR-003: Define and execute non-vacuous reachability and safety properties for every named state space, with a subject mutation that proves each registered property gate can fail. (Stories: US-002, US-004; Acceptance: AC-002)
- FR-004: Define temporal liveness obligations with explicit fairness and finite-check assumptions for election, relation progress, lifecycle convergence, saga disposition, epoch advancement, and rollback convergence; prove at least one transition-removal counterexample per obligation family. (Stories: US-002; Acceptance: AC-003)
- FR-005: Run bounded model checking over the complete registered state-space/property matrix using pinned tools and explicit per-entry state, step, time, memory, and artifact budgets; missing or incomplete exploration is a refusal. (Stories: US-002, US-004; Acceptance: AC-004)
- FR-006: Normalize retained counterexamples into deterministic Quint and ITF artifacts that bind source, executable closure, property, bounds, toolchain, ordered states, and outcome; byte-identical replay is required and semantic rebinding is rejected. (Stories: US-003; Acceptance: AC-005, AC-006)
- FR-007: Validate catalogue completeness, impact selection, evidence identity, negative controls, measurement budgets, and protected-checkpoint full coverage without introducing a second behavioral authority. (Stories: US-004; Acceptance: AC-006, AC-007)

## Ambiguities
- AMB-001: Which state spaces require temporal checks in addition to reachability and safety. Resolution target: all six receive explicit progress obligations, while fairness assumptions and bounded encodings are catalogue data rather than hidden tool defaults.
- AMB-002: Whether a counterexample must come from a naturally failing production invariant. Resolution target: no; deterministic deliberately-invalid subjects supply reproducible negative evidence without weakening the production property.
- AMB-003: Whether examples and simulations may become separate models. Resolution target: no; each imports a canonical executable root and supplies only parameters, runs, witnesses, and properties.
- AMB-004: How exhaustive bounded checking is represented. Resolution target: exhaustive only within the registered finite bound and pinned backend; reports state, transition, time, memory, and artifact measurements and never generalizes beyond that bound.
- AMB-005: Which counterexample format is authoritative. Resolution target: the canonical Quint trace and normalized ITF projection are co-bound artifacts; neither may be silently regenerated from a different source, closure, bound, or toolchain.

## Public Or Tool-Facing Impact
- Adds repository-local formal-test catalogue, native Quint test/property modules, deterministic Quint/ITF counterexample evidence, and qualification validation.
- Changes no production command, network permission, GitHub writer, deployment, or package publication path.

## Constraints
- Preserve the accepted canonical protocol as the sole behavioral authority and import its registered executable roots through their source-derived closure.
- Use only pinned Quint and existing bounded-checking backends admitted by the accepted qualification system.
- Keep runs finite and deterministic with explicit seeds, bounds, fairness assumptions, timeouts, and budgets.
- Retain the smallest useful counterexample; large exploratory traces are measurements, not committed evidence.
- A source, root closure, property, bound, backend, toolchain, normalization, or expected-outcome change invalidates reusable evidence.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 96-gs2-03-5-native-quint-formal-tests`.
