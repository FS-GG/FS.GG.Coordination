---
schemaVersion: 1
workId: gs2-04-9-sandbox-qualification-closure
title: Gs2 04 9 Sandbox Qualification Closure
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-9-sandbox-qualification-closure/spec.md
sourceClarifications: work/gs2-04-9-sandbox-qualification-closure/clarifications.md
sourceChecklist: work/gs2-04-9-sandbox-qualification-closure/checklist.md
publicOrToolFacingImpact: true
---

# GS2-04.9 Sandbox Qualification Closure Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-9-sandbox-qualification-closure/spec.md sha256:d45ea6ecde0a5ee0c80b5ccf61aeb4cb8c4b90f0fd0c143049e94fb9d40e43b2 schemaVersion:1
- clarifications: work/gs2-04-9-sandbox-qualification-closure/clarifications.md sha256:c30fd335704accc51c39adc2c5c0f1f02a332f60192039382eb4df59109a48ae schemaVersion:1
- checklist: work/gs2-04-9-sandbox-qualification-closure/checklist.md sha256:21e25e1aacf27ad38a97e8a86b953e81e6f68d37999710b1e896831056c58bbc schemaVersion:1

## Plan Scope
- Add the Q4 domain and canonical validators to `FS.GG.Coordination.GitHub`, with signatures declared before implementation and no secret-bearing fields.
- Add a pure synthetic qualification corpus and process orchestrator under repository-owned tests and `eng/`; live GitHub effects stay behind an exact-candidate executor invoked by the credential-owning protected workflow in `FS-GG/.github`.
- Retain exact-candidate machine evidence under `evidence/github-substrate-v2/gs2-04-9` and lifecycle readiness under the work id.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] [DEC-002] complete: Declare closed F# unions and records for authority, targets, quotas, fences, operations, compensation, cleanup, child gates, and evidence; validate and canonicalize them without I/O.
- PD-002 [AC-002] [FR-002] [DEC-003] complete: Model execution as prepare → fence → effect → classify → authoritative reread, so ambiguous transport and stale state remain explicit outcomes and cannot advance the plan.
- PD-003 [AC-003] [FR-003] [DEC-003] complete: Build cleanup from the executed-operation journal in reverse order and require an independent final reread of every target before producing a closed result.
- PD-004 [AC-004] [FR-004] [DEC-004] complete: Generate one valid fixture and independent single-field or single-step mutants for every registered inversion; assert the baseline green and each mutant red with its own diagnostic.
- PD-005 [AC-005] [FR-005] [DEC-002] [DEC-004] complete: Add an `eng` comprehensive runner that creates a fresh result directory and nonce, launches each registered Q3 command in a separate cold process, validates eight distinct child artifacts, and only then invokes Q4.
- PD-006 [AC-006] [FR-006] [DEC-004] complete: Implement the literal Q4 validator as an offline exact-candidate check over canonical JSON and SHA-256 bindings; redact or reject credential material and bind immutable workflow/run artifact coordinates.
- PD-007 [AC-007] [FR-007] [DEC-001] complete: Put live readiness ahead of all effects and require separately named sandbox inputs plus authoritative actor/App and target checks; missing data, current human actor, or production-capable classification exits red before write.
- PD-008 [AC-008] [FR-008] [DEC-002] [DEC-004] complete: Expose explicit synthetic/live harness modes and an exact-candidate live executor; the credential-owning protected workflow in `FS-GG/.github` supplies concurrency isolation, short expiry, bounded quota, always-run cleanup, artifact retention, and non-green live status when authority or cleanup proof is absent.

## Contract Impact
- PC-001 [PD-001] publicSurface: Add `GitHubSandboxClosure.fsi` before its implementation, using only typed public values and `Result`-returning pure validators; append the surface to the API baseline.
- PC-002 [PD-005] commandContract: Preserve the registered literal `github-sandbox-closure-contract` identity and its cold process semantics; its JSON result is schema-versioned and exact-candidate bound.
- PC-003 [PD-008] liveExecutorContract: Environment inputs distinguish synthetic from live mode and name only non-production sandbox configuration; the live executor rejects `github.token`-equivalent ambient human authority and is consumed by the protected credential-owner workflow without serializing secrets.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PC-001] semanticTest: Compile the `.fsi` surface and run focused domain/canonical validation tests over complete, stale, ambiguous, compensated, residual, and secret-bearing fixtures.
- VO-002 [PD-004] [PD-006] [PC-002] mutationTest: Prove the exact baseline green and every required independent inversion red, including substituted receipt and omitted child result.
- VO-003 [PD-005] [PC-002] processTest: Capture process identities and clean result directories proving all eight Q3 commands and Q4 were launched cold in registered order.
- VO-004 [PD-007] [PD-008] [PC-003] protectedTest: Prove synthetic execution and fail-before-write live readiness without a sandbox credential; admit live green evidence only from the credential-owner workflow using the separately configured non-production App identity and a full cleanup reread.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The Q4 schema is new and version 1; unsupported versions, missing bindings, and future fields diagnose rather than downgrade or infer.

## Generated View Impact
- GV-001 [PD-001] [PD-006] evidenceView: Lifecycle readiness and `evidence/github-substrate-v2/gs2-04-9` are regenerated from exact source and candidate digests; stale or cross-run views are refused.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Live Q4 evidence is owned by protected `FS-GG/.github` workflow run `33465929736`; this repository contains no App private key and accepts only the exact-candidate artifact and authoritative cleanup receipt produced by that route.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-9-sandbox-qualification-closure`.
