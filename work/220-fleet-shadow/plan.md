---
schemaVersion: 1
workId: 220-fleet-shadow
title: Fleet Shadow
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/220-fleet-shadow/spec.md
sourceClarifications: work/220-fleet-shadow/clarifications.md
sourceChecklist: work/220-fleet-shadow/checklist.md
publicOrToolFacingImpact: true
---

# Fleet Shadow Plan

Prose status: planned

## Source Snapshot
- spec: work/220-fleet-shadow/spec.md sha256:7a21dd937ead3da34dc2226899fa7a7f1417fbf3fd9738fb3075c2085cf30535 schemaVersion:1
- clarifications: work/220-fleet-shadow/clarifications.md sha256:1191a794c2251cfbe923880ff064cb954cf56621f75423ad39202bc60ef7c5f8 schemaVersion:1
- checklist: work/220-fleet-shadow/checklist.md sha256:673e6c02d259fb6812222abd9e1f6f1cbddf3492c36be54ed6ac7cdf7dc66feb schemaVersion:1

## Plan Scope
- Work item 220-fleet-shadow is planned from the current specification, clarification, and checklist facts.
- Requirement count: 4.
- Clarification decision count: 0.
- Checklist result count: 4.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a pure FleetShadowAdapter whose canonical length-framed seal
  covers the roster, permission manifest, bounded observation window, source revisions, completeness proof,
  raw decisions, normalized decisions, and divergence evidence in stable repository/item order.
- PD-002 [AC-001] [FR-002] complete: Validate exact roster/item coverage before comparing; accept equality
  directly and require exactly one supported accountable classification for each unequal pair, with zero
  unexplained divergences. Refuse every incomplete, stale, crossed, altered, unauthorized, mutation-bearing,
  attempted-mutation, partial, unreadable, and indeterminate observation.
- PD-003 [AC-001] [FR-003] complete: Register GS2-05.8 and a Q4 `github-fleet-shadow-contract` whose corpus,
  independent expectations, offline validator, unit tests, and architecture tests bind the accepted
  GS2-05.7 receipt and roadmap revision while proving the canonical Quint tree is unchanged.
- PD-004 [AC-001] [FR-004] complete: Capture a fresh read-only live-fleet evidence document through the
  declared six read capability classes, retain only public identities and fingerprints, and validate it
  offline. No adapter or qualification surface exposes an apply or production-write operation.

## Contract Impact
- PC-001 [PD-001] command report: Add FleetShadowAdapter and public signature, the fleet-shadow
  qualification contract, GS2-05.8 registry entry, exact control corpus, independent expectations, live
  observation evidence, and validator without changing existing adapter contracts.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Run warning-free build, focused and full unit/architecture suites,
  the exact fleet-shadow control validator, evidence self-test, canonical Quint verification, and SDD
  receipt-bound verify/ship gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: This is a temporary additive read-only comparison. It grants no authority,
  performs no migration or cutover, and retained evidence is immutable; later retirement deletes the
  callable shadow surface rather than preserving a second decision authority.

## Generated View Impact
- GV-001 [PD-001] workModel: Regenerate the SDD work model and agent projections from the authored
  source set after each lifecycle change, and bind final verify/ship reports to observed test receipts.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 220-fleet-shadow`.
