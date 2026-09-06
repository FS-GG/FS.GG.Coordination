---
schemaVersion: 1
workId: 315-gs2-07-5-merge-group-support
title: GS2-07.5 merge-group support
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

# GS2-07.5 merge-group support Charter

## Identity
- Implement the registered `GS2-07.5` pure merge-group qualification contract at the exact accepted GS2-07.4 and roadmap frontier.

## Principles
- A merge-group event is only a hint: qualification re-observes every authority at evaluation time and fails closed on stale or contradictory facts.
- Bind canonical merge-group head and base repository identity, full base ref, exact base SHA, observation revision and freshness deadline into one deterministic sealed decision.
- Require the complete aggregate required-check inventory to run for `merge_group`, and preserve byte-identical replay without network or production mutation.

## Scope Boundaries
- In scope: additive repository-local types, pure deterministic validation, canonical length-framed sealing, retained generated and independent controls, focused and full qualification evidence.
- Out of scope: merge queue deployment, workflow publication, network calls, production queue/settings/release/package mutation, stable channels, canonical Quint changes, and any GS2-07.6 work.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Exact executable registration: Coordination main `05c6694b1ee64431ecd8f89507d2bad8b087164c`, unit contract `6ad620d58509ce141ee0eda476375b56d610cb150ee4ee089173f339a30ea9f2`.
- Exact roadmap: revision `64e9a2b7753f438f8ad31298fd17698ff2a142e6`, SHA-256 `da6477affae014d9ef5cc473f608ca1c1cb25bcffe51f99ed9e5b22994844a0a`.
- Next lifecycle action: `fsgg-sdd specify --work 315-gs2-07-5-merge-group-support`.
