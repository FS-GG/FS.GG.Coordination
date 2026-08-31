---
schemaVersion: 1
workId: gs2-04-5-comment-projection-adapter
title: GS2-04.5 comment/projection adapter
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

# GS2-04.5 comment/projection adapter Charter

## Identity
- Work id: `gs2-04-5-comment-projection-adapter`
- Implement the offline Q3 comment/projection adapter required by roadmap unit GS2-04.5.

## Principles
- Preserve GitHub server-issued comment identity and ordering as observations, never as concurrency authority.
- Treat protected journal state as durable authority and human comments only as validated projections.
- Preserve malformed, absent, incomplete, unauthorized, stale, and contradictory outcomes explicitly.
- Keep observation, reduction, and deterministic planning pure; this unit performs no live GitHub write.

## Scope Boundaries
- In: comment observations, marker parsing, journal-digest validation, tamper classification, and deterministic projection regeneration plans.
- In: generated and independently authored Q3 controls over complete offline fixtures.
- Out: sharded journal CAS writes, live GitHub mutation, Q4 sandbox behavior, deployment, publication, and successor-unit work.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Roadmap authority is `FS-GG/.github@7fa7553cb6d805bffd4de0ba09b05694185909fb:docs/github-substrate-v2-roadmap.md`.
- The registered unit contract is `829a9bb643f95729b5d28f8902e0a8016c366730df792f92b6d0140710ce11d4`.

## Lifecycle Notes
- Tier 1 public typed surface: signatures, implementation, qualification contract, fixtures, tests, evidence, and protected acceptance land together.
