---
schemaVersion: 1
workId: 204-roadmap-intake
title: GS2-05.4 roadmap intake
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

# GS2-05.4 roadmap intake Charter

## Identity
- Work id: `204-roadmap-intake`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat accepted GS2-05.9 receipt `59398e603e39b04ff6d971ef923d19513e03d3990a970323add90cf7ce593861` and canonical roadmap projection `.github@6dad31e6efb70b6084e442fcb1f20d310327d02f` as immutable prerequisites.
- Compile a versioned typed roadmap definition, never Markdown or Project rows as semantic input, into one Epic and a bounded graph of create-or-reuse native work projections.
- Make stable source keys, canonical ordering, and sealed-plan digests decide identity and replay; refuse ambiguity instead of guessing which existing issue a roadmap node means.
- Keep native issue identity, parent/sub-issue edges, and dependency edges authoritative. Project membership, fields, status, copied blocker text, and body metadata are projections only.
- Bound authority reads and mutation effects by closed formulas over declared roadmap nodes and edges. Unrelated Project and Backlog cardinality cannot affect plan bytes or cost.
- Preserve validate/plan/apply/inspect safety: graph, type, field, date, observation, precondition, and plan integrity failures are typed refusals before effects; partial controlled-fixture application retains recovery evidence.
- Inspect only owned projections and report every missing, extra, or mismatched fact without classifying unrelated work as drift.
- Prove each boundary with generated cases, independently authored expectations, cardinality growth, source-vocabulary checks, and explicit inversions.

## Scope Boundaries
- In: an additive public F# roadmap-intake contract, a pure compiler, a controlled-fixture adapter, deterministic identity and operation accounting, complete owned-projection inspection, Q3 registration, focused tests, independent evidence, and the complete SDD lifecycle.
- In: one Epic; bounded Feature/Task/Bug/Decision work issues; native parent and dependency edges; start/target dates; accepted organization issue fields; Project membership as a derived projection; and typed create, reuse, update, link, unlink, and inspect outcomes.
- Out: production GitHub writes, live Project administration, organization-wide scans, unrelated Backlog traversal or retriage, claim/review/delivery implementation, deployment, publication, stable release, and successor-unit authority.
- Out: parsing Project status, body metadata, copied blocker text, or Markdown checkbox state as execution authority.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Registered unit authority is `eng/github-substrate-v2-units.json`; the accepted receipt is `evidence/github-substrate-v2/accepted/GS2-05.9.json`; exact roadmap authority is `.github@6dad31e6efb70b6084e442fcb1f20d310327d02f:docs/github-substrate-v2-roadmap.md`.
- Repository constitution principles I, II, III, V, VI, VII, and VIII govern specification, structured authority, additive public surface, pure/effect separation, evidence, shared contracts, and safe failure.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 204-roadmap-intake`.
