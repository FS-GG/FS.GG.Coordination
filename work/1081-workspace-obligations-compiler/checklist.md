---
schemaVersion: 1
workId: 1081-workspace-obligations-compiler
title: Source-bound workspace obligation compiler
stage: checklist
changeTier: tier1
status: planned
publicOrToolFacingImpact: true
---

# Compiler source checklist

## Checks
- CHK-001 [FR-001] [AC-001] blocking: The source contract requires accepted fingerprint, raw source hashes and current authored/generated correspondence checks; execution remains pending.
- CHK-002 [FR-002] [AC-001] blocking: Independent complete population and original facts are not inferred from candidate output.
- CHK-003 [FR-003] [AC-002] blocking: The source contract requires causal controls to retain their own refusal and preserve original inputs; no executed result is claimed.
- CHK-004 [FR-004] [AC-003] blocking: The actual package consumer and isolated dependency graph are qualified.

## Results
- CR-001 [CHK:CHK-001] pass: DESIGN/source-contract completeness observation only: required joins are specified; implementation and acceptance remain pending.
- CR-002 [CHK:CHK-002] pass: DESIGN/source-contract completeness observation only: Root is the named original-fact and acceptance supplier.
- CR-003 [CHK:CHK-003] pass: DESIGN/source-contract completeness observation only: independent mutation controls are required, without claiming they ran.
- CR-004 [CHK:CHK-004] pass: DESIGN/source-contract completeness observation only: isolated project scope excludes Qualification.Contracts/Host; actual graph verification remains separate.
