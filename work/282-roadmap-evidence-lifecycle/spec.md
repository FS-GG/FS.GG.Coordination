---
schemaVersion: 1
workId: 282-roadmap-evidence-lifecycle
title: Harden roadmap evidence and receipt lifecycle
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Harden roadmap evidence and receipt lifecycle Specification

Prose status: specified

## User Value
GS2 roadmap units reject local-only evidence and complete one owning item only after their append-only receipt lands.

## Scope
- SB-001: The github-substrate-v2-work skill, its validator, and architecture coverage; no runtime API, workflow, or accepted receipt changes.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As the accountable roadmap owner, I can reject evidence that exists only in an author's dirty or tool-version-dependent environment.
- US-002 (P1): As the accountable roadmap owner, I can distinguish an actual hosted result from local emulation of a hosted route.
- US-003 (P1): As a board operator, I can follow one roadmap issue through implementation and post-merge receipt acceptance without creating receipt-only rows.
- US-004 (P1): As a reviewer, I can prove descendant-sensitive authority survives harmless advances and rejects a relevant mutation.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002] [FR-003]: Given a unit declares provider evidence, when acceptance is prepared, then an isolated checkout of the exact candidate contains every declared input, uses the contract's canonical tool version, and two consecutive verification runs are coherent no-change fixed points.
- AC-002 [US-002] [FR-004]: Given a unit claims hosted workflow behavior, when acceptance is prepared, then an exact-head hosted run and retained typed artifact are bound; a local or detached run cannot satisfy the hosted field.
- AC-003 [US-003] [FR-005] [FR-006]: Given an acceptance or repair receipt needs protected-merge facts, when implementation lands, then the owning issue remains open and continuously reserved while its claim generation is rotated through a verified markerless `In progress` state for the receipt PR, and receives one terminal completion only after that receipt lands.
- AC-004 [US-004] [FR-007]: Given authority is required to survive protected advancement, when it is reviewed, then the implementation merge, receipt descendant, unrelated descendant, and relevant-mutation refusal are all exercised.
- AC-005 [US-001] [US-002] [US-003] [US-004] [FR-008]: Given any mandatory safety clause is removed from the skill, when the registered validator runs, then it fails with a specific missing-contract diagnostic.

## Functional Requirements
- FR-001: The procedure MUST build provider acceptance evidence from a fresh archive or isolated Git checkout at the exact candidate revision, with no ignored or untracked carry-over. (Stories: US-001; Acceptance: AC-001)
- FR-002: The procedure MUST require every declared provider artifact to be Git-tracked, present, and byte-bound to its declared digest. (Stories: US-001; Acceptance: AC-001)
- FR-003: The procedure MUST install the provider contract's canonical tool version and require two consecutive coherent no-change verification runs before acceptance. (Stories: US-001; Acceptance: AC-001)
- FR-004: A hosted-behavior claim MUST bind an actual exact-head hosted run and retained typed decision artifact; local and detached executions MAY support diagnosis but MUST NOT satisfy hosted evidence. (Stories: US-002; Acceptance: AC-002)
- FR-005: A unit requiring a post-merge acceptance or repair receipt MUST keep its owning issue open and continuously reserved after the implementation merge, rotate its claim generation only through a verified markerless `In progress` state, and use a second PR on that same issue for the receipt. (Stories: US-003; Acceptance: AC-003)
- FR-006: The procedure MUST prohibit receipt-only board items and MUST stamp Done exactly once, after the receipt PR and protected verification complete. (Stories: US-003; Acceptance: AC-003)
- FR-007: Descendant-sensitive authority MUST be tested at the implementation merge, its receipt descendant, an unrelated descendant, and a descendant with a relevant unrefreshed mutation that must refuse. (Stories: US-004; Acceptance: AC-004)
- FR-008: The executable roadmap-skill validator MUST require each clause above, and architecture tests MUST prove deletion or substitution of each required token turns the gate red. (Stories: US-001, US-002, US-003, US-004; Acceptance: AC-005)

## Ambiguities
- AMB-001: Whether the receipt phase should use a new issue or the existing owning issue.
- AMB-002: Whether local workflow execution may ever be labeled hosted.
- AMB-003: Whether one successful canonical verification is sufficient for a provider fixed point.

## Public Or Tool-Facing Impact
- This specification is an SDD lifecycle artifact and command-report contract input.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 282-roadmap-evidence-lifecycle`.
