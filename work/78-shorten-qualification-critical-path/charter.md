---
schemaVersion: 1
workId: 78-shorten-qualification-critical-path
title: Shorten qualification critical path
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

# Shorten qualification critical path Charter

## Identity
- Work id: `78-shorten-qualification-critical-path`
- Lifecycle stage: charter
- Status: chartered

## Principles
- One reviewed semantic plan owns gate identity, topology, artifacts, permissions, pins, and execution entry points.
- Performance evidence never substitutes for semantic evidence; every optimization retains its independent red route.
- Optimize settled execution and runner consumption, and report runner queue delay separately.
- Prefer compiled pure decisions and thin adapters over repeated FSI startup, YAML parsing, or command-string mirrors.
- Adopt dependency caching only when exact-key miss/hit measurements justify its runtime and complexity cost.

## Scope Boundaries
- Preserve the six existing execution gates and the final exact-head evidence join.
- Own the bootstrap plan, workflow projection, validator core, artifact-action pins, cache adoption decision, complexity budget, and hosted A/B evidence.
- Keep #80's cross-run evidence reuse outside this item; expose the one terminal contract it will later consume.
- Do not weaken canonical Quint, bootstrap recovery, architecture inversions, package-source isolation, dependency census, or exact-head binding.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd specify --work 78-shorten-qualification-critical-path`.
