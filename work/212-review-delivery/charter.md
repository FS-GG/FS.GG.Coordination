---
schemaVersion: 1
workId: 212-review-delivery
title: GS2-05.6 review and delivery
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

# GS2-05.6 review and delivery Charter

## Identity
- Work id: `212-review-delivery`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Treat accepted GS2-05.5 receipt `f382502968cf634bf93c7318d24f629eb3ccfbbac6cf759a99434f1a33975059`, roadmap projection `.github@9bd7849e4c90adb89a08f6377d807422504213b1`, and the existing Quint review-epoch model as immutable prerequisites.
- Separate a stable review chain from immutable full-snapshot epochs. A pass belongs to one exact epoch and current phase seat, never to a mutable pull-request identity.
- Preserve one accountable authority across snapshot changes while requiring a fresh phase seat for every new epoch and every same-epoch succession.
- Keep historical verdicts as append-only evidence but authorize effects only through a complete current Review-journal reread.
- Treat merge acceptance and protected-main verification as different states. Done requires the exact merge commit and successful protected-main run.
- Seal delivery and done receipts in the Operation journal and bind them to current review authority, merge evidence, run evidence, commit, and fencing generation.
- Preserve deterministic planning, exact replay, typed fail-closed diagnostics, bounded work, and immutable formal source.

## Scope Boundaries
- In: additive public F# contracts, pure/controlled-fixture review and delivery planning, Review/Operation journal composition, Q3 registration, independent evidence, focused tests, and the complete SDD lifecycle.
- Out: production GitHub writes or credentials, live reviewer dispatch, merge execution, branch administration, later lifecycle projection/fleet/cutover units, publication, and stable release.
- Out: modifying the Quint protocol, accepting mutable projections or historical passes as current authority, or treating merge success as protected-main verification.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 212-review-delivery`.
