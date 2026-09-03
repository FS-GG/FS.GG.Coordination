---
schemaVersion: 1
workId: 288-gs2-06-8-fleet-dry-plans
title: GS2-06.8 fleet dry plans
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

# GS2-06.8 fleet dry plans Charter

## Identity
- Work id: `288-gs2-06-8-fleet-dry-plans`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Canonical roadmap bytes, the registered GS2-06.8 contract, and accepted GS2-06.1 through GS2-06.7 receipts are immutable authority.
- Inspection is complete, read-only, terminally paginated, and explicit about unavailable or insufficient authority; absence is never inferred from a failed read.
- Planning is pure, deterministic, minimal, least-privileged, bound to exact pre-state and desired state, and has no apply boundary.
- Canonical serialization, independent review, and authoritative re-inspection are distinct proofs; relevant drift makes a plan stale.
- Every checkable claim carries retained evidence, and every gate ships with generated and independently authored failure controls.

## Scope Boundaries
- In scope: one additive qualification contract, canonical plan schema/serializer/parser, complete ten-repository live read-only evidence, tests, Q5 validator, architecture documentation, SDD artifacts, candidate gate artifacts, and the post-merge acceptance receipt.
- In scope: explicit supported, unsupported, unauthorized, unavailable, incomplete, unreadable, stale, indeterminate, external-observe-only, and no-op dispositions.
- Out of scope: any GitHub setting, ruleset, workflow, repository, permission, release, package, environment, deployment, tag, branch, feed, or fleet mutation; any apply command or mutation transport; stable publication; canonical Quint changes; and all GS2-07.1 work.
- The issue remains open across implementation and receipt PRs using the verified markerless In progress handoff.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 288-gs2-06-8-fleet-dry-plans`.
