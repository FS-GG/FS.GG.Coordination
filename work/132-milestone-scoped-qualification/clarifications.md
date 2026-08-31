---
schemaVersion: 1
workId: 132-milestone-scoped-qualification
title: Milestone Scoped Qualification
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/132-milestone-scoped-qualification/spec.md
publicOrToolFacingImpact: true
---

# Milestone Scoped Qualification Clarifications

## Source Specification
- work/132-milestone-scoped-qualification/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking answered: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking answered: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking answered: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking answered: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking answered: Resolve source ambiguity AMB-005 before checklist.

## Answers
- CQ-001: The qualification plan owns ordered path selectors for the formal subject. The first set contains `global.json`, dependency locks, the plan and generated workflow, the canonical gate and tool bootstrap, both Quint validators/configurations/baselines, compiled-contract generator inputs, all protocol source/generated outputs, the Qualification.Contracts implementation used by the validator, and formal evidence schemas/fixtures. The current validator's repository-wide parallel-AST scan moves to the always-current architecture suite so unrelated adapter source is not falsely retained in the expensive subject. Selector overlap, an empty selector, an unmatched required selector, or a renderer/validator disagreement refuses.
- CQ-002: Add tracked `eng/milestone-qualification.json`. It names one active parent, ordered child ids and contract digests, accepted child receipt path/digests, policy version, mode, and optional comprehensive-boundary kind. Ordinary state is `scoped`. The final child changes the state to `comprehensive`; terminal evidence supplies the exact head/tree without creating a self-referential file digest. After protected closure evidence is retained, the next parent registration rotates state back to `scoped`; historical closure manifests/receipts remain immutable.
- CQ-003: Reuse the bounded Actions artifact discovery boundary, but index canonical artifacts independently as `canonical-quint-<formal-subject-sha256>`. Only an original successful canonical execution under the same workflow/gate/tool/environment/policy contract is a selectable source; a prior reuse points back to that original source rather than becoming transitive authority. Absence or API failure before selection executes; contradiction, expiry, loss, or changed bytes after selection refuses.
- CQ-004: The first evaluator uses a rolling 14-day window, freshness no older than 36 hours, and a categorical minimum of five applicable observations. `reduce` additionally requires declared closure equivalence, no closure-discovered miss or high-impact actionable defect in the window, high-cost classification, and low unique yield. These thresholds are plan policy, not universal statistics; the daily output exposes the sample and confidence state and a reviewed policy change may adapt them.
- CQ-005: Immutable execution observations come from exact workflow run/job/artifact identities. Unique actionable defects and closure-discovered misses come from a tracked append-only attribution ledger whose records bind defect id, detecting run/gate, responsible gate, discovery boundary, outcome class, review evidence, and digest. Missing attribution is `unattributed`, never zero yield. The evaluator joins exact identities and refuses reduction when failures are unclassified or attribution evidence is stale/malformed.

## Decisions
- DEC-001: [CQ:CQ-001] [AMB:AMB-001] resolved. The typed plan owns exact formal-subject selectors; move the cheap repository-wide parallel-AST guard into current-tree architecture tests and reject selector ambiguity or drift.
- DEC-002: [CQ:CQ-002] [AMB:AMB-002] resolved. Use a generic tracked milestone state with `scoped|comprehensive` mode and ordered child/receipt bindings; exact head/tree remain terminal runtime bindings.
- DEC-003: [CQ:CQ-003] [AMB:AMB-003] resolved. Discover only original content-addressed canonical execution artifacts and preserve preselection execute versus post-selection refuse semantics.
- DEC-004: [CQ:CQ-004] [AMB:AMB-004] resolved. Start with a 14-day/36-hour/five-observation categorical policy; all thresholds remain explicit reviewed plan data and recommendation output remains observational.
- DEC-005: [CQ:CQ-005] [AMB:AMB-005] resolved. Join immutable run observations with a digest-bound attribution ledger; absence is unattributed risk and closure misses bias cadence inward.
- DEC-006: resolved. A `reduce` recommendation never edits plan or workflow. Only a separately reviewed versioned policy change may change cadence, and declared comprehensive or high-blast-radius minimums win over telemetry.
- DEC-007: resolved. Add the approximately daily evaluator to the generated bootstrap workflow under a schedule trigger; scheduled economics runs do not execute qualification gates, while pull-request and protected-main events do not depend on the scheduled job.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 132-milestone-scoped-qualification`.
