# Choreo, Akka, and F# trace correspondence

Status: accepted design; implementation not started

Decision date: 2026-09-16

Delivery sequence: roadmap merge, replay-baseline repair, pinned Choreo adoption, model and trace migration

This roadmap is the durable continuation point for introducing
[Quint Choreo](https://github.com/quint-co/choreo) into the Coordination formal model because the production
boundary is an Akka message system and its executable evidence is written in F#. It records the whole-project
analysis, the selected design, the staged migration, and the exact recovery procedure. A later session should be
able to resume from the first incomplete checkpoint without reconstructing the reasoning below.

## Executive decision

Coordination will add a four-process Choreo model beside the existing `O2HostedWriterModel`:

- `Host` owns admission, workflow progress, pause/resume, and effect selection;
- `Journal` owns durable intent, append order, recovery, and operation status;
- `Runner` owns execution-session launch and observation messages;
- `GitHubProvider` owns candidate, branch, pull-request, merge, and native-readback messages.

The communication medium is an unordered message soup with explicit loss only where the implementation admits an
ambiguous provider response. Process boundaries are message boundaries. Durable append and local pure decisions
remain atomic abstractions. The model keeps the seven hosted-writer effects parameteric: claim, process,
candidate, branch, pull request, merge, and native readback.

Choreo is a source-time formal-model dependency only. It will not become a production, build-time F#, or runtime
Akka dependency. Its required Quint modules will be committed as pinned `quint-library` regions in the canonical
literate protocol source, with upstream commit, license, byte hashes, and architecture guards. This preserves the
repository's single literate Quint source and does not add a local `.qnt`, a network fetch, a compiler fork, or a
second qualification profile.

The current flat model remains authoritative during a dual-model parity window. The Choreo model becomes the
formal workload only after projection parity, actual Quint-generated trace replay, negative controls, and the
existing qualification budgets pass. The first replay work already proposed in Coordination PR 395 is a useful
baseline, but it is not the finished correspondence layer and must be repaired before Choreo work builds on it.

## Why this project needs Choreo

`O2HostedWriterModel` is a useful phase model. It expresses durable intent, an unknown outcome, reconciliation,
same-operation retry, restart pause, identity bindings, and native readback. Its state is one global record and its
actions update that record directly. This is intentionally smaller than the implementation, but it now hides the
most important implementation risks:

- `ExecutionSessionActor` receives Akka messages asynchronously while the flat model has no mailbox or listener;
- the Host, journal, runner, and provider can advance independently, but the flat model collapses them into one
  atomic state transition;
- delayed, duplicated, stale-generation, and wrong-identity messages cannot be represented directly;
- an unconditional `hold` action masks whether a protocol state is terminal, deadlocked, or missing a receiver;
- aggregate reachability does not prove that each message and listener is usable;
- F# replay can accidentally recreate the flat transition function instead of checking the production boundary.

Choreo is valuable here because it makes the participants, message types, listeners, and message interleavings
first-class while compiling to ordinary Quint state machines. It is not being adopted to replace Akka or to prove
the framework itself. Its job is to expose the distributed protocol that the existing flat abstraction omits.

## Whole-project architecture analysis

### Relevant layers

| Layer | Current responsibility | Choreo/trace impact |
| --- | --- | --- |
| `FS.GG.Coordination.Protocol` | Canonical literate Quint source and protocol types | Own the pinned Choreo library regions, the new model, projection, invariants, and witnesses |
| `FS.GG.Coordination.Core` | Pure orchestration state, commands, events, identities, generations, and attempts | Semantic source for message payloads and state projection; no Choreo runtime reference |
| `Orchestration.PostgreSql` | Durable event and execution storage, including Akka persistence integration | Journal semantics must be represented; no model code in this project |
| `Orchestration.Execution` | `ExecutionSessionActor` and neutral execution coordinator | Runner-side message correspondence and actor replay target |
| `Orchestration.Host` | Effect driver, route workflow, runner wire protocol, production callbacks, and composition | Host/provider correspondence and the main F# replay adapters |
| `Qualification.Contracts` | Published semantic-kernel identity and qualification contracts | Retain the accepted Quint 0.32.0/profile boundary; do not copy producer machinery |
| `eng/` and CI | Extract, validate, shard, and qualify the canonical Quint workloads | Add deterministic Choreo provenance checks and later register the new workload |
| Tests | Unit, actor, integration, PostgreSQL, architecture, and formal evidence | Add generated-trace replay, receiver reachability, mutation controls, and parity gates |

No packable project owns this feature. The affected orchestration projects are applications/libraries marked
non-packable, so this roadmap does not introduce a package-version release stream.

### Production correspondence

The model/process boundary follows existing authority rather than project names alone:

| Choreo process | Production correspondence | Authority retained |
| --- | --- | --- |
| `Host` | `MainRouteWorkflow`, `MainEffectDriver`, host effect pump | Select work, enforce admission, pause/resume, choose the next effect; never infer provider completion |
| `Journal` | `HostedWriterJournal`, Core command/event decisions, PostgreSQL store | Sequence events, recover durable status, reject mismatched or duplicate appends |
| `Runner` | `ExecutionSessionActor`, `ExecutionSessionCoordinator`, `RunnerWireRuntime` | Launch/observe/reconnect/cancel an execution session under exact session and generation bindings |
| `GitHubProvider` | `MainProductionCallbacks` and GitHub adapters | Perform/reconcile hosted mutations and supply native authority readback |

The `Host`/`Runner` separation is required even though both are currently composed by the Host executable. Their
wire identities, acknowledgements, reconnect behavior, and failure modes are distinct. The journal is also a
process even when a call is locally hosted: durability, append rejection, and recovery are observable protocol
boundaries, not implementation details.

### Existing formal and replay baseline

The canonical source is `src/FS.GG.Coordination.Protocol/Protocol.md`. It contains the general coordination
protocol and its tests, `O2PilotPermitModel`, `O2HostedWriterModel`, `GS20310JournalModel`, reconciliation,
review-epoch, cutover, administrative-retirement, and qualification roots. `eng/quint-qualification.json`
currently registers nineteen formal workloads. Hosted-writer progress and fault-safety use TLC at depth 20 with
10,000-state, transition, and sample budgets, 105 seconds, 2 GiB memory, and 6 MiB artifact limits.

The current hosted-writer state records a stage, operation status, paused/readback flags, route/attempt/candidate/
repository/generation bindings, and the seven effect completion flags. It proves important rules, including no
effect before intent, unknown-outcome blocking, same-operation retry after proven absence, restart pause, binding
preservation, and native readback before completion. These rules are retained, not rewritten by approximation.

Coordination PR 395 adds a general F# replay harness, a journal fence, and three hosted-writer actor-replay
scenarios. It establishes useful test plumbing but currently constructs model traces in F# by duplicating Quint
actions. The actor under test is a replay wrapper around the production journal/adapter rather than the production
`ExecutionSessionActor`. The proposed lost-response reconciliation can invoke a method named `Dispatch` when no
pending response exists, which must be demonstrated to be observation-only or removed. Its trace fingerprint also
names Quint 0.22.4 while the accepted repository toolchain is Quint 0.32.0. These are baseline defects, not Choreo
features, and are phase C0 work.

## Pinned Choreo dependency

The selected upstream is `quint-co/choreo` at commit
[`000cf4eed315187dc6f216a148781cff7dde6521`](https://github.com/quint-co/choreo/commit/000cf4eed315187dc6f216a148781cff7dde6521),
observed on 2026-09-16. The repository has no release suitable for a semantic version pin; the commit is therefore
the identity. It is Apache-2.0 licensed.

Pinned bytes:

| Upstream file | SHA-256 |
| --- | --- |
| `choreo.qnt` | `f733842c21513e42429567541b8a300545b0c27f2eec77e0a474fd45b1d2e407` |
| `spells/basicSpells.qnt` | `60e566ee21ed308c7f33069199b277fcbc9856bc16b0ac877cacc81d61926102` |
| `LICENSE` | `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4` |

As a compatibility observation, not a qualification claim, the upstream two-phase-commit example typechecked at
that exact commit with the repository-pinned Quint 0.32.0. A 20-sample, 20-step randomized run of its consistency
invariant also completed without a violation. The implementation phase must reproduce the smoke test from
committed bytes in the canonical pipeline.

### Integration alternatives considered

1. Add upstream `.qnt` files and import them from generated Quint. This is rejected because the repository's
   published-kernel architecture gate deliberately rejects local `.qnt` and copied producer-like Quint machinery.
2. Fetch Choreo during CI. This is rejected because it introduces mutable network input and makes old evidence
   unreproducible.
3. Fork the SDD extractor or qualification profile. This is rejected for the first adoption because it expands a
   local modeling improvement into a cross-repository toolchain contract change.
4. Commit the required modules as license- and provenance-marked `quint-library` fences in `Protocol.md`. This is
   selected. Existing extraction concatenates the canonical literate source, the bytes remain reviewable and
   offline, and no second compiler/profile/source path is created.

The copied region must be mechanically exact apart from the literate fence and an adjacent provenance header.
An architecture test will extract the region, hash it, and compare it with a small provenance manifest under
`eng/`. Updating Choreo requires a dedicated PR that changes the commit, hashes, copied bytes, compatibility
evidence, and this decision record together. No floating branch reference is accepted.

## Approved protocol design

### Identities and local state

Every operation message carries an immutable envelope sufficient to reject cross-talk:

```text
OperationRef = {
  route, attempt, operation, effectKind,
  candidate, repository, generation
}
```

Optional fields are represented explicitly for phases that precede candidate or repository selection; empty
sentinels are not used. `operation` remains stable across reconciliation and a proven-absence retry. A new attempt
or generation cannot consume an old response.

The processes keep only their owned local state:

- `Host`: admission, pause state, recovery/readback epochs, current operation reference, and projected workflow
  progress;
- `Journal`: ordered records and the status of each operation (`none`, `intent`, `dispatching`, `unknown`,
  `provenAbsent`, `applied`, or `settled`);
- `Runner`: session state, acknowledged command/revision, generation, and observed result;
- `GitHubProvider`: authoritative remote facts and received operation identities.

The legacy stage and completion flags are a projection of these local states and durable facts. They are not a
second independently mutated state in the Choreo model.

### Message families and listeners

| Direction | Message family | Required listener outcome |
| --- | --- | --- |
| Host → Journal | record intent, mark dispatching, append observation, settle | append or an explicit sequence/identity rejection |
| Journal → Host | append accepted/rejected, recovered operation snapshot | advance, remain blocked, or require fresh authority readback |
| Host → Runner | launch, observe, reconnect, cancel | accepted/rejected acknowledgement with exact bindings |
| Runner → Host | acknowledgement, session observation, candidate result | accept only when session, generation, revision, and operation agree |
| Host → GitHubProvider | perform effect, reconcile operation, read native authority | response may arrive, duplicate, delay, or be lost only on admitted ambiguous paths |
| GitHubProvider → Host | applied, proven absent, still unknown, native fact | journal the observation before progress; stale or wrong bindings are ignored/rejected |

Every message constructor must have a reachable listener witness. Every listener branch that rejects a message
must have at least one negative witness. Unconsumed messages may remain in the soup only when the model explicitly
classifies them as stale, duplicate, or foreign; there is no generic discard action.

### Atomicity and failure assumptions

- A pure Core decision is one model step.
- A successful journal append is one durable step; the request and reply are separate messages.
- Provider invocation and provider response are separate. The response can be lost after the provider applied the
  effect, creating `unknown`; durable intent cannot be lost.
- Runner and provider requests may be duplicated or delayed. Operation identity makes their handling idempotent.
- Crashes clear volatile Host/Runner knowledge but not journal records or provider facts.
- Restart enters `paused`. Recovery of the journal is necessary but insufficient: a fresh authority readback and
  a distinct authenticated resume are also required.
- Time is modeled only where a lease/deadline changes an allowed action. Scheduling delay is nondeterminism, not a
  synthetic clock.
- Fairness, when introduced for progress, applies only to continuously enabled delivery/processing actions. Safety
  properties never depend on fairness.

### Required properties

Safety invariants:

1. No hosted effect occurs before its durable intent and dispatching record.
2. An `unknown` operation cannot be replaced, skipped, or redispatched; only observation/reconciliation is enabled.
3. Retry is enabled only after durable `provenAbsent` and retains the same operation identity.
4. Restart remains paused until journal recovery, fresh authority readback, and a separate valid resume complete.
5. Route, attempt, operation, candidate, repository, generation, session, and revision bindings cannot be crossed.
6. Workflow completion requires a native provider readback agreeing with the durable projection.
7. Duplicate or stale messages cannot create an additional effect or advance the stage.
8. Journal sequence rejection cannot be treated as success.
9. At most one current operation exists for an admitted route/attempt/effect tuple.
10. The Choreo-to-legacy projection satisfies every retained `O2HostedWriterModel` safety invariant.

Progress is split deliberately:

- an assumption-free property says the system never advances through an unsafe shortcut;
- under explicit delivery/provider fairness, an admitted non-faulting operation can reach the next durable stage;
- an ambiguous operation can reach either observed-applied or proven-absent, never progress by redispatch;
- after restart, progress remains impossible until all three recovery gates complete.

Reachability witnesses cover happy path, lost response and restart, duplicate response, stale generation, wrong
identity, proven-absence retry, missing native readback, every message constructor, every listener, and each of the
seven effect kinds. The `hold` action is not a substitute for terminal-state or receiver reachability.

## Trace and F# replay design

The trace contract is generated by Quint execution. F# must not synthesize model transitions that it then claims
to replay. The canonical artifact is an ITF trace plus a small manifest:

```text
schema, model, scenario, sourceCommit, quintVersion, quintBinarySha256,
choreoCommit, seed, maxSteps, invariant, traceSha256
```

The observable step schema contains the action label, participant, message identity, operation envelope, durable
status, projected legacy state, and relevant provider/session facts. Choreo internal representation details such
as opaque message-soup indexes are excluded from the stable F# contract.

Replay has three layers:

1. Parse and validate the Quint artifact, toolchain identity, model source digest, and step schema.
2. Drive the real production seam for that action: journal API, `ExecutionSessionActor`, runner wire runtime,
   effect driver, or a recording GitHub adapter. Fakes supply external authority facts but do not decide policy.
3. Project the resulting F# observation into the same observable schema and compare it step-by-step, including the
   first divergence and its Quint source/action label.

The existing general replay harness from PR 395 should be retained where it satisfies layer 1 and comparison
diagnostics. Its hand-authored hosted-writer transition generator is temporary. The wrapper actor may remain a
focused journal test, but actor correspondence must also exercise `ExecutionSessionActor`. Negative controls must
mutate at least generation, operation identity, candidate/repository binding, message duplication, journal
sequence, and native readback; each mutation must fail at the expected first divergent step.

Trace artifacts may be deterministic fixtures when generated by a documented seed and exact toolchain. CI must
also regenerate a selected trace and compare its digest so fixtures cannot drift silently. Large exploration
artifacts remain CI outputs rather than repository files.

## Migration strategy

The migration is additive until parity is demonstrated:

```text
O2HostedWriterModel ──retained invariants──┐
                                          ├─ projection parity ─ F# replay
O2HostedWriterChoreoModel ─ actual traces ─┘
```

The legacy model remains registered and unchanged through C3. The new model initially imports its effect-kind and
identity vocabulary and exposes a pure `legacyProjection`. Bounded tests compare reachable projected safety facts;
scenario traces compare named milestones. This is refinement evidence, not a claim of general bisimulation.

Only C5 may switch hosted-writer progress/fault-safety qualification to the Choreo root. At that point the flat
model is either retained as an explicitly documented abstraction test or reduced to a projection oracle. It is
not deleted merely because the new model exists.

## Delivery roadmap

Each phase is independently reviewable and ends in a merged protected-main commit. Checkboxes are updated in the
same PR that supplies their evidence. Do not combine later phases to save PRs: the merge commits are recovery
points.

### C0 — stabilize the replay baseline

- [x] Rebase/update PR 395 on the roadmap merge and repair all exact-head checks.
- [x] Replace the obsolete Quint 0.22.4 fingerprint with the accepted 0.32.0 identity and binary hash.
- [x] Prove the lost-response path calls observation/reconciliation only; remove any redispatch-shaped fallback.
- [x] Mark hand-authored F# traces as transitional and keep their claims scoped to the flat model.
- [ ] Merge the reusable harness, journal fence, and focused actor tests without claiming Choreo correspondence.

Exit evidence: protected-main PR checks, replay tests, journal tests, and a source note naming the temporary trace
origin. No Choreo bytes are added in C0.

### C1 — pin Choreo and establish the source boundary

- [ ] Add the exact Apache-2.0 Choreo/basic-spells modules as provenance-marked `quint-library` regions.
- [ ] Add `eng/choreo-source-pin.json` with repository, commit, file hashes, license hash, and integration schema.
- [ ] Add positive and mutation architecture tests for copied bytes, commit/hash changes, missing license, local
  `.qnt`, and forbidden network fetching.
- [ ] Add a minimal imported smoke module and typecheck/run it with Quint 0.32.0 in the canonical preparation path.
- [ ] Confirm all existing nineteen formal workloads and published-kernel architecture tests remain unchanged.

Exit evidence: deterministic offline extraction, exact hashes, smoke run, full architecture tests, and no formal
catalog switch.

### C2 — implement and review the four-process model

- [ ] Add typed identities, local process states, message payloads, and the unordered message soup.
- [ ] Implement Host, Journal, Runner, and GitHubProvider listeners incrementally, typechecking each slice.
- [ ] Add the seven effect kinds through one parameteric protocol rather than copied transition families.
- [ ] Add `legacyProjection`, retained safety invariants, explicit progress assumptions, and per-listener witnesses.
- [ ] Run small randomized exploration after every participant, then bounded invariants and all named scenarios.
- [ ] Conduct a structural/runtime model review: dead actions, vacuous invariants, unconstrained messages, accidental
  atomicity, symmetry, state-space growth, and counterexample readability.

Exit evidence: model-review checklist, typecheck, randomized runs, bounded checks within an initial measured budget,
and traceable coverage for every message/listener.

### C3 — make Quint traces the executable contract

- [ ] Define and version the stable observable trace schema and trace manifest.
- [ ] Export actual Quint traces for happy path and the six required fault/identity scenarios.
- [ ] Extend the F# harness to validate the source/toolchain/Choreo identities and parse the Choreo projection.
- [ ] Remove F#-generated model transitions from correspondence tests.
- [ ] Add deterministic regeneration/digest checks and first-divergence diagnostics.
- [ ] Retain negative mutations proving that an invalid trace cannot pass through a permissive projection.

Exit evidence: exact seeded command lines, committed small fixtures, reproducible hashes, and F# tests replaying
Quint output rather than a reimplementation.

### C4 — close production correspondence gaps

- [ ] Replay runner actions against the production `ExecutionSessionActor` and neutral coordinator.
- [ ] Replay journal actions against the production journal contract and a real PostgreSQL integration slice.
- [ ] Replay Host steps through `MainEffectDriver`/`MainRouteWorkflow` with recording external adapters.
- [ ] Replay GitHub facts through `MainProductionCallbacks` without granting the fake policy authority.
- [ ] Cover crash/recovery, reconnect, duplicate, stale generation, wrong identity, proven absence, and missing native
  readback across the composed seam.
- [ ] Demonstrate that all ambiguity paths are observation-only until durable proven absence.

Exit evidence: unit, actor, Host composition, and PostgreSQL tests, plus mutation controls at each process boundary.

### C5 — qualify and migrate the formal workload

- [ ] Measure state-space and trace sizes; introduce safe symmetry or bounds without erasing identity failures.
- [ ] Add Choreo progress/fault-safety entries to `eng/quint-qualification.json` with explicit budgets.
- [ ] Run the complete canonical aggregate, sharded CI path, and performance baseline from a cold preparation.
- [ ] Compare legacy and Choreo scenario projections and document every intentional strengthening/weakening.
- [ ] Switch the hosted-writer qualification root only after parity and negative controls pass.
- [ ] Decide explicitly whether the flat model remains an abstraction test or becomes projection-only.

Exit evidence: protected-main qualification artifacts, updated baseline, within-budget execution, and an architecture
decision recording the legacy-model disposition.

### C6 — operational handoff and optional expansion

- [ ] Update architecture and contributor documentation with model, trace-regeneration, and failure-triage commands.
- [ ] Record owner, cadence, upstream-update procedure, and Choreo security/license review.
- [ ] Confirm O3 installed adoption remains a separate explicit operation with no behavior change from this work.
- [ ] Evaluate other actor/wire protocols only after hosted-writer evidence is accepted; adoption elsewhere is a new
  roadmap decision, not an implied rollout.

Exit evidence: a clean-room continuation exercise using only merged docs and repository commands.

## Project impact and non-goals

Expected touched areas are `Protocol.md`, an `eng/` provenance manifest and qualification catalog, formal/architecture
tests, replay fixtures/harness, orchestration actor/Host tests, and architecture documentation. Production F# should
change only when replay exposes a real mismatch; the model must not force decorative runtime abstractions.

This programme does not:

- add Choreo, Quint, or a model interpreter to a deployed binary;
- replace Akka, PostgreSQL, GitHub APIs, the Core command/event model, or the SDD package;
- model Akka internals, mailbox implementation details, supervision trees, clustering, or network transport bytes;
- add a second executor, scheduler, provider-authority path, or production fake;
- activate O3-04 installed adoption, dispatch a workflow, or change an existing store;
- claim exhaustive proof from randomized `quint run` samples;
- widen PR 395 into the Choreo migration.

## Risks and controls

| Risk | Control |
| --- | --- |
| Choreo upstream has no release | Exact commit and byte hashes; committed offline source; explicit update PR |
| Copied library silently diverges | Extract-and-hash architecture test plus license/provenance manifest |
| Model state space grows beyond CI | Incremental listeners, bounded identities, symmetry review, measured budgets, sharded qualification |
| Choreo changes semantics relative to flat model | Dual-run window, pure legacy projection, retained invariants, named scenario parity |
| F# tests merely duplicate the model | Quint-generated ITF is the input; production seams decide runtime behavior |
| Fakes grant themselves authority | Recording adapters provide facts only; Core/Host production policy remains in control |
| Lost response causes duplicate mutation | Unknown is observation-only; retry requires durable proven absence and same identity |
| Stale actor/provider responses cross attempts | Complete immutable operation envelope and negative mutation tests |
| Unconditional stutter hides dead protocol | No generic discard/hold witness; terminal and receiver reachability are explicit |
| Toolchain policy is accidentally forked | Retain Quint 0.32.0, profile, canonical source, and published-kernel gates |
| Existing roadmap/adoption work is disturbed | Source-only work, separate branches/PRs, no O3-04 dispatch or installed change |

## Continuation protocol

On any restart or handoff:

1. Read this file and find the first unchecked phase item.
2. Confirm protected `main`, open PRs, and exact branch heads before editing. Never assume PR 395 or an implementation
   branch still has the hash recorded by an earlier session.
3. Work in a dedicated worktree named for one phase (`routine/choreo-cN-...`). Do not reuse a dirty or unrelated
   worktree.
4. Reproduce the previous phase's exit evidence from the merged main commit. If it fails, repair that boundary
   before advancing.
5. Record exact upstream/source/tool hashes, seeded commands, fixture digests, and check URLs in the phase PR.
6. Update only the completed checkboxes and any decision changed by evidence. Do not mark a phase complete on a
   feature branch unless its evidence is part of the same protected-main PR.
7. Squash-merge after required checks. Start the next phase from the resulting protected-main merge commit.

The immediate continuation after this roadmap merges is C0 on PR 395. The first new Choreo implementation branch
is C1 and must start only after C0 is merged. If PR 395 cannot meet the C0 boundary without redesign, supersede it
with a narrowly scoped C0 PR and record that disposition here; do not carry an ambiguous baseline into C1.

## Acceptance gate for the programme

The programme is complete only when all phases are checked and protected main demonstrates, from a cold checkout:

- exact, licensed, offline-reproducible Choreo source identity;
- a typechecked and bounded four-process model with every message/listener reachable;
- all retained safety invariants plus explicit fault/progress scenarios;
- actual Quint-generated traces replayed through production F# seams;
- negative controls that fail at the intended boundary;
- complete canonical qualification within recorded time, memory, state, transition, sample, and artifact budgets;
- no runtime Choreo dependency, no local `.qnt`, no toolchain/profile fork, and no implicit installed adoption.
