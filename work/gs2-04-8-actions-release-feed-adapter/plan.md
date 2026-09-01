---
schemaVersion: 1
workId: gs2-04-8-actions-release-feed-adapter
title: Gs2 04 8 Actions Release Feed Adapter
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/gs2-04-8-actions-release-feed-adapter/spec.md
sourceClarifications: work/gs2-04-8-actions-release-feed-adapter/clarifications.md
sourceChecklist: work/gs2-04-8-actions-release-feed-adapter/checklist.md
publicOrToolFacingImpact: true
---

# Gs2 04 8 Actions Release Feed Adapter Plan

Prose status: planned

## Source Snapshot
- spec: work/gs2-04-8-actions-release-feed-adapter/spec.md sha256:7f1f2abc440191c4fbaf10394dc5e9fb3e9b278b9e1ad55ef0b6266590bf4700 schemaVersion:1
- clarifications: work/gs2-04-8-actions-release-feed-adapter/clarifications.md sha256:d149e4e78c1a427140a10a3b2fc1ffdffae7d7c56596aac96e39e0972e506020 schemaVersion:1
- checklist: work/gs2-04-8-actions-release-feed-adapter/checklist.md sha256:3953ac6ced854b327128f64584ef698adb015de2fb24e0add757e127d7d98b0d schemaVersion:1

## Plan Scope
- Add a pure-first `ActionsReleaseFeedAdapter` module to `FS.GG.Coordination.GitHub`, exposing closed identity, lifecycle, availability, provenance, and served-content types through `.fsi` before `.fs`.
- Keep live GitHub transport and every mutation outside this unit; the adapter consumes typed observations and returns validation plus canonical fingerprints.
- Add an independently authored qualification contract/result model, comprehensive JSON fixture, executable FSI validator, focused unit tests, architecture controls, and retained evidence.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] [DEC-002] complete: Define exact closed identities for repository, workflow, run attempt, job, checks, commit, and merge group; validate cross-subject agreement and keep check outcome non-authoritative.
- PD-002 [AC-002] [FR-002] [DEC-001] complete: Model lifecycle as closed outcomes, require explicit endpoint/page completeness, preserve reruns as independent attempts, and canonicalize independently of response order.
- PD-003 [AC-003] [FR-003] [DEC-004] complete: Define release/tag/asset identity and immutable/deleted/tampered/expired evidence with historical identity separated from current availability.
- PD-004 [AC-004] [FR-004] [DEC-005] complete: Validate attestation subject/predicate/digest and package owner/name/version/feed/repository coordinates as one exact provenance boundary.
- PD-005 [AC-005] [FR-005] [DEC-003] complete: Represent upload acceptance, durable metadata, authenticated retrieval, redirect resolution, and anonymous served bytes as distinct ordered evidence rungs.
- PD-006 [AC-006] [FR-006] [DEC-003] complete: Hash exact bytes and bind request/redirect/final content identity while structurally excluding credentials and authorization values.
- PD-007 [AC-007] [FR-007] [DEC-004] complete: Preserve every registered availability/failure state and derive stable per-surface plus aggregate fingerprints without inferred absence, provenance, mergeability, or availability.
- PD-008 [AC-008] [FR-008] [DEC-006] complete: Generate positive qualification inventory from closed vocabularies and keep independently authored negative outcomes for every registered control, with deterministic typed result evidence.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] publicSurface: Add `src/FS.GG.Coordination.GitHub/ActionsReleaseFeedAdapter.fsi` before its `.fs`, compile both, and keep identity, lifecycle, availability, provenance, download, validation, and fingerprint cases explicit and structurally comparable.
- PC-002 [PD-008] toolContract: Implement the registered literal `dotnet fsi eng/validate-github-actions-release-feed.fsx -- .` command and emit `fsgg.coordination.github-actions-release-feed-qualification/1` results without changing catalog identity.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PD-005] [PD-006] [PD-007] [PC-001] semanticTest: Add focused unit tests and fixtures for positive behavior and every named identity, lifecycle, pagination, provenance, availability, redirect, digest, stale, incomplete, and no-live-write control.
- VO-002 [PD-008] [PC-002] gateMutation: Run the registered validator green, invert at least one load-bearing accepted predicate and observe red, restore it, then bind catalog/index/candidate/results through `roadmap-work manifest` and `roadmap-work gates`.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The adapter is a new public module with no runtime wiring or live migration; existing GitHub adapters remain unchanged and no prior authority is retired in GS2-04.8.

## Generated View Impact
- GV-001 [PD-008] [VO-002] evidenceViews: Refresh `readiness/gs2-04-8-actions-release-feed-adapter` through analyze, evidence, verify, and ship; retain the independently authored Q3 result and ignored roadmap manifest/gate outputs bound to the exact commit.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Existing published immutable artifacts may seed local fixtures, but no validator path performs network access or writes to GitHub.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work gs2-04-8-actions-release-feed-adapter`.
