---
schemaVersion: 1
workId: 1-establish-solution-boundary
title: Establish Solution Boundary
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/1-establish-solution-boundary/spec.md
sourceClarifications: work/1-establish-solution-boundary/clarifications.md
sourceChecklist: work/1-establish-solution-boundary/checklist.md
publicOrToolFacingImpact: true
---

# Establish Solution Boundary Plan

Prose status: planned

## Source Snapshot
- spec: work/1-establish-solution-boundary/spec.md sha256:9d108dc973bda94eecea705c8858722c104b2c456a4793270afa29650c59251d schemaVersion:1
- clarifications: work/1-establish-solution-boundary/clarifications.md sha256:2e951037f1625173db672d24b38c058dbac816454e9f16b34b0aa14822de2393 schemaVersion:1
- checklist: work/1-establish-solution-boundary/checklist.md sha256:b76b2c5c6cade99323d54c6f27b4d957bc4e5ced65128770dc0dc6d6f91d1538 schemaVersion:1

## Plan Scope
- Establish the compilable repository skeleton and dependency-policy gate only.
- Leave protocol semantics, GitHub transport, command behavior, hosting, deployment, and production authority to later roadmap units.

## Technical Context
- F# projects target .NET 10 and use repository-wide deterministic build settings.
- Production assemblies are `FS.GG.Coordination.Protocol`, `FS.GG.Coordination.Core`, `FS.GG.Coordination.GitHub`, `FS.GG.Coordination.Cli`, `FS.GG.Coordination.App`, and `FS.GG.Coordination.Qualification.Contracts`.
- Test assemblies exercise the pure core boundary, host inertness, and the repository dependency verifier.

## Design
- Protocol contains compiled marker contracts and has no project or transport dependencies.
- Core references Protocol only and contains a pure boundary descriptor suitable for a first executable architectural test.
- GitHub references Core and Protocol but contains no SDK or HTTP binding in this unit.
- CLI and App reference GitHub, Core, and Protocol; App is a class library with an explicit inert-host status and no listener or executable entry point.
- Qualification.Contracts references Protocol and declares qualification vocabulary without granting runtime authority.
- `eng/verify-dependencies.fsx` reads project XML directly, validates the allow-listed graph, and rejects forbidden GitHub/HTTP package or assembly references in Protocol and Core.
- `tests/fixtures/forbidden-dependency` is a non-solution fixture whose Core project references a GitHub-layer project; a test invokes the same verifier and asserts the named rejection.

## Plan Decisions
- PD-001 [AC-001] [FR-001] [DEC-001] [DEC-003] complete: Declare the six production projects and focused test projects in `FS.GG.Coordination.sln`, with signatures or minimal compiled contracts preceding implementation where a public surface exists.
- PD-002 [AC-001] [FR-002] [DEC-001] complete: Encode the permitted project graph in repository policy and validate project and package references directly from project XML on every test run.
- PD-003 [AC-001] [FR-003] [DEC-004] complete: Exercise the dependency verifier against both the real solution and a deliberately invalid fixture, asserting the exact forbidden-edge diagnostic.
- PD-004 [AC-001] [FR-004] [DEC-002] complete: Represent App as an inert class library and prove the repository contains no web SDK, listener, deployment, secret, subscription, webhook registration, or production mutation surface.

## Contract Impact
- PC-001 [PD-001] compiledBoundary: Public marker and boundary-status signatures establish assembly ownership only; they deliberately expose no protocol operations or production capabilities.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] cleanBuildAndBoundaryTest: Restore, build, and test from a clean checkout; run the positive dependency scan; prove the negative fixture fails with the expected rule; scan for forbidden hosting and transport references.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] newOnly: This is a new repository boundary with no persisted state, compatibility migration, v1 import, or cutover behavior.

## Generated View Impact
- GV-001 [PD-001] solutionAndPolicy: The solution file and dependency-policy report are derived from committed project declarations and must remain reproducible from a clean checkout.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 1-establish-solution-boundary`.
