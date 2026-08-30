---
schemaVersion: 1
workId: 104-gs2-03-7-supply-chain
title: GS2-03.7 reproducibility and supply-chain checks
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

# GS2-03.7 reproducibility and supply-chain checks Charter

## Identity
- Work id: `104-gs2-03-7-supply-chain`
- Lifecycle stage: charter
- Status: chartered

## Principles
- One package operation creates the candidate bytes; every later channel, evidence, and consumer step must bind those same bytes.
- The org GitHub Packages NuGet feed is the only pre-production publication authority for this unit; nuget.org, releases, stable tags, and deployments remain forbidden.
- A green workflow is insufficient without an independent served-byte download and clean-consumer execution receipt.

## Scope Boundaries
- In scope: the Protocol package, deterministic SPDX and in-toto evidence, exact-byte GitHub Packages candidate publication, independent download, and clean-consumer execution.
- Out of scope: stable/production publication, nuget.org, releases/tags, deployment, runtime adapters, production writes, and GS2-03.8.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 104-gs2-03-7-supply-chain`.
