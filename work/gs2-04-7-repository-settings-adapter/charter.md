---
schemaVersion: 1
workId: gs2-04-7-repository-settings-adapter
title: GS2-04.7 repository/settings adapter
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

# GS2-04.7 repository/settings adapter Charter

## Identity
- Implement the registered GS2-04.7 Q3 adapter that makes GitHub repository and settings observations and plans an executable, typed boundary in `FS.GG.Coordination`.
- Bind the implementation to roadmap contract `a04f3ef2182825d3b9fa71081fe56cee8ee85e79bdee49e25dc404f9aaf4c3d9` and accepted prerequisite GS2-04.6.

## Principles
- Complete, exact repository identity and paginated per-surface observations precede every plan; unauthorized, unavailable, incomplete, and unreadable never mean absent or compliant.
- Plans are deterministic and minimal, bind canonical complete pre-state plus desired-state digests, preserve unrelated settings, name least required permissions, and carry stable operation identities.
- Stale or indeterminate effects require authoritative reread and replan; an API response never proves success without exact post-state verification.
- Secret values are neither accepted nor emitted. Q3 remains offline: local loopback fixtures and already-protected administrative observations are allowed, but no live GitHub mutation or Q4 sandbox claim is authorized.

## Scope Boundaries
- In: typed repository/settings domain surface, canonical codec and fingerprints, complete observation validation, deterministic planning and outcome reconciliation, post-state/repair intent, executable Q3 validator, independent tests, and evidence.
- Covered surfaces: repository identity/default branch, custom properties, branch/tag rulesets and bypass/effective rules, merge policy, Actions policy, environments, releases/tags, code security, dependency features, and immutable-release capability.
- Out: live GitHub writes or settings changes, production credentials, secret values, deployment/publication, runtime cutover, Actions/release/feed adapter work, GS2-04.8+, and new claims about Q4 behavior.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- The canonical roadmap is `FS-GG/.github@66dc685921e5465e503e3932eab7faff0ad2099b:docs/github-substrate-v2-roadmap.md`.
- The registered unit contract and gate are `eng/github-substrate-v2-units.json` and `eng/github-substrate-v2-gates.json` at protected merge `61bb7dbf2abf6c26c8ebb92a5e6d1591f3e8b196`.
- Existing administrative reports and observations are inputs to validate, never authority to perform a new mutation.

## Lifecycle Notes
- Tier 1 contracted change: declare the `.fsi` surface before implementation and land signatures, validator, tests, retained evidence, and generated readiness together.
- No deferral may silently move a required GS2-04.7 contract clause into GS2-04.8 or GS2-04.9.
