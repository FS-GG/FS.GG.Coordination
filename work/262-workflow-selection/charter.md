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
- In scope: a production-consumable pure Core selector and CLI, repository-owned callable reusable/composite/aggregate contracts, Q3/Q7 gates, a scheduled sentinel contract, read-only fleet observations, tests, retained evidence, and amended SDD readiness.
- Read-only consumption of the accepted GS2-06.6 receipt, immutable original GS2-06.7 receipt, exact roadmap, current base/settings, workflow/non-file inventories, and GitHub Actions observations is allowed.
- Out of scope: consumer-repository changes, fleet enablement, production settings/ruleset/required-check mutation, package or release publication, rewriting the original receipt, and GS2-06.8.

## Policy Pointers
- The canonical roadmap at `.github` revision `b6d4b60493d1f0b99daf73b98f4e8ad9bbbc0ed9` and accepted GS2-06.6 receipt are authority.
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Tier 1 contracted repair: Core/CLI signatures, implementation, callable workflow contracts, both gate validators, real observation evidence, tests, and the compact ship verdict land together. A separately indexed superseding receipt is created only after protected merge and independent requalification.
