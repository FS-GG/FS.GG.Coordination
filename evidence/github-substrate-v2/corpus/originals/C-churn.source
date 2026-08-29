---
title: "Roadmap: Agent-authored F# specification and protocol kernel"
category: Design
categoryindex: 4
index: 24
description: "A S.I.R.-first roadmap to a reusable F# specification AST and the coordination process/mutation extension."
---

# Roadmap: Agent-authored F# specification and protocol kernel

> **Successor program:** P0–P4 below remain completed historical delivery. Future canonical authoring,
> backend migration, S.I.R. re-adoption, coordination qualification, provider rollout, F# retirement, and
> any later default decision are governed by [ADR-0077](../adr/0077-quint-first-typed-specification-authority.md)
> and the [Quint-first migration design](../coordination/2026-08-25-quint-first-typed-sdd-migration-design.md).
> The published F# backend remains current until that producer-first sequence lands.

| Field | Value |
|---|---|
| Created | 2026-08-24T09:43:48+02:00 |
| Updated | 2026-08-26 — record completed P4 release reconciliation, defer P5's default flip until OperatingV2, and begin the GS2 handoff |
| Status | P-series completed historical delivery; future authoring and coordination execution superseded |
| Design authority | [Agent-authored F# specification kernel and canonical mutation algebra](../coordination/2026-08-24-typed-protocol-kernel-design.md) |
| Coordination successor | [GitHub Substrate v2 and coordinated fleet cutover](../coordination/2026-08-25-github-substrate-v2-fleet-cutover-design.md) |
| Authoring successor | [Quint-first Typed SDD migration and feature preparation](../coordination/2026-08-25-quint-first-typed-sdd-migration-design.md) |
| Starting point | `main` at `0d56bb104da22478dfef72825f8cf19425635ed0` |
| Evidence window | 2026-08-21T07:26:29Z through 2026-08-24T07:26:29Z |

## Outcome

First prove the agent-authored specification EDSL/AST against S.I.R.'s live executable rules corpus. Then
extract only the reusable specification substrate into an FS.GG.SDD-owned, published contract. Finally,
move the FS-GG coordination protocol from strong but locally typed subsystems to process and protocol
extensions whose facts, commands, events, mutations, receipts, schemas, and projections have exactly one
authority. Preserve all current external contracts until each bounded surface has a proved replacement.

The consumer-facing process is **Typed SDD**, machine value `typed-sdd`. It will become an additive option
beside Standard SDD (`sdd`) and Freeform (`none`) before it is eligible to replace `sdd` as the workspace
default. The roadmap treats option introduction and default selection as separate contract changes.

The P-series was intentionally incremental and remains the record for kernel and Typed SDD delivery. The
coordination M-series below is retained as a technical inventory, but no longer governs implementation:
maintainer direction selected a new-only fleet cutover, a dedicated `FS.GG.Coordination` repository, and an
independent bootstrap qualification process.

The published generic kernel and `typed-sdd` lifecycle now exist. Standard SDD continues to govern work that
selects that lifecycle, but it does **not** certify the coordination replacement. V2 uses the successor
design's custom qualification contract so the machinery under replacement is not its own acceptance oracle.

The enduring P0–P4 deliverables are stable identity, provenance, evidence, validation, fingerprints,
semantic diff, projections, migrations, bindings, and the `typed-sdd` lifecycle. ADR-0077 supersedes the
claim that one normalized typed AST should remain the future behavioral corpus: Quint source owns future
behavioral meaning, while a small generated contract supplies stable integration facts. Proof remains
bounded to a named model/property pair and never becomes a parallel authority.

## 2026-08-25 coordination supersession boundary

The following remain authoritative inputs to v2: the M-series vocabulary census, observation distinctions,
mutation algebra, operation receipts, event/process semantics, compiler surface, schemas/fingerprints,
protocol-surface controls, replay/model/formal proof obligations, and deletion measurements.

The following are superseded: per-surface production strangling, compatibility adapters as the normal v2
route, preserving all public coordination commands until individually migrated, implementation inside
`.github`, Standard SDD/v1 completion evidence as v2 qualification, and multiple production cutovers. Their
replacement is the successor design's F0–F9 sequence: independently build and qualify v2, ship one universal
v1 bridge fence, prepare and shadow-read, freeze all normal writers, switch and verify the closed fleet,
open v2 once, then retire v1. Preparation may be additive; production authorship may not be dual.

## Live handoff to GitHub Substrate v2

This section is the dispatch boundary between the two roadmaps. A board column does not override it.

| Existing or prospective work | Current meaning | Next action |
|---|---|---|
| P0–P4 | Complete producer/consumer history and the published dependency v2 uses | Preserve receipts and exact package identities; repair any release residue under v1. |
| P5 readiness/soak | Independent lifecycle work, not a coordination prerequisite | May proceed while v2 is in `GS2-00`–`GS2-09`; publish every identity change so the cutover census can ingest it. |
| P5 default flip | Deferred fleet-wide default-bearing contract change | Do not start the flip during cutover preparation. Resume only after the cutover ledger reaches `OperatingV2`. |
| `.github#2932` | An open row filed from the former M0 execution plan | Do not claim as written. Transfer census/baseline/corpus obligations to `GS2-00`; transfer typed coordination implementation to `GS2-02`; record the supersession on the issue. |
| M1–M9 rows or future rows derived from those headings | Technical inventory, not milestones | Map each requirement to a GS2 unit or mark it historical/non-protocol; never schedule it merely because it is `Ready`. |

The exact transfer from `.github#2932` is:

- authority/read, codec, decision, mutation, receipt, projection, and protocol-string censuses →
  `GS2-00.3`, `GS2-00.4`, and `GS2-00.6`;
- incident mapping, 72-hour churn, replay/mutation baselines, and omission/misclassification controls →
  `GS2-00.5` and Q0 evidence;
- deletion/shadow classification → `GS2-00.7`;
- canonical coordination role types, AST nodes, and compiler-owned vocabulary → `GS2-02` in
  `FS.GG.Coordination`;
- representative byte-compatibility fixtures → v1 frozen corpus inputs for Q3/Q5/Q9; and
- Standard SDD checklist/plan/tasks/analyze/verify/ship artifacts → historical process evidence only, not
  a v2 acceptance gate.

### Concurrency and change control

Ordinary product work continues through v1 until the announced cutover window. Product branches do not
need a v2 rebase. Work that changes the generic kernel, lifecycle defaults, provider/profile descriptors,
workspace scaffolding, registry contracts, coordination receivers, reusable workflows, or repository
settings is different: it changes an input to the cutover manifest.

Before `GS2-10`, such a change may land through its owning program, after which the v2 census, receiver
snapshot, dependency locks, and affected qualification evidence are refreshed. At and after `GS2-10`, the
same change is deferred unless operators intentionally mint a new candidate identity and rerun Q0–Q7.
No release, provider flip, lifecycle-default flip, or receiver mutation may cross `GS2-11`–`GS2-12`.
Deferred P5 work resumes only after `OperatingV2`, through v2 coordination.

### Residual-work checkpoint before v2 starts

Before assigning `GS2-00`, the program owner records a clean handoff receipt containing:

1. the terminal disposition of every P4 release/tag/feed/registry repair, including `.github#2968` and
   its delivery PR if still open;
2. the disposition of `.github#2932` and a mapping from each still-valid acceptance clause to GS2 units;
3. the P5 decision—`not started`, `readiness only`, `complete before GS2-10`, or `deferred until
   OperatingV2`—with exact package/provider identities;
4. all active Typed SDD claims, reviews, delivery operations, releases, and receiver PRs, each classified
   `finish`, `park`, or `defer`; and
5. confirmation that no worker can obtain a superseded M-series row solely because its Project status is
   `Ready`.

Dependencies formerly named by `.github#2932`, including `.github#2903`, `.github#2905`, `.github#2841`,
and `.github#2850`, are adjudicated independently at this checkpoint. A bounded defect required for safe
v1 operation may finish under v1; completed evidence enters the frozen corpus; replacement-only work maps
to a GS2 unit or is closed; and genuinely non-critical work may be parked or deferred. Its old dependency
edge neither makes the row a v2 blocker nor authorizes the superseded M0 implementation.

This checkpoint does not make Typed SDD a blocker for v2. It prevents an ambiguous handoff and a moving
candidate from being mistaken for independent parallel work.

## Baseline and success measures

The starting 72-hour board window measured:

- 54 issues opened;
- 32 issues closed;
- net row growth of 22;
- 34 newly opened rows still open; and
- 156 repository commits.

Track these measures from milestone 0 onward:

| Measure | Baseline action | Target after final cutover |
|---|---|---|
| Independently authored representations per protocol fact | Census every registered surface | One authority; all remaining representations generated or external observations |
| Remote mutation entry points outside interpreters | Static/project-reference census | Zero |
| Protocol string comparisons outside codecs | Static census | Zero, excluding named expiring exemptions |
| Structural omissions discovered after independent review starts | Record per PR | Zero for modelled surfaces |
| Successor rows from one missing model concept | Churn reading every pass | Zero over a closed 30-day window |
| Partial-operation retries requiring human reconstruction | Receipt/recovery audit | Zero |
| Retained histories replayable by current engine | Replay corpus | 100% or an explicit versioned migration |
| Mutation controls caught | Named control inventory | 100% of required controls red |
| Agent-authoring iteration cost | P0 records questions, revisions, diagnostics, and elapsed work per S.I.R. session | No repeated question caused by missing model vocabulary; semantic diff reviewed each iteration |
| Shared-kernel consumer copies | Census local shared substrate after P2 | Zero after P3 and each later adoption |
| Provider/profile Typed SDD coverage | P4 derives the supported matrix from provider descriptors | Every supported row proves explicit `none`, `sdd`, and `typed-sdd` |
| Default-bearing surfaces | P4 derives the census while `sdd` remains default | One P5 flip changes the complete census; zero omitted or divergent defaults |
| Migration ambiguities | Record typed reason and source location per Standard SDD import | Zero silent guesses; every unresolved fact remains explicit |

Counts never substitute for the qualitative churn reading. A lower issue count achieved by suppressing
findings is failure.

## Delivery principles

1. **P-series: land bounded producer/consumer slices. Coordination: qualify separately and switch once.**
   The coordination replacement does not enter production one adapter at a time.
2. **One source at cutover.** Shadow comparison is temporary evidence, never a permanent second
   authority.
3. **Classify every compatibility surface.** Preserve only the v1 bridge and sealed audit readability;
   migrate or explicitly retire CLI paths, JSON schemas, exit codes, markers, and receipts at cutover.
4. **Cheap closure first.** Model compilation and protocol-surface checks run before expensive CI and
   review.
5. **No automatic row explosion.** Milestones use existing class rows and bounded children. Findings
   become evidence on the relevant model surface unless they establish a new cause.
6. **Every milestone has deletion criteria.** Adding a model without retiring a shadow representation
   makes the root problem worse.

### Cross-cutting AST-consumer and proof-capability rule

Every remaining milestone and successor program preserves the same capability boundary:

1. Agent skills and application-building tools receive the normalized typed AST through a typed API;
   they do not reconstruct semantics by scraping its Markdown, JSON, XML, or diagram projections.
2. Human-readable authoring and review remain mandatory even when the most important consumer is an
   agent. Surface syntax may be convenient F#, while consumers depend only on the compiled AST.
3. A domain may declare no executable or proof capability. Concepts, relationships, types/data,
   requirements, protocols, examples, and explicit unknowns remain complete specification material.
4. Beyond mandatory compiler validation and human projection, optional domain-specific validators,
   simulators, generators, model checkers, SMT encodings, theorem provers, and extractors register as
   versioned AST consumers. Results bind the exact model fingerprint, lowering, toolchain, assumptions,
   scope, and evidence strength.
5. Generated code is used only by an explicit runtime adoption decision. Maintaining generated or
   proof-language code beside an independent implementation does not establish equivalence.
6. No proof-language-specific construct enters the shared AST merely to make one backend convenient;
   domain extensions own their vocabulary and unsupported properties remain explicit.

## Implementation process and break-glass boundary

This section records the process used for the P-series and ordinary lifecycle work. It does not govern the
superseding coordination v2 program. That program records intent, acceptance, evidence, compatibility,
adversarial controls, and unresolved decisions through its approved bootstrap qualification contract; v1
board status, review transitions, delivery receipts, and done stamps are projections rather than evidence.

### Visible progress checkpoint

Legend: 🟩 complete · 🟨 active/pending evidence · 🟥 blocked · ⬜ not started. The color is redundant with
the checked state so the status remains readable without color.

- 🟩 [x] **P0 — S.I.R. baseline and pilot charter:** completed historical producer evidence.
- 🟩 [x] **P1 — agent-authored S.I.R. specification pilot:** completed historical authoring evidence.
- 🟩 [x] **P2 — shared FS.GG.SDD specification kernel:** published.
- 🟩 [x] **P3 — published-kernel re-adoption:** completed with producer/consumer receipts.
- 🟩 [x] **P4 — additive Typed SDD option:** published and supported across the provider/profile matrix.
- 🟨 [ ] **P5 — evidence-gated default flip:** explicitly deferred until `OperatingV2`; readiness-only
  evidence may continue, but no provider, template, scaffolder, wizard, registry, or workspace default
  changes during cutover preparation.
- 🟩 [x] **Residual v1 safety checkpoint — `.github#2905`:** merged as `.github#2973` at
  `3d1e6b186397f807163ed144290bee6a7c20343c`; exact default-branch push successes established the
  post-merge verification fact, the issue is Closed, the board row is Done, and the claim is released.
- 🟩 [x] **P4 release residue — `.github#2968` and `.github#2983`:** the New SDD Workspace
  `0.10.1` repair and coherent coordination set `0.75.5` are published, promoted, dual-feed verified,
  registry-reconciled, and closed with typed delivery receipts. The 0.75.5 release source is
  `34b1fd8b074e926cb3bce2c156f439cd8dcb0943`; reconciliation merged in PR #2992.
- 🟩 [x] **Active Templates input — FS.GG.Templates PR #437:** classified **finish**; it merged and
  published `fs-gg-templates/v0.10.0`. The later Game.Core 0.14.0 adoption is independent receiver work,
  not a Typed SDD prerequisite or reason to reopen P4.
- 🟩 [x] **GS2-00.0 roadmap handoff:** `.github#2932` is mapped to its GS2 successors and closes with
  this checkpoint; epic #2952 has checkable child acceptance; #2964 and #2965 use native `Blocked by`
  fields; and a fresh lifecycle reconciliation reports no projection repairs.

The final checkbox is **not M4 progress**. It is a bounded v1 correctness repair whose evidence joins the
frozen defect corpus. M0–M9 remain historical inventory and are not schedulable merely because their old
board rows are Ready.

### Milestone recipe — how the work is done

1. **Work the roadmap, not the Coordination board.** Read this roadmap and its design authority first,
   select the next dependency-ready roadmap milestone, and use the board only to claim, route, and record
   that already-selected work. A Ready board row cannot override supersession, dependency, or stop
   boundaries.
2. **Bind one SDD package before implementation.** Carry the work id, canonical `work/<id>/` source, and
   `readiness/<id>/` projections through `charter → specify → clarify → checklist → plan → tasks → analyze`;
   implement only after `implementationReady`.
3. **Keep claim, branch, and touch-set aligned.** Use one minted identity, one canonical item branch, and
   the declared paths. Coordinate overlaps before editing; widen before touching any additional path.
4. **Prove the change can fail.** Run focused tests and a bounded inversion for each changed refusal gate,
   then regenerate `evidence → agents → verify → ship`. Commit the complete digest chain and prove a fresh
   detached second pass makes no tracked changes.
5. **Review immutable evidence.** A fresh critic reviews the exact head and production route. Any repair
   moves the head and requires the engine-derived successor wait. Never translate prose into acceptance.
6. **Recover authority with a fresh generation, not rewritten history.** Before rotating a claim, cancel
   its unconsumed review wait. If one generation already elected another PR, release and reclaim
   legitimately, then let the engine mint a new authorization/election for the current unmerged PR; create
   a successor PR only when the branch/head identity itself must be replaced. If rotation already happened
   and the old worker-owned queue entry makes a new wait impossible, remove only that exact stale automation
   entry, record the recovery, and create a current-generation wait.
7. **Make the delivery handoff self-contained before merge.** Put `Closes #<item>.` on a line by itself;
   bind exact-head obligations; copy the exact `Paths:` declaration into the PR body when terminal recovery
   must reconstruct the handoff from a merged PR; and issue only the typed delivery action the engine names.
8. **Gate, merge, and rebuild.** Wait for exact-head landability, merge only the accepted head, and rebuild
   the shared Coordination engine immediately from fresh `origin/main` before the next board mutation.
9. **Verify post-merge without rewriting history.** Read existing runs for the exact merge SHA, default
   branch, `push` event, completed status, and successful conclusion. One qualifying success establishes
   verification; red, cancelled, pending, and unreadable runs remain diagnostics and do not veto that
   success. Never rerun historical workflows merely to manufacture a clean set.
10. **Complete, checkpoint, and stop.** Apply the terminal typed receipt, verify Closed/Done/claim-release
    projections, update this recipe with the milestone evidence and lessons, and stop at the requested
    milestone rather than starting the next dependency-ready unit.

### Checkpoint lessons from the P4 release residue

What went right:

- 🟩 [x] Publication packed each 0.75.5 member once, kept all component tags on the exact merged source,
  and promoted only after both feeds served matching unsigned payloads.
- 🟩 [x] Post-merge SDD reconciliation replaced 18 pre-merge deferrals with 18 observed release evidence
  records and reached a no-change `shipReady` fixed point.
- 🟩 [x] Independent review compared immutable release assets, GitHub Packages, nuget.org, nuspec source
  commits, tags, public installation, registry projections, and engine freshness rather than trusting
  workflow conclusions alone.
- 🟩 [x] The terminal delivery receipts kept release verification and registry reconciliation distinct,
  so the item could not complete after publication while the authored registry still advertised 0.75.4.

What went wrong, and the retained rule:

- 🟩 [x] All three first publisher runs pushed successfully but timed out while nuget.org indexed.
  **Retained rule:** resume the exact prepared manifest after the feed becomes readable; never repack or
  issue a blind duplicate push, and treat feed observation—not a run's first conclusion—as publication
  authority.
- 🟩 [x] Regenerating the publishing skill also changed the driver manifest, and a historical tracked
  `.trx` contradicted the terminal M6 artifact rule. **Retained rule:** run the complete policy-subject
  and generated-manifest checks for a registry reconciliation, not only the focused release suites.
- 🟩 [x] A second PR on the original claim generation lost the append-only merge election already won by
  PR #2990. **Retained rule:** one claim generation authorizes one winning merge; rotate the generation
  before authorizing post-merge reconciliation.
- 🟩 [x] Head recovery accumulated stale and duplicate no-obligations markers, and rotating before
  cancelling the old review wait left an unconsumable queue entry. **Retained rule:** amend known marker
  ids explicitly, keep exactly one current-head declaration, and terminalize the wait before claim
  rotation.
- 🟩 [x] The typed semantic-diff host found 252 generated SDD projection occurrences across nine token
  pairs, beyond the artifact rename that triggered manual review. **Retained rule:** let the engine
  inventory the whole exact-base/head diff and attach accountable receipts for every discovered
  occurrence.

### Checkpoint lessons from `.github#2905`

What went right:

- 🟩 [x] The functional classifier used existential success while preserving every non-success run as
  diagnostic evidence; mixed success/failure/pending/cancelled controls and 2,585 tests stayed green.
- 🟩 [x] Independent review found the stale Standard SDD digest chain; regeneration with `fsgg-sdd` 1.2.5
  and a second detached no-change pass restored a real fixed point.
- 🟩 [x] Canonical-successor recovery preserved semantic evidence: predecessor and successor had identical
  raw binary diff digest and stable patch ID, while claim, election, and review identities were fresh.
- 🟩 [x] The host rebuilt the merged Coordination engine with zero warnings/errors before terminal reads,
  and completion used existing exact-main push successes without rerunning history.

What went wrong, and the retained rule:

- 🟩 [x] Work initially drifted toward burning down the Coordination board. **Retained rule:** the roadmap
  chooses the work; the board coordinates that work and never becomes the roadmap.
- 🟩 [x] A stale generated digest chain survived the first implementation handoff. **Retained rule:** commit
  every regenerated SDD artifact and require a second detached full-sequence no-change proof.
- 🟩 [x] `Closes #2905.` was first embedded in prose, and an old claim generation had already elected the
  predecessor PR. **Retained rule:** use a standalone closing line and rotate claim/PR identities rather
  than contesting append-only election history.
- 🟩 [x] Claim rotation left the prior review wait bound to retired authority. **Retained rule:** create a
  fresh canonical successor PR and enter an engine-derived initial wait; never edit durable review history.
- 🟩 [x] GitHub auto-closed the issue before the terminal reader could reconstruct declared paths from the
  merged PR. **Retained rule:** make the merged PR handoff self-contained, then use metadata-only
  reopen/reclaim recovery if the typed reader refuses.
- 🟨 [ ] A known completion-receipt edge can compare a short merge SHA with the full SHA after the Done
  projection. **Retained rule:** treat that mismatch as a typed defect, preserve the already-written
  projection, and do not retry terminal mutation blindly. The same caution applies when terminal replay
  reports `FSGG-NOT-DONE` after projecting Done but before explicit issue closure.

`$chainsaw` is not an alternative implementation process. It is an explicitly invoked, bounded
break-glass operation inside the governing SDD lifecycle and is admissible only when evidence establishes
that a specific generator, wrapper, workflow, gate, or other project-local mechanism prevents the current
SDD slice from progressing. One invocation repairs one named obstruction at the lowest trustworthy layer,
with an exact touch-set, captured before-state, rollback boundary, and verification independent of the
bypassed mechanism. The implementation returns to Standard SDD immediately after that repair.

Break-glass authority does not permit an implementation to:

- bypass a live claim, overwrite concurrent work, or evade cross-repository sequencing;
- replace producer publication with a source-project or local-package shortcut;
- skip compatibility, migration, release, independent-review, or default-flip evidence;
- describe a bypassed gate as passing; or
- widen a repair into another roadmap milestone or institutionalize its direct path as normal operation.

Every use records what was bypassed, why the normal route could not deliver, the files or state changed,
independent verification, remaining drift, rollback, and how the ordinary SDD path was restored. Repeated
need for the same bypass is evidence of a roadmap or machinery defect and triggers the stop condition below;
it is not precedent for a broader exemption.

## Dependency map

```text
P0 S.I.R. baseline and pilot charter
 |
 v
P1 S.I.R. canonical authoring pilot
 |
 v
P2 extract shared specification kernel into FS.GG.SDD
 |
 v
P3 publish, register, and re-adopt from S.I.R.
 |
 +--> P4 all-provider Typed SDD opt-in
 |      |
 |      v
 |    P5 evidence-gated workspace default flip
 |
 +--> M0 coordination census and vocabulary
 |
 +--> M1 observation/evidence kernel
 |      |
 |      +--> M2 mutation algebra + dependency-field pilot
 |              |
 |              +--> M3 durable operation plans + intake pilot
 |
 +--> M4 process event model + delivery/review pilots
          |
          +--> M5 coordination compiler extensions
                    |
             +------+------+
             |             |
             v             v
        M6 schemas and   M7 protocol-surface gate
        fingerprints       |
             |             |
             +------+------+
                    v
          M8 model/replay/formal verification
                    |
                    v
          M9 retire shadows and measure
```

P0–P3 are ordered producer/consumer work: no cross-repository PR assumes unpublished contracts. P4 begins
only after the published boundary is proved by P3; P5 is independent of the coordination M-series and
cannot use coordination progress as a substitute for consumer readiness. M2 and M4 may proceed in parallel
only when their touch-sets and shared extension types are separated. M5 must
incorporate what both coordination pilots actually needed; it extends the already-proven specification
compiler and must not design a universal DSL from hypothetical requirements.

## P0 — S.I.R. baseline and pilot charter

### Deliverables

- Freeze representative S.I.R. corpus slices covering a fact, predicate, formula, transition, registered
  algorithm, supersession, evidence, generated documentation, and historical replay binding.
- Measure the current explicit-record and provisional-builder authoring cost, semantic diff quality,
  coherence runtime, and failure diagnostics.
- Define the smallest candidate shared concepts: stable node/specification IDs, vocabulary, references,
  supersession, provenance, evidence obligations, schema version, normalization, and extension contracts.
- Record S.I.R.'s gameplay types and interpreters as explicitly non-transferable domain ownership.

### Acceptance

- Every proposed shared concept has at least two concrete uses across S.I.R. rules, lifecycle
  specifications, or coordination; hypothetical abstractions are removed.
- The fixtures are content-addressed and reproduce under .NET and Fable where the current corpus promises
  parity.
- No authoritative S.I.R. behavior changes.

### Exit criterion

The pilot can distinguish reusable specification substrate from S.I.R.-owned rule semantics using checked
examples rather than naming intuition.

## P1 — Agent-authored S.I.R. specification pilot

### Minimum vertical slice

Before widening the migrated corpus or designing the coordination algebra, land one end-to-end S.I.R.
slice containing exactly the infrastructure needed to prove the central claim:

1. canonical `SpecificationModel` identity, schema version, and provenance;
2. the smallest F# authoring surface that constructs the inspectable AST;
3. compilation, validation, deterministic normalization, and actionable diagnostics;
4. one real S.I.R. specification migrated without changing its authoritative behavior;
5. one generated human-readable projection carrying source fingerprint and freshness evidence;
6. a gate proving reproducibility and rejecting a stale or directly edited projection; and
7. one complete iterative human/agent authoring session through the governing skill.

No second rule family, general mutation algebra, or provider/profile expansion enters P1 until this slice
passes its semantic-diff, execution, replay, projection, and wrong-path controls. The slice is deliberately
small enough that `$chainsaw`, if explicitly invoked for one broken mechanism, cannot become its de facto
implementation process.

### Deliverables

- Implement the candidate inspectable AST and try direct records, computation expressions, and a hybrid
  authoring surface against the frozen corpus.
- Extend `sir-author-rule` into the inspect → intent → one material question → typed proposal plus human
  projection → edit → validate → semantic diff → evidence/coherence → revise loop.
- Add canonical normalization, provenance/authoring receipt, stable fingerprint, derived Markdown/manifest
  views, and deliberate direct-edit/emergency-exemption controls.
- Keep registered algorithms explicit and opaque, with inputs, outputs, reads, writes, evidence, and
  implementation fingerprint visible to the AST.

### Acceptance

- The migrated rule slices execute, render, replay, and fingerprint identically except for accepted,
  versioned changes.
- Two syntactic authoring forms that mean the same thing normalize to byte-identical ASTs.
- A human can review the semantic diff without reading builder mechanics.
- The gate proves capability-mediated authoring without relying on commit identity and cannot block a new
  finding from being recorded.
- The minimum vertical slice completes through Standard SDD with its specification, plan, tasks, evidence,
  and ship record mutually consistent.
- Any break-glass repair used during the slice has a bounded audit record and either restores the normal
  SDD route or leaves an explicit unresolved blocker; skipped machinery is never reported as verified.

### Exit criterion

At least three real iterative human/agent rule sessions complete without a second semantic authority or an
untyped escape hatch, and their friction report selects the authoring surface.

## P2 — Extract the specification kernel into FS.GG.SDD

### Deliverables

- Move the proven shared AST, compiler, normal form, versioned codecs, semantic-diff protocol, provenance,
  evidence contracts, and base authoring skill contract into FS.GG.SDD.
- Define typed extension registration without `obj`, reflection discovery, or a platform-wide closed union.
- Add a requirements extension covering current SDD scope, user value, requirements, acceptance,
  ambiguities/decisions, and evidence obligations.
- Provide a versioned Markdown migration adapter and generated human projection; do not silently reinterpret
  legacy prose.
- Register the package/contracts and compatibility policy under publish-before-flip sequencing.

### Acceptance

- Existing supported SDD artifacts either migrate losslessly or produce a stable, actionable ambiguity.
- Extension compiler, codec, semantic-diff, projection, and evidence-validator fixtures are public contract
  tests.
- The package is independently consumable without S.I.R. or coordination dependencies.

### Exit criterion

FS.GG.SDD publishes a stable preview containing only concepts proven by the pilot and its own requirements
extension.

## P3 — Re-adopt the published kernel in S.I.R.

### Deliverables

- Replace the pilot's local shared substrate with the published FS.GG.SDD-owned package while retaining the
  S.I.R.-owned rule extension.
- Re-run the frozen corpus, .NET/Fable parity, generated views, coherence, replay, and agent-authoring
  sessions through the package boundary.
- Delete the local shadow substrate and publish compatibility/consumer receipts.

### Acceptance

- S.I.R. has one rule authority and no vendored or locally forked copy of the shared kernel.
- The package boundary does not expose gameplay semantics or require S.I.R. at runtime.
- A negative source/semantic/package identity mismatch fails before rule execution or projection.

### Exit criterion

The producer/consumer cycle is proven end to end; other FS.GG repositories and coordination may adopt the
kernel without treating the S.I.R. pilot as a package source.

## P4 — Make Typed SDD an additive option for every consumer

### Deliverables

- Add `typed-sdd` to the lifecycle choice contract while retaining current machine values `sdd`, `none`,
  and the separately retiring `spec-kit`; keep the omitted-value default at `sdd`.
- Publish FS.GG.SDD support first, then update provider descriptors, template parameters, workspace wizard,
  scaffold provenance, registry projections, generated guidance, and consumer pins in dependency order.
- Route the existing SDD stage skills through a representation backend selected from provenance. Add
  Typed SDD authoring/inspection operations without copying the lifecycle stage instructions.
- Add Standard SDD → Typed SDD analysis/migration with explicit `Migrated | Ambiguous | Unsupported`
  outcomes, semantic diff, rollback boundary, and no writes before acceptance.
- Add refresh, upgrade, doctor, readiness, and ship checks for compiler/package identity, extension
  compatibility, canonical source, normalized AST, authoring receipt, and projection freshness.
- Exercise explicit `none`, `sdd`, and `typed-sdd` across every supported provider/profile, including clean
  creation, restore, agent authoring, build/test, lifecycle completion, refresh, and upgrade.

### Acceptance

- No supported provider rejects, drops, aliases, or silently downgrades `typed-sdd`.
- Omitted lifecycle selection still resolves to `sdd` on every default-bearing surface.
- A fresh consumer installs published artifacts only; no source checkout or S.I.R. dependency is required.
- Wrong lifecycle, missing compiler, stale projection, unsupported extension, direct edit, and agent-
  unavailable controls all produce distinct actionable failures.
- Standard SDD and Freeform behavior remain compatible, and `spec-kit` retirement is neither delayed nor
  widened.

### Exit criterion

Typed SDD is a fully supported opt-in lane for every workspace/product shape, with a published migration
path and derived compatibility receipt; it is not yet the default.

## P5 — Evidence-gated Typed SDD workspace default

**Status:** default flip deferred until `OperatingV2`. P5 is independent of coordination-v2 feature
delivery. Readiness and soak evidence may continue without changing default-bearing contracts, but the
provider, template, scaffolder, wizard, registry, and workspace-default flip must not start during cutover
preparation. This is the selected branch of the earlier either/or sequencing rule.

### Deliverables

- Run representative non-S.I.R. opt-in work through complete Typed SDD lifecycles and publish the authoring
  friction, ambiguity, failure-recovery, and semantic-authority results.
- Freeze default-path fixtures for every provider/profile immediately before the flip.
- Write the separate cross-repo ADR that amends ADR-0056 and names the exact package, template, provider,
  registry, scaffolder, and wizard versions carrying the new default.
- In the ordered producer/consumer rollout, flip omitted lifecycle selection from `sdd` to `typed-sdd`
  everywhere; retain explicit `sdd` and `none` choices.
- Publish migration and operator guidance, then verify installed artifacts and fresh workspaces from both
  feeds rather than source-project references.
- Observe 7-, 14-, and 30-day default cohorts and retain a versioned rollback plan that restores selection
  semantics without rewriting canonical specifications.

### Acceptance

- All nine default-readiness conditions in the design hold at the exact release identities being flipped.
- Raw template, every provider, scaffolder, wizard, provenance, registry, docs, and tests agree that omitted
  lifecycle means `typed-sdd`.
- Explicit `sdd` remains Standard SDD and explicit `none` remains Freeform; neither is an alias or fallback.
- A wrong-default mutation makes every default-bearing contract test red.
- No default-created workspace is silently lifecycle-less or unable to author its first specification.
- The post-flip cohorts show no second authority, silent migration, or recurring missing-vocabulary chain.

### Exit criterion

Typed SDD is the coherent workspace default across all consumer entry points. Standard SDD and Freeform
remain explicit supported choices, and any later retirement decision is outside this roadmap.

## Historical coordination M-series inventory

The M0–M9 sections below are retained because their vocabulary, incidents, deliverables, and proof
obligations remain useful inputs. They are **not schedulable milestones** and their incremental adapters,
compatibility promises, and per-surface cutovers are superseded by the linked GitHub Substrate v2 F0–F9
fleet-cutover plan.

## M0 — Ratify vocabulary and produce the protocol census

**Status:** superseded as an executable milestone. `.github#2932` retains useful census and corpus
acceptance, but must not be claimed as a v1 implementation row; use the handoff mapping above.

### Deliverables

- Add the canonical terms from the design to a small kernel namespace without behavior changes.
- Produce a machine-readable census of:
  - external authorities and reads;
  - decoders/codecs;
  - process decisions;
  - remote writes;
  - durable ledgers and receipts;
  - projections and generated documents; and
  - raw protocol string comparisons.
- Map every current open protocol issue to an existing model surface, a new proposed surface, or a
  non-protocol defect.
- Record the baseline measures above with reproducible commands.

### Acceptance

- The census is derived from source/project structure where possible and labels unavoidable manual
  entries explicitly.
- Zero subjects is a refusal, not a clean result.
- Every census row names authority, subject shape, freshness/revision source, and current owner module.
- No runtime behavior or wire output changes.

### Exit criterion

The team can answer “where is this fact decided and where can it be mutated?” for every live protocol
surface using one command.

## M1 — Observation and evidence kernel

### Deliverables

- Implement `AuthorityId`, `SubjectId`, `Revision`, `Evidence`, and `Observation<'a>` in Core.
- Add constructors that prevent `ConfirmedAbsent` without complete-read evidence.
- Define strict adapter contracts for REST, GraphQL, Actions, git, filesystem, and feed observations.
- Migrate two representative reads:
  - a paginated GitHub board/issue fact; and
  - an exact-head Actions/check-run fact.
- Emit authority, subject, revision, and evidence identity in structured diagnostic output.

### Acceptance

- Rate limit, truncated pagination, malformed response, permissions failure, and legitimate absence are
  five distinguishable outcomes.
- Existing JSON/plain/rich results remain compatible or change behind an explicit schema version.
- Mutation controls coercing each failure to absence are caught.
- Adapter tests preserve raw evidence by bytes or digest.

### Exit criterion

No migrated decision accepts a naked source value or performs its own external read.

## M2 — Mutation algebra and `Blocked by` pilot

### Deliverables

- Implement the closed mutation DU, typed outcomes, interpreter capability, and receipt envelope.
- Extend board field metadata to distinguish scalar and set-valued semantics.
- Migrate `.github#2907`:
  - atomic revision-bound `AddMember`/`RemoveMember` for `Blocked by`;
  - explicit replace retained only under a separately named administrative command;
  - body-only inert dependency declarations reported by lint.
- Make every existing board write compile through the algebra, initially via compatibility adapters.

### Acceptance

- Concurrent adds preserve both edges or one fails stale; neither silently overwrites.
- Removing one member preserves every other member.
- Duplicate add/remove is idempotent and reports `AlreadyApplied` distinctly.
- `SetScalar` cannot be constructed for a registered set field.
- Existing callers see compatible outputs until migrated.

### Exit criterion

There is one implementation of board-field mutation semantics and no generic path can masquerade as
set membership intent.

## M3 — Durable operation plans and intake pilot

### Deliverables

- Implement `OperationId`, `MutationPlan`, step dependencies, step receipts, and resumption.
- Define `Applied`, `AlreadyApplied`, `RefusedBeforeWrite`, `Stale`, and `Indeterminate` outcomes.
- Migrate intake creation from `.github#2835` to a resumable plan:
  issue creation → labels → board placement → field projection → completion receipt.
- Add failure injection before and after every remote call.
- Define roll-forward versus compensation rules for irreversible GitHub effects.

### Acceptance

- Killing the process at every injected boundary and retrying reaches one identical final state.
- No retry creates a second issue or appends a duplicate receipt.
- An indeterminate create is re-observed before any repeat write.
- A rejected field value leaves a durable partial-state receipt and a correct next action.
- Plans and receipts carry model/schema version and protocol fingerprint.

### Exit criterion

No human must inspect remote state to reconstruct where a migrated operation stopped.

## M4 — Process events and lifecycle pilots

### Deliverables

- Define state/intent/event/decision/evolve modules for Delivery and Review.
- Make `Merged`, `AwaitingPostMergeVerification`, `Verified`, and `Completed` distinct delivery states.
- Migrate `.github#2905` and the complete transition decisions identified by the 2026-08-22 design.
- Represent review generation, wait, repair, succession, and acceptance as events rather than
  independently authored tokens.
- Compile authorized events into M2/M3 mutation plans.

### Acceptance

- No event sequence reaches `Completed` without an exact-merge protected/default-branch receipt.
- Red, unreadable, or absent post-merge verification stays visible and recoverable.
- Review head movement and claim reacquisition have one total transition decision consumed unchanged by
  projection and writer.
- Retained histories from the named defect corpus replay to their expected state.
- A writer cannot add local preconditions around a shared decision without a gate failure.

### Exit criterion

Delivery and Review have one executable process authority each; issue state and Projects status are
projections only.

## M5 — Coordination extensions for the specification compiler

### Deliverables

- Reuse the P1-selected authoring conventions and deviate only where the M2/M4 evidence requires it.
- Implement process and protocol extensions plus compiler validation for:
  - stable/duplicate IDs;
  - state/event reachability;
  - authority and codec ownership;
  - mutation interpreter coverage;
  - projection sources and cycles;
  - schema versions; and
  - model-test dimensions.
- Compose process models without introducing a single monolithic process union.

### Acceptance

- Equivalent builder and direct AST construction produce byte-identical normalized specification models.
- Model fingerprint is independent of declaration order but changes on semantic structure.
- The compiler rejects named negative fixtures for every validation class.
- No arbitrary closure is treated as inspectable transition structure.
- Existing `Protocol.fs` documentation data can be derived from or associated with model IDs.

### Exit criterion

One compiled specification model can enumerate all migrated authorities, processes, mutations,
projections, and schemas without scanning prose, while S.I.R. and requirements extensions remain absent
from the coordination dependency closure.

## M6 — Versioned schemas, envelopes, and protocol fingerprint

### Deliverables

- Implement versioned event/receipt envelopes with source, id, subject, schema, correlation,
  causation, revision, and model version.
- Generate JSON Schema 2020-12 for losslessly representable contracts.
- Add explicit schema fragments and codecs where F# unions require custom encoding.
- Report package version, model version, and protocol fingerprint from the engine and wrapper.
- Stamp generated docs/skills and structured outputs with model identity where compatible.
- Provide upcasters for every retained old event/receipt version.

### Acceptance

- Old retained documents decode or fail with an explicit unsupported-version result.
- Unknown fields follow the declared compatibility policy; they never disappear accidentally.
- Encode/decode and old→new→encode fixtures are deterministic.
- A conflicting wrapper manifest/runtime artifact is named before any protocol decision.
- XML/SCXML, if prototyped, is generated only and absent from production reads.

### Exit criterion

Every durable protocol document says which semantics interpret it, and runtime output proves which
model produced the answer.

## M7 — Protocol-surface gate and architectural enforcement

### Deliverables

- Add the early `protocol-surface` required context.
- Enforce project-reference boundaries around Core, adapters, model, and interpreters.
- Add AST/source checks for direct mutations, raw registered-state comparisons, duplicate codecs,
  unrevisioned writes, unowned projections, and unclassified wire changes.
- Add `MODELLED | EXEMPT | UNMODELLED` reporting.
- Define an exemption record with reason, issue, owner surface, and enforced expiry.
- Generate the census from the normalized specification model's protocol extension and interpreter
  registration.

### Acceptance

- One deliberate violation of every rule makes a named control red.
- An empty source census returns no-verdict/refusal.
- The gate completes before expensive suites under the repository's bounded target.
- A finding packet can still be recorded when no model case exists.
- An expired exemption fails without relying on a human reminder.
- Existing independent property gates remain and prove their own non-vacuity.

### Exit criterion

A new protocol behavior cannot merge merely because its raw implementation compiles.

## M8 — Model-based, replay, and optional proof capabilities

### Deliverables

- Add FsCheck state-machine suites comparing the pure model with fixture-backed interpreters.
- Turn every shrunk sequence failure into a deterministic corpus fixture.
- Replay retained production-safe histories on every model/codec change.
- Add mutation controls for guards, revisions, subjects, authorities, idempotency keys, event ordering,
  and projection ownership.
- Register verification outputs as typed capability evidence bound to the normalized model fingerprint,
  toolchain, lowering/correspondence contract, assumptions, scope, and evidence strength.
- Model at least these bounded protocols in TLA+ or an equivalently checkable state specification:
  - comment-order claim CAS and lease/reacquisition;
  - set-valued concurrent add/remove; and
  - mutation-plan retry around indeterminate effects.

### Acceptance

- Model and interpreter agree on state, events, calls, receipts, and refusals for generated sequences.
- Historical replay is deterministic under each supported model version.
- The formal checks find injected lost-update, double-apply, stale-authorize, and deadlock controls.
- Formal artifacts are derived from or explicitly mapped to model IDs and cannot execute production
  writes.
- A model with no applicable proof capability remains valid, queryable specification material and does
  not require a placeholder theorem or extraction target.

### Exit criterion

The highest-risk concurrency and recovery invariants have both executable tests and state-space
evidence.

## M9 — Retire shadows, complete migration, and measure

### Deliverables

- Migrate remaining claim, dependency, intake, review, delivery, finding, and release surfaces.
- Delete compatibility parsers, generic mutation routes, duplicate predicates, and hand-authored
  projections immediately after each cutover.
- Remove every temporary exemption or convert it into a separately accepted permanent external boundary.
- Publish the final protocol-surface census and deletion ledger.
- Run closed-window churn readings at 7, 14, and 30 days after the final cutover.

### Acceptance

- All architecture acceptance conditions in the design document hold.
- No remote mutation bypass exists.
- No registered fact has more than one semantic codec or decision authority.
- Every remaining representation is labelled as authority, observation, or projection.
- The 30-day reading finds no successor chain caused by hand-authored second representations.
- Any remaining churn is named by a different measured mechanism rather than absorbed into this claim.

### Exit criterion

The protocol kernel is ordinary infrastructure: new concepts extend one model and the old drift
surfaces no longer exist.

## Existing issue alignment

This table is a routing aid, not a replacement for live board state.

| Current row/class | Roadmap home |
|---|---|
| `.github#2903` second representations | Class anchor across M0–M9 |
| `.github#2905` merged versus verified | M4 |
| `.github#2906` red verdict provenance | M1 and M4 |
| `.github#2907` dependency set mutations | M2 |
| `.github#2908` engine/manifest identity | M1 and M6 |
| `.github#2841` touch-set representations | M0, M5, M7 |
| `.github#2842` evidence-result ambiguity | M1 and M6 |
| `.github#2848` status vocabulary | M5 and M6 |
| `.github#2850` duplicate actor ownership | M0 and M5 |
| `.github#2852` runtime skill identity | M1 and M6 |
| `.github#2862` false chain-validation name | M4 and M6 |
| `.github#2835` non-atomic intake | M3 |
| `.github#2846`, `#2853`, `#2867` ledger/retry chains | M3 and M4 |
| `.github#2893`, `#2896` merge-election/recovery order | M3, M4, M8 |

Before implementation, re-read each row. A mapping here does not freeze its title, state, scope, or
acceptance criteria.

## Cutover protocol for each migrated surface

1. Freeze representative input/output fixtures from the old authority.
2. Introduce the typed model and interpreter behind a compatibility adapter.
3. Run old and new decisions against the defect corpus and fixture traffic.
4. Classify every divergence as old defect, new defect, or intentional versioned change.
5. Add a mutation control proving the new invariant can fail.
6. Switch the one production caller set to the new authority.
7. Delete the old predicate/parser/writer in the same PR or a pre-declared immediately following PR
   that blocks further surface changes.
8. Regenerate projections and record the model fingerprint.
9. Add the surface to replay and model-based sequence tests.

There is no indefinite shadow mode. A shadow that remains callable is another authority.

## Stop conditions

Pause the roadmap and return to design if any milestone demonstrates that:

- the algebra needs a generic untyped mutation escape hatch;
- model compilation depends on executing arbitrary external IO;
- the AST cannot represent a migrated pilot without embedding opaque workflow closures;
- retained histories cannot be versioned without rewriting immutable evidence;
- early enforcement blocks finding intake or honest `Unreadable`/`Unknown` outcomes;
- generated projections would require removing an independent property gate; or
- the kernel must become a network service to satisfy correctness;
- the Standard SDD process cannot express or verify an independently landable implementation slice; or
- the same project-local obstruction requires repeated `$chainsaw` bypasses instead of a bounded repair to
  the normal path.

These are evidence that the proposed boundary is wrong, not implementation inconvenience to hide.

## Completion report

After M9, write a timestamped report containing:

- the final derived protocol census;
- every retired representation and mutation bypass;
- schema/model versions and supported migration window;
- model/replay/formal verification results;
- Standard SDD execution receipts for each milestone and an inventory of any bounded `$chainsaw` uses,
  including restoration or unresolved-drift evidence;
- the 7-, 14-, and 30-day churn readings;
- counter-evidence and remaining failure classes; and
- a decision on whether SCXML export or a richer visual process projection is now worth adding.

P5 additionally produces a Typed SDD default-transition report containing the derived provider/profile and
default-bearing-surface censuses, exact release identities, migration results, opt-in soak evidence, wrong-
default controls, rollback boundary, and 7-, 14-, and 30-day default-cohort readings.
