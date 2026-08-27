---
schemaVersion: 1
workId: 8-create-github-substrate-v2-work-skill
title: Create Github Substrate V2 Work Skill
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Create Github Substrate V2 Work Skill Specification

Prose status: specified

## User Value
Maintainers and agents can execute exactly one GitHub Substrate v2 roadmap unit from accepted, revision-bound evidence.

## Scope
- SB-001: Add the repository-owned github-substrate-v2-work skill, a deterministic local roadmap command, typed bootstrap unit/index inputs, manifest output, Q-gate runner, documentation, and tests.

## Non-Goals
- SB-002: Do not schedule from mutable Project state, claim work, mutate GitHub settings, run a successor unit, import v1 completion authority, deploy, or grant production writes.
- SB-003: Do not implement the general GS2-03 qualification system or GS2-01.7 evidence store; this unit supplies the bounded bootstrap command and compact contracts they will extend.

## User Stories
- US-001 (P1): As a maintainer, I can inspect a stable unit contract and see its owner, prerequisites, permission ceiling, exit gate, Q gates, and current evidence disposition.
- US-002 (P1): As a worker, I can prove prerequisites from accepted fingerprint-bound receipts and create a deterministic exact-candidate evidence manifest before running qualification.
- US-003 (P1): As an independent reviewer, I can prove the command runs only declared gates for the selected unit and refuses every attempt to cross its boundary.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: Given the versioned bootstrap index, when a known unit is inspected, then the command emits its complete deterministic contract and verifies the index is bound to the canonical roadmap revision and digest.
- AC-002 [US-002] [FR-002] [FR-003]: Given every declared prerequisite has one current accepted receipt bound to its exact source and artifact fingerprints, when prerequisites are checked, then the selected unit is ready; absent, rejected, stale, contradictory, duplicate, malformed, or mismatched receipts are distinct refusals.
- AC-003 [US-002] [FR-004]: Given a ready unit and exact candidate tree, when a manifest is created, then canonical bytes bind the unit, roadmap/index, candidate revision and tree, prerequisite receipts, gate commands, artifact digests, generator identity, and creation time without claiming acceptance.
- AC-004 [US-003] [FR-005]: Given the exact manifest and declared gate inventory, when gates run, then only the selected unit's declared commands execute, each result and artifact is fingerprinted, and any absent, unknown, duplicated, failing, or changed gate/candidate refuses qualification.
- AC-005 [US-003] [FR-006]: Given a ready successor, Project status, prose comment, path escape, or command override, when execution is attempted, then the command retains the selected unit ID and refuses successor execution, external scheduling authority, and writes outside its declared local evidence boundary.

## Functional Requirements
- FR-001: A concise `github-substrate-v2-work` skill must direct one named unit through inspect, prerequisite check, manifest creation, relevant Q gates, evidence recording, and an explicit stop at its exit gate. (Stories: US-001, US-003; Acceptance: AC-001, AC-005)
- FR-002: The deterministic command must consume a versioned unit index bound to the canonical roadmap revision and SHA-256, use stable unit IDs, and reject unknown, duplicate, incomplete, or roadmap-mismatched definitions. (Stories: US-001, US-002; Acceptance: AC-001, AC-002)
- FR-003: Prerequisite readiness must derive only from a complete typed set of accepted receipts whose unit ID, source revision, artifact fingerprints, acceptance state, and receipt digest validate; mutable Project state and prose must confer no authority. (Stories: US-002, US-003; Acceptance: AC-002, AC-005)
- FR-004: Manifest creation must be deterministic apart from an explicit ISO-8601 creation time and must bind the selected unit, roadmap/index, exact candidate commit and tree, prerequisite receipt digests, declared gate commands, artifact fingerprints, and generator identity without claiming qualification or acceptance. (Stories: US-002; Acceptance: AC-003)
- FR-005: Gate execution must use only a closed, versioned command inventory for the selected unit's declared Q gates, reject overrides and changed inputs, stop on failure, and record command identity, exit status, and output/artifact digests. (Stories: US-002, US-003; Acceptance: AC-004)
- FR-006: Every operation must preserve one selected unit boundary, confine writes to an explicit local output path, reject traversal/symlink escape and successor requests, and never schedule, claim, change GitHub settings, deploy, or grant production authority. (Stories: US-003; Acceptance: AC-005)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds a repository-owned agent skill and a local deterministic command/JSON contract for later GitHub Substrate v2 roadmap workers; it adds no remote mutation authority.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 8-create-github-substrate-v2-work-skill`.
