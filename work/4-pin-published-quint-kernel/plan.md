---
schemaVersion: 1
workId: 4-pin-published-quint-kernel
title: Pin Published Quint Kernel
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/4-pin-published-quint-kernel/spec.md
sourceClarifications: work/4-pin-published-quint-kernel/clarifications.md
sourceChecklist: work/4-pin-published-quint-kernel/checklist.md
publicOrToolFacingImpact: true
---

# Pin Published Quint Kernel Plan

Prose status: planned

## Source Snapshot
- spec: work/4-pin-published-quint-kernel/spec.md sha256:0aae1e3da6b647d9b91ada3f617369d3933c0271275be1c498698039c5d811a8 schemaVersion:1
- clarifications: work/4-pin-published-quint-kernel/clarifications.md sha256:3f6eb440cb8440d0b92706d5d588f41c32c068b4832a564ee2a5f76c8b6cb134 schemaVersion:1
- checklist: work/4-pin-published-quint-kernel/checklist.md sha256:d7dfa1c22a958a3e1f36ec81430b7fcd48fdff1ebb94d233fc16406c9a53b16d schemaVersion:1

## Plan Scope
- Add only the published-kernel consumer and qualification boundary required by GS2-01.4.
- Leave authoring, extraction, compilation, ITF replay, protocol semantics, and runtime authority in their producer or later roadmap units.

## Technical Context
- `FS.GG.SDD.Artifacts` 1.4.0 is publicly released for .NET 10 and contains the accepted typed specification kernel plus `quint/q1-identity-manifest.json`.
- NuGet package signing can alter container bytes, so the stable semantic bundle binding is the exact manifest payload hash and its typed fields, not one universal signed-container hash.
- Central package management and locked restore are already enabled repository-wide.

## Design
- Declare version `1.4.0` once in `Directory.Packages.local.props` and reference it only from `FS.GG.Coordination.Qualification.Contracts` with `GeneratePathProperty=true` for qualification-time package-root discovery.
- Add a small pure `PublishedQuintKernel` contract which references the actual published assembly, exposes expected identity constants, validates supplied manifest bytes and required JSON paths, and returns typed diagnostics on mismatch.
- Extend the architecture verifier to enforce the single allowed package consumer and reject project references to FS.GG.SDD, checkout-relative sources, and producer-mirroring paths/names.
- Add positive tests over the restored package manifest and assets graph plus independent mutation fixtures for version, digest, identity, project-reference, local-feed, and producer-copy defects.
- Keep test discovery of the package root outside the production contract; production code accepts bytes and performs deterministic validation without filesystem or network I/O.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] complete: Pin `FS.GG.SDD.Artifacts` 1.4.0 centrally, reference it only from Qualification.Contracts, and commit the regenerated lock file.
- PD-002 [AC-001] [FR-002] [DEC-002] complete: Validate the exact manifest payload digest and required schema/profile/toolchain/guidance identities through a pure consumer contract bound to the published assembly.
- PD-003 [AC-002] [FR-003] [DEC-002] [DEC-003] complete: Add independent mutations covering pin, digest, identity, source-project, checkout-relative source, and producer-copy defects with stable diagnostics.
- PD-004 [AC-003] [FR-004] [DEC-003] complete: Extend the repository dependency policy so only the qualification assembly may consume the package and no producer implementation enters the source tree.

## Contract Impact
- PC-001 [PD-001] [PD-002] compiledBoundary: `FS.GG.Coordination.Qualification.Contracts` gains a deterministic published-kernel identity validator backed by the actual `FS.GG.SDD.Artifacts` assembly; it grants no authoring or execution authority.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] cleanLockedConsumerQualification: Restore locked from a clean package cache, build and test, validate exact manifest payload bytes and identities, inspect `project.assets.json` and lock files, run the dependency policy, and prove every independent mutant fails for its named reason.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] newOnly: This is a new consumer pin with no persisted-state migration, compatibility fallback, or source-project transition.

## Generated View Impact
- GV-001 [PD-001] [PD-002] packageIdentityEvidence: Lock files, test results, and the SDD work model are regenerated from the exact committed pin and source digests.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- The optional `quint-llm-kit` guidance identity remains recorded in the producer manifest and is not runtime authority in this repository.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 4-pin-published-quint-kernel`.
