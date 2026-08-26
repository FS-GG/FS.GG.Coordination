---
schemaVersion: 1
workId: 1-establish-solution-boundary
title: GS2-01.3 Establish the solution boundary
stage: clarify
sourceSpec: work/1-establish-solution-boundary/spec.md
changeTier: tier1
status: clarified
---

# GS2-01.3 Establish the solution boundary Clarifications

## Source Specification
- work/1-establish-solution-boundary/spec.md

## Clarification Questions
- **CQ-001** [FR-001] [FR-002]: What is the exact project dependency graph?
- **CQ-002** [FR-001] [FR-004]: How can the App/webhook host exist without creating runtime authority?
- **CQ-003** [FR-001] [FR-002]: Which framework and test baseline applies to the scaffold?
- **CQ-004** [FR-003]: What makes the forbidden-dependency negative control independent and deterministic?

## Answers
- CQ-001 → Protocol has no project references; Core references Protocol; GitHub references Core and Protocol; CLI and App reference GitHub, Core, and Protocol; Qualification.Contracts references Protocol; test projects reference only the production layers they verify.
- CQ-002 → App is a compiled class-library boundary with an inert refusal contract, not a listening or deployable web host. HTTP hosting, webhook registration, subscriptions, secrets, and production mutation remain absent.
- CQ-003 → Use F# on .NET 10 with repository-wide deterministic build settings and xUnit test projects. Do not consume FS.GG.SDD or GitHub SDK packages in this unit; those bindings belong to later roadmap units.
- CQ-004 → A repository verifier inspects declared project/package references against an explicit allow-list, while a separate negative fixture presents a forbidden Core-to-GitHub dependency and asserts that the verifier rejects it for the expected rule.

## Decisions
- **DEC-001** [CQ-001] [FR-001] [FR-002] [AC-001]: Adopt the explicit acyclic dependency graph recorded in CQ-001; all undeclared production-project edges are forbidden.
- **DEC-002** [CQ-002] [FR-001] [FR-004] [AC-001]: Model the App/webhook boundary as an inert class library with no network listener, deployable entry point, secrets, subscriptions, or mutation authority.
- **DEC-003** [CQ-003] [FR-001] [FR-002] [AC-001]: Target F# and .NET 10, use xUnit for tests, and defer external protocol and GitHub packages until their dedicated roadmap units.
- **DEC-004** [CQ-004] [FR-003] [AC-001]: Require both a positive dependency scan of the real solution and a negative fixture whose expected rejection is asserted independently.

## Accepted Deferrals
- None.

## Remaining Ambiguity
- None.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 1-establish-solution-boundary`.
