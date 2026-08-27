---
schemaVersion: 1
workId: 8-create-github-substrate-v2-work-skill
title: GS2-01.6 Create the bounded roadmap work skill
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

# GS2-01.6 Create the bounded roadmap work skill Charter

## Identity
- Work id: `8-create-github-substrate-v2-work-skill`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat the canonical roadmap and accepted, fingerprint-bound qualification receipts as authority; mutable Project status is visibility only.
- Make every command deterministic, inspectable, fail-closed, and scoped to one explicitly selected stable unit ID.
- Separate generated evidence from independently authored controls and bind both to the exact candidate revision and artifacts.
- Stop at the selected unit's named exit gate; discovering a ready successor never grants scheduling, claim, mutation, or execution authority.

## Scope Boundaries
- Add only the repository-owned `github-substrate-v2-work` skill, its small roadmap command, typed bootstrap index/contracts, documentation, and tests.
- Cover roadmap inspection, prerequisite verification, evidence-manifest creation, relevant Q-gate execution, and boundary refusal.
- Do not mutate GitHub settings, schedule from the Coordination Project, claim unrelated work, execute a successor unit, import v1 completion authority, or add production writes.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.
- The authoritative external scope is GS2-01.6 in the GitHub Substrate v2 roadmap and `FS-GG/FS.GG.Coordination#8`.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 8-create-github-substrate-v2-work-skill`.
