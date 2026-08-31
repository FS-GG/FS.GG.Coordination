---
schemaVersion: 1
workId: gs2-04-6-sharded-journal-adapter
title: GS2-04.6 sharded Git journal adapter
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

# GS2-04.6 sharded Git journal adapter Charter

## Identity
- Implement the registered GS2-04.6 Q3 adapter that makes protected sharded Git journals an executable, typed authority boundary in `FS.GG.Coordination`.
- Bind the implementation to roadmap contract `33b2dabe28c4e0133b5503c8bb907f83d036b6da0b8532f3f60a93b649d85b51` and accepted prerequisite GS2-04.5.

## Principles
- Append-only Git ancestry and exact expected-parent receive-pack CAS are the concurrency authority; comments, webhooks, and object existence are never authority.
- Every effect is fenced by the current journal commit and monotonically increasing generation.
- Canonical bytes, digests, shard derivation, terminal checkpoints, ruleset observations, and multi-aggregate compensation fail closed under incomplete or contradictory evidence.
- Q3 remains offline: local Git/loopback fixtures and already-protected administrative receipts are allowed, but no live GitHub mutation or Q4 sandbox claim is authorized.

## Scope Boundaries
- In: typed journal domain surface, deterministic codec and validation, CAS planning/outcome reconciliation, fencing, ruleset observation validation, deterministic saga ordering/compensation, executable Q3 validator, independent tests, and evidence.
- Out: live repository or ruleset mutation, production credentials, deployment/publication, runtime cutover, repository/settings adapter work, GS2-04.7+, and new claims about Q4 behavior.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- The canonical roadmap is `FS-GG/.github@051d811169b4b6695def252e7e6c251f91ec73b2:docs/github-substrate-v2-roadmap.md`.
- The detailed storage and mutation contract is already accepted in `src/FS.GG.Coordination.Protocol/Protocol.md` under “Protected journal storage and mutation contract (GS2-03.10)”.
- Existing protected administration is evidence, not new mutation authority: authority repository `1351660651`, writer ruleset `21872113`, integrity ruleset `21872115`, writer App `4166418`, and policy run `33330220225`.

## Lifecycle Notes
- Tier 1 contracted change: declare the `.fsi` surface before implementation and land signatures, validator, tests, retained evidence, and generated readiness together.
- No deferral may silently move a required GS2-04.6 contract clause into GS2-04.7 or GS2-04.9.
