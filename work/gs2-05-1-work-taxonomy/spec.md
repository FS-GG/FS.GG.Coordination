---
schemaVersion: 1
workId: gs2-05-1-work-taxonomy
title: GS2-05.1 work taxonomy contract
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-05.1 work taxonomy contract Specification

Prose status: specified

## User Value
Native issue type is the sole post-migration classification authority; Class and Kind are retired projections.

## Scope
- SB-001: Repository-local pure taxonomy, complete frozen legacy corpus, deterministic migration planner, plain Quint Q2 model, validator, independent tests, immutable evidence, and SDD artifacts only; exclude every live GitHub write, GS2-05.2, intake, deployment, publication, release, and successor authority.

## Non-Goals
- SB-002: Do not read or write GitHub, create or mutate organization issue types or fields, convert issues, mutate Projects, use production credentials, deploy, publish, or release.
- SB-003: Do not implement the GS2-05.2 organization-field contract, GS2-05.3 intake, or any successor-unit behavior.
- SB-004: Do not preserve Class or Kind as post-migration authority. They may appear only as fingerprinted prestate and explicit retired projections in a migration disposition.

## User Stories
- US-001 (P1): As a fleet operator, I can classify every supported current row into exactly one native issue type or receive an explicit fail-closed diagnostic before any live mutation is authorized.
- US-002 (P1): As a lifecycle reducer, I can determine from the native issue type alone whether the row is lifecycle-managed work or a lifecycle-exempt standing row.
- US-003 (P1): As an independent reviewer, I can reproduce the same canonical migration bytes from the frozen corpus and prove that no supported combination was omitted.
- US-004 (P2): As a future cutover implementer, I can consume stable row identities, prestate fingerprints, target types, retired projections, and preservation facts without reinterpreting legacy prose.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-003] [FR-004]: Given a complete readable current row, when the pure classifier evaluates it, then exactly one native type and lifecycle applicability are returned, or one or more canonically ordered refusal diagnostics are returned with no plan.
- AC-002 [US-002] [FR-001]: Given Epic, Feature, Task, Bug, or Decision, lifecycle applicability is `work`; given Register or Directive, lifecycle applicability is `standing-exempt`; no Class or Kind input is consulted after migration.
- AC-003 [US-003] [FR-002] [FR-005]: Given the frozen corpus, the validator proves every declared combination exactly once, verifies its SHA-256 and independent expectation, and rejects removal, duplication, reordering, or mutation of any case.
- AC-004 [US-004] [FR-003] [FR-005]: Given accepted observations in any input order, planning emits byte-identical dispositions sorted by stable row identity; replanning the canonical poststate is an explicit no-op and changes no preserved hierarchy or repository scope.
- AC-005 [US-001] [FR-004] [FR-005]: Given independent fixtures for missing, unknown, contradictory, ambiguous, unsupported, lossy, duplicate, incomplete, stale, and unreadable observations, each fixture is refused for its expected reason and produces no partial plan.
- AC-006 [US-003] [FR-006] [FR-007]: Given the plain Quint model and focused validation suite, sampled execution reaches both planned and refused witnesses while sole authority, preservation, uniqueness, totality, and determinism invariants hold.

## Functional Requirements
- FR-001: Admit exactly Epic, Feature, Task, Bug, Decision, Register, and Directive. Epic, Feature, Task, Bug, and Decision MUST be lifecycle-managed work; Register and Directive MUST be standing lifecycle exemptions. (Stories: US-001, US-002; Acceptance: AC-001, AC-002)
- FR-002: Inventory every supported legacy Class/Kind/native-type combination, including absent Kind as canonical `work`, hierarchical anchor, register, directive, defect, hardening, decision, capability, and already-native no-op cases; bind canonical corpus bytes and each row prestate to SHA-256. (Stories: US-001, US-003; Acceptance: AC-003)
- FR-003: Produce exactly one canonical disposition per accepted stable row identity with prestate fingerprint, target native type, the exact Class/Kind projections to retire, lifecycle applicability, hierarchy preservation, repository-scope preservation, no-op status, and empty diagnostics. A refused row MUST produce diagnostics and no disposition. (Stories: US-001, US-004; Acceptance: AC-001, AC-004)
- FR-004: Refuse missing stable identity or fingerprint material, unknown tokens, contradictory native/legacy evidence, ambiguous Class/Kind combinations, unsupported native types, lossy hierarchy or repository scope, duplicate stable identities, incomplete observations, stale observations, and unreadable source facts. Diagnostics MUST use a closed vocabulary and canonical ordering. (Stories: US-001; Acceptance: AC-001, AC-005)
- FR-005: Prove corpus totality, unique row identities and prestates, unique classification, canonical order, idempotency, explicit already-native no-op behavior, byte stability, hierarchy and repository-scope preservation, and independent negative cases including omission of one corpus combination. (Stories: US-003, US-004; Acceptance: AC-003, AC-004, AC-005)
- FR-006: Model one cohesive repository-local input/result state in plain Quint with pure guard/classification/plan functions, separate guarded plan and refuse actions, non-zero planned/refused witnesses, and invariants for sole native authority, preservation, uniqueness, totality, and determinism. Tests MUST be in a separate Quint test module and use `quint typecheck` plus sampled `quint run`, never an unrequested exhaustive verify. (Stories: US-003; Acceptance: AC-006)
- FR-007: Pass `dotnet fsi eng/validate-github-work-taxonomy.fsx -- .` plus focused Quint typecheck/run, focused unit and architecture tests, warning-free build, every independent inversion, immutable evidence validation, and complete SDD analyze/verify/ship gates on the exact protected candidate. (Stories: US-003; Acceptance: AC-006)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Introduces a repository-local pure F# taxonomy/planning API and a Q2 validation command. It does not introduce a live command or effect boundary.

## Requirement Traceability
- Fleet design §5.1 fixes the native mapping: anchor→Epic, capability→Feature, hardening→Task, defect→Bug, decision/human-decision→Decision, register→Register, directive→Directive.
- Board schema `Class` fixes the current closed operational values defect, hardening, and decision; the migration corpus additionally retains the design-declared capability prestate so it cannot be silently omitted.
- Board schema `Kind` fixes work, anchor, register, and directive; absent Kind is explicitly equivalent to work.
- Registration contract `ed9ae9d198d6eaaf89030f85d214a0a359333598be7ceb3597c2c4aeb629ef28` fixes Q2, the refusal families, proof obligations, and the no-write ceiling.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-05-1-work-taxonomy`.
