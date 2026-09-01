---
schemaVersion: 1
workId: 208-claim-touch-sets
title: GS2-05.5 claims and touch sets
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

# GS2-05.5 claims and touch sets Charter

## Identity
- Work id: `208-claim-touch-sets`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat accepted GS2-05.4 receipt `0017ef59099ee14e6c3d0df73b4fb05a9c45a34f2067cecdf19a4b29e0a7a0fe`, canonical roadmap projection `.github@b776da763a490c2c3310a10c8db234a62a5b6bc4`, and the existing Quint GS2-03.10 journal model as immutable prerequisites.
- Make the protected sharded Git journal the sole concurrency authority. Native issue fields, Project fields, comments, labels, webhooks, clocks, and local object existence are projections, prefilters, or wake-up hints only.
- Bind each claim to a canonical subject, owner, complete canonical touch set, journal commit, and monotonically increasing fencing generation.
- Treat lease expiry only as permission to attempt a successor expected-parent CAS. It never transfers ownership or authorizes an effect by itself.
- Acquire multiple conflict domains in the existing total order, persist the complete touch set and expected generations before effects, release unconsumed grants, and append reverse-order fenced compensation without erasing original results.
- Re-read complete authoritative journal state immediately before every external effect and require the exact owner, commit, generation, and touch authority.
- Preserve deterministic planning, stable operation identity, idempotent recovery, complete diagnostics, and cost bounded only by declared touches.
- Prove the existing Quint safety and reachability properties after each substantive implementation phase; never weaken the formal specification to fit code.

## Scope Boundaries
- In: an additive public F# claim/touch-set contract, pure claim decisions, controlled-fixture execution, canonical path conflict semantics, successor eligibility, ordered multi-touch sagas, Q3 registration, focused tests, independent evidence, and the complete SDD lifecycle.
- In: existing `ShardedJournalAdapter` expected-parent CAS, fencing, journal validation, saga ordering, and compensation primitives as the storage foundation.
- Out: production GitHub writes or credentials, journal App administration, Project or comment authority, review/delivery, derived lifecycle projection, fleet shadowing, deployment, publication, stable release, and successor-unit implementation.
- Out: modifying the Quint protocol source or accepting a clock, lease, projection, successful response, or object existence as concurrency authority.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Registered unit authority is `eng/github-substrate-v2-units.json`; the accepted prerequisite is `evidence/github-substrate-v2/accepted/GS2-05.4.json`; exact roadmap authority is `.github@b776da763a490c2c3310a10c8db234a62a5b6bc4:docs/github-substrate-v2-roadmap.md`.
- Repository constitution principles I, II, III, V, VI, VII, and VIII govern formal authority, additive public surfaces, pure/effect separation, evidence, shared contracts, and safe failure.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 208-claim-touch-sets`.
