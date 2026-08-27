---
schemaVersion: 1
workId: 20-complete-repository-provisioning
title: Complete Repository Provisioning
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Complete Repository Provisioning Specification

Prose status: specified

## User Value
Maintainers and contributors receive a fail-closed repository whose protected main branch, release tags, Actions permissions, security scanning, ownership, and live receipts are independently verifiable.

## Scope
- SB-001: Configure only FS-GG/FS.GG.Coordination repository security, Actions, merge, ownership, branch/tag rulesets, contract files, and exact evidence; exclude organization-wide policy, runtimes, environments, webhooks, production subscriptions, releases, packages, fleet repositories, and GS2-01.9 output.

## Non-Goals
- SB-002: Do not implement later lifecycle commands or Governance enforcement in this specification.

## User Stories
- US-001 (P1): As a maintainer, I can merge only an independently reviewed pull request whose six exact qualification checks passed on the current head.
- US-002 (P1): As a security owner, I can prove supported scanning and read-only Actions defaults are active and that moving or unapproved Actions are excluded.
- US-003 (P1): As a release owner, I can create a signed release tag once while updates, deletion, and unsigned tags are refused.
- US-004 (P1): As an auditor, I can replay a compact exact post-state receipt against live GitHub responses and distinguish unsupported organization-only controls from compliant repository settings.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given the reviewed source contract, when ownership is evaluated, then `coordination-maintainers` owns the repository and protected workflow, protocol, contract, evidence, and release surfaces without receiving Admin.
- AC-002 [US-002] [FR-002]: Given repository and organization security and Actions APIs, when provisioning applies, then organization code-security configuration 17 is attached, CodeQL default setup is configured, every supported scanner is enabled, workflow tokens default read-only, workflow self-approval is off, and only GitHub-owned Actions are allowed.
- AC-003 [US-001] [FR-003]: Given the exact six successful GitHub Actions check identities, when `main` changes, then an active no-bypass ruleset requires a pull request, one approval including CODEOWNERS where applicable, resolved conversations, no deletion/force update, and all six current-head checks.
- AC-004 [US-003] [FR-004]: Given a `v*` release tag, when it is created or mutated, then an active no-bypass ruleset requires a signed tag and refuses deletion and non-fast-forward/update behavior.
- AC-005 [US-004] [FR-005]: Given a protected write, when it completes, then a canonical compact receipt binds pre-state, desired contract, operation results, rule ids, authoritative post-state, repository and Actions App identities, unsupported organization SHA enforcement, and rollback/forward-repair instructions.
- AC-006 [US-004] [FR-006]: Given any partial, forbidden, incomplete, stale, or indeterminate API result, when validation runs, then GS2-01.1 remains unaccepted and no runtime, environment, webhook, package, release, fleet, or successor behavior occurs.

## Functional Requirements
- FR-001: The repository MUST contain CODEOWNERS assigning the repository and protected workflow, protocol, qualification-contract, evidence, and release surfaces to `@FS-GG/coordination-maintainers`; live access MUST show that team at Maintain and no unexpected repository team grant. (Stories: US-001; Acceptance: AC-001)
- FR-002: Repository settings MUST preserve public visibility, `main`, Issues, squash-only PR merges, auto-merge, branch deletion, disabled wiki/repository Projects, read-only workflow tokens, and no workflow review approval. Organization code-security configuration 17 MUST be attached and its exact policy projection MUST be bound; CodeQL default setup MUST be configured for Actions with the default query suite and weekly schedule. The dependency graph, Dependabot alerts and security updates, private vulnerability reporting, secret scanning, push protection, and extended metadata MUST be enabled. The effective repository projection MUST record validity checks and non-provider pattern scanning as disabled and license-unsupported while the attached configuration requests them; neither may be reported operational without entitlement. Configuration 17's distinct generic-secrets field MUST remain `not_set` and MUST NOT be conflated with non-provider patterns. Actions MUST use the selected policy with GitHub-owned Actions only. Organization-only SHA enforcement MUST be recorded as unsupported when the current credential receives 403 and MUST NOT be reported compliant. (Stories: US-002, US-004; Acceptance: AC-002, AC-005)
- FR-003: One active branch ruleset MUST target `~DEFAULT_BRANCH`, carry no bypass actor, block deletion and non-fast-forward changes, require a pull request with one approval, stale-review dismissal, last-push approval, CODEOWNERS review and resolved conversations, and require exactly `deterministic-build`, `compiler-and-tests`, `dependency-and-security`, `package-install-smoke`, `bootstrap-recovery`, and `evidence-manifest` from GitHub Actions App id 15368 with strict current-head enforcement. (Stories: US-001; Acceptance: AC-003)
- FR-004: One active tag ruleset MUST target `refs/tags/v*`, carry no bypass actor, block deletion, update, and non-fast-forward mutation, and require signed tags. It MUST NOT create a release, tag, environment, secret, or publication identity. (Stories: US-003; Acceptance: AC-004)
- FR-005: A versioned desired-state contract and deterministic validator MUST fail closed against a canonical compact `fsgg.coordination.repository-settings-receipt/2` document. The receipt MUST bind repository id/node, observed UTC instant, exact merge/security/Actions/team/check/ruleset values, configuration 17 association and policy, CodeQL default setup, the repository workflow-permissions response, the organization Actions-permissions 403, every other per-operation result, unsupported controls, raw-response SHA-256 values, and rollback or forward-repair guidance. (Stories: US-004; Acceptance: AC-005)
- FR-006: The unit MUST remain inside protected repository settings explicitly approved by the organization administrator. Any 403, incomplete page, lost response, unsupported field, stale check identity, unexpected bypass/team/action, or post-write mismatch MUST block acceptance; no GS2-01.9 output or operational runtime surface may be created. (Stories: US-004; Acceptance: AC-006)

## Ambiguities
No material ambiguities recorded.

## Public Or Tool-Facing Impact
- Adds CODEOWNERS, a repository settings contract/validator, protected GitHub rulesets, and a versioned live settings receipt consumed by GS2-01.1 acceptance.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 20-complete-repository-provisioning`.
