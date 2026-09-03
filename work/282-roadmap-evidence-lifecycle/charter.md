---
schemaVersion: 1
workId: 282-roadmap-evidence-lifecycle
title: Harden roadmap evidence and receipt lifecycle
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

# Harden roadmap evidence and receipt lifecycle Charter

## Identity
- Work id: `282-roadmap-evidence-lifecycle`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Evidence must be reproducible from the candidate Git object, not inherited from an author's working directory.
- A claim about hosted behavior requires hosted execution evidence.
- One roadmap unit has one accountable work item even when its acceptance receipt must follow the implementation merge.

## Scope Boundaries
- Change only the GS2 worker procedure and the executable checks that prevent its safety clauses from being removed.
- Do not rewrite accepted receipts, change roadmap unit semantics, or add a second scheduling system.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 282-roadmap-evidence-lifecycle`.
