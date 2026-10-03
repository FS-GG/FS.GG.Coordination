# V2-PREFLIGHT-01 — Typed prerequisite admission

**A–B locally source-qualified; native source CI, merge, publication and product adoption remain pending.** Owning path:
`FS.GG.Coordination/docs/roadmaps/v2-preflight-01.md`. Owner: Coordination for reusable
execution contracts; BAR and SC2 for their real runner adapters; `.github` for programme
and skill integration. Route: routine. Named Unified part: **Typed prerequisite admission**,
V2 follow-on, linked from [Unified §9.8](https://github.com/FS-GG/.github/blob/main/docs/2026-09-07-154210-fs-gg-unified-development-roadmap.md#98-feature-parts-and-subroadmap-index).
The [selected parent contract](https://github.com/FS-GG/.github/blob/ffcc5f69bcd24c6d189780c0f97e4ad67e553e5b/docs/github-substrate-v2-roadmap.md#v2-preflight-01--typed-prerequisite-admission-next-item-2026-10-03)
owns the four aggregate outcomes. This document owns their bounded execution windows;
parent completion is derived from these results, not a second competing checklist.

## Outcome and retained authority

The actual expensive execution edge accepts only a privately constructed `PreparedAttempt`
whose command, configuration, transitive input closure, required observations and deadlines
match the attempt. The preparation command executes bounded import and discovery checks
against the assembled capsule. Unknown, malformed, unavailable, timed-out and incomplete
observations cannot become readiness. Changes after preparation invalidate the affected
admission, and mutable identity/time facts are checked at use.

A missing BAR capture import must stop before host/engine/browser workload launch. Repeated
SC2 observations of the same identity must not exhaust a distinct-identity bound, while a
new identity beyond that bound still refuses and closes owned resources. An invalid request
must leave reducer state unchanged; a valid request whose time advance expires older work
must retain retirement, timer and settlement effects even if that request subsequently
refuses. Both actual product adapters, with their real artifact layouts, must use the checked
route before the parent closes.

Preparation grants no native-operation permission. Preserve native checks, coherent validation,
existing bounds, source custody and each product's native authorization. Existing admitted
work continues. No new package, daemon, policy registry, issue/claim or approval cycle is selected.

## Evidence inspected and gaps

Read-only inspection used `.github` protected contract `ffcc5f69`, Coordination local/default
`2309964321a895100d12ededb1c9c00c8342b509`, SC2 source checkout `e521fa9` with
local remote reference `827377e5`, BAR explicit source `3d723b`, SDD local remote reference
`5f07e02`, and the Game candidate named below. These are source observations, not a fresh
hosted acceptance census. Execution workers re-observe their own source heads before editing.

| Reuse | Observed capability | Remaining gap |
|---|---|---|
| Coordination `src/FS.GG.Coordination.Orchestration.Execution/PortableWorkspaceAdapter.fs`, `PortableWorkspaceExecutor.fs`, `PortableWorkspaceContract.fs` | `prepare` validates exact profile/command/scope/source/image/workflow/fence/time; executor selects reviewed fixed operations, checks deadline immediately before reservation, journals duplicates and requires cleanup | `PreparedWorkspaceOperation` is a public record. It does not establish actual assembled-capsule import/discovery or mutable prerequisite observations. Extend this execution boundary |
| Coordination `src/FS.GG.Coordination.Cli/PortableWorkspaceCommand.fs`, `PortableWorkspaceRuntimeCommand.fs`; `eng/run-packaged-portable-workspace-qualification.py` | Existing `workspace-contract operation prepare`; exact CLI archive extraction and Execution DLL qualification; existing fixed runtime route | Current prepare output is contract agreement, not launch readiness. A JSON receipt must never deserialize into a trusted prepared value |
| Coordination `docs/roadmaps/v2-lang-portable-integration.md` | Owning source says portable CLI 0.2.0 publication/readback complete; 0.2.1 trusted-provider source prepared, installed/provider/public join pending | Preserve this newer owner evidence even where the Unified index still mentions 0.1.7/P3 pending. This feature may use published machinery but must not claim 0.2.0 includes new checks or close V2-LANG P4/P5 |
| Coordination `Protocol.md`, `Protocol.bindings.json`, generated compiled outputs, canonical qualification/replay | Published SDD compiler boundary and canonical source/contract drift validation already exist. Intake pins were `FS.GG.SDD.Artifacts [1.5.0]`, `FsQuint 0.1.0`; the qualified compiler join below uses public SDD 2.1.0 | Add consumer-owned semantics through the existing compiler route. The architecture guard rejects copied compiler/profile/replay sources and local `.qnt` files; do not evade it with a second extractor |
| SC2 `src/SC2.Client.Contracts/ModulePreparation.fs`; `eng/sc2c-author-preparation/ModulePreparation.qnt`, `_test.qnt`, `Program.fs` | Artifact/config/provenance preparation, invalidation/cleanup model, real `ModulePreparation.apply` replay through FsQuint | Prepared module alone does not prove the complete native execution capsule. Existing `tests/live-qualification-preflight.mjs` deliberately establishes only `assets-ready` |
| BAR `tests/Broker.NativeProof/RuntimeEvidence/` at `3d723b` | `GrowingLogEvidence.fs/.fsi/.qnt`, full-state/effect correspondence, policy/data-root/complete-record checks; bounded complete `/proc` reads repaired | These checks are product policy; no generic preflight can replace them. Private stock runner/capture package requires its actual closure and discovery check |
| Game candidate `b72f6155c3e38d84f37dcfd1a10c01799feb690f` | `src/Wasm.Browser/Lifecycle.fs` returns `HostDecision`; `submitLimited` preserves post-admission expiry state/effects on Error. Canonical `eng/wasm-shared/lifecycle.qnt`, selected traces, production facade replay, native API compatibility and installed browser fixture exist | Parent reports 13 selected traces, 24 reached witnesses and 8 browser controls; README/source corroborate scope, planner did not rerun them. Candidate remains unpublished/root-review pending. Reuse its correction, do not reimplement it |
| SDD `docs/reference/typed-specifications.md`, `docs/release/fsquint-migration.md` | Compiler owns source extraction/profiles/compiled contracts; generic ITF/replay belongs to published FsQuint. SDD 2.0.2 records stable FsQuint 0.1.0 adoption | No new SDD compiler or generic replay implementation is needed for this feature |

Active work is explicitly excluded from the first touch-set: BAR
`/tmp/bar-rp4-capture-custody-20261003` (`stock_entry.py`, `capture_custody.py`, launcher and
controls); SC2 private `sc2-p2-owned-census-20261003` helper/control directory; Game supervisor
candidate; SDD knowledge/provider-contract-2 source; Templates and wizard receivers. Private
paths are custody references, never package contents or portable authority. The SC2 kernel
helper summary reports synthetic own-process and one ordinary managed-probe qualification,
with mapped-memory/native provenance **Unknown** and actor/native grant false. Those limits
must survive reuse. Do not edit the private helpers while their current owners are working.

## Contract and model decisions

Use an additive opaque `PreparedAttempt` inside the existing Execution assembly. Preserve
existing public preparation DTOs for compatibility; they are descriptive inputs, not effect
capabilities. A single trusted preparation path constructs the value from validated command
and configuration bytes, exact source/image/recipe/input-closure identities, required check
results and an applicable deadline. Snapshot mutable collections before binding them. An
untrusted caller cannot satisfy a required check by passing a Boolean or by replaying a receipt.

Keep shell/Python/JavaScript products independent of Coordination CLR/Akka references. They
invoke the existing tool boundary or their owner adapter, which executes preparation in the
same process/session that gates the effect. Serialized output describes what was checked;
execution reconstructs readiness and revalidates facts. Public JSON is not a bearer token.
The old callable run route must delegate through this boundary so no new bypass remains.
Recovery/cleanup remains callable for previously owned work even when new launch preparation
refuses; otherwise a stale prerequisite could strand an existing process.

Preparation has no product workload or protected effect. Import/discovery subprocesses are
bounded and use the actual cwd, environment, tool, arguments and transitive capsule files.
Their scratch/output/cache is outside immutable inputs. Checks that cannot safely run without
launching a product return unavailable rather than claiming success. Probe output/processes
are cleaned up; no package download or repair happens implicitly during a gate. Revalidate
files/path/descriptor/process identity, selected configuration and remaining budget immediately
before each dependent effect. A digest alone does not bind ongoing inode or process identity.

Partition only the relevant state:

- Artifact preparation/invalidation: closure identity, required-check outcomes, ready/invalidated,
  mutation between preparation and effect, reprepare. Reuse SC2's model shape; do not move its
  WASM/provenance policy into Coordination.
- Admission/deadlines: invalid input versus valid time advance, inclusive expiry, decision state
  and ordered effects independent of result. Compose against the existing Game reducer/model
  observations; no copied lifecycle scheduler.
- Owned observation/cleanup: distinct process/mapping identities, repeated observations,
  separate event/byte/time bounds, cancellation and observed cleanup. Product owners retain the
  actual kernel/filesystem semantics and observations.

The shared canonical modules live as new fenced modules in Coordination's existing
`src/FS.GG.Coordination.Protocol/Protocol.md` and bindings/compiled output route. Avoid a
monolithic cross-product model. Tests compose a small artifact/admission/cleanup interface,
while each product replays its own actual reducer. Keep preparation, mutation and effect as
separate transitions so the model can reveal a stale-check race. Requirements are independently
stated; test plan/closure/command data are extracted from or compared with the real runner inputs.

Use pinned typecheck then bounded `quint run`, concrete module instances, reachable completion
and causal mutants. Retain native repository formal gates where applicable. New sampling is
sampled evidence, not exhaustive verification; this plan does not add unbounded model checking.
Trace replay compares real production state and ordered effects, including cleanup, rather
than a new F# copy of the abstract transition table.

## Executable windows

The first window is one Coordination owner for **A–B together**, avoiding competing edits to
execution, canonical model bindings and generated artifacts. Root documentation can proceed
in parallel. Product owners may independently inspect their own final capsules and prepare
fixtures, but shared-contract adoption waits for the accepted interface. Source merge is routine;
publication and native execution have their existing separate boundaries.

- [ ] **V2-PREFLIGHT-01.A — Opaque preparation and the existing execution edge** — route: routine.
  Parent .1. Depends on: current Coordination source and an inspected actual BAR/SC2 input/check
  contract; no completed private repair is needed for the generic source slice.
  Touch-set: existing Execution `PortableWorkspaceAdapter.fs`, `PortableWorkspaceExecutor.fs`,
  additive `PreparedAttempt.fs` plus project compile order, existing Execution tests, and
  CLI `PortableWorkspaceCommand.fs`/`PortableWorkspaceRuntimeCommand.fs` only as needed for
  one preparation command and checked delegation. One owner owns all these files.
  Acceptance: direct construction fails at the F# API boundary; good bounded checker observations
  construct readiness; missing capture dependency, empty/unexpected discovery, malformed output,
  failed/unknown/timeout/tool-unavailable checks refuse with zero workload launches. A changed
  argument/config/input or expired clock refuses at consumption. Idempotent settled execution
  remains settled, and owned cleanup still runs when new preparation is unavailable. Test actual
  bounded subprocess discovery in an isolated assembled fixture as well as an instrumented runner.
  Local source qualification is complete; the checkbox awaits native source CI and merge readback.

- [ ] **V2-PREFLIGHT-01.B — Partitioned model and production correspondence** — route: routine.
  Parent .2. Depends on: A's state/interface shape, implemented in the same owner window.
  Touch-set: Coordination `Protocol.md`, `Protocol.bindings.json`, their generated outputs,
  `eng/validate-canonical-quint-protocol.fsx`/qualification inventory only where needed, Execution
  replay tests and genuine trace fixtures. Existing generated/source digests are regenerated by
  the published compiler. BAR, SC2 and Game canonical files remain with their existing owners.
  Acceptance: success reaches done with owned cleanup; stale input between check and use prevents
  effect; invalid admission leaves state/effects unchanged; valid clock advance can expire prior
  work and then refuse while retaining effects. Repeated identity does not increment the distinct
  count; distinct-cap-plus-one and event/byte/time-cap-plus-one refuse. Causal mutations must expose
  bypassed discovery, dropped invalidation, dropped expiry effects and false cleanup completion.
  Changing actual capsule contents, runner argv, model, implementation or replay projection
  invalidates relevant evidence; an unrelated-file control remains reusable. Record explicit
  bounds/tool identities and first divergence. Run the applicable canonical/native gates.
  Local source qualification is complete; the checkbox awaits native source CI and merge readback.

### Next bounded adoption window — open only from accepted A–B and owner handoffs

The following are outcome outlines, not dispatchable checkboxes until the named source/capsule
joins are available. They preserve parent .3/.4 identity and qualify products independently.

**C / BAR checked runner.** Depends on A–B and the current BAR owner's frozen capture repair.
Initial likely repository touch-set: `tests/Broker.NativeProof/RuntimeEvidence/` adapter/controls,
`tests/Broker.NativeProof/tactical-native-journey.spec.js`, exact launcher discovery tests and
owning roadmap evidence. The actual `stock_entry.py`, `stock_config.py`, `capture_custody.py`
and launcher currently reside in a private assembled helper bundle, so the BAR owner must name
its authoritative source and fresh capsule digest before edits are dispatched. Do not guess a
new repository path or patch the retained packet. Exercise import and Playwright discovery from
the exact assembled layout, remove the capture import as a causal negative, and verify zero
host/engine/receiver/browser workload launches. Prove required tests are discovered, outputs
remain outside immutable inputs, and capsule/input/output identity drift refuses. Good preparation
permits only an otherwise authorized operation. Current RP3/RP4/RP5 and 0/6 useful-play status
remain owned by BARC-01; this item cannot close them by proxy.

**D / SC2 checked runner.** Depends on A–B and the current SC2 owner's frozen kernel-census
helper/control export. Likely repository touch-set: `src/SC2.Client.Contracts/ModulePreparation.fs`,
`eng/sc2c-author-preparation/`, `scripts/qualify-live-sc2.mjs` and its relevant test/caller,
plus the explicitly handed-off private actor-preparation edge. Avoid editing all native or
WASM files under one broad scope. Bind the actual repeated/distinct identity key (PID/start and
mapping identity as applicable); deduplicate only the distinct census count, never event/byte/time
resource accounting. Preserve unknown historical/mapped-memory provenance. Independently prove
bad preparation prevents actor launch, exact-cap acceptance, distinct-cap-plus-one refusal,
timeout, malformed ledger, owned cleanup, and good prepared route acceptance. Adoption of Game
supervisor behavior additionally depends on its accepted exact producer artifact; shared tests
alone never qualify SC2 native actors.

**E / Existing distribution, local usability and caller adoption.** Depends on accepted A–B
and the respective C/D source routes; BAR and SC2 installed checks can proceed independently.
Use the existing Coordination CLI package, whose archive already carries Execution.dll although
the Execution project itself has `IsPackable=false`. Reuse
`eng/run-packaged-portable-workspace-qualification.py` and the normal publication route. Exact
successor version is assigned by the release owner; no version is reserved by this proposal.
Qualify the same preparation command from a fresh installed tool with the repository unavailable,
then from the actual product capsules. Diagnostics identify the missing file/import/test or
changed predicate without leaking private paths. Cold setup and warm preflight timing, cleanup,
missing tool and malformed observation cases must be recorded. Integrate the check before each
product's costly workload, with failure limited to that dependency. Publication, public readback,
installed adapter adoption and any actual native run are individually reported.

**F / Guidance and joined closure.** Root owns the `.github` roadmap/skill change. SDD owns
`docs/typed-sdd-lifecycle.md` and `docs/reference/typed-specifications.md`; agree with the active
knowledge lane before touching the former. Add a possible strategy for recurrent costly
preparation/admission defects: assess existing static checks/tests first, then small typed
preparation and partitioned models where ordering/mutation/bounds recur. This is not universal
Typed SDD ceremony and creates no lifecycle gate. Keep the published compiler/replay boundary.
Close parent .4 only after both C and D use the checked route and regressions plus actual package
layout checks pass; report any remaining publication, installed or native acceptance separately.

## Economics, stop conditions and workspace impact

Use [pipeline-preflight](https://github.com/FS-GG/.github/blob/main/.agents/skills/pipeline-preflight/SKILL.md)
with its economics reference. The concrete defects are import/discovery failure before a costly
BAR attempt, repeated census work and lost post-admission expiry effects. Static checks and
actual discovery catch missing artifacts; small models address order/invalidation/refusal/cleanup.
The identified reusable consumers are BAR and SC2. Start new modeling with a 30-minute exploratory
cap per genuinely missing partition, including setup/diagnosis; then reduce scope or explicitly
reassess based on concrete residual risk. Existing required formal checks remain required.
Target warm overhead at the smaller of 60 seconds and 2% of the avoided critical path, recording
any justified exception. No defect frequency, avoided-time or monetary saving is currently known.

Stop the affected boundary on unknown checks, stale identities, unbounded/unsafe imports, failed
cleanup, unexpected source ownership overlap, mismatched model/runner inputs, missing accepted
shared interface, or absent operation authority. Do not respond by increasing production bounds,
restarting unrelated lanes, silently skipping a required check, or treating an old receipt as
current readiness. Keep existing attempts and failed evidence. A contract change that requires a
new product execution framework invalidates this plan and warrants a narrower replan.

**Unified §9.9:** A–B first change source execution capability, with no generated workspace effect.
C/D first change the corresponding source/private product runner after owner adoption. E first
changes installed behavior through an exact published Coordination CLI plus separately adopted
product capsules. Current 0.2.0 publication is prior capability, not publication of this feature.
General SDD/Templates provider or lifecycle defaults do not change. The optional guidance can
reach new Typed SDD workspaces only if its owner-sourced skill/doc material is actually packaged,
published and materialized by an existing SDD/Templates route; documentation merge alone is not
that adoption. If packaged guidance is selected, qualify one clean creation with exact producer,
provider and lifecycle pins and one separate preserving upgrade; other/local-only workspaces
must still function. No change to Templates/wizard is required by A–D, and their current owners
continue independently. Installed local usability must require no active coordination service
merely to run preparation against local allowed inputs.

Observation sources are native command exit/results, launch counters, exact assembled inventories,
model traces and reducer/effect comparisons, package/public readback and product evidence. Telemetry
begins **not-configured**; usage is **usageUnknown**, never zero. Preserve Unified accounting for
productive/overhead work, unknown coverage, shared planning/review/integration and later attributed
repairs; engineering effort, tokens, runner minutes and wall time remain separate. Missing usage
cannot block a valid source merge or certify efficiency.

## Index patch and first dispatch packet

Proposed new §9.8 row (retain the existing selected parent link):

| **Typed prerequisite admission — V2-PREFLIGHT-01** | V2 follow-on: checked attempt construction, bounded actual capsule discovery, partitioned model/reducer correspondence and independent BAR/SC2 adoption | Coordination owns shared execution; BAR/SC2 own adapters; `.github` owns guidance. A–B source window ready, product adoption awaits accepted contract and existing owner handoffs; source/publication/installed/native states separate | [Selected parent](github-substrate-v2-roadmap.md#v2-preflight-01--typed-prerequisite-admission-next-item-2026-10-03); [owning subroadmap](https://github.com/FS-GG/FS.GG.Coordination/blob/main/docs/roadmaps/v2-preflight-01.md) |

Until delivered, replace that durable-plan URL with the actual implementation-branch/draft link;
current draft is `/tmp/v2-preflight-01-plan-20261003.md`. Root may update the index asynchronously.

First dispatch: one fresh Sol-medium Coordination worker, A–B only, isolated worktree from current
protected source, specified execution/CLI/canonical-model touch-set and native checks. Parent is
integrator. It may land routine source without awaiting product repairs; no new publication or
native grant is inferred. Root's bounded documentation change is disjoint. Next dispatch: BAR
and SC2 in parallel after exact shared interface and their own repair handoffs; serialize each
product's shared files with its current owner. No user decision is outstanding. The material open
facts are the finalized private capsule source identities, accepted Game successor artifact and
release version; they block their consuming adoption effects, not the first source window.

### Earlier source checkpoint — superseded cleanup and compiler evidence

The local A–B slice now constructs an opaque attempt from owner-reviewed bounded
imports and discovery of an exact assembled input closure. Consumption rechecks
command, configuration, deadline and input identities before reservation and before
runner effects. Existing reviewed fixed operations and settled/idempotent recovery
retain their operation-specific contract checks; an owner-declared unavailable
capsule refuses new work while cleanup remains callable.

Execution controls pass 78/78; existing CLI runtime command controls pass 4/4.
Actual external F# construction fails with FS0509, and JSON deserialization cannot
reconstruct readiness. Real Python import/discovery controls cover missing imports,
malformed observations, timeout, output caps and unavailable tools without executing
the discovered workload. Owned FIFO and symlink inputs refuse promptly. Held file
descriptors bind regular-file identity while hashing within declared size/time bounds.
Controlled descendant cleanup uses fresh PID/start/session joins and pidfds; an
escaped live session refuses unknown cleanup without signaling an unrelated process.
This bounded ordinary probe custody does not claim a complete kernel fork/escape
census or BAR/SC2 native qualification.

Three canonical qualification partitions retain distinct preparation/invalidation,
admission/time/refusal, and observation/cleanup obligations. Genuine native Quint
traces replay the production reducers through FsQuint; four actual production
mutations produce first-divergence failures and restored controls pass. The
100-sample, 12-step seeded exploration and four model mutants are bounded sampling,
not exhaustive proof. Published SDD 1.5.0 generated the current canonical projection;
all 19 prior exports and existing protocol tables are preserved, plus one contract
version export. The established qualification fence keeps the measured 4,093-row
core below the fixed 4,096-row limit.

Full native gates and fresh calibration remain pending the separately owned
published compiler-capacity join. Historical calibration authority and timings
remain unchanged. Source readiness, publication, installed consumer acceptance, and
product-native admission remain separate. A–B acceptance boxes remain open until
that coherent gate and integration review complete.

Independent cleanup review subsequently found a real custody gap: a checker can
spawn a separate-session child, redirect its streams, emit readiness and exit before
the first census. An empty later PID/group view cannot establish cleanup. The new
owned fast-orphan control blocks source admission until complete custody or an
actual OS constraint closes that case. Read-only capability inspection found the
local cgroup mount read-only and no delegated user-systemd bus; installed util-linux
`setpriv` supports inherited seccomp filters, a candidate bounded no-fork checker
route for the separately reviewed repair. Existing fixed-operation semantics remain
unchanged. No source/publication/native acceptance is claimed.

Three-way interaction controls now join actual capsule observations, changing input
identity or admission deadline, and the runner effect boundary; both require zero
runner calls on refusal. The existing settled-cleanup/unavailable-new-admission case
checks the recovery side of that composition. These additions still require their
focused run after the custody repair.

The generated-entry audit observed 1,012 lookup rows, 4,095 type rows and 4,095 effect
rows with distinct node IDs. Repeated payloads are present: lookup IDs 3556 and 3603
resolve declaration 3509 at separate AST use sites; literals 160–163 have distinct
values but the same string type and empty effect. The audit proved no redundant
semantic entry, so removal count is zero. It does not claim every possible source
declaration minimal. All 19 previous exports and the catalogue, relationships, action
effects, verification profiles, bounds and compatibility values remain preserved.

The approved measurement-only route retains ordinary exact-baseline refusal and
writes unadmitted observations outside source through exclusively created, held
and rechecked directory identities. Ordinary qualification and source-refresh
options are incompatible with this route. A pinned native parser control showed
that `run --out` suppresses witness logs; state measurement therefore uses native
ITF plus observed witness sample counts, while native test artifacts provide actual
passed-case counts. Actual bounded parser controls pass; full fresh calibration
still awaits the published compiler and final cleanup/model join.


### Current A–B qualification window — 2026-10-03

The earlier census defect is closed by the selected Linux x86_64 no-process-descendants
checker profile, not by interpreting an empty census as completion. Assembly-owned static
bootstrap, source and fixed filter bytes are hashed, held and sealed. The parent opens a
whole-process pidfd before ACK; the bootstrap confirms installed filtering before executing
the checker. Process creation and alternate ABIs are process-fatal; strictly qualified
thread creation is supported; clone3 and all three io_uring calls return ENOSYS. Final
readiness requires actual whole-group termination, drained output and owned scratch cleanup.
Unsupported custody capabilities refuse before checker execution. Existing legacy operation
requirements remain unchanged. This profile is not BAR or SC2 native qualification.

Current production evidence passes 89 Execution tests and four existing CLI runtime controls.
Eight real source reducer mutations and a real omitted-filter launcher mutation expose
causal failures; each restored source passes. The fast-orphan control holds the test-owned
child identity before allowing its parent to exit and closes that exact pidfd, without an
unrelated signal. Thread-outliving-leader controls close their output first and still require
the last-thread pidfd boundary. Seven resource/setup mutations refuse before checker effects.
A private installed CLI package supports actual preparation and consumption from an external
project referencing only its installed assembly. Current installed-resource Python, Node
worker and CLR Thread/Task checks perform meaningful import/discovery without executing
workload bodies. These are source-window controls, not package publication.

Canonical source `ab114cbfd7738dd1568ce2da3250b7b141b7d5759169bd9d9fb23d3165bdd354`
uses genuine published SDD 2.1.0. The full production contract retains 20 exports and
4,095 type/effect rows. The only changed prior export value records compiler provenance.
Three independent whole-module projections preserve canonical spans, imports and source/tool
identities; no native IR rows are merged or deleted. Thirteen bounded native calls cover
three completion witnesses, eight causal counterexamples and two replay traces. Both traces
match the entire core-plus-qualification source and production FsQuint replay, including
custody state and ordered effects. Nine provenance mutations refuse at the actual pre-use
guard. Sampling is not exhaustive proof or aggregate-profile authority.

The refreshed generated-entry audit retains 1,012 distinct lookup IDs and 4,095 distinct
IDs in each type/effect table. Same-payload nodes have distinct use-site/literal provenance;
no redundant semantic entry was demonstrated, so zero entries were removed. The audit does
not establish that all possible authored declarations are minimal.

Measurement controls preserve ordinary stale-baseline refusal, reject incompatible receipts,
source refresh and failure-exercise options, and refuse linked ancestors or replaced output
inodes. State/test parsing is checked against actual pinned-native artifacts and missing
witness logs refuse. One full fresh calibration pass began at 11:38 UTC, after the public
compiler, current helper, partitions, installed API and causal controls joined. Its fresh
external observations remain incomplete: GNU timeout returned 124 after seven roots,
19 simulations and 11 completed formal obligations. Two classified physical checker timeouts
retried under the existing policy. Their failed durations and memory were omitted by the
old terminal-only projection; the full actual partial log and 33 counterexample files are
retained as diagnostics. Original baseline hashes and timings remain untouched. This attempt
provides no full qualification or new calibration authority.

The prospective accounting-v2 source window retains the 300,000 ms physical checker timeout,
300,000 ms terminal-work gate and 6,144 MiB peak gate. A separate 2,100,000 ms observed gate
sums all physical attempts, including failed attempts and actual cleanup; failed-attempt RSS
participates in the peak. The observed gate is conservative: cleanup and rounding can cause
six full timeouts plus terminal work to exceed it and refuse. Seven logical calls remain
separate from their seven to thirteen physical attempts. Durable held-directory records mark
active interrupted attempts unknown and bind completed observations to one attempt, current
source, configuration, validator and toolchain. Ordinary receipts retain their existing
logical-count projection; measurement receipts carry the additional physical accounting.

Focused controls exercise actual owned timeout/retry and memory behavior through both
wrappers, exhausted and unclassified failures, exact and exceeded aggregate gates, omitted,
duplicate and incomplete observations, source/tool/validator/receipt substitution, and replaced
output inode refusal. Private collector fixtures emit no candidate and claim no native proof.
A prospective 5,400-second whole-run operating cap is finite and does not promise the worst
46,200-second retry envelope before other work. A new whole measurement attempt requires
root review of frozen source and producer identities and a separate launch decision.
A–B acceptance remains open until fresh complete qualification and root integration review.

Root review additionally binds every successful terminal to its declared positive or negative
semantic role, exit status and diagnostic outcome; crashes and timeouts cannot satisfy a
negative witness. The thirteen sampling calls and two interaction traces join the same
attempt, compiler, source, validator, native toolchain and physical journal. Interaction
artifacts and formal witness paths are hashed against their actual receipts. The prospective
compiler is the already qualified private SDK 10.0.401, FSI 15.2.401.0 and Core SHA7516;
nested FSI calls use the running host and FSI DLL directly, and the genuine pinned Fantomas
8 formatter DLL avoids SDK resolution through the repository's older global.json. Earlier
SDK10.0.400 controls remain earlier-environment evidence and are not relabelled as SDK401.

Correction to the preceding prospective SDK401 note: the earlier symlinked SDK entry
resolved its FSI entry and loaded Core from the global directory, with actual Core SHA2232698,
despite the intended private Core7516 file. Its prior packet, freeze and logs remain immutable;
the earlier claim of loaded Core7516 was false and provides no admission. The successor
materializes the complete SDK401 FSharp runtime closure as dereferenced single-linked files
in a new owned directory. A probe verifies the actual loaded Core path and SHA7516 before
focused controls. Measurement admission now checks the loaded Core bytes explicitly; nested
FSI invocations remain bound to the owned entry DLL and actual qualified host. No global SDK,
global.json, public SDD Core39b or Fable203 bytes are modified.

The separately admitted whole accounting-v2 attempt exited 1 after 26.059 seconds,
before formal qualification: direct owned FSI preserved a leading `--` script boundary
that the compiled-output generator rejected. Its six physical observations retain
22,248 ms of actual cost, including the failed generator's 2,318 ms and 224 MiB;
one early call's peak remains unknown. The failed attempt, frozen inputs and baseline
remain unchanged, and no candidate or automatic retry was produced.

The focused successor consumes exactly one leading script boundary in the generator.
Unknown options, embedded boundaries and a repeated boundary still refuse. Seven
actual owned-Core7516 focused calls used 33.725 seconds: generation with and without
the boundary preserved all eleven retained output files byte for byte, three argument
refusals passed, and nested qualification retained its seven-root, nineteen-formal
selection. These controls provide argument-boundary evidence, not whole qualification.
Any fresh whole attempt requires root review and a distinct launch admission.

The admitted argument-successor whole attempt later exited 1 after 2,463.651 seconds
at its final inventory gate. All nineteen formal obligations, thirteen sampling calls,
two interaction traces and seven roots completed, but four state roots using `--out-itf`
were classified as base calls. The unchanged plan expected 63 base calls and seven root
calls; the journal recorded 67 and three. Its 264 physical observations retain 2,739,594 ms
of summed work, separately from whole wall time, including one 300,048 ms classified
startup retry and its 632 MiB peak. The failed receipt remains unadmitted and unchanged.

The focused classifier successor recognizes either root output flag, one rooted JSON
path and its exact `root-artifacts` parent directory component for run/test calls.
Substring lookalikes, relative paths, duplicate output options, wrong extensions and
unknown output options do not enter the root bucket. Preflight sampling keeps its
own bucket. Actual assembled root arguments exercise both ordinary and measurement
dispatch, and diagnostic replay of the completed journal recovers the planned 63/7
classification without changing historical receipts or inventing whole-run success.
Budget, model, retry, accounting and expected invocation totals remain unchanged.

## Local A–B source qualification and admitted baseline

The final whole measurement exited 0 in 2,446.638 seconds with seven roots, nineteen
formal obligations, thirteen explicitly sampled preflight calls and two full-source
interaction traces. Its 263 logical calls have 264 physical observations and 2,723,158 ms
of summed work; overlapping work is separate from whole wall time. One classified
startup retry retains its failed 300,050 ms and 642 MiB peak. Formal maxima were
181,155 ms terminal work, 363,174 ms all-attempt work and 4,866 MiB peak, within the
unchanged 300,000/2,100,000 ms and 6,144 MiB gates. The early tool-call peak remains
unknown, as do historical missing argv and native usage counters.

Two preceding accounting-v2 whole attempts failed: the argument boundary at 26.059
seconds and the root classification inventory at 2,463.651 seconds. The older 1,600-second
timeout remains separate incomplete historical evidence. The final successful native
run's first collector exited 1 before writing because it expected a sampling label for
interaction rows. A separately reviewed one-expression collector correction then exited
0 and produced the exact candidate; it did not rerun models or change native observations.
The parent-reported collector duration is 0.002722642 seconds. Its invocation timestamp
and literal captured argv were not supplied and are not reconstructed from the prospective
command. Original failures, receipts and source snapshots remain private immutable history.

Root admitted baseline SHA-256
`2e7571915033d54067319abfd30bcb455d3a6972590872949846ba5f251c1f34`
from the successful receipt and complete physical inventory. The exact canonical baseline
validator accepts it, while the preserved historical source baseline still refuses as stale.
The ordinary protected qualification validator passes the current seven-root/nineteen-formal
catalogue, oracles, controls and baseline. Existing actual Execution, CLI, installed-resource,
custody, reducer/replay and causal-mutant evidence applies to unchanged source inputs.
Normal native source CI and merge remain required; this admitted calibration grants no
package publication, BAR/SC2 adapter acceptance or product launch authority.

All 57 declared counterexample files now match the completed native run exactly. The
nineteen manifests bind the current canonical and assembled source; the thirty-eight
trace/ITF files were already byte-identical. The previous actual source witnesses are
preserved privately. Historical receipt assertions check that immutable receipt separately,
without reversing current source identities to reconstruct historical evidence. All seven
current qualification architecture checks pass under the owned Core7516 compiler, and
routine eligibility and operation-boundary fixtures pass. These source checks preserve
current witness freshness and all admission budgets; required remote CI remains pending.
