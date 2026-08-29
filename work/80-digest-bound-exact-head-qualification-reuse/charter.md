---
schemaVersion: 1
workId: 80-digest-bound-exact-head-qualification-reuse
title: Digest-bound exact-head qualification reuse
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

# Digest-bound exact-head qualification reuse Charter

## Identity
- Work id: `80-digest-bound-exact-head-qualification-reuse`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Reuse is authorization, not an optimization hint: an exact closed subject must be proven before any prior green result can apply to a new head.
- The reuse receipt is content-addressed, append-only evidence. Missing, stale, malformed, incomplete, or contradictory facts trigger fresh execution or red; they never become a cache hit.
- A reused result must retain every required artifact, reviewer decision, gate identity, and exact candidate-head acceptance claim.
- Workflow latency may improve only by removing semantically redundant execution; branch protection and independent review remain unchanged.

## Scope Boundaries
- In scope: the qualification-subject digest, reuse receipt schema/validation, exact-head workflow decision, artifact availability checks, negative controls, and avoided-minute metrics.
- In scope: deterministic reuse of an already successful protected qualification when the full subject is byte-identical.
- Out of scope: weakening or deleting Q gates, reusing partial/failed/cancelled runs, trusting GitHub cache contents without digest validation, or changing roadmap semantics.
- Out of scope: Q1/Q2 process consolidation and general CI topology work, which remain owned by #79 and #78.
- Keep SDD lifecycle ownership separate from optional Governance enforcement.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 80-digest-bound-exact-head-qualification-reuse`.
