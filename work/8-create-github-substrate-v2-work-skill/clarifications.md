---
schemaVersion: 1
workId: 8-create-github-substrate-v2-work-skill
title: Create Github Substrate V2 Work Skill
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/8-create-github-substrate-v2-work-skill/spec.md
publicOrToolFacingImpact: true
---

# Create Github Substrate V2 Work Skill Clarifications

## Source Specification
- work/8-create-github-substrate-v2-work-skill/spec.md

## Clarification Questions
- **CQ-001** [FR-001] [FR-002]: Where does the command obtain the canonical roadmap when `FS.GG.Coordination` is independently cloned?
- **CQ-002** [FR-002] [FR-003]: What is the smallest bootstrap index and receipt vocabulary that remains deterministic and fail-closed before GS2-01.7?
- **CQ-003** [FR-004]: Which candidate and artifact identities are required in the compact unit evidence manifest?
- **CQ-004** [FR-005]: How can gate execution be useful before later GS2 units define their full qualification commands without becoming an open command runner?
- **CQ-005** [FR-006]: What exactly constitutes the unit boundary, and can a ready successor be reported?
- **CQ-006** [FR-001] [FR-006]: Which implementation surface best keeps the skill portable while giving tests a compiled deterministic core?

## Answers
- CQ-001 → Vendor no mutable roadmap copy. Pin a canonical `.github` repository URL, exact 40-hex revision, path, and SHA-256 in the bootstrap index; `inspect` validates caller-supplied roadmap bytes against that binding before projecting the selected stable unit.
- CQ-002 → Use strict JSON contracts for a unit index and accepted receipts. The index explicitly lists unit ID, prerequisites, owner, permission ceiling, exit gate, Q gates, and closed gate-command IDs. A receipt states `accepted` and binds its unit, source revision, artifacts, and its own canonical digest; exactly one receipt per prerequisite is required.
- CQ-003 → Bind index and roadmap fingerprints, selected unit, candidate commit and tree, every prerequisite receipt digest, declared Q gates and command identities, explicitly supplied artifact path/digest pairs, generator ID/version, and an explicit UTC timestamp. Manifest creation asserts `candidate`, never `qualified` or `accepted`.
- CQ-004 → Ship a closed bootstrap gate catalog with safe repository-local process invocations. Unknown gate IDs, command overrides, environment-shell interpolation, duplicate gates, path escapes, candidate changes, or outputs not matching the manifest fail before or during execution. Later units extend the versioned catalog by reviewed changes.
- CQ-005 → The selected unit ID is immutable for the command invocation and manifest. The command may report successor IDs only as inert metadata after stopping; it never inspects Project readiness, claims, schedules, creates successor output, or runs successor gates.
- CQ-006 → Put the pure parser/validator/planner in `FS.GG.Coordination.Qualification.Contracts`, expose a thin `roadmap-work` CLI verb from the existing host, and keep the skill as concise orchestration guidance plus versioned reference contracts. This avoids shell-policy parsing and a second implementation in the skill.

## Decisions
- **DEC-001** [CQ-001] [FR-001] [FR-002] [AC-001]: Bind caller-supplied canonical roadmap bytes to a pinned `.github` revision/path/SHA-256 in the strict unit index; never schedule from a locally inferred checkbox or Project field.
- **DEC-002** [CQ-002] [FR-002] [FR-003] [AC-001] [AC-002]: Define strict versioned JSON index and receipt contracts with complete stable unit metadata, canonical digests, exact-one accepted prerequisite semantics, and rejection of unknown members.
- **DEC-003** [CQ-003] [FR-004] [AC-003]: Emit canonical candidate-state manifests binding exact git and artifact identities while reserving qualification and acceptance for later result/receipt transitions.
- **DEC-004** [CQ-004] [FR-005] [AC-004]: Execute only closed catalog commands represented as executable plus literal argument arrays; prohibit caller overrides, shell evaluation, unknown/duplicate gates, and catalog/index disagreement.
- **DEC-005** [CQ-005] [FR-006] [AC-005]: Freeze one unit ID per invocation and output root, stop after its exit-gate report, and treat successor discovery as non-authoritative display only.
- **DEC-006** [CQ-006] [FR-001] [FR-006] [AC-001] [AC-005]: Implement validation/planning in the published-boundary-safe Qualification.Contracts project, expose it through the existing CLI, and keep the agent skill declarative and concise.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 8-create-github-substrate-v2-work-skill`.
