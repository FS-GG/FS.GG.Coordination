---
schemaVersion: 1
workId: 310-gs2-07-4-event-security
title: GS2-07.4 event security
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

# GS2-07.4 event security Charter

## Identity
- Implement the registered `GS2-07.4` pure event-security contract at the exact accepted GS2-07.3 and roadmap frontier.

## Principles
- Treat event delivery as an untrusted hint until its signature, scope, time, payload/API agreement, permissions, and replay identity are proven together.
- Events may schedule reconciliation only; the shared fresh-observe/reduce/sealed-plan/apply/verify reconciler remains the exclusive writer.
- Preserve deterministic canonical serialization, exact replay, explicit refusal, and zero network or production mutation paths.

## Scope Boundaries
- In scope: additive repository-local types, pure validation, HMAC-SHA256 verification, canonical length-framed seal, retained generated and independent controls, focused and full qualification evidence.
- Out of scope: webhook subscription or deployment, network calls, production queue writes, derived-state mutation, settings, workflows, releases, packages, stable channels, canonical Quint changes, and any GS2-07.5 work.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Exact executable registration: Coordination main `41885e9a2a2a9c50bf99e6a085d0ce7c861e4e84`, unit contract `64b16f2d6228fc6a575e9814703671f8fcd2080cfee49f3ee1006eb21a16512a`.
- Next lifecycle action: `fsgg-sdd specify --work 310-gs2-07-4-event-security`.
