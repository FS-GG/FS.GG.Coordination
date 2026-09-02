---
schemaVersion: 1
workId: 232-ruleset-plans
title: Ruleset Plans
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/232-ruleset-plans/spec.md
sourceClarifications: work/232-ruleset-plans/clarifications.md
sourceChecklist: work/232-ruleset-plans/checklist.md
publicOrToolFacingImpact: true
---

# Ruleset Plans Plan

Prose status: planned

## Source Snapshot
- spec: work/232-ruleset-plans/spec.md sha256:09112f7b7fb47fe77bc6d0539bd60d39c25c9e70b30d6a2a85de0e8cc4a5562a schemaVersion:1
- clarifications: work/232-ruleset-plans/clarifications.md sha256:dde826df2ad445f12401bca57cbcae6be52c5d207febaeada440dc0108df7300 schemaVersion:1
- checklist: work/232-ruleset-plans/checklist.md sha256:ff64e5e5d2bff8bda994410e79b8c665cfc727a0392dc748a1f88deeed6154c5 schemaVersion:1

## Plan Scope
- Work item 232-ruleset-plans is planned from the current specification, clarification, and checklist facts.
- Requirement count: 1.
- Clarification decision count: 4.
- Checklist result count: 1.

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Add a pure `RulesetPlanAdapter` after the accepted profile and census adapters. Its complete snapshot binds the GS2-06.2 receipt, exact profile report/profile, exact census report, current-policy source binding, bypass registry, exception registry, and observation time. Canonical length-framed serialization covers every input and output field; `verify` recompiles and compares the exact seal.
- PD-002 [AC-001] [FR-001] [DEC-001] [DEC-002] complete: Compile a closed disposition: organization-administered authority/framework/hosted-non-participant profiles receive complete default-branch, release-tag, and repository behavior targets; external observe-only profiles receive no rule bodies and `MutationPermitted=false`. Carry every stable census identity into strict required checks. Enable merge queue only when both PR and merge-group readiness are true; otherwise retain a stable disabled reason without hiding the complete negative evidence.
- PD-003 [AC-001] [FR-001] [DEC-003] [DEC-004] complete: Resolve bypass actors by exact registry id/kind and selected profile class, reject duplicates or unknowns, and validate every exception as uniquely identified, owned, reasoned, scope-limited, current, and at most 30 days. Emit desired state rather than a delta: squash-only, auto-merge, delete branch, protected default branch with PR/review/conversation/check rules, and immutable signed `v*` tags. Expose no plan-application or GitHub transport operation.
- PD-004 [AC-001] [FR-001] complete: Register GS2-06.3 behind the accepted GS2-06.2 receipt. Retain one exact FS.GG.Coordination corpus, independently authored expectations, generated and independent Q3 qualification, an offline validator, focused unit tests, architecture gates, and SDD ship evidence. Do not alter the canonical Quint protocol.

## Contract Impact
- PC-001 [PD-001] [PD-002] [PD-003] command report: Add `RulesetPlanAdapter` and public signature, a ruleset-plan qualification contract, GS2-06.3 registry entry, retained corpus and independent expectations, and an exact offline validator. Existing profile, census, settings, and canonical Quint contracts remain unchanged; no apply operation is exposed.

## Verification Obligations
- VO-001 [PD-001] [PD-002] [PD-003] [PD-004] [PC-001] semanticTest: Run the exact plan validator against digest-bound profile, census, current-policy, bypass, and exception evidence; generated and independently authored receipt/profile/census/source binding, branch target, tag target, required-check retention, reviews, conversation resolution, merge methods, auto-merge, merge queue, branch deletion, bypass, exception identity/window/scope, observe-only, freshness, ordering, replay, seal, prerequisite, Quint, and no-apply inversions; warning-free build; focused and full unit/architecture suites; evidence self-test; and receipt-bound SDD analyze, verify, and ship gates.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: This is an additive desired-state compiler over accepted evidence. It performs no GitHub mutation and grants no administrative permission; GS2-12 alone may consume an accepted plan under explicit cutover authority.

## Generated View Impact
- GV-001 [PD-001] [PD-004] workModel: Regenerate the SDD work model and agent projections from the authored source set after lifecycle changes, and bind final verify/ship reports to observed ruleset-plan test and qualification receipts.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 232-ruleset-plans`.
