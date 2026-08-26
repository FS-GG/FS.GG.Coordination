---
schemaVersion: 1
workId: 4-pin-published-quint-kernel
title: GS2-01.4 Pin the published Quint-capable kernel
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

# GS2-01.4 Pin the published Quint-capable kernel Charter

## Identity
- Work id: `4-pin-published-quint-kernel`
- Lifecycle stage: charter
- Status: chartered

## Principles
- Consume semantic authority only through the exact published FS.GG.SDD package accepted by Q1 and ADR-0077.
- Fail closed when package identity, embedded Quint toolchain identity, profile identity, or bundle fingerprint differs.
- Keep producer machinery in FS.GG.SDD; this repository owns only the qualification-facing consumer boundary.

## Scope Boundaries
- Pin and verify the released artifact without adding protocol semantics, extraction, profile definition, ITF machinery, GitHub writes, or deployment authority.
- Preserve the one-way solution boundary established by GS2-01.3 and deterministic locked restore.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.
- The authoritative external decisions are ADR-0077, Q1 in `FS-GG/FS.GG.SDD#924`, and the GS2-01.4 roadmap acceptance text.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 4-pin-published-quint-kernel`.
