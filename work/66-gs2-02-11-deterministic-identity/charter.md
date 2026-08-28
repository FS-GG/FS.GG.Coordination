---
schemaVersion: 1
workId: 66-gs2-02-11-deterministic-identity
title: GS2-02.11 deterministic identity
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

# GS2-02.11 deterministic identity Charter

## Identity
- Implement the ratified GS2-02.11 deterministic behavioral-identity unit for the canonical literate Quint protocol compiler.
- Authority is unit contract `d1a50fef666ea9adc9e7710100d008213f28fbb88068a20a347a193764fd33be`, after accepted GS2-02.10 receipt `b1cbbd1d0e2e57af4e2138616e3d4cf243e5ef7e380182a77912b0ef6540cb54`.

## Principles
- The literate Quint source remains the sole behavioral authority; generated `.qnt`, JSON, Markdown, and F# artifacts remain projections.
- Equivalent supported authoring forms must converge before identity is computed, while semantic changes must remain visible as stable diffs.
- Unsupported source, extractor, Quint, profile, or schema versions fail before any execution boundary.
- Every new gate ships with a bounded fail-before mutation and an observed pass-after result.

## Scope Boundaries
- In: repository-local extraction, normalization, behavioral identity, semantic diff, version compatibility, generated contract facts, tests, and Q1/Q2 evidence.
- Out: network access, GitHub mutation, independent qualification-system behavior, hosted runtime, deployment, publication, production writes, and GS2-03 work.

## Policy Pointers
- Follow `.fsgg/constitution.md` principles I–III, VI–VIII, the pinned GS2 roadmap, ADR-0077, and the accepted FS.GG Quint profile.
- Preserve the permission ceiling and exact gate-command bindings in `eng/github-substrate-v2-units.json`.

## Lifecycle Notes
- Tier 1: the canonical schema/generated-view contract changes, so specification, signatures where applicable, generated artifacts, tests, docs, mutation evidence, and independent review land together.
- No successor unit may be inspected or implemented inside this unit invocation.
