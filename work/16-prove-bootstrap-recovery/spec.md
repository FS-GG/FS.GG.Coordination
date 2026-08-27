---
schemaVersion: 1
workId: 16-prove-bootstrap-recovery
title: Prove Bootstrap Recovery
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Prove Bootstrap Recovery Specification

Prose status: specified

## User Value
Maintainers and independent reviewers can prove a fresh machine can recover the exact committed candidate through build, test, pack, install, and execution without hidden local state.

## Scope
- SB-001: Add one closed clean-clone recovery command, its pinned Q7 identity, hosted evidence lane, compact recovery receipt, adversarial controls, and operator architecture documentation.

## Non-Goals
- SB-002: Do not publish packages, mutate GitHub, change organization settings, provision runtime, consume events, or execute GS2-01.9.

## User Stories
- US-001 (P1): As a maintainer or independent reviewer, I can reproduce the exact candidate from committed bytes through install and execution without inheriting developer state.
- US-002 (P1): As a repository maintainer, I can trust that the recovery proof reads dependencies only from the declared published feed and cannot be redirected by caller input.
- US-003 (P1): As a qualification reviewer, I can bind every recovery stage to the reviewed commit, closed Q7 command identity, hosted artifact, and compact digest evidence.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given a clean committed candidate, when recovery runs, then it clones the exact HEAD into new scratch storage and proves the source and clone revisions agree before executing build tools.
- AC-002 [US-001, US-002] [FR-002]: Given an initially empty package cache, when restore runs, then the locked solution resolves only through the explicit HTTPS NuGet feed and fails on graph or feed substitution.
- AC-003 [US-001] [FR-003]: Given the recovered clone, when qualification runs, then the Release build has zero warnings/errors and both unit and architecture suites pass without restore or rebuild substitution.
- AC-004 [US-001, US-002] [FR-004]: Given the recovered build, when package validation runs, then it packs the protocol package, installs it into a clean consumer through the local candidate feed plus published dependency feed, and proves the consumer executes.
- AC-005 [US-003] [FR-005]: Given a successful recovery, when evidence is emitted, then a compact canonical receipt binds the candidate commit, package SHA-256, published source, and ordered completed stages; tampering is rejected by the bootstrap evidence contract.
- AC-006 [US-002, US-003] [FR-006]: Given the roadmap runner, when GS2-01.8 executes, then only the independently pinned `bootstrap-recovery` Q7 command runs and the invocation stops at the unit boundary.

## Functional Requirements
- FR-001: The gate MUST refuse a dirty source tree, resolve its repository root and exact lowercase 40-hex HEAD, clone with local-object sharing disabled into newly created scratch storage, check out that exact revision detached, and prove the clone remains clean. (Stories: US-001; Acceptance: AC-001)
- FR-002: Recovery MUST isolate `DOTNET_CLI_HOME`, NuGet global packages, HTTP cache, plugins cache, and scratch state. Solution restore MUST be locked and use an explicit generated configuration whose only dependency source is `https://api.nuget.org/v3/index.json`; SDK library-pack fallback and caller-provided feed/command overrides are forbidden. (Stories: US-001, US-002; Acceptance: AC-002)
- FR-003: Recovery MUST build `FS.GG.Coordination.sln` in Release with warnings as errors and no restore, then run the unit and architecture projects with `--no-build --no-restore`. Every nonzero process result fails the gate immediately. (Stories: US-001; Acceptance: AC-003)
- FR-004: Recovery MUST pack `FS.GG.Coordination.Protocol` from the recovered build, install it into the repository's clean bootstrap consumer through only the candidate package directory and NuGet.org, and require the exact expected execution output. It MUST NOT publish the package. (Stories: US-001, US-002; Acceptance: AC-004)
- FR-005: Recovery MUST emit canonical compact `fsgg.coordination.bootstrap-recovery/1` JSON under ignored artifacts, binding the candidate revision, protocol package SHA-256, exact published dependency source, and ordered `clone`, `restore`, `build`, `unit-tests`, `architecture-tests`, `pack`, `install`, and `execute` stages. The hosted evidence manifest MUST index and digest this receipt. (Stories: US-003; Acceptance: AC-005)
- FR-006: The gate catalog MUST expose `bootstrap-recovery` as literal `dotnet fsi eng/bootstrap-recovery.fsx -- .`; the unit index MUST independently pin the Q7 command digest and recomputed unit-contract digest. No caller override, shell interpolation, organization setting, credential, GitHub writer, deployment, or successor behavior is permitted. (Stories: US-002, US-003; Acceptance: AC-006)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds `fsgg.coordination.bootstrap-recovery/1` evidence and the closed `bootstrap-recovery` Q7 command.
- Extends the read-only bootstrap workflow and evidence manifest with one exact-head recovery artifact.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 16-prove-bootstrap-recovery`.
