---
schemaVersion: 1
workId: gs2-05-3-intake
title: GS2-05.3 intake contract
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

# GS2-05.3 intake contract Charter

## Identity
- Work id: `gs2-05-3-intake`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat registered contract `01e29e3b7f0f049364a64b85c3b75f1e47f37f256778052e66d3894b23632ae6` and accepted GS2-05.2 receipt `a8474e696d2c1ff149ec1efb6a4c4b4cb6fe6e56b86ec840871b4430864f0a50` as immutable prerequisites.
- Compose issues, native types and organization fields, Project membership, hierarchy, dependencies, and protocol initialization through one typed intake boundary without creating a competing authority.
- Keep validation and planning pure, total, deterministic, canonical, and byte-stable; every plan binds a complete observation, exact revision/preconditions, stable operation identities, postconditions, compensations, and integrity digest.
- Apply only an exact sealed plan over controlled fixtures, re-observing before every effect and rereading authoritative post-state; drift, partiality, ambiguity, or an unreadable fact refuses before further mutation.
- Keep inspection read-only and exhaustive across pagination, with explicit completeness and outcome distinctions; never infer absence or success from a partial or response-only observation.
- Initialize only revision-bound journal, scheduling-intent, contract, touch-set, and projection intents, with Project fields remaining projections rather than the execution ledger.
- Prove correspondence with canonical fixtures, independently authored expectations and inversions, durable step outcomes, idempotent replay/resume, roll-forward, and reverse compensation.

## Scope Boundaries
- In: a repository-local F# intake contract and controlled-fixture executor; complete recorded observation corpus; canonical validate, plan, apply, and inspect phases; protocol initialization; focused validator; independent tests/inversions; digest-bound evidence; and the complete SDD lifecycle.
- In: preservation of issue identity, native type, organization fields, Project membership, hierarchy, dependencies, repository scope, and revision-bound protocol projections.
- Out: production GitHub writes, organization issue-type or field mutation, live Project or issue mutation, production credentials, deployment, publication, and stable release.
- Out: roadmap compilation and every GS2-05.4 or successor-unit behavior.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- The registered gate is `dotnet fsi eng/validate-github-intake.fsx -- .` at Q3; its command identity digest is `1db2b6e766e4a024a31c918b68dea21420728b6ad280661799b76e022412002a`.
- Repository constitution principles I, III, V, VI, and VIII govern specification, public surface, pure/effect separation, evidence, and safe failure.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work gs2-05-3-intake`.
