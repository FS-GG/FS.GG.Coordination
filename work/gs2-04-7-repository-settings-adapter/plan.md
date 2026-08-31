---
schemaVersion: 1
workId: gs2-04-7-repository-settings-adapter
title: Gs2 04 7 Repository Settings Adapter
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-7-repository-settings-adapter/spec.md
sourceClarifications: work/gs2-04-7-repository-settings-adapter/clarifications.md
sourceChecklist: work/gs2-04-7-repository-settings-adapter/checklist.md
publicOrToolFacingImpact: true
---

# Gs2 04 7 Repository Settings Adapter Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-7-repository-settings-adapter/spec.md sha256:68bc746924278ee1b2c338b3e613c5de3d2a0ea328204463009b2d219157d091 schemaVersion:1
- clarifications: work/gs2-04-7-repository-settings-adapter/clarifications.md sha256:62d99dc879c2846566612a0429700b6f9442a7a4e93020c1f5a2235a0595c3c2 schemaVersion:1
- checklist: work/gs2-04-7-repository-settings-adapter/checklist.md sha256:e91a724f1144ed1a4415fb078c0a8f4de95c40bdc791a90052d4b512a8d2e7f6 schemaVersion:1

## Plan Scope
- Add a pure-first `RepositorySettingsAdapter` module to `FS.GG.Coordination.GitHub`, exposing closed domain types and total validation/planning/reconciliation functions through `.fsi` before `.fs`.
- Keep live GitHub transport and all settings mutation outside this unit; the adapter consumes typed observations and emits typed plans and repair dispositions.
- Add an independently authored qualification contract/result model, comprehensive JSON fixture, executable FSI validator, focused unit tests, architecture controls, and retained evidence.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Define canonical repository identity and require every surface observation to bind the same node/database/owner/name/default-branch/source facts.
- PD-002 [AC-002] [FR-002] [DEC-004] complete: Model each surface as a closed availability union, require complete endpoint/pagination evidence for supported values, and hash canonical per-surface plus aggregate observation bytes.
- PD-003 [AC-003] [FR-003] [DEC-002] complete: Define the complete settings vocabulary with typed values and environment metadata, rejecting secret/variable values before canonicalization and diagnostics.
- PD-004 [AC-004] [FR-004] [DEC-001] complete: Canonicalize desired state and derive minimal operations ordered by closed surface rank, subject key, kind, and stable operation identity, binding exact pre-state/desired digests and least permission.
- PD-005 [AC-005] [FR-005] [DEC-004] complete: Refuse any composite plan whose required pre-state is partial, unauthorized, unavailable, unreadable, contradictory, or stale; return an explicit authoritative reread requirement.
- PD-006 [AC-006] [FR-006] [DEC-003] complete: Reconcile transport/result and complete post-state into verified, reread-and-replan, rollback, forward-repair, or definite-refusal without response-based success inference.
- PD-007 [AC-007] [FR-007] [DEC-005] complete: Represent no-op as a digest-bound empty plan and preserve unsupported and unrelated supported controls as explicit evidence.
- PD-008 [AC-008] [FR-008] [DEC-006] complete: Generate the positive qualification inventory from the closed control vocabulary and keep independently authored outcomes for every named failure class, with a deterministic typed result artifact.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] publicSurface: Add `src/FS.GG.Coordination.GitHub/RepositorySettingsAdapter.fsi` before its `.fs`, compile both in the project, and keep all availability, validation, planning, reconciliation, and repair cases explicit and structurally comparable.
- PC-002 [PD-008] toolContract: Implement the already-registered literal `dotnet fsi eng/validate-github-repository-settings.fsx -- .` command and emit `fsgg.coordination.github-repository-settings-qualification/1` results without changing catalog identity.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PC-001] semanticTest: Add focused unit tests and complete fixtures for positive behavior and every independently named refusal, including identity, pagination, availability, surface values, planning, stale/indeterminate reconciliation, unrelated preservation, no-op, and secret redaction.
- VO-002 [PD-008] [PC-002] gateMutation: Run the registered validator green, invert at least one load-bearing accepted predicate and observe red, restore it, then bind catalog/index/candidate/results through `roadmap-work manifest` and `roadmap-work gates`.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The adapter is a new public module with no runtime wiring or live migration; existing transport, projection, and journal adapters remain unchanged and no v1 authority is retired in GS2-04.7.

## Generated View Impact
- GV-001 [PD-008] [VO-002] evidenceViews: Refresh `readiness/gs2-04-7-repository-settings-adapter` through analyze, evidence, verify, and ship; retain the independently authored Q3 result and ignored roadmap manifest/gate outputs bound to the exact commit.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Existing administrator reports and protected observations are fixture inputs, but the Q3 adapter must reject drifted synthetic observations and must not perform a live write.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-7-repository-settings-adapter`.
