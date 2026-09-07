---
schemaVersion: 1
workId: 318-gs2-07-6-registration
title: GS2-07.6 queue sandbox/pilot registration
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

# GS2-07.6 queue sandbox/pilot registration Charter

## Identity
- Register exactly the `GS2-07.6` queue sandbox/pilot authority at the accepted GS2-07.5 and roadmap frontier; this item does not execute the pilot.

## Principles
- Pin the exact roadmap bytes and prerequisite receipt, preserve every accepted predecessor contract, and add only one new unit.
- Keep the permission ceiling isolated and reversible: no fleet enablement, ordinary production queue mutation, release, package, or successor authority.
- Treat provider capability as typed input. Unsupported or unknown ruleset/branch-protection capability must refuse unless an explicitly named public representative is used or the dedicated sandbox undergoes a bounded, recorded, no-secrets public-visibility transition with exact rollback to private and verified readback.

## Scope Boundaries
- In scope: catalog and gate registration, architecture coverage, documentation, SDD/readiness sources, feedback report, and later independent critique artifact.
- Register immutable Q4 sandbox and Q6 recovery command identities whose exit contract covers admission, exact base movement, required-check growth, expiry, interruption/failure recovery, deterministic retry, compensation/rollback, cleanup/readback, and fail-closed negative controls.
- Out of scope: queue pilot behavior, live visibility or settings mutation, fleet enablement, production writers, acceptance-receipt creation, and any GS2-07.7 authority or inspection.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Exact roadmap: revision `7e5754e23d274b31d21f9a2b4c0c0a00265ee366`, SHA-256 `33d303a888752d0b0f53e5443b2322bd601ebce43c86166ab6dc8d8387bd82ee`.
- Accepted prerequisite: `GS2-07.5`, receipt digest `dd321136fe28e135ba5ee29a3b81a2041b81c8eb29126762cf893bb98ece34d8`.
- Dedicated sandbox preflight: `FS-GG/FS.GG.GitHub.Substrate.Sandbox` repository id `1353050537`, private at `8fc2b1c6f492...`; rulesets and branch protection currently return provider-capability HTTP 403.
- Next lifecycle action: `fsgg-sdd specify --work 318-gs2-07-6-registration`.
