---
schemaVersion: 1
workId: 26-canonical-quint-protocol
title: Canonical Quint Protocol
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/26-canonical-quint-protocol/spec.md
sourceClarifications: work/26-canonical-quint-protocol/clarifications.md
sourceChecklist: work/26-canonical-quint-protocol/checklist.md
publicOrToolFacingImpact: true
---

# Canonical Quint Protocol Plan

Prose status: planned

## Source Snapshot
- spec: work/26-canonical-quint-protocol/spec.md sha256:bfb402e7528b7cee6437b52092b987a003fe302da7c38344d9fa1a7a1f405273 schemaVersion:1
- clarifications: work/26-canonical-quint-protocol/clarifications.md sha256:204154fd7b95c310b6f26cd2aa640ccaeecc7af9af9e8e82b636261f20876b99 schemaVersion:1
- checklist: work/26-canonical-quint-protocol/checklist.md sha256:d13462ebf523dde9e5aed0a7f2f9c451b5967e226bed89798f30575a1389f255 schemaVersion:1

## Plan Scope
- Work item 26-canonical-quint-protocol is planned from the current specification, clarification, and checklist facts.
- Requirement count: 8.
- Clarification decision count: 3.
- Checklist result count: 8.

## Plan Decisions
- PD-001 [AC-003] [FR-001] [DEC-001] complete: Replace the rejected GS2-01.9 executable entry with one exact GS2-02.1 unit whose prerequisites are the immutable GS2-01.1 through GS2-01.8 receipts and whose gate contracts bind Q1 plus pure Q2.
- PD-002 [AC-001] [FR-002] [DEC-002] complete: Author the canonical source at `src/FS.GG.Coordination.Protocol/Protocol.md` with ordered `quint <target>.qnt +=` fences and the closed selector manifest at `Protocol.bindings.json`, giving every GS2-02.1 vocabulary family a stable promoted catalogue identity.
- PD-003 [AC-002] [FR-003] [DEC-001] complete: Invoke only published `fsgg-sdd typed-sdd author/inspect` from the coherent 1.5.0 set with backend `quint-specification-v1`, explicit `fsgg-quint-profile/2`, project-relative source/bindings, and an offline cache containing the unchanged exact Q1 tool objects.
- PD-004 [AC-002] [FR-004] [DEC-003] complete: Accept only manifest-v2 semantic closure that binds canonical Markdown, selector manifest, ordered fences, generated Quint, typed effect, source map, compiled-contract v2, bindings, receipt, and exact tool/profile/schema fingerprints.
- PD-005 [AC-004] [FR-005] [DEC-003] complete: Add architecture and command gates that fail on a rival protocol AST, committed generated `.qnt`, hidden behavioral prose, missing/duplicate/dynamic fences, wrong tool/profile/schema identity, stale projections, and untracked evidence.
- PD-006 [AC-005] [FR-006] complete: Register a literal closed `dotnet` gate command and capture bounded mutation-red evidence for every new validation branch before recording green candidate evidence.
- PD-007 [AC-006] [FR-007] [DEC-002] complete: Keep successor semantic families as explicitly named inert seams and assert absence of runtime, environment, deployment, secret, webhook, subscription, publication, and production-write outputs.
- PD-008 [AC-003] [FR-008] complete: Commit a clean candidate before manifest creation, bind tracked artifacts and exact head, then let roadmap-work validate the ordered gate catalogue without command override or shell interpolation.

## Contract Impact
- PC-001 [PD-001] [PD-006] roadmap-work: `eng/github-substrate-v2-units.json` and `eng/github-substrate-v2-gates.json` gain the first GS2-02 unit and its ordered Q1/pure-Q2 command contract while retaining schema `fsgg.coordination.roadmap-unit-index/1`.
- PC-002 [PD-002] [PD-003] [PD-004] typed-authority: manifest-v2 `quint-specification-v1` with `fsgg-quint-profile/2` from the published FS.GG.SDD 1.5.0 coherent set is the only author/inspect contract; product code consumes compiled-contract v2 and never reinterprets Quint.
- PC-003 [PD-005] [PD-007] architecture: `FS.GG.Coordination.Protocol` remains a boundary marker plus generated bindings; no hand-authored F# semantic union or committed generated `.qnt` is permitted.

## Verification Obligations
- VO-001 [PD-001] [PD-008] [PC-001] roadmapContract: Inspect and check prerequisites against the pinned roadmap and exact accepted receipts, then invert the unit id, prerequisite set, gate digest, and external-index location.
- VO-002 [PD-002] [PD-003] [PD-004] [PC-002] hermeticCompilation: Run two isolated profile-2 typed-author operations from identical source/bindings/cache inputs and require byte agreement, successful inspect, Quint typecheck/run/test/verify, and exact compiled-contract-v2 identity while profile 1 remains byte-compatible.
- VO-003 [PD-005] [PD-006] [PC-003] mutationControls: Execute independently authored corrupt-fence, prose-semantics, generated-output, identity, profile, schema, path, and parallel-AST mutations and record observed red results.
- VO-004 [PD-007] [PC-003] absenceInventory: Search the exact candidate tree and GitHub live settings for prohibited runtime and successor outputs, distinguishing proven absence from unreadable state.

## Performance Intent
No performance intent is declared for this work item.

## Migration Posture
- PM-001 [PC-001] [PC-002] additive: Preserve every accepted GS2-01 receipt and the existing manifest-v1/F# backend; only the executable roadmap frontier moves from the rejected conditional branch to GS2-02.1, and no existing authority is reinterpreted.

## Generated View Impact
- GV-001 [PD-002] [PD-004] typedAuthority: The published backend atomically generates manifest-v2 authority, catalogue, contract, bindings, map, and receipt views from canonical Markdown and reports stale or tampered members rather than repairing from them.
- GV-002 [PD-001] [PD-008] roadmapManifest: `artifacts/roadmap-work/GS2-02.1/` remains ignored candidate/gate output; only compact reviewed contracts and accepted receipts enter git.

## Accepted Deferrals
No accepted plan deferrals recorded.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 26-canonical-quint-protocol`.
