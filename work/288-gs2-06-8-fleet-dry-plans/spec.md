---
schemaVersion: 1
workId: 288-gs2-06-8-fleet-dry-plans
title: Gs2 06 8 Fleet Dry Plans
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Gs2 06 8 Fleet Dry Plans Specification

Prose status: specified

## User Value
Produce an auditable byte-stable fleet dry-plan closure for all ten rostered FS-GG repositories without applying any operation.

## Scope
- SB-001: Add one qualification contract, tests, Q5 validator, documentation, live read-only evidence, candidate qualification artifacts, SDD artifacts, and an immutable post-merge GS2-06.8 acceptance receipt; exclude all production mutations, apply paths, Quint changes, publication, and GS2-07.1 work.

## Non-Goals
- SB-002: Do not change the canonical Quint protocol, publish a stable package, or inspect, prepare, or implement GS2-07.1.
- SB-003: Do not provide an apply command, mutation transport, or any code path that changes GitHub state.

## User Stories
- US-001 (P1): As a user, I can produce an auditable byte-stable fleet dry-plan closure for all ten rostered FS-GG repositories without applying any operation.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the exact accepted roadmap and GS2-06.1 through GS2-06.7 receipts, when the closure is compiled, then the exact ten-repository roster, receipt identities, source revision, default branches, observation instants, terminal pagination proofs, and pre-state fingerprints are sealed.
- AC-002 [US-001] [FR-002]: Given complete read-only observations, when a repository capability or permission differs, then one explicit supported, unsupported, unauthorized, unavailable, incomplete, unreadable, stale, indeterminate, external-observe-only, or no-op disposition is retained without inventing absence or compliance.
- AC-003 [US-001] [FR-003]: Given a supported repository target, when a dry plan is compiled twice or reparsed, then its minimal ordered operations, stable identities, least permissions, desired-state digest, preservation contract, and rollback or forward-repair intent are semantically equal and byte-identical.
- AC-004 [US-001] [FR-004]: Given a reviewed plan, when authoritative state is re-inspected, then unchanged relevant state confirms the plan and any relevant drift marks it stale, while unrelated setting drift does not create an operation.
- AC-005 [US-001] [FR-005]: Given a clean exact candidate, when qualification runs, then all eight preceding GS2-06 Q3/Q7 commands run cold before Q5 and generated plus independent mutations prove every declared control can fail.
- AC-006 [US-001] [FR-006]: Given the implementation merge is protected and verified, when acceptance is recorded, then the same issue remains continuously reserved across a markerless handoff and a second reviewed PR appends the immutable GS2-06.8 receipt before the single Done transition.

## Functional Requirements
- FR-001: Bind the exact accepted roadmap revision, registered GS2-06.8 contract, all accepted GS2-06.1 through GS2-06.7 receipts, and the reviewed ten-repository roster into one length-framed closure seal. (Stories: US-001; Acceptance: AC-001)
- FR-002: Represent every repository observation with exact identity/default branch, complete terminal pagination, observation instant, pre-state fingerprint, permission evidence, and one explicit disposition; unreadable or incomplete evidence must never become absence or compliance. (Stories: US-001; Acceptance: AC-002)
- FR-003: Compile pure canonical plans whose operations are minimal, deterministically ordered, stably identified, least-privileged, bound to exact pre-state and desired-state digests, preserve unrelated settings, and carry rollback or forward-repair intent; serialization must reparse without semantic loss and exact replay must be byte-identical. (Stories: US-001; Acceptance: AC-003)
- FR-004: Preserve independent plan-review evidence separately from authoritative re-inspection; refuse or mark stale every plan whose relevant pre-state changes while allowing explicitly unrelated observation change. (Stories: US-001; Acceptance: AC-004)
- FR-005: Retain comprehensive live read-only fleet evidence and generated plus independently authored adversarial controls for authority, roster, completeness, pagination, identity, time, state, ordering, permissions, every disposition, review, re-inspection, serialization, replay, omission, Quint preservation, and no-mutation behavior; run all nine registered gates in declared order. (Stories: US-001; Acceptance: AC-005)
- FR-006: Expose no apply or GitHub mutation path and complete acceptance through one owning issue with protected implementation merge, verified markerless claim handoff, immutable receipt PR, protected receipt merge, and one terminal Done transition. (Stories: US-001; Acceptance: AC-006)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds the public qualification-contract module `GitHubFleetDryPlanQualification`, its canonical JSON artifact grammar, and the registered `eng/validate-github-fleet-dry-plans.fsx` Q5 gate.
- Adds retained `evidence/github-substrate-v2/gs2-06-8` provider artifacts and the append-only `evidence/github-substrate-v2/accepted/GS2-06.8.json` receipt.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 288-gs2-06-8-fleet-dry-plans`.
