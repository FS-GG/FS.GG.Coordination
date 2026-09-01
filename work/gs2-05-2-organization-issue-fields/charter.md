---
schemaVersion: 1
workId: gs2-05-2-organization-issue-fields
title: GS2-05.2 organization issue-field contract
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

# GS2-05.2 organization issue-field contract Charter

## Identity
- Work id: `gs2-05-2-organization-issue-fields`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat registration contract `054ee50545c55b314447b7636ee35faf866adba40f6ff9b5ef07effb2009b41f` and accepted GS2-05.1 receipt `6a2c26033946b986b25fc4fc99257d21d6fa5d728e1f365170d9bc17a9226df5` as immutable prerequisites.
- Make Scheduling Intent the human or policy authority and lifecycle Status its derived projection; never allow both to become competing intent inputs.
- Keep Contract and touch set as revision-bound projections of their authoritative records, never mutable parallel ledgers.
- Keep normalization and migration repository-local, pure, deterministic, complete, and fail-closed. Every accepted row binds a stable identity and exact prestate fingerprint.
- Preserve native type, hierarchy, dependencies, repository scope, and lifecycle exemptions exactly. Refuse any ambiguous, stale, contradictory, or lossy input.
- Freeze the current field corpus and prove the contract through independent oracles, inversions, and byte-stable evidence.

## Scope Boundaries
- In: the registered closed vocabularies for scheduling intent, hold reason, priority, effort, dates, severity, phase, workstream, contract reference, and canonical touch set; complete frozen current-field corpus; canonical deterministic migration plan and diagnostics.
- In: a plain Quint pure model, repository-local F# contract, focused validator, independent tests and inversions, digest-bound evidence, and the complete SDD lifecycle.
- Out: every GitHub write, organization issue-type or field mutation, Project mutation, issue conversion, production credential, deployment, publication, and stable release.
- Out: GS2-05.3 intake implementation and authority for any successor roadmap unit.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- The registered gate is `dotnet fsi eng/validate-github-organization-issue-fields.fsx -- .` at Q2; its command identity digest is `6311e9ca2c92315c48981983efcb93f2717b6b5273aa6fafa75c3fb8496ebcbd`.
- Repository constitution principles I, III, V, VI, and VIII govern specification, public surface, pure/effect separation, evidence, and safe failure.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work gs2-05-2-organization-issue-fields`.
