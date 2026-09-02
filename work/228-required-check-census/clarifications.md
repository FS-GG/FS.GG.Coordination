---
schemaVersion: 1
workId: 228-required-check-census
title: Required Check Census
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/228-required-check-census/spec.md
publicOrToolFacingImpact: true
---

# Required Check Census Clarifications

## Source Specification
- work/228-required-check-census/spec.md

## Clarification Questions
- CQ-001: How is one required check identified across classic protection and rulesets when either source may constrain the producing integration?
- CQ-002: What evidence is sufficient to call a required check unconditionally produced for both pull requests and merge groups?
- CQ-003: Which census facts may cross the external contract boundary without coupling consumers to workflow and job names?
- CQ-004: Does a complete but filtered, conditional, or missing-event producer observation make the census unreadable, or does it make readiness false?

## Answers
- CQ-001: Use the exact context plus an optional positive integration identity internally. Context-only entries coalesce only with other context-only entries; integration-bound entries coalesce only on the same positive integration identity. Mixed or conflicting identities for one context refuse rather than guessing GitHub precedence.
- CQ-002: Require one complete producer observation per effective identity, bind its exact workflow digest, and derive each readiness proof from explicit event admission, absence of event-level branch/path/activity restrictions, complete dependencies, and absence of conditional or continue-on-error escapes. Missing or unreadable workflow/job facts refuse compilation.
- CQ-003: Retain exact contexts, authorities, ruleset ids, integration ids, workflow/job provenance, and production proof internally. Expose only schema version, repository identity, authority-source counts, effective requirement count, integration-bound count, unconditional PR count, unconditional merge-group count, readiness booleans, and the deterministic seal.
- CQ-004: A complete noncompliant observation is valid census evidence and must compile to readiness `false`; refusing it would hide the gap, while treating it as ready would falsify it. Exact-seal verification still rejects any alteration from the retained observation.

## Decisions
- DEC-001: Required-check identity is exact and source preserving; ambiguous context/integration combinations are terminal findings, never latest-wins reconciliation.
- DEC-002: Unconditional production is a closed proof over explicit event admission plus the complete workflow/job dependency chain; source reading with an omitted or unknown condition cannot produce a positive result, and a complete negative observation remains a sealed not-ready result.
- DEC-003: The full census remains repository-local evidence for later planning, while the public summary contains stable aggregates and readiness only.
- DEC-004: GS2-06.2 is a pure compiler and validator with no GitHub write, ruleset plan, workflow rewrite, or successor-unit surface.
- DEC-005: The qualification baseline is an exact observation rather than a synthetic green target: no classic protection, active ruleset 21633423 with six required checks, the digest-bound bootstrap qualification workflow, conditional pull-request jobs, and no merge-group trigger.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 228-required-check-census`.
