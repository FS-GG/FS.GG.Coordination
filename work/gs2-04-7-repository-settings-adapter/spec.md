---
schemaVersion: 1
workId: gs2-04-7-repository-settings-adapter
title: Gs2 04 7 Repository Settings Adapter
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# GS2-04.7 Repository/Settings Adapter Specification

Prose status: specified

## User Value
FS.GG.Coordination can deterministically inspect and plan GitHub repository settings without converting missing permission, platform unavailability, incomplete reads, stale state, or ambiguous responses into false compliance or success.

## Scope
- SB-001: Repository-local Q3 typed adapter, canonical complete observation and desired-state codecs, exact-prestate minimal planning, outcome reconciliation, post-state verification/repair intent, tests, and retained evidence.
- SB-002: Cover repository identity/default branch, custom properties, branch/tag rulesets and effective rules, merge and Actions policies, environments, releases/tags, code security, dependency features, and immutable-release capability.

## Non-Goals
- SB-003: No live GitHub writes, organization/repository settings mutation, production credential, secret value, deployment, publication, stable release, runtime cutover, or Q4 sandbox claim.
- SB-004: Do not implement GS2-04.8 Actions/release/feed behavior or any successor unit.

## User Stories
- US-001 (P1): As an operator or auditor, I can inspect each settings surface completely and distinguish its exact value from unsupported, unauthorized, unavailable, incomplete, or unreadable evidence.
- US-002 (P1): As a settings planner, I can derive a deterministic minimal plan bound to exact pre-state and desired state without exposing secrets or disturbing unrelated controls.
- US-003 (P1): As an executor or reviewer, I can reconcile stale, refused, accepted, and indeterminate outcomes only through authoritative post-state evidence and an explicit repair disposition.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given a repository observation, when identity is validated, then exact repository node/id/name/owner/default-branch facts bind every included surface and mismatches refuse authority.
- AC-002 [US-001] [FR-002]: Given paginated observations for every registered surface, when normalized, then canonical fingerprints preserve complete supported values and explicit unauthorized, unavailable, incomplete, and unreadable outcomes without inventing absence or compliance.
- AC-003 [US-001] [FR-003]: Given ruleset, bypass, effective-rule, merge, Actions, environment, release/tag, security, dependency, custom-property, and capability observations, when validated, then exact typed values are preserved and secret material is rejected or redacted.
- AC-004 [US-002] [FR-004]: Given a complete current observation and desired state, when planning, then only minimal deltas are produced, unrelated settings remain unchanged, unsupported controls are explicit, and every operation binds stable identity, pre-state/desired digests, and least permission.
- AC-005 [US-002] [FR-005]: Given incomplete, unauthorized, unavailable, unreadable, or stale pre-state, when planning is requested, then the adapter refuses to guess and requires complete authoritative reread/replan.
- AC-006 [US-003] [FR-006]: Given an apply result and post-state observation, when reconciled, then accepted requires exact verified post-state, stale or indeterminate requires reread/replan, definite refusal remains failure, and rollback or forward-repair intent is explicit.
- AC-007 [US-001] [FR-007]: Given a no-op or platform-specific unsupported feature, when evaluated, then the adapter emits deterministic evidence without inventing an operation or weakening an unrelated supported control.
- AC-008 [US-001] [FR-008]: Given the registered Q3 gate and independent fixture suite, when every required mutant is applied, then each mutant is red while the unmutated candidate remains green and evidence binds the exact candidate.

## Functional Requirements
- FR-001: The adapter MUST bind every observation to a canonical repository identity containing exact node id, database id, owner/name, default branch, visibility, and source revision; contradictory identities or default branches MUST refuse. (covers AC-001)
- FR-002: The adapter MUST require complete pagination and endpoint coverage, canonicalize per-surface observations, compute stable surface and aggregate fingerprints, and preserve `supported`, `unauthorized`, `unavailable`, `incomplete`, and `unreadable` as distinct typed outcomes that never imply absence or compliance. (covers AC-002)
- FR-003: The adapter MUST represent custom-property values; branch and tag rulesets including target patterns, enforcement, rules, and bypass actors; effective branch rules; merge policy; Actions permissions, allow-list, SHA policy, workflow token defaults, and PR approval setting; environments without secrets; release/tag controls; code-security controls; dependency graph, alerts, and Dependabot features; and immutable-release capability. Secret names MAY be retained where required, but secret values MUST be rejected and never serialized. (covers AC-003)
- FR-004: The adapter MUST canonicalize desired state and derive a deterministic minimal ordered plan that binds repository identity, complete pre-state fingerprint, desired-state digest, stable operation identity, exact endpoint/surface, and least required permission; it MUST preserve unrelated settings and surface unsupported desired controls explicitly. (covers AC-004)
- FR-005: The adapter MUST refuse planning from partial, unauthorized, unavailable, unreadable, contradictory, or stale observations; concurrent or stale pre-state MUST require a complete authoritative reread and replan before any effect. (covers AC-005)
- FR-006: The adapter MUST reconcile `accepted`, `stale-prestate`, `definite-refusal`, and `indeterminate-requires-reread` results without inferring success from a transport response; accepted requires exact authoritative post-state, and every nonmatching result MUST yield deterministic rollback or forward-repair intent appropriate to the observed state. (covers AC-006)
- FR-007: The adapter MUST emit a stable no-op plan only when complete observation equals desired state, preserve unrelated supported controls when another surface is unsupported, and treat platform/version capability differences as evidence rather than silent omission. (covers AC-007)
- FR-008: The registered `github-repository-settings-contract` Q3 validator and independent tests MUST cover positive cases plus pagination, repository-identity, custom-property, ruleset-target, bypass-actor, effective-rule, merge-policy, Actions-permission, environment, release, tag, security, dependency-feature, unsupported-capability, unauthorized, unavailable, incomplete-observation, stale-prestate, indeterminate-result, unrelated-setting, no-op, and secret-redaction mutations with exact-candidate evidence. (covers AC-008)

## Ambiguities
- AMB-001: The canonical ordering and stable identity of heterogeneous settings operations must be fixed without depending on GitHub response order.
- AMB-002: Environment and secret metadata must prove configuration without retaining secret values.
- AMB-003: Post-state mismatch may admit rollback, forward repair, or reread-only disposition depending on what was authoritatively observed.

## Public Or Tool-Facing Impact
- Add a public `.fsi` repository/settings adapter surface in `FS.GG.Coordination.GitHub`.
- Add one repository-owned Q3 validator and retained typed result artifact; do not change the registered command identity.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work gs2-04-7-repository-settings-adapter`.
