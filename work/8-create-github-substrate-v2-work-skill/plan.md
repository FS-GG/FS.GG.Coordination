---
schemaVersion: 1
workId: 8-create-github-substrate-v2-work-skill
title: Create Github Substrate V2 Work Skill
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/8-create-github-substrate-v2-work-skill/spec.md
sourceClarifications: work/8-create-github-substrate-v2-work-skill/clarifications.md
sourceChecklist: work/8-create-github-substrate-v2-work-skill/checklist.md
publicOrToolFacingImpact: true
---

# Create Github Substrate V2 Work Skill Plan

Prose status: planned

## Source Snapshot
- spec: work/8-create-github-substrate-v2-work-skill/spec.md sha256:fdf82e6c56f2789fce51d1a18b868ea7a5f952c60b1433e3d157d99181c9a67e schemaVersion:1
- clarifications: work/8-create-github-substrate-v2-work-skill/clarifications.md sha256:35502fa95267686186adc0d4a936604304bbce1afc87856d168a82a1cfe601dc schemaVersion:1
- checklist: work/8-create-github-substrate-v2-work-skill/checklist.md sha256:ae1cfea708903939f4059fb0becefae9d183b5b185a4c295efbade310c8df812 schemaVersion:1

## Plan Scope
- Work item 8-create-github-substrate-v2-work-skill is planned from the current specification, clarification, and checklist facts.
- Requirement count: 6.
- Clarification decision count: 6.
- Checklist result count: 6.

## Plan Decisions
- PD-001 [AC-001] [AC-005] [FR-001] [DEC-006] complete: Add a concise repository skill that names the five operations, requires an explicit unit, uses the compiled command for decisions, and always ends with the selected exit-gate stop report.
- PD-002 [AC-001] [AC-002] [FR-002] [DEC-001] [DEC-002] complete: Define strict JSON codecs and pure validation for one pinned roadmap/index plus unique unit definitions, rejecting unknown members, duplicate identities, malformed hashes/revisions, incomplete contracts, and roadmap byte mismatch.
- PD-003 [AC-002] [AC-005] [FR-003] [DEC-002] complete: Validate a complete receipt set by canonical digest and exact unit/source/artifact identities; derive readiness solely from one accepted receipt per prerequisite and return typed refusal codes for every other state.
- PD-004 [AC-003] [FR-004] [DEC-003] complete: Canonically serialize a candidate-state manifest from explicit UTC time, git commit/tree, validated prerequisite digests, closed gate identities, and regular-file artifact SHA-256 values beneath the repository root.
- PD-005 [AC-004] [FR-005] [DEC-004] complete: Plan and run only executable-plus-literal-argument commands from the validated closed gate catalog, revalidate candidate and artifact bindings around execution, stop at first failure, and emit deterministic result digests.
- PD-006 [AC-005] [FR-006] [DEC-005] [DEC-006] complete: Expose `roadmap-work inspect|prerequisites|manifest|gates` through the existing CLI with explicit paths/unit/output arguments, reject traversal and symlink escapes, and include no network or GitHub mutation implementation.

## Contract Impact
- PC-001 [PD-001] skill contract: `.agents/skills/github-substrate-v2-work/SKILL.md` is the discoverable one-unit workflow and permission ceiling.
- PC-002 [PD-002] [PD-003] roadmap contracts: `fsgg.coordination.roadmap-index/1` and `fsgg.coordination.unit-acceptance/1` are strict JSON inputs with canonical SHA-256 self-binding for receipts.
- PC-003 [PD-004] evidence contract: `fsgg.coordination.unit-evidence/1` is canonical JSON in candidate state and binds roadmap, index, git, prerequisite, gate, artifact, generator, and timestamp identities.
- PC-004 [PD-005] gate catalog: `fsgg.coordination.gate-catalog/1` admits only reviewed executable and literal argument arrays keyed by exact command IDs and Q gates.
- PC-005 [PD-006] CLI contract: `roadmap-work inspect|prerequisites|manifest|gates` returns JSON on stdout, bounded diagnostics on stderr, zero only for a complete successful operation, and never performs network or GitHub writes.

## Verification Obligations
- VO-001 [PD-001] [PD-006] [PC-001] [PC-005] skillAndCli: Validate the skill package and exercise every CLI operation from a clean repository checkout with one explicit unit and exact JSON outputs.
- VO-002 [PD-002] [PC-002] indexRoadmap: Accept the pinned roadmap/index fixture; reject changed roadmap bytes, unknown/duplicate units, malformed revisions/hashes, unknown fields, incomplete metadata, and inconsistent prerequisites/gates.
- VO-003 [PD-003] [PC-002] prerequisiteAuthority: Accept exactly one valid accepted receipt per prerequisite; independently reject missing, duplicate, rejected, malformed, stale-source, artifact-mismatched, digest-tampered, and prose/Project substitutes.
- VO-004 [PD-004] [PC-003] manifestIntegrity: Repeated creation with the same explicit timestamp is byte-identical and exact-candidate-bound; changed commit/tree/artifact bytes, path traversal, symlink escape, unknown gates, or qualification/acceptance claims fail.
- VO-005 [PD-005] [PC-004] gateBoundary: Run the exact declared safe gate inventory; independently reject overrides, shell evaluation, unknown/duplicate commands, index/catalog disagreement, changed candidate, failing commands, and every successor-unit request.
- VO-006 [PD-006] [PC-005] noRemoteAuthority: Static architecture controls prove the new core/CLI/skill contains no GitHub API, Project scheduling, claim, settings, deployment, production-write, or v1 completion route.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additiveBootstrap: Add the skill and contracts without changing existing CI identities, repository settings, production authority, or accepted GS2-01.3–01.5 receipts; later units extend versioned index/catalog data through reviewed changes.

## Generated View Impact
- GV-001 [PD-004] evidenceManifest: Dynamic unit evidence is written only to an explicitly supplied ignored artifact directory; git retains schemas, pinned bootstrap index/catalog fixtures, tests, and compact SDD evidence.
- GV-002 [PD-001] workModel: The SDD work model and generated agent guidance refresh from current sources and must be current before review.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 8-create-github-substrate-v2-work-skill`.
