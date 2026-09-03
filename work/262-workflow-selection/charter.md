---
schemaVersion: 1
workId: 262-workflow-selection
title: GS2-06.7 workflow consolidation and change-impact selection
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

# GS2-06.7 workflow consolidation and change-impact selection Charter

## Identity
- Work id: `262-workflow-selection`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Select only the smallest sound transitive obligation closure while preserving unconditional policy and core obligations.
- Treat unknown, ambiguous, stale, incomplete, unsupported, or missed impact as a fail-closed decision.
- Keep aggregates stable and explicit even when expensive child jobs are not provisioned.
- Bind generated qualification and independently authored controls to one canonical sealed inventory.

## Scope Boundaries
- In scope: repository-local additive Q3 and Q7 contracts, typed inventory and graph compilation, tests, retained evidence, and SDD readiness.
- Read-only consumption of the accepted GS2-06.6 receipt, exact roadmap, workflow/non-file inventories, and measured baselines is allowed.
- Out of scope: production GitHub settings, workflows, rulesets, required checks, merge queues, repositories, releases, packages, feeds, environments, deployments, fleet mutation, an acceptance receipt, and successor-unit work.

## Policy Pointers
- The canonical roadmap at `.github` revision `b6d4b60493d1f0b99daf73b98f4e8ad9bbbc0ed9` and accepted GS2-06.6 receipt are authority.
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Tier 1 contracted change: signature, implementation, both gate validators, tests, retained evidence, and the compact ship verdict land together.
