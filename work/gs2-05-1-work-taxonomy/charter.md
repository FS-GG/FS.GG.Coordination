---
schemaVersion: 1
workId: gs2-05-1-work-taxonomy
title: GS2-05.1 work taxonomy contract
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

# GS2-05.1 work taxonomy contract Charter

## Identity
- Work id: `gs2-05-1-work-taxonomy`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat registration contract `ed9ae9d198d6eaaf89030f85d214a0a359333598be7ceb3597c2c4aeb629ef28` and accepted GS2-04.9 receipt `11defafd12353bbcb9b96cc06d3d9e29553ddca4ba912bacd7476c067f9802ed` as immutable prerequisites.
- Make native issue type the sole classification authority; legacy Class and Kind are observed migration inputs that the plan retires, never parallel post-migration truth.
- Keep classification and planning repository-local, pure, deterministic, complete, and fail-closed. Every accepted row binds a stable identity and prestate fingerprint.
- Preserve hierarchy and repository scope exactly. Refuse any migration whose interpretation or projection would be ambiguous or lossy.
- Freeze the current corpus and prove the contract through independent oracles, inversions, and byte-stable evidence.

## Scope Boundaries
- In: closed Epic/Feature/Task/Bug/Decision/Register/Directive vocabulary; explicit work versus standing lifecycle applicability; complete frozen legacy Class/Kind corpus; canonical deterministic migration plan and diagnostics.
- In: a plain Quint single-actor pure model, repository-local F# contract, focused validator, independent tests and inversions, digest-bound evidence, and the complete SDD lifecycle.
- Out: every GitHub write, organization issue-type or field mutation, issue conversion, Project mutation, production credential, deployment, publication, and stable release.
- Out: GS2-05.2 field-contract work, intake implementation, and authority for any successor roadmap unit.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- The registered gate is `dotnet fsi eng/validate-github-work-taxonomy.fsx -- .` at Q2; its command identity digest is `c10ddf0ee6bb9328e09fceae4d0deebcc688478c1b2ae80c3fc3b4fbc766ef7f`.
- Repository constitution principles I, III, V, VI, and VIII govern specification, public surface, pure/effect separation, evidence, and safe failure.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work gs2-05-1-work-taxonomy`.
