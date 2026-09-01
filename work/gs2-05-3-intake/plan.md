---
schemaVersion: 1
workId: gs2-05-3-intake
title: GS2-05.3 intake contract
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-05-3-intake/spec.md
sourceClarifications: work/gs2-05-3-intake/clarifications.md
sourceChecklist: work/gs2-05-3-intake/checklist.md
publicOrToolFacingImpact: true
---

# GS2-05.3 intake contract Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-05-3-intake/spec.md sha256:edb8c4f33cfcc4562924a099582ad2d7b71020060e906d4581311239977ac911 schemaVersion:1
- clarifications: work/gs2-05-3-intake/clarifications.md sha256:6cafd5e9d60caf8d58ff0826478a1a78d79cbe7ea905ec279512c407b405918b schemaVersion:1
- checklist: work/gs2-05-3-intake/checklist.md sha256:9aa61287e7a8250029ce023f9795cf9a2dec103e8cbd1e241fd4ae9b5dbcb753 schemaVersion:1

## Plan Scope
- Add one composed, signature-first intake boundary to `FS.GG.Coordination.GitHub`; this unit performs no HTTP, credential lookup, or hosted write.
- Compose the existing issue, issue-field, Project, hierarchy, dependency, and protocol contracts into a complete observation, canonical plan, controlled apply, and recovery algebra.
- Add an independent closed qualification contract, canonical synthetic corpus, focused unit and architecture suites, and the literal registered offline Q3 validator.
- Record exact SDD lifecycle and qualification evidence without compiling the roadmap or claiming GS2-05.4 behavior.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] [DEC-002] complete: Define `IntakeAdapter.fsi` before its implementation with a typed complete-observation envelope spanning issue identity/type/fields, Project membership, hierarchy, dependencies, and protocol state. Validate it purely and return stable diagnostics for invalid identity, duplicate or missing facts, incomplete pagination, unreadable outcomes, inconsistent revisions, and unsupported state.
- PD-002 [AC-001] [FR-002] [DEC-002] [DEC-004] complete: Produce a deterministic sealed plan only from a valid complete observation. The plan carries canonical ordered effects, exact preconditions, intended post-state, compensation facts, causation, observation revision/digest, and a length-framed SHA-256 plan digest; an already-satisfied observation yields a typed no-op plan.
- PD-003 [AC-002] [FR-003] [DEC-001] [DEC-004] complete: Expose controlled apply as a pure state transition over an injected, fixture-owned effect outcome stream. Reobserve and compare the full plan fence before the first effect, enforce canonical effect order, reread after each accepted effect, verify exact final post-state, and record durable typed outcomes without embedding a production transport.
- PD-004 [AC-002] [FR-004] [DEC-004] complete: Make replay idempotent and recovery explicit. Resume skips only effects whose durable result and reread prove the intended post-state; roll-forward continues remaining effects; compensation executes sealed inverse effects in reverse accepted order and refuses missing, stale, or ambiguous recovery evidence.
- PD-005 [AC-003] [FR-005] [DEC-002] complete: Preserve exhaustive page-chain and outcome distinctions across every composed surface. Missing, duplicate, redacted, unauthorized, archived, external, draft, unsupported, incomplete, stale, concurrently changed, and indeterminate states remain typed refusals and never collapse to absence or success.
- PD-006 [AC-003] [FR-006] [DEC-003] complete: Restrict protocol initialization intents to the five registered families—initial journal, scheduling intent, contract, touch set, and projections—and reject any mutation outside that closed set before a plan is sealed.
- PD-007 [AC-004] [FR-007] complete: Define `GitHubIntakeQualification.fsi` as an independently authored closed control inventory. Require separate generated and independent expectations, two passing baselines, every named mutation turning red, canonical-byte replay, omission controls, and a committed synthetic corpus bound to the registered contract and accepted predecessor receipt.
- PD-008 [AC-004] [FR-008] complete: Implement `eng/validate-github-intake.fsx` as the literal registered Q3 command loading production signatures and the independent qualification contract. It runs offline without HTTP, credentials, live writes, roadmap compilation, GS2-05.4 claims, or dependency inversion.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-004] publicSurface: `src/FS.GG.Coordination.GitHub/IntakeAdapter.fsi` is the additive signature-first intake contract; it exposes plans and controlled transition results, not a live transport.
- PC-002 [PD-007] qualificationContract: `src/FS.GG.Coordination.Qualification.Contracts/GitHubIntakeQualification.fsi` owns the closed Q3 control/result vocabulary independently of the production project.
- PC-003 [PD-008] gateCommand: `eng/validate-github-intake.fsx` is the exact offline command registered by the roadmap gate catalog.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PC-001] semanticTest: Run focused unit tests covering valid/no-op planning, every typed observation refusal, digest determinism, full-fence drift, canonical apply order, post-state mismatch, idempotent replay, resume, roll-forward, and reverse compensation.
- VO-002 [PD-007] [PC-002] mutationControl: Prove generated and independently authored inventories agree exactly, both baselines pass, every mutation fails its named control, and omission or self-attestation cannot satisfy Q3.
- VO-003 [PD-008] [PC-003] registeredGate: Run the literal registered command, focused unit and architecture suites, warning-free build, canonical evidence replay, SDD analyze/verify/ship, and protected exact-head qualification before acceptance.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additiveControlledBoundary: Add the composed adapter after the accepted GS2-05.2 field contract. No persisted-data migration, production transport wiring, hosted mutation, or legacy-authority retirement occurs in GS2-05.3.

## Generated View Impact
- GV-001 [PD-007] evidenceViews: The canonical corpus, generated controls, independent controls, qualification result, and TRX must agree on the exact sealed plan/control inventory while retaining independent implementations.
- GV-002 [PD-008] lifecycleViews: Refresh `readiness/gs2-05-3-intake` through analyze, evidence, verify, and ship from the exact authored sources; stale or missing generated views cannot satisfy acceptance.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- The injected controlled interpreter is a qualification boundary, not production mutation authority. Its complete scripted effect/outcome stream makes correspondence testable without GitHub credentials.
- The committed corpus is deliberately synthetic and credential-free. Later cutover units own destructive sandbox and production correspondence; this unit claims only the registered Q3 gate.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-05-3-intake`.
