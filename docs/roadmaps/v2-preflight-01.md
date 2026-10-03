# V2-PREFLIGHT-01 — Typed prerequisite admission

**Accepted A–B implementation window; source qualification in progress. No publication or product adoption claimed.** Owning path:
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
| Coordination `Protocol.md`, `Protocol.bindings.json`, generated compiled outputs, canonical qualification/replay | Published SDD compiler boundary and canonical source/contract drift validation already exist. Current pins: `FS.GG.SDD.Artifacts [1.5.0]`, `FsQuint 0.1.0` | Add consumer-owned semantics through the existing compiler route. The architecture guard rejects copied compiler/profile/replay sources and local `.qnt` files; do not evade it with a second extractor |
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

### 2026-10-03 source checkpoint

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
