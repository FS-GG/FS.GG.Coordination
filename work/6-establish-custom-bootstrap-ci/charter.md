---
schemaVersion: 1
workId: 6-establish-custom-bootstrap-ci
title: GS2-01.5 Establish custom bootstrap CI
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

# GS2-01.5 Establish custom bootstrap CI Charter

## Identity
- Work id: `6-establish-custom-bootstrap-ci`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Make every bootstrap qualification claim an explicit, named, independently failable gate.
- Bind CI results to the exact checked-out candidate and use only locked, published dependencies.
- Keep v1 coordination review, delivery, done, deployment, and production mutation authority out of v2 bootstrap CI.

## Scope Boundaries
- Cover deterministic build, compiler/unit and architecture tests, dependency/security policy, package/install smoke, and evidence-manifest validation.
- Retain compact evidence and adversarial controls; defer releases, deployment, live GitHub integration, and production events to their later GS2 units.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.
- The authoritative external scope is GS2-01.5 in the GitHub Substrate v2 roadmap and `FS-GG/FS.GG.Coordination#6`.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 6-establish-custom-bootstrap-ci`.
