---
schemaVersion: 1
workId: 200-staged-intake-admission
title: GS2-05.9 staged intake admission
stage: charter
changeTier: tier1
status: chartered
policyPointers:
  - .fsgg/sdd.yml
  - .fsgg/agents.yml
  - .fsgg/policy.yml
  - .fsgg/capabilities.yml
  - .fsgg/tooling.yml
---

# GS2-05.9 staged intake admission Charter

## Identity
- Work id: `200-staged-intake-admission`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat accepted GS2-05.3 receipt `f5ac79b55dfa001903a4173209f09a71e7265641f5891c6498c65ce395364be0` and the canonical GS2-05.9 roadmap amendment at `.github@2ff646743e770f0ec6be5566acd04df0b1a83dec` as immutable prerequisites.
- Preserve GS2-05.3's validate/plan/apply/inspect transaction guarantees, explicit create-or-reuse identity, idempotent recovery, compensation, and authoritative post-state readback.
- Split item-local Backlog capture from Ready promotion: discovery may preserve unknown root cause, deferred verification, and no touch set; promotion requires complete schedulability facts and reports every missing input.
- Keep capture cost bounded by a sealed plan containing at most six authority-read operations and six mutation operations, independent of Project and Backlog cardinality, with no unrelated traversal, retriage, or organization-wide reconciliation.
- Keep claim and pull-request evidence causally downstream of Ready; they are lifecycle outputs and cannot be promotion prerequisites.
- Preserve unknown and partial observations and the existing `fsgg.coord.intake/v1` caller contract during migration rather than turning unreadable or incomplete evidence into false absence.
- Prove every new boundary with independently authored fixtures and gate inversions, including cardinality growth, one omission per promotion fact, and forbidden global-work mutations.

## Scope Boundaries
- In: a receiver-owned staged intake contract; minimal Backlog capture; complete Ready promotion; bounded plan accounting; actionable diagnostics; index/roadmap sequencing; focused tests and inversions; and the complete SDD lifecycle.
- In: compatibility for existing `fsgg.coord.intake/v1` callers without weakening sealed-plan, replay, drift, recovery, authorization, pagination, or readback guarantees.
- Out: production GitHub writes, organization-wide reconciliation, full-Backlog traversal/retriage, live Project administration, deployment, publication, and GS2-05.4 roadmap compilation behavior.
- Out: treating claim or pull-request evidence as inputs to Ready promotion; those remain later lifecycle stages.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Registered unit authority is `eng/github-substrate-v2-units.json`; the exact roadmap authority is `.github@2ff646743e770f0ec6be5566acd04df0b1a83dec:docs/github-substrate-v2-roadmap.md`.
- Repository constitution principles I, II, III, V, VI, VII, and VIII govern specification, structured authority, public surface, pure/effect separation, evidence, shared contracts, and safe failure.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 200-staged-intake-admission`.
