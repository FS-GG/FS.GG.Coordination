---
schemaVersion: 1
workId: 254-release-hardening
title: GS2-06.6 release hardening
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

# GS2-06.6 release hardening Charter

## Identity
- Work id: `254-release-hardening`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Preserve the accepted OIDC and dual-feed saga semantics while strengthening release policy.
- Keep compilation pure, deterministic, sealed, and fail-closed over complete evidence.
- Bind every generated release artifact and served download to the bytes from exactly one pack.

## Scope Boundaries
- In scope: a repository-local Q3 contract, tests, gate catalog/index registration, and retained evidence.
- Read-only consumption of the exact roadmap and accepted GS2-06.5 receipt is allowed.
- Out of scope: production GitHub settings, workflows, releases, tags, packages, feeds, environments, deployments, stable releases, and GS2-06.7.

## Policy Pointers
- The canonical roadmap at `.github` revision `185494fa8ba3986834141c2ddc4e8325410df260` and accepted GS2-06.5 receipt are authority.
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Tier 1 contracted change: signature, implementation, gates, tests, and evidence land together.
