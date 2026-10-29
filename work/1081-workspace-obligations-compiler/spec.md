---
schemaVersion: 1
workId: 1081-workspace-obligations-compiler
title: Source-bound workspace obligation compiler
stage: specify
changeTier: tier1
status: planned
publicOrToolFacingImpact: true
---

# Workspace obligation compiler specification

## User Value
A source owner can compile the actual accepted workspace and independently complete authored work population into existing authoritative obligations, while receiving a precise refusal when the source or original facts do not support that composition.

## Scope
- SB-001: Reference source functionality only; the governing complete contract is Coordination1081.

## User Stories
- US-001 (P1): As a Coordination source owner, I need a real published-package composition API for my source Delivery.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001] [FR-002]: The exact accepted model and authored population compose with original source facts and existing authority digest validation.
- AC-002 [US-001] [FR-003]: Source mutation, omitted work or missing original facts refuses without changing independent expectations.
- AC-003 [US-001] [FR-004]: The compiled reference consumer and independent controls use the isolated public package graph and retain unsupported production projection.

## Functional Requirements
- FR-001: Compile existing AuthoritativeObligations from source-bound accepted WorkspaceModel, current WorkModel and explicit component/repository identity. (Stories: US-001; Acceptance: AC-001)
- FR-002: Preserve independent ExpectedWorkKeys, Governance/Root obligations and original operational facts; framework Provider is the accepted RepositoryProfile identity, generated Provider uses actual PreparedConfiguration/provenance. (Stories: US-001; Acceptance: AC-001)
- FR-003: Refuse unavailable, changed, contradictory, malformed, omitted, over-bound or unsupported inputs without fabricating authority. (Stories: US-001; Acceptance: AC-002)
- FR-004: Qualify a real typed caller with published Artifacts2.5.3, causal controls and source review, preserving frozen global Artifacts1.5.0/default locks. (Stories: US-001; Acceptance: AC-003)

## Non-Goals
No new persisted protocol, workspace aggregate, decoder, lifecycle, package identity, Host/Main route or installed acceptance.

## Ambiguities
Root must accept the concrete workspace/profile proposal and explicitly supply original operation facts before the first genuine positive source invocation.
