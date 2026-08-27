---
schemaVersion: 1
workId: 26-canonical-quint-protocol
title: Canonical Quint Protocol
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Canonical Quint Protocol Specification

Prose status: specified

## User Value
Reviewers and qualification tooling receive one canonical, readable, executable coordination protocol whose generated identities are reproducible.

## Scope
- SB-001: GS2-02.1 only: the literate Quint source, published-profile extraction boundary, compiled identity, executable unit index, and Q1/pure-Q2 gates.

## Non-Goals
- SB-002: Do not implement GS2-02.2 through GS2-02.11 authority, observation, lifecycle, relation, stream, mutation, plan, desired-state, compiled-output, or identity-proof semantics beyond the vocabulary and seams required by GS2-02.1.
- SB-003: Do not create a hosted runtime, environment, deployment identity, secret, ingress, webhook, event subscription, package publication, or production mutation authority.
- SB-004: Do not amend the accepted roadmap bytes, rewrite accepted GS2-01 receipts, recreate the FS.GG.SDD extractor/profile/toolchain, or introduce a parallel F# protocol AST.

## User Stories
- US-001 (P1): As a protocol reviewer, I can read one Markdown source and inspect its named executable Quint blocks without reconciling a second semantic source.
- US-002 (P1): As qualification tooling, I can extract and identify the exact protocol deterministically through the pinned published FS.GG.SDD profile.
- US-003 (P1): As a roadmap worker, I can inspect GS2-02.1 only after every accepted bootstrap prerequisite and exact gate contract is current.

## Acceptance Scenarios
- AC-001 [US-001] [FR-002]: Given the canonical Markdown source, when the published profile extracts it, then every required named block is present exactly once and the extracted Quint typechecks.
- AC-002 [US-002] [FR-003] [FR-004]: Given identical accepted source, profile, and toolchain identities, when extraction and compilation run twice, then both extracted bytes and compiled-contract identities are byte-identical.
- AC-003 [US-003] [FR-001] [FR-008]: Given the pinned roadmap and accepted GS2-01 receipts, when roadmap-work inspects and checks GS2-02.1, then it reports the exact owner, prerequisites, permission ceiling, Q1/pure-Q2 gates, and ready status.
- AC-004 [US-001] [US-002] [FR-005]: Given hidden prose semantics, an independently edited extracted artifact, a wrong profile/tool identity, or a parallel F# protocol AST, when qualification runs, then it fails closed with a specific finding.
- AC-005 [US-002] [FR-006]: Given each new or changed qualification gate, when its bounded fixture is inverted, then the gate observes red before the correct candidate observes green.
- AC-006 [US-001] [US-003] [FR-007]: Given the GS2-02.1 candidate, when paths and outputs are inventoried, then no successor implementation, runtime, deployment, secret, subscription, package publication, or production-write artifact exists.

## Functional Requirements
- FR-001: The executable unit index must preserve the pinned roadmap and accepted GS2-01.1 through GS2-01.8 receipts, omit the rejected conditional GS2-01.9 provisioning branch, and register one exact GS2-02.1 contract with no successor output. (Stories: US-003; Acceptance: AC-003)
- FR-002: One reviewer-oriented Markdown source must contain deterministically extractable, uniquely named Quint blocks that establish the GS2-02.1 vocabulary for subjects, authorities, codecs, commands, events, mutations, projections, observation plans, settings profiles, evidence obligations, and version identities. (Stories: US-001; Acceptance: AC-001)
- FR-003: Extraction, normalization, and compiled-contract generation must consume the pinned published FS.GG.SDD 1.5.0 `fsgg-quint-profile/2` and exact tool identity without a source-project reference, checkout-relative dependency, local extractor, or profile fork. Profile 1 must remain frozen and must not be misrepresented as a consumer-model profile. (Stories: US-002; Acceptance: AC-002)
- FR-004: Identical accepted source, profile, and toolchain inputs must produce byte-identical extracted Quint and compiled-contract identities with explicit source, profile, tool, schema, and behavioral digests. (Stories: US-002; Acceptance: AC-002)
- FR-005: Qualification must reject prose-hidden semantics, duplicate or missing named blocks, independently edited extracted output, wrong source/profile/tool/schema identity, path traversal or untracked evidence, and a parallel F# protocol AST. (Stories: US-001, US-002; Acceptance: AC-004)
- FR-006: The closed GS2-02.1 gate must run Q1 and the pure portion of Q2 plus independently authored bounded negative controls, and every new or modified gate must carry observed inversion-red evidence. (Stories: US-002; Acceptance: AC-005)
- FR-007: The candidate must stay within GS2-02.1 and create no GS2-02.2 successor semantics, hosted runtime, environment, deployment identity, secret, ingress, webhook, event subscription, package publication, or production mutation authority. (Stories: US-001, US-003; Acceptance: AC-006)
- FR-008: Roadmap-work inspect, prerequisites, manifest, and gates must bind the tracked unit index, pinned roadmap digest, accepted prerequisite receipts, clean committed candidate, declared artifacts, ordered command contracts, and exact candidate head. (Stories: US-003; Acceptance: AC-003)

## Ambiguities
- AMB-001: The canonical literate source path and the exact published-profile API used to extract and compile it must be selected from the installed `FS.GG.SDD.Artifacts` surface.
- AMB-002: GS2-02.1 must name all roadmap vocabulary while leaving detailed GS2-02.2 through GS2-02.11 semantics unimplemented; the minimum executable baseline needs an explicit boundary.
- AMB-003: Generated extracted Quint and compiled contracts need a storage rule that preserves deterministic verification without creating a second editable source.

## Public Or Tool-Facing Impact
- Adds the tracked GS2-02.1 roadmap-work unit and gate contract.
- Adds the canonical literate protocol source and versioned compiled-identity/evidence contract consumed by later roadmap units.
- Does not add a production command, network permission, runtime, or mutation surface.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 26-canonical-quint-protocol`.
