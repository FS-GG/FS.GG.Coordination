---
schemaVersion: 1
workId: 220-fleet-shadow
title: Fleet Shadow
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/220-fleet-shadow/spec.md
publicOrToolFacingImpact: true
---

# Fleet Shadow Clarifications

## Source Specification
- work/220-fleet-shadow/spec.md

## Clarification Questions
No clarification questions recorded.

## Answers
No clarification answers recorded.

## Decisions
- CD-001: The fleet roster is an immutable, fingerprinted ordered source. Every roster entry and every
  observed coordination item appears exactly once; pagination termination and item counts are sealed.
- CD-002: One observation has bounded begin/end instants and exact per-source revisions. Later live churn
  creates a successor observation and never rewrites the retained one.
- CD-003: Equal normalized decisions require no divergence row. Unequal decisions require exactly one
  `v1-defect`, `v2-defect`, or `intentional-versioned-change` classification with non-empty accountable
  evidence. The raw decisions remain part of the seal.
- CD-004: Read-only is positively enumerated as roster, metadata, issue, Project, journal, and check reads.
  Mutation capability and attempted mutation are independently forbidden; the shadow exposes no apply path.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 220-fleet-shadow`.
