---
schemaVersion: 1
workId: 254-release-hardening
title: Release Hardening
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Release Hardening Specification

Prose status: specified

## User Value
Release execution can be compiled into a sealed plan that preserves OIDC and dual-feed recovery while proving every hardening obligation.

## Scope
- SB-001: Repository-local pure release-hardening contract, validation, tests, retained evidence, and no production mutation or successor-unit work.

## Non-Goals
- SB-002: Do not mutate or publish any production setting, workflow, release, tag, package, feed, environment, deployment, or stable channel.
- SB-003: Do not inspect, prepare, or implement GS2-06.7.

## User Stories
- US-001 (P1): As a release operator, I can inspect one deterministic plan proving that release identity, artifacts, publication, recovery, and public availability remain coherent.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the exact roadmap, prerequisite receipt, and complete retained release corpus, when the Q3 compiler runs, then it returns one sealed plan with protected OIDC identity, immutable release surfaces, one-pack artifact identity, ordered dual-feed recovery, dependency controls, and anonymous public-byte verification.

## Functional Requirements
- FR-001: The exact GS2-06.6 Q3 gate passes only for one protected OIDC release plan with immutable tags and releases, one-pack byte identity, SBOM and attestation binding, dependency controls, dual-feed recovery, and public-download verification. (Stories: US-001; Acceptance: AC-001)

## Ambiguities
- AMB-001: Whether release hardening applies production mutations in this unit or compiles the mutation-free contract first.

## Public Or Tool-Facing Impact
- Adds a public F# qualification signature and one exact Q3 roadmap gate command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 254-release-hardening`.
