---
schemaVersion: 1
workId: gs2-04-1-transport-foundation
title: Gs2 04 1 Transport Foundation
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-1-transport-foundation/spec.md
sourceClarifications: work/gs2-04-1-transport-foundation/clarifications.md
sourceChecklist: work/gs2-04-1-transport-foundation/checklist.md
publicOrToolFacingImpact: true
---

# Gs2 04 1 Transport Foundation Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-1-transport-foundation/spec.md sha256:d6687e0754902dc86da1d11080a7f87299e9fe3a421661869f0c9b70ab8b0f5d schemaVersion:1
- clarifications: work/gs2-04-1-transport-foundation/clarifications.md sha256:cb4625d9259687bcd87c7c49af27c0bd393b54f510f4eab3b959c806b598a3bf schemaVersion:1
- checklist: work/gs2-04-1-transport-foundation/checklist.md sha256:5e5f4fe37d000213730bb0d50755a89222b4fc513e0accb51922fcad8dc10539 schemaVersion:1

## Plan Scope
- Add a repository-local pure transport model in `FS.GG.Coordination.GitHub`; it describes requests, responses, retry decisions, conditional revisions, rate budgets, pagination, and redacted fixture projections without owning credentials or GitHub authority.
- Add a qualification contract in `FS.GG.Coordination.Qualification.Contracts` that enumerates the closed Q3 fault/control inventory and validates deterministic fixture evidence.
- Add unit tests for semantic transitions, architecture tests for surface/order/fixture/gate constraints, committed scripted fixtures, and the registered `eng/validate-github-transport.fsx` entry point.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-002] complete: Declare the public `Transport` surface in `Transport.fsi` before its implementation; use closed F# unions and records for REST/GraphQL requests, API version, response envelopes, and protocol failures, with no raw-client or credential type crossing the boundary.
- PD-002 [AC-002] [FR-002] [DEC-001] complete: Model idempotency as an explicit request value and implement retry as a pure decision over request classification, typed outcome, and attempt budget; only transient plus replay-permitted combinations may schedule another attempt.
- PD-003 [AC-003] [FR-003] [DEC-002] complete: Represent conditional revisions and their absent, matched, mismatched, and unreadable outcomes as data so adapters cannot collapse stale or unknown state into success.
- PD-004 [AC-004] [FR-004] [DEC-002] complete: Model rate-budget observations and scheduling as a pure transition that requires authoritative limit, remaining, reset, and cost facts and refuses unknown, exhausted, or unaffordable work.
- PD-005 [AC-005] [FR-005] [DEC-003] complete: Implement REST Link and GraphQL page-info traversal through one continuation state machine that detects missing, malformed, repeated, and truncated continuations and returns values only after terminal completeness.
- PD-006 [AC-006] [FR-006] [DEC-004] complete: Capture fixtures through a canonical allow-listed projection with stable field order and explicit secret/private-field classification; redaction leakage or an unclassified sensitive path is a typed validation failure.
- PD-007 [AC-007] [FR-007] complete: Define an independent closed control inventory in the qualification project and execute every truncation, replay, revision, rate, pagination, redaction, and mapping mutation against the public transport surface, requiring each mutation red and each unmutated control green.
- PD-008 [AC-008] [FR-008] [DEC-005] complete: Make `eng/validate-github-transport.fsx` a literal offline Q3 runner over committed scripted fixtures and compiled contracts; architecture tests reject network endpoints, production credential lookup, live-write verbs, or Q4 claims.

## Contract Impact
- PC-001 [PD-001] publicSurface: `src/FS.GG.Coordination.GitHub/Transport.fsi` is the Tier-1 typed transport contract and its `.fs` implementation remains signature-constrained.
- PC-002 [PD-007] qualificationContract: `src/FS.GG.Coordination.Qualification.Contracts/GitHubTransportQualification.fsi` declares the closed Q3 control/result model consumed by tests and the registered validation command.
- PC-003 [PD-008] gateCommand: `eng/validate-github-transport.fsx` preserves the registered literal command identity and emits deterministic pass/fail diagnostics only from repository-local inputs.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PC-001] semanticTest: Unit tests exercise the typed happy paths and fail-closed branches, including unsafe replay, stale revision, unknown rate facts, cyclic or truncated continuation, and secret leakage.
- VO-002 [PD-007] [PC-002] mutationControl: The generated inventory and an independently authored architecture inventory must agree exactly; every mutation must make its named control red and no generated-only/self-attested result may satisfy Q3.
- VO-003 [PD-008] [PC-003] registeredGate: The literal registered command, full test solution, architecture suite, SDD analyze/verify/ship, Bootstrap qualification, and exact-head hosted checks must pass without network writes.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: Replace the placeholder `AdapterBoundary.transportBound = false` surface with the declared typed transport modules while retaining `AdapterBoundary.name`; no stored or remote data migration is permitted in Q3.

## Generated View Impact
- GV-001 [PD-007] generatedControls: A deterministic generated control manifest under the qualification project and an independent test inventory must remain exact mirrors by identifier, while their implementations remain separate.
- GV-002 [PD-008] lifecycleViews: `readiness/gs2-04-1-transport-foundation/*` and `evidence/github-substrate-v2/*` are regenerated from current source and exact command receipts; stale or missing views cannot satisfy acceptance.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Later Q4 adapters may consume this boundary, but this unit deliberately contains no Octokit client, token acquisition, repository-policy decision, or authority escalation.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-1-transport-foundation`.
