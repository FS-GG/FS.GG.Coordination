---
schemaVersion: 1
workId: 16-prove-bootstrap-recovery
title: Prove Bootstrap Recovery
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

# Prove Bootstrap Recovery Charter

## Identity
- Work id: `16-prove-bootstrap-recovery`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Prove recovery from committed bytes, not the implementer's populated working tree, caches, or machine state.
- Permit only local read-only repository cloning and explicit published dependency-feed reads; no GitHub mutation, credentials, publishing, deployment, or settings authority enters the gate.
- Bind every recovery stage and artifact to the exact candidate revision, fail closed at the first divergent stage, and retain compact digest evidence.
- Keep the roadmap command closed and independently pinned so callers cannot replace feeds, commands, or scratch inputs.

## Scope Boundaries
- Add the clean-clone recovery runner, its Q7 catalog/unit identities, hosted read-only evidence lane, architecture controls, and operator documentation.
- Reuse the pinned SDK, locked dependency graph, existing package-install consumer, and bootstrap evidence collector.
- Do not publish a package, mutate a remote repository, administer organization settings, provision runtime, consume GitHub events, or execute GS2-01.9.
- Keep SDD lifecycle ownership separate from optional Governance enforcement.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 16-prove-bootstrap-recovery`.
