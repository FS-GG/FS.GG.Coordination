---
schemaVersion: 1
workId: 20-complete-repository-provisioning
title: Complete Repository Provisioning
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

# Complete Repository Provisioning Charter

## Identity
- Work id: `20-complete-repository-provisioning`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Live GitHub rereads, not prose checkboxes, are authority for protected settings.
- Apply only repository-administrator controls already authorized by the roadmap and fail closed on organization-only surfaces the current credential cannot inspect or change.
- Bind every setting, ruleset identifier, exact required check, team grant, unsupported control, and post-write reread into a deterministic receipt.
- Preserve repository usability: require the six proven exact-head checks and independent pull-request review without personal bypass, direct pushes, force updates, or branch deletion.

## Scope Boundaries
- In scope: repository security settings, Actions policy, merge/default settings, CODEOWNERS, `main` and release-tag rulesets, a versioned desired-state contract, live receipt capture/verification, and GS2-01.1 evidence.
- Out of scope: organization-wide Actions policy, runtime or environment provisioning, webhooks, secrets, production subscriptions, releases, package publication, fleet settings, and GS2-01.9 output.
- Organization-only SHA-pinning or installation changes are recorded as unsupported when authoritative reads or writes return 403; they never become an inferred pass.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 20-complete-repository-provisioning`.
