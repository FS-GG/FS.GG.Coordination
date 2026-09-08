---
schemaVersion: 1
workId: 326-gs2-07-7-event-benefit
title: GS2-07.7 event benefit measurement
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

# GS2-07.7 event benefit measurement Charter

## Identity
- Work id: `326-gs2-07-7-event-benefit`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Derive every result from retained bound inputs; caller assertions are never acceptance evidence.
- Keep complete scheduled audits authoritative and retain polling regardless of replay economics.
- Preserve source category, exact head, population/window, page, attempt, and digest provenance.

## Scope Boundaries
- Add one pure measurement contract, bounded read-only observation/replay gates, tests, documentation, retained evidence, and the post-merge acceptance receipt.
- Do not create or activate a hosted controller or writer, alter production polling/settings/visibility, deploy, publish, use secrets, or inspect or implement GS2-07.8.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 326-gs2-07-7-event-benefit`.
