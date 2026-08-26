---
schemaVersion: 1
workId: 4-pin-published-quint-kernel
title: Pin Published Quint Kernel
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Pin Published Quint Kernel Specification

Prose status: specified

## User Value
FS.GG.Coordination restores the accepted Quint-capable kernel from an immutable published package and can prove exactly which semantic bundle it consumes.

## Scope
- SB-001: Pin and verify the published FS.GG.SDD artifact as a consumer-only dependency; no producer machinery or protocol semantics.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a coordination implementer, I can restore one immutable Quint-capable kernel and prove its semantic identity before compiling against it.
- US-002 (P1): As an independent reviewer, I can see mechanical failures for package, digest, and source-boundary drift rather than trusting repository prose.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given a clean checkout using a supported read feed, when locked restore and qualification run, then `FS.GG.SDD.Artifacts` resolves exactly at `1.4.0` and its embedded Q1 identity manifest and bundle fingerprint equal the accepted constants.
- AC-002 [US-002] [FR-003]: Given any altered version, expected identity, expected bundle digest, source-project reference, or checkout-relative package source, when qualification runs, then it fails with the matching bounded diagnostic.
- AC-003 [US-001] [US-002] [FR-004]: Given the completed consumer pin, when repository contents and dependency edges are inspected, then no local extractor, Quint profile, compiled-contract generator, generic ITF machinery, protocol semantics, or mutation authority has been introduced.

## Functional Requirements
- FR-001: The repository MUST centrally pin and directly consume `FS.GG.SDD.Artifacts` version `1.4.0` only from a supported configured read feed, with the exact dependency retained in the lock file. (Stories: US-001; Acceptance: AC-001)
- FR-002: Qualification MUST verify the package identity plus the embedded `fsgg.quint.q2-toolchain-identity/1` manifest, accepted `fsgg-quint-profile/1` identity, and exact published bundle fingerprint before exposing the compiled-contract boundary. (Stories: US-001, US-002; Acceptance: AC-001)
- FR-003: Independent negative controls MUST reject an altered package version, altered expected identity or digest, source-project reference, checkout-relative package source, and locally recreated producer machinery. (Stories: US-002; Acceptance: AC-002)
- FR-004: The change MUST remain consumer-only and MUST NOT add an extractor, profile definition, compiled-contract generator, generic ITF machinery, protocol semantics, GitHub mutation authority, or deployment behavior. (Stories: US-001, US-002; Acceptance: AC-003)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- The qualification assembly gains a package-identity contract for downstream GS2 work; it does not gain behavioral protocol authority.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 4-pin-published-quint-kernel`.
