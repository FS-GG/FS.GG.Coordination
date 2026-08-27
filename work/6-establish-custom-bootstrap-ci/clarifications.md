---
schemaVersion: 1
workId: 6-establish-custom-bootstrap-ci
title: Establish Custom Bootstrap CI
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/6-establish-custom-bootstrap-ci/spec.md
publicOrToolFacingImpact: true
---

# Establish Custom Bootstrap CI Clarifications

## Source Specification
- work/6-establish-custom-bootstrap-ci/spec.md

## Clarification Questions
- **CQ-001** [FR-001] [FR-002]: Which job identities form the complete bootstrap gate set?
- **CQ-002** [FR-003]: What does the security gate prove without importing a third-party scanning service or live mutation authority?
- **CQ-003** [FR-004]: What package is safe to pack and install before release topology exists?
- **CQ-004** [FR-005]: How can the final evidence manifest bind results produced by separate jobs to one candidate without treating prose as evidence?
- **CQ-005** [FR-006]: How is the bootstrap-only ceiling enforced mechanically?

## Answers
- CQ-001 → Use exactly five required jobs: `deterministic-build`, `compiler-and-tests`, `dependency-and-security`, `package-install-smoke`, and `evidence-manifest`. Keep commands in a reviewed gate contract so workflow labels alone cannot invent success.
- CQ-002 → Validate the evaluated dependency/source policy, run NuGet vulnerability inspection over the locked graph, and reject any reported vulnerability or unreadable/incomplete report. No token or write permission is required.
- CQ-003 → Pack the inert `FS.GG.Coordination.Protocol` library with a non-publishable CI-only version, then restore and compile a fresh consumer against the staged package plus the supported public read feed. Do not persist or upload to a package feed.
- CQ-004 → Prerequisite jobs upload compact receipts and artifacts. The final job downloads them, computes SHA-256 digests, records the exact event candidate SHA and reviewed command identities, and runs a fail-closed validator before uploading the manifest.
- CQ-005 → Workflow permissions remain `contents: read`; repository tests reject v1 lifecycle commands/markers, publishing/deployment steps, mutable local feeds, and a gate inventory that differs from the exact five-job contract.

## Decisions
- **DEC-001** [CQ-001] [FR-001] [FR-002] [AC-001]: Define exactly five explicit bootstrap jobs and keep their required identities and command contracts in a machine-validated repository manifest.
- **DEC-002** [CQ-002] [FR-003] [AC-002] [AC-004]: Treat unreadable vulnerability output as failure and combine it with the existing evaluated dependency/source verifier; use no external write-capable scanner.
- **DEC-003** [CQ-003] [FR-004] [AC-002]: Package only the inert Protocol library at a CI-only version and compile a fresh consumer from the staged bytes without publishing them.
- **DEC-004** [CQ-004] [FR-005] [AC-003] [AC-004]: Generate the final evidence manifest only after all four producing jobs pass, bind it to the exact candidate SHA, and validate exact gate membership plus artifact digests.
- **DEC-005** [CQ-005] [FR-006] [AC-004]: Enforce a read-only, bootstrap-only workflow vocabulary and reject v1 completion, release, deployment, and production-write authority.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 6-establish-custom-bootstrap-ci`.
