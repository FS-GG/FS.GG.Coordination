---
schemaVersion: 1
workId: gs2-04-8-actions-release-feed-adapter
title: Gs2 04 8 Actions Release Feed Adapter
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.8 Actions/Release/Feed Adapter Specification

Prose status: specified

## User Value
FS.GG.Coordination can deterministically audit GitHub Actions, checks, releases, attestations, packages, feeds, and public served bytes without converting incomplete history, missing permission, stale metadata, upload acceptance, or an HTTP response into false merge authority, provenance, or durable availability.

## Scope
- SB-001: Repository-local pure-first Q3 typed adapter, canonical complete observation codecs, provenance and availability validation, stable fingerprints, tests, complete fixtures, and retained evidence.
- SB-002: Cover Actions workflows/runs/attempts/jobs, check suites/runs, commits and merge-group heads, releases/tags/assets, attestations, packages/versions/feeds, authenticated retrieval, redirects, and anonymous served bytes.

## Non-Goals
- SB-003: No live GitHub write, workflow dispatch/rerun/cancellation/deletion, upload, package or release publication, production credential, deployment, stable release, or Q4 sandbox claim.
- SB-004: Do not implement comprehensive GS2-04 closure, GS2-04.9, or any successor unit.

## User Stories
- US-001 (P1): As an operator or auditor, I can observe complete Actions and check histories with exact attempt and subject identities and without treating check state as merge authority.
- US-002 (P1): As a release auditor, I can distinguish immutable, deleted, tampered, expired, incomplete, and unavailable release, asset, attestation, package, and feed evidence.
- US-003 (P1): As a consumer, I can prove what authenticated or anonymous bytes were actually served, including redirects and digests, without mistaking upload acceptance or metadata for delivery.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given Actions and check observations, when normalized, then exact repository, workflow, run, attempt, job, suite, run, commit, and merge-group identities remain bound and contradictory subjects refuse authority.
- AC-002 [US-001] [FR-002]: Given paginated run and check histories, when validated, then every page is complete and requested, queued, in-progress, completed, skipped, cancelled, stale, neutral, timed-out, and action-required outcomes remain distinct.
- AC-003 [US-002] [FR-003]: Given release, tag, and asset observations, when validated, then immutable identity and content facts remain distinct from deletion, expiry, tampering, unavailability, and unreadability.
- AC-004 [US-002] [FR-004]: Given an artifact attestation or package/feed observation, when validated, then subject, predicate, digest, package, version, repository, and feed identities agree exactly or the evidence is refused.
- AC-005 [US-003] [FR-005]: Given upload response, durable metadata, authenticated retrieval, and public download evidence, when classified, then each stage remains separate and only served bytes establish served availability and content digest.
- AC-006 [US-003] [FR-006]: Given redirects and response bodies, when observed, then redirect chain, final content identity, authentication context, byte count, and SHA-256 bind the evidence without secrets.
- AC-007 [US-001] [US-002] [FR-007]: Given unauthorized, unavailable, incomplete, expired, deleted, unreadable, or stale evidence, when normalized, then the exact outcome is preserved and absence, provenance, mergeability, or availability is not invented.
- AC-008 [US-001] [US-002] [US-003] [FR-008]: Given the registered Q3 gate and independent fixture suite, when each required mutant is applied, then it is red while the exact unmutated candidate remains green and performs no live write.

## Functional Requirements
- FR-001: The adapter MUST bind repository node/name/owner, workflow id/path/ref/SHA, run id/number/attempt, job id, check-suite id, check-run id, commit SHA, and merge-group head SHA; contradictory identities MUST refuse and check conclusions MUST NOT imply merge authority. (covers AC-001)
- FR-002: The adapter MUST require explicit endpoint and pagination completeness, canonicalize complete histories, and preserve requested, queued, in-progress, completed, skipped, cancelled, stale, neutral, timed-out, and action-required as distinct closed outcomes. Reruns MUST remain separate attempts. (covers AC-002)
- FR-003: The adapter MUST bind release id/tag/target/name/draft/prerelease/immutability, asset id/name/size/digest/content type, and tag identity; it MUST preserve immutable, deleted, tampered, expired, unavailable, incomplete, and unreadable distinctions without inventing an asset or release. (covers AC-003)
- FR-004: The adapter MUST validate attestation subject names and digests, predicate type and identity, and package owner/name/version/feed/repository coordinates; any mismatch, duplicate identity, unsupported digest, or incomplete provenance MUST refuse. (covers AC-004)
- FR-005: The adapter MUST model upload acceptance, durable release-asset metadata, authenticated package/feed retrieval, redirect resolution, and anonymous public served-download observation as separate evidence stages. Upload acceptance or metadata MUST NOT prove retrieval or served availability. (covers AC-005)
- FR-006: The adapter MUST hash exact served bytes with SHA-256 and bind request URI, redirect chain, final URI, status, content type, length, authentication class, and content identity; credentials and authorization values MUST never enter canonical output or diagnostics. (covers AC-006)
- FR-007: Every surface MUST preserve supported, unauthorized, unavailable, incomplete, expired, deleted, unreadable, and stale outcomes as applicable, bind a stable canonical fingerprint, and refuse inferred absence, provenance, merge authority, or availability. (covers AC-007)
- FR-008: The registered `github-actions-release-feed-contract` Q3 validator and independent tests MUST cover positive cases plus run-attempt, rerun, check-suite, merge-group, pagination, immutable-release, asset-deletion, attestation-subject, package-version, authenticated-feed, public-download, redirect, byte-digest, upload-response, unauthorized, unavailable, incomplete, and stale mutations with exact-candidate evidence and no live writes. (covers AC-008)

## Ambiguities
- AMB-001: Canonical ordering must be independent of GitHub response and pagination order while preserving run-attempt chronology.
- AMB-002: A stable merge-group identity must bind head SHA and constituent commits without treating a successful check as current merge permission.
- AMB-003: Durable availability requires an explicit evidence hierarchy across upload response, metadata, authenticated retrieval, redirect resolution, and anonymous bytes.
- AMB-004: Deleted or expired artifacts may retain trustworthy historical identity while no longer proving current availability.

## Public Or Tool-Facing Impact
- Add a public `.fsi` Actions/release/feed adapter surface in `FS.GG.Coordination.GitHub`.
- Add one repository-owned Q3 validator and retained typed result artifact; do not change the registered command identity.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-8-actions-release-feed-adapter`.
