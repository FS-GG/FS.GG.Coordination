---
schemaVersion: 1
workId: 1081-workspace-obligations-compiler
title: Source-bound workspace obligation compiler
stage: plan
changeTier: tier1
status: planned
publicOrToolFacingImpact: true
---

# Compiler implementation plan

## Plan Scope
Implement one maintained nonpackable compiler/reference consumer and isolated control project under the admitted paths.

## Plan Decisions
- PD-001 [FR-001] [DEC-001] complete: Use existing SDD parsers/regeneration and WorkspaceLifecycle fingerprint, with exact source closure.
- PD-002 [FR-002] [DEC-002] complete: Reuse existing OrdinaryAssignment records, authority digest and validation.
- PD-003 [FR-003] [DEC-003] complete: Keep causal missing/conflicting/decomposition/projection refusals.
- PD-004 [FR-004] complete: Build and call the published2.5.3 API in an isolated graph and retain first failures.

## Verification Obligations
- VO-001 [PD-001] [PD-002]: The real1081 authored population composes after Root accepts its fingerprint and supplies original facts.
- VO-002 [PD-003]: Independent authored-input mutations demonstrate causal refusal.
- VO-003 [PD-004]: Isolated package-backed build/tests and exact-source independent review precede protected source delivery.

## Accepted Deferrals
Production projection, publication, installed adoption, Governance423(b), containedglibc2.44 and whole928 acceptance remain with the parent.
