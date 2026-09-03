---
schemaVersion: 1
workId: 288-gs2-06-8-fleet-dry-plans
title: Gs2 06 8 Fleet Dry Plans
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/288-gs2-06-8-fleet-dry-plans/spec.md
publicOrToolFacingImpact: true
---

# Gs2 06 8 Fleet Dry Plans Clarifications

## Source Specification
- work/288-gs2-06-8-fleet-dry-plans/spec.md

## Clarification Questions
No clarification questions recorded.

## Answers
No clarification answers recorded.

## Decisions
- DEC-001: The exact accepted GS2-06.1 roster is the repository universe. A plan compiler receives a typed observation for each roster row and refuses missing, duplicate, or extra identities; it does not discover repositories opportunistically.
- DEC-002: "All repository settings" means the complete settings surface governed or observed by the accepted GS2-06.1 through GS2-06.7 contracts: repository identity/default branch/features, branch protection and required checks, repository rulesets, Actions permissions and selected-actions policy, workflow permissions, security/update capabilities, environments, release/tag observations, and workflow inventory. Each endpoint carries terminal pagination or a typed non-page proof and its HTTP/permission disposition.
- DEC-003: Production GitHub reads are performed outside the pure contract and retained as canonical evidence. The implementation accepts typed observations, compiles and reviews plans, serializes/reparses them, and re-inspects supplied authoritative observations; it contains no HTTP writer, mutation verb, apply function, or production adapter.
- DEC-004: Desired state is a canonical per-repository digest plus typed desired settings. Supported differences yield minimal ordered operations; external-owner rows are observe-only; unsupported and insufficient-permission settings remain explicit non-applicable/refused dispositions; no-op means complete fresh pre-state already agrees.
- DEC-005: Relevant state is exactly the fields named by the desired setting and operation subjects. Re-inspection drift elsewhere is retained but does not stale or enlarge the plan; any relevant fingerprint change stales it.
- DEC-006: Live API evidence is a read-only provider artifact, not a hosted mutation claim. Exact-head hosted evidence remains required only for the repository's normal build/test/check integration; the implementation and receipt PRs bind successful exact-head and exact-merge runs separately.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 288-gs2-06-8-fleet-dry-plans`.
