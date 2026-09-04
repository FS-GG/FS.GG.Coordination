---
schemaVersion: 1
workId: 294-gs2-07-1-event-envelope
title: GS2-07.1 event envelope and cursor
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

# GS2-07.1 event envelope and cursor Charter

## Identity
- Work id: `294-gs2-07-1-event-envelope`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Preserve exact accepted GS2-06.8 and roadmap authority; the registered GS2-07.1 contract is the implementation ceiling.
- Keep normalization, reduction, sealing, parsing, and verification pure and deterministic.
- Every refusal and idempotency claim receives mutation-discriminating focused coverage plus independently authored controls.

## Scope Boundaries
- In: repository-local envelope/cursor contracts, signatures, implementation, tests, Q3 validator, retained evidence, and append-only acceptance receipt.
- Out: webhook subscription, ingestion host, queue mutation, network calls, GitHub writes, repository settings, workflow publication, release, package publication, and successor-unit work.
- The implementation PR does not close issue #294; the same item remains reserved through the protected-merge acceptance-receipt phase.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Tier 1 tool-facing contract: declare `.fsi` surface before `.fs`, bind canonical length-framed bytes, and keep the canonical Quint protocol byte-identical.
- Next lifecycle action: `fsgg-sdd specify --work 294-gs2-07-1-event-envelope`.
