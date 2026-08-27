---
schemaVersion: 1
workId: 12-create-evidence-storage
title: Create Evidence Storage
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

# Create Evidence Storage Charter

## Identity
- Work id: `12-create-evidence-storage`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Evidence is useful only when an independent reviewer can reproduce its identity and provenance.
- Git retains compact, reviewable contracts, indexes, digests, manifests, reviews, and receipts.
- Bulky or machine-generated payloads are immutable Actions artifacts or release assets, never repository history.
- Accepted receipts are append-only records and must remain byte-for-byte stable.

## Scope Boundaries
- This unit owns repository-local schemas, storage policy, indexes, validation, fixtures, and documentation.
- It does not create organization settings, deployments, credentials, network writers, or successor-unit behavior.
- Keep SDD lifecycle ownership separate from optional Governance enforcement.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 12-create-evidence-storage`.
