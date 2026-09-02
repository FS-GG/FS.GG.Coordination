---
schemaVersion: 1
workId: 232-ruleset-plans
title: Ruleset Plans
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/232-ruleset-plans/spec.md
publicOrToolFacingImpact: true
---

# Ruleset Plans Clarifications

## Source Specification
- work/232-ruleset-plans/spec.md

## Clarification Questions
- CQ-001: Which profile facts select a writable plan, and what happens to an external observe-only profile?
- CQ-002: When may the plan enable merge queue and which required checks enter the default-branch ruleset?
- CQ-003: Which bypass and exception shapes are admissible?
- CQ-004: What is the secure retained target for repository merge behavior and release tags?

## Answers
- CQ-001: The exact accepted profile selects one of `authority`, `framework`, or `hosted-non-participant`; all organization-administered profiles receive a sealed desired target. An external observe-only profile receives a sealed non-mutable disposition with no ruleset bodies, never an implied write plan.
- CQ-002: Carry every stable effective identity from the exact sealed census into the default-branch status-check rule. Enable merge queue only when that census proves both pull-request and merge-group readiness; otherwise retain an explicit disabled reason. No exception may enable an unready queue; a bounded exception may only disable a ready queue. A complete negative census remains plan evidence rather than becoming a refusal.
- CQ-003: Bypass actors must resolve by exact id and actor kind through the approved registry, be explicitly admitted for the selected profile class, and be unique. A bypass-principal exception projects that exact approved principal into both branch and tag targets. Exceptions must name a stable id, owner, rationale, exact rule scope, approval time, start time, and expiry; the active window must be positive, current at compilation, and no longer than 30 days. Current-policy evidence carries its own exact repository identity, completeness bit, and observation time and is freshness-checked independently of the plan wrapper. Baseline inputs carry neither bypass nor exception.
- CQ-004: Organization-administered targets require pull requests, stale-review dismissal, conversation resolution, strict required checks, deletion and non-fast-forward protection on the default branch, squash as the sole merge method, auto-merge and delete-branch-on-merge enabled, and merge commits/rebase disabled. `refs/tags/v*` is immutable and signature-required. Minimum approvals remains zero because structured Coordination review is the current accepted review authority; changing that organization constraint is not inferred here.

## Decisions
- DEC-001: Profile role and administration select a closed plan class; external authority is retained observe-only and cannot yield a mutation-shaped target.
- DEC-002: The sealed GS2-06.2 census is the sole required-check input. Complete noncompliance disables merge queue with a reason; incomplete or altered census authority refuses.
- DEC-003: Bypass is deny-by-default and registry-bound. Exceptions are explicit, current, scope-limited, and capped at 30 days; no latest-wins or indefinite compatibility escape exists.
- DEC-004: The target plan is desired state rather than a delta and contains no apply operation. All rule, repository behavior, provenance, bypass, and exception facts participate in stable ordering and the exact replay seal.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 232-ruleset-plans`.
