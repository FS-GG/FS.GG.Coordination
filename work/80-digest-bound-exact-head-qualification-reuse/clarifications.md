---
schemaVersion: 1
workId: 80-digest-bound-exact-head-qualification-reuse
title: Digest Bound Exact Head Qualification Reuse
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/80-digest-bound-exact-head-qualification-reuse/spec.md
publicOrToolFacingImpact: true
---

# Digest Bound Exact Head Qualification Reuse Clarifications

## Source Specification
- work/80-digest-bound-exact-head-qualification-reuse/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking answered: Resolve source ambiguity AMB-001 before checklist.
- CQ-002 [AMB:AMB-002] blocking answered: Resolve source ambiguity AMB-002 before checklist.
- CQ-003 [AMB:AMB-003] blocking answered: Resolve source ambiguity AMB-003 before checklist.
- CQ-004 [AMB:AMB-004] blocking answered: Resolve source ambiguity AMB-004 before checklist.
- CQ-005 [AMB:AMB-005] blocking answered: Resolve source ambiguity AMB-005 before checklist.

## Answers
- CQ-001: Initial reuse is intentionally coarse: the semantic subject is the complete tracked Git tree, hashed canonically from modes, paths, lengths, and bytes. No tracked path is classified provenance-only. Commit parents, author, committer, message, signatures, and other commit-object metadata are outside the tree and may differ. The receipt also exposes component digests for auditability, but the complete-tree digest is the closed-set authority.
- CQ-002: Freshness is the live GitHub retention boundary, not a guessed age window. The prior workflow must still be completed successfully, every named artifact must be present, unexpired, immutable, and downloadable, and all bytes must be re-hashed both during selection and immediately before terminal acceptance. Current-head independent review remains governed by the ordinary delivery protocol and cannot be borrowed from the prior head.
- CQ-003: Absence of a compatible prior run, an empty bounded search, or a discovery/API failure before selection is a safe `execute` miss because full qualification can restore authority. A selected candidate that is malformed, contradictory, digest-invalid, moved, deleted, expired, or unavailable at acceptance is `refuse`; it cannot silently fall back after execution gates were skipped. Unsupported schemas and non-canonical receipts are also `refuse`.
- CQ-004: Discovery reads repository artifacts named `bootstrap-evidence-manifest`, newest first within a bounded page, excludes the current run, and admits only artifacts whose owning run is the same workflow path, exact attempt, completed `success`, and still live. Search order grants no authority: every candidate is independently downloaded and validated, and selection is deterministic by completion time then run/artifact identity. Mutable cache keys, restore prefixes, branch names, and PR numbers are never inputs.
- CQ-005: The semantic environment contract is the plan-declared runner label and architecture, least-privilege permissions, exact action pins/runtimes, global SDK manifest, dependency manifests, gate environment variables, and tool/backend identities already contained in the complete tree and exposed as component digests. Ephemeral runner instance/image patch identifiers are operational provenance, not semantic inputs; fresh full runs already tolerate them and reuse cannot claim a stronger equivalence than execution provides.

## Decisions
- DEC-001 [CQ:CQ-001] [AMB:AMB-001] resolved: Use the complete tracked-tree canonical SHA-256 as the closed semantic boundary; expose named component digests without creating a second authority.
- DEC-002 [CQ:CQ-002] [AMB:AMB-002] resolved: Re-read exact run and artifact liveness at terminal acceptance; preserve current-head review as separate exact-head delivery evidence.
- DEC-003 [CQ:CQ-003] [AMB:AMB-003] resolved: Use `execute` only before a candidate is selected; any contradiction or loss after selection is fail-closed `refuse`.
- DEC-004 [CQ:CQ-004] [AMB:AMB-004] resolved: Discover immutable workflow artifacts through the Actions read API and validate their owning workflow run; never use cache lookup as authority.
- DEC-005 [CQ:CQ-005] [AMB:AMB-005] resolved: Bind the reviewed runner/environment contract and tool identities, not ephemeral host allocation details.
- DEC-006 resolved: Keep `evidence-manifest` as the terminal required check. Full gate jobs may be conditionally skipped only from a successful decision job; the terminal job runs under `always()`, downloads and re-hashes either current-run or selected prior-run artifacts, and is red on `refuse` or missing decision evidence.
- DEC-007 resolved: Prove the hosted hit path with a provenance-only commit that has a new commit SHA and byte-identical tree after an exact-head full-execution run; do not manufacture a cache hit by weakening the subject.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 80-digest-bound-exact-head-qualification-reuse`.
