---
schemaVersion: 1
workId: 1081-workspace-obligations-compiler
title: Source-bound workspace obligation compiler
stage: clarify
changeTier: tier1
status: planned
publicOrToolFacingImpact: true
---

# Compiler decisions

## Decisions
- DEC-001 [FR-001] [FR-002]: Derive task/evidence/skill obligations from current authored snapshots using the existing SDD producer; a generated WorkModel is verified against that regeneration.
- DEC-002 [FR-002] [FR-003]: Keep independently supplied completeness and original Requirements facts as typed data; compilation creates no attempts or budgets.
- DEC-003 [FR-003] [FR-004]: The first cohort uses actual child roots. Root="." and every production projection remain unsupported by this reference.
