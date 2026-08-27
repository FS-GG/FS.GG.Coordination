---
schemaVersion: 1
workId: 6-establish-custom-bootstrap-ci
title: Establish Custom Bootstrap CI
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Establish Custom Bootstrap CI Specification

Prose status: specified

## User Value
Maintainers can trust one exact bootstrap candidate because every required build, test, policy, package install, and evidence check is visible and independently failable.

## Scope
- SB-001: Add bootstrap qualification workflows, validation tools, retained evidence, and adversarial controls to FS.GG.Coordination.

## Non-Goals
- SB-002: Do not publish or release packages, deploy a runtime, contact live GitHub authority, or add production mutation permission.
- SB-003: Do not import v1 coordination review, delivery, done, or completion gates.

## User Stories
- US-001 (P1): As a maintainer, I can see each bootstrap qualification concern as a named required job bound to the exact candidate.
- US-002 (P1): As an independent reviewer, I can prove every gate class rejects a deliberately broken subject instead of accepting an absent or self-reported result.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given a clean checkout and empty package/output state, when bootstrap CI runs, then named locked-build and test jobs pass against the same exact candidate and retain machine-readable test results.
- AC-002 [US-001] [US-002] [FR-003] [FR-004]: Given the accepted source and package boundaries, when dependency/security and package-install jobs run, then the locked graph is policy-clean and a locally packed bootstrap library installs in a clean consumer using only declared feeds.
- AC-003 [US-001] [US-002] [FR-005]: Given all prerequisite jobs passed, when the final evidence job runs, then it emits and validates a compact manifest binding the exact candidate, required gate identities, commands, and artifact digests.
- AC-004 [US-002] [FR-003] [FR-005] [FR-006]: Given an absent gate, stale head, malformed digest, tampered artifact, prohibited dependency/source, or forbidden v1/deployment authority token, when its matching control runs, then qualification fails with a bounded diagnostic.

## Functional Requirements
- FR-001: A clean checkout with an empty package cache and output tree must pass a named deterministic locked build gate. (Stories: US-001; Acceptance: AC-001)
- FR-002: Named compiler, unit, and architecture test gates must pass and retain exact-candidate results. (Stories: US-001; Acceptance: AC-001)
- FR-003: Named dependency and security policy gates must fail for a prohibited dependency or unsafe repository input. (Stories: US-001, US-002; Acceptance: AC-002, AC-004)
- FR-004: A package and clean-consumer install smoke gate must prove the bootstrap outputs can be packed and consumed without checkout-relative inputs. (Stories: US-001, US-002; Acceptance: AC-002)
- FR-005: An evidence-manifest gate must bind required gate identities, candidate revision, commands, and compact artifact digests and must reject absent, stale, malformed, or tampered evidence. (Stories: US-001, US-002; Acceptance: AC-003, AC-004)
- FR-006: CI must import no v1 review, delivery, done, or completion gate and must add no release, deployment, live GitHub write, or production mutation authority. (Stories: US-002; Acceptance: AC-004)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Pull requests and main pushes gain explicit bootstrap qualification jobs and a compact downloadable evidence manifest; no production runtime or coordination authority changes.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 6-establish-custom-bootstrap-ci`.
