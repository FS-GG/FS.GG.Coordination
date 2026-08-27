---
schemaVersion: 1
workId: 26-canonical-quint-protocol
title: GS2-02.1 canonical literate Quint protocol
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

# GS2-02.1 canonical literate Quint protocol Charter

## Identity
- Work id: `26-canonical-quint-protocol`
- Lifecycle stage: charter
- Status: chartered

## Principles
- The accepted GitHub Substrate v2 roadmap bytes and unit receipts are authority; Project fields and prose summaries are not.
- The canonical behavioral source is literate Quint consumed through the pinned published `FS.GG.SDD.Artifacts` profile; no local extractor, profile fork, or parallel F# protocol AST is permitted.
- Generated Quint and compiled-contract views are deterministic derivatives, never independently authored sources.
- Every qualification gate is closed, exact-candidate-bound, and carries an executable negative control.
- Q0's scheduled-complete-audit posture remains authoritative; this work creates no hosted runtime or production mutation authority.

## Scope Boundaries
- Execute only `GS2-02.1`: establish the literate source, baseline vocabulary, deterministic extraction/identity contract, and its Q1/pure-Q2 qualification gate.
- Preserve accepted GS2-01 receipts and the pinned roadmap bytes while replacing rejected `GS2-01.9` in the consumer index with the exact `GS2-02.1` contract.
- Defer detailed authority bindings, observation algebra, lifecycle semantics, relation/stream/mutation algebra, durable plans, desired state, compiled outputs, and identity proofs to GS2-02.2 through GS2-02.11.
- Exclude deployment, environments, secrets, ingress, webhooks, event subscriptions, package publication, production writes, and successor acceptance receipts.
- Keep SDD lifecycle ownership separate from optional Governance enforcement.

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Governance files are optional compatibility pointers and are not evaluated by this command.

## Lifecycle Notes
- Delivery route: `sdd-required`, decision revision 1, work id `26-canonical-quint-protocol`.
- Required qualification: Q1 and the pure portion of Q2, plus locked build, unit, architecture, evidence-storage, exact-head CI, and independent review.
- Next lifecycle action: `fsgg-sdd specify --work 26-canonical-quint-protocol`.
