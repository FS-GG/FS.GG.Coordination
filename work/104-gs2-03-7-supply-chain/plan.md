---
schemaVersion: 1
workId: 104-gs2-03-7-supply-chain
title: GS2-03.7 reproducibility and supply-chain checks
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/104-gs2-03-7-supply-chain/spec.md
sourceClarifications: work/104-gs2-03-7-supply-chain/clarifications.md
sourceChecklist: work/104-gs2-03-7-supply-chain/checklist.md
publicOrToolFacingImpact: true
---

# GS2-03.7 reproducibility and supply-chain checks Plan

Prose status: planned

## Source Snapshot
- spec: work/104-gs2-03-7-supply-chain/spec.md sha256:2b7f429d293adea4c226f83d9719d3b3fcc1f99f88518366d05447c1ac015862 schemaVersion:1
- clarifications: work/104-gs2-03-7-supply-chain/clarifications.md sha256:cc09ac8459ba709aba3f5d333ddd493e23c5c37c7630dd1c45ba4b3a7206272e schemaVersion:1
- checklist: work/104-gs2-03-7-supply-chain/checklist.md sha256:07b69407280be71b10e08e300a14fcc8b3caa9a29ed6a471a6eeb2b25a8ef7f5 schemaVersion:1

## Plan Scope
- Work item 104-gs2-03-7-supply-chain is planned from the current specification, clarification, and checklist facts.
- Requirement count: 8.
- Clarification decision count: 0.
- Checklist result count: 8.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add one F# script authority that accepts only a clean exact HEAD, derives the prerelease version `0.0.0-gs2-03-7.<first12sha>`, fixes evidence time to the commit instant, and names `github-packages-candidate` as the sole channel.
- PD-002 [AC-001] [FR-002] complete: The script validates the exact clean candidate, projects only that commit's tracked bytes through `git archive`, disables build-server and shared-compilation reuse, and maps projected source paths deterministically. It emits Portable PDBs and a canonical `.snupkg`; because the F# compiler sizes the PE debug-data layout from the pre-PathMap intermediate PDB path, every build uses the candidate-bound fixed intermediate `/tmp/fsgg-gs2-03-7-<sha>` and refuses an occupied path rather than sharing it. Independent unequal source/output roots then produce byte-identical DLL, PDB, `.nupkg`, and `.snupkg` identities. One counted `dotnet pack` creates both archives, each archive is canonicalized without repacking, and package, symbol, PDB, and assembly SHA-256 values remain immutable inputs for evidence, upload, served-byte comparison, and consumer verification.
- PD-003 [AC-001] [FR-003] complete: Generate canonical compact SPDX 2.3 JSON from package zip entries, the package nuspec, and committed lock files; sort every collection, bind SHA-256 values and generator/schema identities, and validate by independently re-reading the source package and lock inputs.
- PD-004 [AC-001] [FR-004] complete: Generate canonical in-toto Statement v1 provenance plus a separate verification attestation with SLSA v1 predicate identity; both bind the candidate package, symbol package, portable PDB, and installed assembly digests together with the source revision, build parameters, lock digests, builder, channel, SBOM digest, and ordered verification claims.
- PD-005 [AC-001] [FR-005] complete: Add an explicit manual post-merge GitHub Actions workflow with `packages: write`, exact expected-SHA input, protected-main ancestry verification, prerelease-only version validation, and the sole push endpoint `https://nuget.pkg.github.com/FS-GG/index.json`; an executable validator reads the production workflow and refuses extra, disabled, unreadable, bypassed, or unprotected publication routes, and publication remains a post-merge delivery obligation.
- PD-006 [AC-001] [FR-006] complete: After push, fetch the package through the authenticated GitHub Packages download surface into a second directory, parse and require the exact owner/download/package/version/file route bound to the prepared channel and source, reject query/fragment/extra-segment smuggling, retry exhaustion, or any digest/length/version mismatch, and compare the local and served files byte-for-byte before evidence completion.
- PD-007 [AC-001] [FR-007] complete: Copy two minimal fixture consumers into isolated directories with empty DOTNET/NuGet homes, restore exclusively through a source-mapped GitHub Packages plus nuget.org configuration, assert the cached package digest derives from the independently downloaded bytes, hash the installed Protocol assembly against the prepared package entry, and execute both consumers against the candidate API.
- PD-008 [AC-001] [FR-008] complete: Add architecture tests for schema, tracked-source projection with ignored `bin` and `obj` exclusion, container determinism, one-pack accounting, protected ancestry, semantic workflow publication policy, byte substitution, SBOM and attestation tamper, and clean-consumer configuration; retain the exact architecture TRX beneath the tracked work package so clean clones can independently validate every evidence entry; the production-workflow validator carries positive, channel-substitution, bypass, unreadable-input, and unprotected-route controls without changing the closed GS2 gate catalog.

## Contract Impact
- PC-001 [PD-001] supply-chain contract: Add versioned `fsgg.coordination.supply-chain-candidate/2`, SPDX 2.3, and in-toto Statement v1 evidence plus a manual candidate workflow; version 2 makes symbol-package, Portable-PDB, and installed-assembly identities mandatory. This is additive qualification surface and creates no runtime API.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Architecture tests and workflow evidence must execute local prepare/verify, prove exactly one pack invocation, exercise every fail-closed inversion, publish the exact protected-main bytes once, independently download them, and run two clean consumers before host completion.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] additive: The new candidate package and evidence schemas are prerelease-only and additive; existing bootstrap package identity and stable/public release topology remain unchanged, and unsupported schemas or channels refuse before publication.

## Generated View Impact
- GV-001 [PD-001] workModel: Refresh the SDD work model and equivalent Claude/Codex projections; ignored workflow evidence binds the exact candidate while the compact ship verdict remains the only committed readiness view.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 104-gs2-03-7-supply-chain`.
