---
schemaVersion: 1
workId: 79-optimize-canonical-quint-q1-q2
title: Optimize canonical Quint Q1/Q2 execution
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

# Optimize canonical Quint Q1/Q2 execution Charter

## Identity
- Work id: `79-optimize-canonical-quint-q1-q2`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Preserve fail-closed qualification: performance work must not weaken any positive property, negative control, pin, timeout, or evidence obligation.
- Prefer eliminating repeated work and exposing independent critical paths over adding opaque caches.
- Make Q1 and Q2 separately attributable even when they share deterministic preparation.
- Adopt concurrency or long-lived processes only when hosted measurements demonstrate a material, repeatable improvement without semantic drift.
- Keep the formal runner independent from the SDD lifecycle of any particular roadmap item.

## Scope Boundaries
- Optimize canonical Quint Q1/Q2 preparation, execution topology, phase attribution, and retained evidence.
- Preserve the eight positive invariants and all 51 mutation controls, including the exact pinned toolchain and bounded execution behavior.
- Permit a dedicated hosted Quint job so formal qualification can start independently of build and architecture tests.
- Exclude inductive-invariant mode, qualification reuse across workflow runs, and the broader terminal-check consolidation owned by follow-on items.
- Keep SDD lifecycle ownership separate from the reusable formal qualification runner.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 79-optimize-canonical-quint-q1-q2`.
