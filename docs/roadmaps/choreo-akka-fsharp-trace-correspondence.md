# Choreo, Akka, and F# trace correspondence

Status: accepted design; C0–C3 merged; C4 production correspondence in protected-main PR; C5 next

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
Akka dependency. Its required Quint modules are committed as pinned regions inside the canonical source's existing
`quint-test` fence, with upstream commit, license, byte hashes, and architecture guards. `basicSpells.qnt` is
byte-exact; `choreo.qnt` has one guarded rewrite from its filesystem import to the equivalent same-file import
required by the combined Q2 source. This preserves the repository's single literate Quint source and does not add
a local `.qnt`, a network fetch, a compiler fork, or a second qualification profile.

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
4. Commit the required modules as license- and provenance-marked regions inside the existing `quint-test` fence in
   `Protocol.md`. This is selected. The canonical validator already concatenates that fence into its combined Q2
   source, the bytes remain reviewable and offline, and no second compiler/profile/source path is created.

The basic-spells region must be mechanically exact. The Choreo region permits only the manifest-declared single
import rewrite; reversing that rewrite must reproduce the upstream hash. An architecture test extracts both
regions and compares them with a small provenance manifest under `eng/`. Updating Choreo requires a dedicated PR
that changes the commit, hashes, copied bytes, compatibility evidence, and this decision record together. No
floating branch reference is accepted.

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
- [x] Merge the reusable harness, journal fence, and focused actor tests without claiming Choreo correspondence.

Exit evidence: protected-main PR checks, replay tests, journal tests, and a source note naming the temporary trace
origin. No Choreo bytes are added in C0.

### C1 — pin Choreo and establish the source boundary

- [x] Add the pinned Apache-2.0 Choreo/basic-spells modules as provenance-marked `quint-test` regions.
- [x] Add `eng/choreo-source-pin.json` with repository, commit, file hashes, license hash, and integration schema.
- [x] Add positive and mutation architecture tests for copied bytes, commit/hash changes, missing license, local
  `.qnt`, and forbidden network fetching.
- [x] Add a minimal imported smoke module and typecheck/run it with Quint 0.32.0 in the canonical preparation path.
- [x] Confirm all existing nineteen formal workloads and published-kernel architecture tests remain unchanged.

Exit evidence: deterministic offline extraction, exact hashes, smoke run, full architecture tests, and no formal
catalog switch.

Implementation note: embedding the pinned library increases each compiled root artifact from roughly 8.8 MiB to a
measured maximum of 12,246,193 bytes. C1 therefore raises the common root-artifact ceiling from 10 MiB to 16 MiB;
the existing per-root fail-closed check and all semantic workload budgets remain in force.
The pinned regions are appended after the established formal test modules so their source locations, retained ITF
states, and diagnostic trace bytes remain stable.
Each semantic shard now has a 25-minute workflow envelope (formerly 15 minutes). The workload's own time, memory,
state, transition, and sample limits are unchanged; this only accommodates the larger canonical compilation cost
when all nineteen independent shards contend for hosted-runner capacity.

### C2 — implement and review the four-process model

- [x] Add typed identities, local process states, message payloads, and the unordered message soup.
- [x] Implement the non-faulting Host, Journal, Runner, and GitHubProvider listener slice and typecheck it.
- [x] Add the seven effect kinds through one parameteric protocol rather than copied transition families.
- [x] Add `legacyProjection`, retained safety invariants, explicit progress assumptions, and per-listener witnesses.
- [x] Run small randomized exploration after every participant, then bounded invariants and all named scenarios.
- [x] Conduct a structural/runtime model review: dead actions, vacuous invariants, unconstrained messages, accidental
  atomicity, symmetry, state-space growth, and counterexample readability.

Exit evidence: model-review checklist, typecheck, randomized runs, bounded checks within an initial measured budget,
and traceable coverage for every message/listener.

C2 foundation evidence (2026-09-16): `O2HostedWriterChoreoModel` closes Choreo's string process carrier to four
constants, then gives each process a tagged local-state variant. This carrier choice is required because the pinned
Choreo module has global state and can be instantiated only once in the canonical combined source; it does not
weaken the closed four-process set. Nine typed message variants traverse an unordered per-process set and are
removed only by an explicit `Consume` custom effect. The happy-path listener chain has separate request/reply
steps for durable intent, dispatch, external application, durable observation, and Host settlement. `ProcessWork`
routes to `Runner`; the other six effect kinds route to `GitHubProvider` through the same parameteric transition.
The stable C1 smoke entry point now executes this complete chain.

Exact Quint 0.32.0 evidence used the accepted binary SHA-256
`939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f`: typecheck passed; all seven named effect
witnesses and the smoke witness each passed 10,000 executions; and seed `0xC2F0` completed 200 samples of 30 steps
against `safety` without a violation. This is foundation evidence, not the C2 exit: fault listeners, projection,
progress assumptions, negative witnesses, bounded checking, and the structural/runtime review remain unchecked.

The protected C2 runs measured the cost of compiling the larger typed combined source in every independent TLC
shard. State and transition counts were unchanged, but observed elapsed time reached 129,530 ms and peak process
memory reached 2,592 MiB. C2 therefore adds fixed source-compilation headroom: the 90/105-second elapsed ceilings
become 135/150 seconds respectively, and the TLC peak-memory ceiling becomes 3,072 MiB. Depth, state, transition,
sample, artifact, toolchain, and workflow-envelope limits are unchanged. These are operational compilation
ceilings, not larger semantic exploration bounds.

The enlarged assembled source also exposed an outer CI constraint after those inner budgets passed: three canonical
semantic shards completed compilation and simulation but were canceled at the workflow's 25-minute job boundary.
The semantic job timeout was initially raised to 40 minutes, but the 10,000-sample administrative-retirement
negative-control shard subsequently completed compilation and simulation and then reached that outer boundary while
its bounded check was still active. A later protected run still had a canonical semantic shard active after 56
minutes, leaving less than 7% slack under a provisional 60-minute ceiling. The semantic job allowance is therefore
90 minutes (about 60% headroom over that observed active duration).
This changes only the outer runner allowance; it does not relax any Quint/TLC elapsed, memory, depth, state,
transition, sample, or artifact limit.

The independent epoch/performance shard subsequently completed compilation and simulation but reached its separate
15-minute job boundary while the bounded performance check was still running. Its outer job allowance is therefore
30 minutes. As with the semantic-shard allowance, this is runner headroom only: the epoch's 150-second formal
measurement ceiling and every semantic/resource bound remain unchanged.

Protected runs also exposed an Apalache startup lifecycle in which `verify` exited zero immediately after
`SanyParser`, with the server launch/shutdown markers but without either TLC state measurements or an invariant
result. The existing bounded startup retry now classifies that exact signature as `early-lifecycle-exit`, alongside
the already recognized nonzero early-exit signature. It retries once on a fresh isolated endpoint and remains
fail-closed if the retry does not produce a real result; retry counts remain explicit in qualification receipts.

The combined C2 plus GS2-08.5 source then exposed a second lifecycle defect: an Apalache child could remain alive
after simulation until GitHub canceled the entire semantic job at 90 minutes. The retained elapsed ceilings had
previously been checked only after child exit, so they measured completed work but could not enforce a bound on a
hung process. `runMeasured` now treats each formal test's declared 135/150-second elapsed ceiling as a wall-clock
process deadline, kills the complete child tree when it expires, emits `APALACHE_EXECUTION_TIMEOUT`, and routes a
timed-out `verify` through the same single bounded fresh-endpoint retry. A second timeout fails closed. This makes
the existing inner ceiling enforceable; it does not increase any semantic or resource bound. The killed
infrastructure attempt is represented by the receipt's explicit physical-process/startup-retry counters, while
elapsed and peak measurements use the one successful logical attempt. Otherwise a retry triggered exactly at the
ceiling could never pass the unchanged semantic budget, even when the fresh attempt completed immediately.
The same 150-second maximum ceiling also guards unmeasured Apalache `verify` calls used by the base invariant and
negative-control suite; those calls use the same one-retry lifecycle classification, and negative controls must
still emit their expected invariant/ITF evidence, so a timeout cannot be mistaken for a successful red control.

C2 fault/recovery checkpoint evidence (2026-09-17): the Choreo model now orders the seven effects, exposes a pure
`legacyProjection`, separates normal progress from fault scheduling, and models ambiguous outcomes, applied/absent
reconciliation, same-operation retry, crash recovery, fresh authority readback, authenticated resume, duplicate
responses, stale generation, wrong identity, and journal rejection. Fifteen deterministic Quint scenarios pass
against the pinned 0.32.0 binary, including every effect kind and all named fault/identity controls. The model and
scenario helpers are separate modules so an executable test cannot make `init` part of the verification step.

Random exploration found two real recovery defects and the checkpoint fixes both. First, recovery after durable
`Applied` failed to reconstruct Host completion and could restart the same effect. Second, a delayed
`ReconcileAbsent` result could cross a proven-absence retry, because the stable operation id alone did not identify
the retry round. The retry now preserves `operation` while incrementing `revision`; the journal accepts only the
next exact revision and the Runner binds its acknowledgement/reconciliation state to that revision. The journal
also fences its recovery response until earlier append messages have drained. The explicit Choreo consume effect
now carries a finite `{ process, message-key }` rather than recursively embedding the entire `Message` union; this
preserves exact stale/wrong-identity removal and makes Quint's TLA+ conversion possible.

This checkpoint does **not** complete C2. After conversion succeeded, TLC exposed the delayed-absence violation
after 6,470 distinct states; the fixed model no longer reproduces that trace, but a subsequent full TLC run and
both combined-source and 73,834-byte lean Apalache bounded runs reached the unchanged 150-second process ceiling.
The lean Apalache run passed parsing and Snowcat typechecking before timing out in symbolic transformation. No
budget was raised and no bounded-success claim is made. The next C2 continuation must reduce transition/state
encoding cost or partition an equivalent bounded verification root, then rerun bounded safety, listener coverage,
and the structural/runtime review before checking the remaining C2 boxes. C3 trace export remains blocked on that
exit rather than synthesizing fault behavior in F#.

C2 bounded-exit evidence (2026-09-17): `eng/verify-choreo-c2-bounded.sh` mechanically extracts the pinned Choreo,
basic-spells, and hosted-writer modules from the canonical `quint-test` fence and refuses any Quint binary except
the accepted 0.32.0 SHA-256. It typechecks the selected source, runs all fifteen deterministic scenarios with
10,000 samples, repeats the 200-sample/30-step randomized safety exploration at seed `0xC2F0`, and runs two TLC
roots at 20 configured steps under independent 150-second process ceilings. The provider root generated 715
states / 593 distinct states with maximum outdegree 4; the Runner root generated 673 / 569 with maximum outdegree
4. Both completed with an empty queue and no safety violation.

The bounded roots partition by external authority rather than weakening the protocol. `Claim` represents the six
provider-routed effect kinds, whose common listener is also exercised individually by the deterministic effect
witnesses; `ProcessWork` covers the distinct Runner listener. Both roots retain normal application, ambiguous
outcomes, applied/absent reconciliation, durable retry, crash at any enabled point, the three recovery gates,
delayed-message rejection, and journal rejection. Exploration is bounded to one proven-absence retry and one
completed crash/recovery cycle per operation. The unrestricted production `step` remains unchanged. The Runner
root begins at a second initializer that is the exact reachable state after settled `Claim`, including the
journal's `Applied` operation, append count, and provider response facts; it does not invent an abstract state.

Structural/runtime review:

- Dead actions and receiver coverage: the architecture test inventories the bounded root; the two roots plus the
  named duplicate, stale-generation, wrong-identity, and out-of-sequence scenarios cover every listener and all
  four processes. Seven effect witnesses cover every effect constructor.
- Vacuity: randomized exploration previously produced two genuine `safety` counterexamples, including the
  delayed-absence revision crossing. Their repaired paths remain in the bounded roots and deterministic suite.
- Message constraints and atomicity: the closed `Message` union, `typedMessageSoup`, exact `Consume` key, and one
  `stepWith` receiver per transition prevent unconstrained delivery and preserve each request/reply boundary.
- Symmetry and state growth: only the six genuinely identical provider-routed effect labels are represented by
  `Claim`; the structurally different Runner has its own root. Removing unbounded equivalent retry/restart cycles
  reduces the failed multi-million-state search to 1,162 distinct states across the two complete partitions.
- Counterexample readability: each root retains typed process/message/operation state and named listener actions;
  no opaque symmetry quotient or message identity erasure is introduced.

The base canonical semantic shard reruns this script in protected CI before its receipt can be accepted. C2 is
therefore complete when this PR merges, and C3 may replace the remaining F#-assembled hosted-writer traces with
genuine Quint ITF artifacts.

### C3 — make Quint traces the executable contract

- [x] Define and version the stable observable trace schema and trace manifest.
- [x] Export actual Quint traces for happy path and the six required fault/identity scenarios.
- [x] Extend the F# harness to validate the source/toolchain/Choreo identities and parse the Choreo projection.
- [x] Remove F#-generated model transitions from correspondence tests.
- [x] Add deterministic regeneration/digest checks and first-divergence diagnostics.
- [x] Retain negative mutations proving that an invalid trace cannot pass through a permissive projection.

Exit evidence: exact seeded command lines, committed small fixtures, reproducible hashes, and F# tests replaying
Quint output rather than a reimplementation.

C3 exit evidence (2026-09-17): `eng/verify-choreo-c3-traces.sh` extracts the same canonical pinned Choreo source as
C2, refuses any Quint executable except the accepted 0.32.0 SHA-256, executes deterministic named tests with the
Rust backend and one sample, removes only Quint's volatile description/timestamp metadata, binds the canonical
source name, and byte-compares the result. The base semantic shard regenerates the 64-state happy path in
protected CI. The committed manifest binds the protocol source commit/digest, Quint and Choreo identities, exact
command shape, normalization, raw variable, scenario, state count, durable milestone list, and trace digest.

Eight genuine raw traces are retained: happy path, lost-applied reconciliation, proven-absence retry, three-gate
restart, duplicate response, stale generation, wrong identity, and missing native readback. They contain 180 raw
Choreo states and 69 selected durable milestones. F# validates every raw process-local operation envelope before
projecting it, derives `QuintReplayTrace` states directly from `O2HostedWriterChoreoModel::choreo::s`, and locates
source bindings in the canonical Choreo definitions. The former `WriterModel`, `modelStep`, and F# ITF builder are
deleted. Happy and lost-applied paths replay through the Akka wrapper over the production journal/provider
contracts; faulty native readback reports the exact final `hostSettles` divergence. Negative controls cross a raw
generation and corrupt a projected state identity, and both are rejected. Production `ExecutionSessionActor`,
Host composition, and PostgreSQL correspondence remain deliberately assigned to C4.

#### C3 continuation checkpoint — 2026-09-17

Safe resume branch: `routine/choreo-c3-quint-itf`, rebased onto `origin/main` at
`794458ec586660aa2603dcb74374a9356abd9d61` (which contains merged C2 commit
`a598f27fc8d2c23647f4dc45df7d5119f3461b74`). The branch is intentionally limited to C3; do not begin C4 in the
same PR. Draft [PR #420](https://github.com/FS-GG/FS.GG.Coordination/pull/420) is the durable review/CI handoff.

Completed on the branch:

- eight normalized raw Quint ITF fixtures and the versioned identity/milestone manifest;
- deterministic all-scenario regeneration plus protected base-shard regeneration of `happy-path`;
- fail-closed F# parsing of raw Choreo process state and operation envelopes;
- removal of `WriterModel`, `modelStep`, and the F#-assembled hosted-writer ITF;
- happy/lost-response Akka wrapper replay, exact native-readback divergence, and two negative mutation controls;
- architecture guards and this roadmap evidence.

Validated before the checkpoint:

```text
Release build: Host tests project, 0 warnings / 0 errors
Host test suite: 63 passed
Choreo trace + replay focus: 6 passed
Choreo architecture focus: 9 passed
Pinned happy-path regeneration: 64 states, SHA-256 422986cf7e35c538151d9fe650e45437b9c9e02c2b986d481e904355577f0b90
All eight fixture regenerations: byte/digest exact, 180 raw states total
```

Resume procedure:

1. Fetch the draft PR branch and read this checkpoint plus the C3 exit-evidence paragraphs above.
2. Run `git diff --check` and `bash eng/verify-choreo-c3-traces.sh` with the accepted Quint binary supplied through
   `FSGG_QUINT_BIN`; the script refuses any other binary digest.
3. Run the full Host and architecture suites. On this workstation, use the already-qualified canonical NuGet cache
   if the locally installed SDK reports the known `FSharp.Core 10.1.302` NU1403 cache collision; clean GitHub
   runners do not exhibit it.
4. Inspect draft-PR CI, repair only C3 regressions, mark ready, arm squash auto-merge, and wait for protected checks.
5. After merge, confirm there are still no packable projects (there were none at C2/C3) and start C4 from fresh
   `origin/main` on a new branch.

Do not regenerate milestone indices in F#, collapse the committed raw ITF to a hand-authored projection fixture,
or claim C4 production correspondence from the wrapper actor. The raw `O2HostedWriterChoreoModel::choreo::s`
state remains the executable source of truth.

### C4 — close production correspondence gaps

- [x] Replay runner actions against the production `ExecutionSessionActor` and neutral coordinator.
- [x] Replay journal actions against the production journal contract and a real PostgreSQL integration slice.
- [x] Replay Host steps through `MainEffectDriver`/`MainRouteWorkflow` with recording external adapters.
- [x] Replay GitHub facts through `MainProductionCallbacks` without granting the fake policy authority.
- [x] Cover crash/recovery, reconnect, duplicate, stale generation, wrong identity, proven absence, and missing native
  readback across the composed seam.
- [x] Demonstrate that all ambiguity paths are observation-only until durable proven absence.

Exit evidence: unit, actor, Host composition, and PostgreSQL tests, plus mutation controls at each process boundary.

C4 exit evidence (2026-09-18): C3 merged as `9222dbdd01a6cf86ac737efa7550fa20c343415b` in
[PR #420](https://github.com/FS-GG/FS.GG.Coordination/pull/420). `ChoreoProductionReplayTests.fs` now reads all
eight committed raw Quint scenarios and drives `MainAdmissionPreparer`, `MainRouteWorkflow`, `MainEffectDriver`,
`HostedWriterJournal`, the real `ExecutionSessionActor`/neutral coordinator, and the dispatch/native-readback
methods of `MainProductionCallbacks`. Recording providers supply external facts; production policy decides
acceptance. The exact same replay runs against both memory and PostgreSQL work-item/execution journals.

The observable comparison binds completed-effect count, pause state and current authority after each selected
Quint milestone, plus dispatch/unknown states, duplicate rejection, no redispatch during ambiguity, and no
continuation on absence. The production workflow eagerly persists its next intent during continuation; this is
an explicit microstep coalescing, not a second F# transition oracle. Candidate bytes and other external effects
remain recording boundaries; existing candidate-pipeline and GitHub adapter tests retain their separate scope.

Independent generation, operation, candidate and repository readback mutations fail at raw state 9
(`hostSettles`); stale journal sequence fails at raw state 4 (`journalRecordsDispatch`); a crossed execution
actor generation fails at raw state 18; missing native delivery fails at raw state 63. Duplicate provider responses
leave completion unchanged. Restart replay requires journal recovery, fresh readback and explicit resume.

Replay found and repaired two production defects without changing Quint: `MainEffectDriver` formerly invoked
its completion continuation for a proven-absence result, and `HostedWriterJournal` omitted retry effect metadata
that PostgreSQL requires. Absence now returns `EffectProvenAbsent` without advancing, and retry metadata derives
from the recovered pre-event state. An explicit `AuthorizeEffectRetry` retains the original operation identity.
Neither repair enables implicit dispatch or changes the canonical protocol.

Commands: `dotnet test tests/FS.GG.Coordination.Orchestration.Host.Tests -c Release`; from the PostgreSQL test
project, `DOTNET_EXE=<pinned-dotnet> bash run-private-postgres.sh`; and
`FSGG_QUINT_BIN=<pinned-quint> bash eng/verify-choreo-c3-traces.sh`. The integration command creates and stops
its own private PostgreSQL 18.6 cluster. The unchanged trace digests and source/tool identities remain in the C3
manifest. The CLI has become packable through unrelated work since C3's historical checkpoint; C4 changes only
non-packable Host implementation and tests and does not publish or change the CLI package identity.

C4 also repairs the C2 runner's test selection: Quint defaults to names ending in `Test`, while this suite
uses descriptive names. The script now extracts the fifteen declared `run` names and selects them explicitly,
excluding imported helper actions and predicates. Earlier C2 script success alone did not establish that its
named-test command executed those scenarios; C3 exports did select their scenarios explicitly. The corrected
C2 invocation and bounded roots are rerun before the formal-workload migration.

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
