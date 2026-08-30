---
schemaVersion: 1
workId: 104-gs2-03-7-supply-chain
title: GS2-03.7 reproducibility and supply-chain checks
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-03.7 reproducibility and supply-chain checks Specification

Prose status: specified

## User Value
trust and install one exact pre-production coordination candidate from served package bytes with verifiable supply-chain evidence

## Scope
- SB-001: GS2-03.7 reproducible package identity, deterministic SBOM and attestations, one allowed pre-production publication channel, served-byte verification, and clean-consumer execution only

## Non-Goals
- SB-002: No nuget.org or stable/production publication, GitHub release or tag, deployment, production write, unrelated live GitHub mutation, runtime authority adapter, or GS2-03.8 implementation.
- SB-003: No second package build may substitute for the candidate bytes selected for publication.

## User Stories
- US-001 (P1): As a user, I can trust and install one exact pre-production coordination candidate from served package bytes with verifiable supply-chain evidence.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002] [FR-003] [FR-004] [FR-005] [FR-006] [FR-007] [FR-008]: Given an exact protected candidate and explicit pre-production authorization, when the supply-chain workflow completes, then one package operation supplies immutable bytes whose local and independently downloaded digests agree, whose SPDX SBOM and in-toto attestations are digest-bound and validated, and whose served bytes install and execute in isolated clean consumers.

## Functional Requirements
- FR-001: The workflow MUST bind the repository, exact 40-character candidate revision, prerelease package identity, commit time, source-tree state, and explicitly allowed `github-packages-candidate` channel before packaging. (Stories: US-001; Acceptance: AC-001)
- FR-002: Exactly one `dotnet pack` operation MUST create the candidate package, and every comparison, evidence document, upload, download, and consumer MUST use those bytes without repacking. (Stories: US-001; Acceptance: AC-001)
- FR-003: The candidate MUST carry a deterministic SPDX 2.3 SBOM binding package SHA-256, package entries, declared dependency closure, source revision, and generator identity; missing, stale, malformed, or substituted inputs MUST be refused. (Stories: US-001; Acceptance: AC-001)
- FR-004: The candidate MUST carry deterministic in-toto/SLSA-shaped provenance and verification attestations whose subjects name the package SHA-256 and whose predicates bind the exact build definition, source and lock inputs, builder, channel, and verification outcomes. (Stories: US-001; Acceptance: AC-001)
- FR-005: Publication MUST target only the FS-GG GitHub Packages NuGet endpoint with a unique prerelease version derived from the exact protected revision; nuget.org, stable versions, releases, tags, and deployments MUST be rejected. (Stories: US-001; Acceptance: AC-001)
- FR-006: Verification MUST independently download the served package, compare it byte-for-byte and by SHA-256 with the single local candidate, and fail closed on absence, substitution, truncation, or channel/version disagreement. (Stories: US-001; Acceptance: AC-001)
- FR-007: At least two isolated clean consumers MUST restore from the allowed channel, build without warnings, and execute the served exact package while an override route proves that the downloaded bytes, not local build outputs, supplied the package. (Stories: US-001; Acceptance: AC-001)
- FR-008: Canonical retained evidence, architecture/evidence-storage/bootstrap controls, positive tests, and independent gate inversions MUST bind the exact candidate; no generated-only green roll-up or later-unit behavior may satisfy acceptance. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 104-gs2-03-7-supply-chain`.
