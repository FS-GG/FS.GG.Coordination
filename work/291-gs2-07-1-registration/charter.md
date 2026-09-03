---
schemaVersion: 1
workId: 291-gs2-07-1-registration
title: Register GS2-07.1 event-envelope frontier
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

# Register GS2-07.1 event-envelope frontier Charter

## Identity
- Work id: `291-gs2-07-1-registration`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat the canonical `.github` roadmap bytes, the accepted GS2-06.8 receipt, and the reviewed gate decision as exact authority.
- Preserve every previously accepted unit contract while admitting only the next executable frontier.
- Make stale roadmap, stale index, and mismatched gate identities independently observable refusals.

## Scope Boundaries
- In scope: the roadmap pin, one GS2-07.1 unit record, one future Q3 event-envelope command identity, focused architecture tests, this SDD package, and bounded architecture documentation.
- Read-only consumption of `.github` commit `d0267c02c59de75571f6ee9086f924e8c924da08` and accepted GS2-06.8 evidence is allowed.
- Out of scope: event-envelope implementation or execution, candidate or acceptance evidence for GS2-07.1, successor-unit inspection or implementation, and every production mutation.

## Policy Pointers
- The canonical roadmap SHA-256 is `152956bff4f264d7a6e034c0d8553d3df2cd44ac6773b03e83f85ff52dfb4655`.
- The accepted GS2-06.8 receipt is `evidence/github-substrate-v2/accepted/GS2-06.8.json`; its exact self-digest and tracked file digest are verified by `roadmap-work prerequisites` rather than copied into this charter.
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Tier 1 contracted registration: index, catalog identity, independent refusals, documentation, and SDD evidence land together.
