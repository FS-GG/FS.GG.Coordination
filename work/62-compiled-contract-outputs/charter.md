---
schemaVersion: 1
workId: 62-compiled-contract-outputs
title: GS2-02.10 compiled-contract outputs
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

# GS2-02.10 compiled-contract outputs Charter

## Identity
- Work id: `62-compiled-contract-outputs`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Keep the literate Quint source as the sole behavioral authority.
- Derive every compiled output mechanically and bind it to source, profile, and contract identity.
- Prefer focused counterexamples and one independent critic over evidence volume.

## Scope Boundaries
- Cover schemas, command metadata, permission and mutation censuses, settings plans, Markdown/JSON views, semantic diff, diagrams, and model-test inventory.
- Refuse incomplete, duplicate, substituted, unsupported, non-deterministically ordered, or stale output sets.
- Remain repository-local and pure: no network, GitHub mutation, hosted runtime, deployment, publication, or production write authority.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 62-compiled-contract-outputs`.
