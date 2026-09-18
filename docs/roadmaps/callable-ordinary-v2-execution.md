# Callable ordinary v2 execution

Feature identity: `V2-CALL-01`  
Owner: `FS.GG.Coordination`  
Unified roadmap: [§9.8 — Callable ordinary v2 execution](https://github.com/FS-GG/.github/blob/7d2db1c32c47b6f9c445a77f9a91510c61a17281/docs/2026-09-07-154210-fs-gg-unified-development-roadmap.md#98-next-feature-preparation-status)  
Stage: V2, before candidate qualification

## Outcome and boundary

An installed and explicitly selected Coordination command can join fresh native observations, a canonical
decision, a durable sealed plan, guarded GitHub effects, authoritative readback, and recovery for an ordinary
code PR without Host, actors, a model runtime, or PostgreSQL. The ordinary path has one owner and one PR,
selected checks, and same-PR repair/native delivery. Its authority is the internal sharded journal; it does not
need public phase comments, a second acceptance actor, a receipt-only PR, or synchronous board/usage updates.

This feature does not implement GS2-09 discovery, migration manifests, transforms, archives, rollback, or
representative rehearsal. It hands that work a callable runtime and recovery interface. Production writing stays
closed until `OpenV2` and later class/receiver admission. The v1 fence remains intact and Q4 remains unclaimed.
Local files are immutable qualification requests or diagnostics, never authority. Scheduled complete audit
remains authoritative; continuous webhook hosting is not a prerequisite. Native observations retain unknown
telemetry and usage rather than replacing them with a second ledger.

The first installed behavior change is `.3c`; `.1`, `.2`, and `.3a` change source and repository-local
qualification/preparation only, while `.3b` is separately authorized publication. Clean creation and retained
upgrade are separate opt-in paths, and both must refuse before `OpenV2`. No provider or lifecycle default changes
here.

## Selected architecture

The CLI is a thin domain host with `delivery inspect|plan|advance` over one shared `OrdinaryDelivery`
reconciliation service. `inspect` validates a complete read. `plan` emits canonical, deterministic sealed bytes
bound to repository and PR identities, base/source, policy revision, selected check identities and conclusions,
epoch, journal generation, and journal head. `advance` consumes those exact bytes, persists intent before an
effect, and re-observes source, policy, checks, epoch, journal and native effect state at effect boundaries.

The sole registered interpreter is `ordinary-source-delivery`. It cannot invoke release, administration,
credential, publication, migration, or cutover operations. Selected native checks are not a legacy review pass,
post-merge protected-main result, or legacy Done receipt. `ReviewDeliveryAdapter` stays byte-compatible and its
strict semantics are not reinterpreted.

## Governing amendment and gates

`GS2-09.9 — Qualify callable ordinary v2 execution` is collision-free in the reviewed roadmap, whose GS2-09
catalog ends at `.8`. The owner amendment is
[`GS2-09.9.json`](../../evidence/github-substrate-v2/roadmap-amendments/GS2-09.9.json), bound to `.github`
revision `7d2db1c32c47b6f9c445a77f9a91510c61a17281` and roadmap SHA-256
`9c49a0efd1440d8a71130758be39394ae4cdd67f3d10b9cb6cb71998154c1a17`. It is an additive prerequisite
immediately before GS2-09.7 representative rehearsal, not work waiting after GS2-09.8. The repository catalog
registers exact Q3 contract and Q6 recovery command identities. Synthetic dummy unit IDs and synthetic receipts
are never acceptance evidence.

## Prior sources and reusable evidence

- `ShardedJournalAdapter` and the accepted [GS2-04.9 receipt](../../evidence/github-substrate-v2/accepted/GS2-04.9.json)
  (`11defafd…`) supply journal/CAS behavior and sandbox-closure history.
- `ClaimTouchSetAdapter`, `ReviewDeliveryAdapter`, and the accepted
  [GS2-05.6 receipt](../../evidence/github-substrate-v2/accepted/GS2-05.6.json) (`24de3578…`) remain reusable,
  without manufacturing review or Done authority for the ordinary profile.
- The pure GS2-07.2 and GS2-07.3 reconciliation contracts and receipts (`6ae56a7c…`, `4c6a18a…`) supply
  observation/audit correspondence, not proof of this production composition.
- GS2-06.7 selection (`c6d1662e…`), GS2-08 fencing, and accepted V1 closure
  [PR #419](https://github.com/FS-GG/FS.GG.Coordination/pull/419) are preserved. Suitable O0–O3 identity,
  readback, and recovery behavior is reused only at its proven boundary.
- Host `GitHubRouteClient.fs` retains legacy `fsgg:claim` markers and is explicitly not the ordinary-v2 authority.

## Executable windows

- [x] **V2-CALL-01.1 — Bind the callable contract and deliver the read-only command boundary.** The owner
  amendment, catalog identities, permission ceiling, and exact reviewed roadmap binding are registered.
  Installed-source `delivery inspect|plan` validates strict identities through the shared boundary. Complete
  observation produces stable plan bytes; missing pages, unauthorized/unsupported input, cross-subject facts,
  stale policy, changed source, and altered plans refuse explicitly. Controlled CLI composition and domain tests
  prove inspect/plan and pre-`OpenV2` advancement perform zero external effect. Historical acceptance receipts
  are untouched.
- [x] **V2-CALL-01.2 — Compose guarded advancement and durable recovery.** Intent and exact plan digest persist
  before dispatch; every effect boundary re-observes current decision facts. CAS generation conflicts refuse,
  lost CAS is reconciled, and leases never transfer authority. Interruption before dispatch, after dispatch before
  response, and after effect before receipt converges in a fresh invocation without duplicate dispatch. Unknown
  effect remains pending, settled replay is a no-op, changed source/base/policy needs a new decision, missing
  observation is not absence, observer outage retains delivery state, and failed required checks block merge.
  Repository-local controlled-provider qualification passes without Host, PostgreSQL, or a model process.
- [x] **V2-CALL-01.3a — Prepare the callable producer and exact release operation.** `.1`/`.2` remain valid
  controlled-provider source acceptance, but their file-only/in-memory runtime is simulated and does not prove
  real GitHub or journal composition. Compose the production GitHub REST observation/readback path, protected
  sharded Git journal, and native merge interpreter behind the same command. Bind epoch generation and commit,
  preserve pending/unknown outcomes and persist-before-effect/CAS recovery across process restart, and keep the
  interpreter limited to ordinary source delivery. Produce the standalone `fsgg-coordination` dotnet tool with
  its complete private dependency closure, a reproducible/custodied `FS.GG.Coordination.Cli` `0.1.0` candidate,
  clean local install proof, corruption/substitution/refusal controls, and a preparation-only release route. The
  proposed package/tag identity remains unreserved while GitHub Packages ownership inventory is unavailable
  (`403`); no package, tag, receiver, credential, or provider effect is created by this window.
- [x] **V2-CALL-01.3b — Publish the protected dual-feed artifact.** Requires concrete separate publication
  authority and collision-free live ownership readback. Publish first to GitHub Packages, verify provenance and
  served bytes, then publish the byte-identical candidate to nuget.org. Recover partial publication by reading
  the first feed before resuming, prove both served digests, and create `v0.1.0` only after served readback. This
  does not authorize receiver adoption. The separately authorized operation is bound by
  `eng/callable-cli-release-operation.json`; its workflow must reproduce the historical prepared archive at its
  original canonical build root, attest and retain it before effects, prove Trusted Publishing authorization
  before the first feed write, and refuse unless repository Actions policy admits the exact pinned login action.
  Completed through protected runs
  [`35320209230`](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/35320209230) and
  [`35320873209`](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/35320873209): the first published the
  retained `ce318148d288051eaeb55ebb0e81bb0172d3194523c95ea9caeed5b5091a15cf` archive to GitHub Packages and
  then nuget.org before stopping on public indexing, and the recovery observed both existing feeds, performed no
  duplicate push, proved normalized payload identity, and passed anonymous clean tool install/invocation. The
  served archives are `ce318148d288051eaeb55ebb0e81bb0172d3194523c95ea9caeed5b5091a15cf` (GitHub Packages)
  and `e7f440a2a1f94d51dbcdd7146494c97e6386f9dcc8034a028e3e851d364390e3` (repository-signed nuget.org).
  Immutable tag and [release `v0.1.0`](https://github.com/FS-GG/FS.GG.Coordination/releases/tag/v0.1.0) bind
  exact source merge `1bd60a3e2dddc37827a7562b133305e1282e2c20`; release assets were independently downloaded and
  matched the retained package, preparation manifest, and dual-feed readback receipt.
- [ ] **V2-CALL-01.3c — Adopt the exact callable artifact.** First installed behavior change. Separately authorize
  clean creation and retained upgrade as opt-in receiver paths, prove idempotency, explicit conflict, and no
  partial writes, and preserve coexistence with the legacy bridge. Both paths refuse effects before `OpenV2` and
  require class/receiver admission; no provider or lifecycle default changes implicitly.
- [ ] **V2-CALL-01.4 — Qualify installed isolated provider journey and native acceptance.** Exercise the installed
  artifact against an authorized isolated provider and obtain native external acceptance; do not reuse local
  controlled responses as Q4.
- [ ] **V2-CALL-01.5 — Hand off callable readiness to the migration feature.** Supply GS2-09 discovery/migration
  work with exact runtime/recovery identities while preserving separate manifest, transform, archive, rollback,
  and rehearsal acceptance.

Completion evidence for `.1` and `.2`: the exact candidate runs a warning-free Release solution build; all 417
unit tests and all 651 architecture tests pass; the focused ordinary suite contributes five unit and two
architecture controls; both registered Q3/Q6 commands report passed controlled-provider qualification; evidence
storage self-test reports 57 negative controls; routine eligibility, exact-head, and protected-operation fixtures
pass. `evidence/github-substrate-v2/accepted/` is unchanged. The PR marker and native check suite bind these
results to the final candidate head; native merged state and merge commit are read back before this window is
reported complete.

Completion evidence for `.3a`: the warning-free Release solution build, all 424 unit tests, and all 654
architecture tests pass. The focused provider/domain suite contributes 12 controls and the producer architecture
suite contributes three controls. Q3/Q6 qualification, the unchanged Protocol reproducibility route, immutable
workflow inventory, and routine fixtures pass. The preparation command canonicalizes two independent package
outputs to identical bytes, verifies the exact source-bound manifest, installs `fsgg-coordination` from a
local-only feed, runs it without checkout dependencies, and refuses package corruption and source substitution.
The final PR marker and native check suite bind those results to the reviewed head; merge and authoritative
readback are required before `.3a` is reported outside this plan as delivered.

## Dependencies, invalidators, and completion examples

Dependencies are the exact GS2 receipts named above, the unchanged roadmap bytes, canonical JSON behavior, the
v1 epoch fence, and selected-check identity semantics. A roadmap digest/revision change, new GS2-09 identity,
changed journal encoding, receiver/class admission change, source/base/policy/check drift, incomplete pagination,
unknown provider effect, or any attempt to widen the sole interpreter invalidates the applicable decision.

Examples of completion are: two differently ordered but identical check observations emit byte-identical plans;
one altered plan byte refuses; `OperatingV1` refuses with zero journal/effect calls; two competing generations
cannot both reserve; a response lost after merge is reconciled from native effect readback; a crash after native
effect but before settlement produces exactly one dispatch; replay of the settled plan does nothing; an observer
outage returns pending/refusal and never erases an already observed delivery.

For `.3a`, completion additionally means the production REST/journal composition passes loopback provider tests
for three crash cuts, duplicate prevention, stale generation/check/source/policy/epoch, incomplete pagination,
wrong subject, unknown merge result, observer outage, and restart persistence. Two independent preparations must
produce byte-identical canonical package bytes, an isolated local-only feed must install and run the tool without
checkout dependencies, manifest verification must reject corruption or substituted identity, and the release
workflow must be mechanically incapable of publishing or tagging. Live provider delivery is not `.3a` evidence.

## §9.9 impact and observation gaps

This owner amendment adds GS2-09.9 as the callable prerequisite to representative rehearsal while leaving
GS2-09.1–.8 migration scope unchanged. The eventual Unified index row points to
`https://github.com/FS-GG/FS.GG.Coordination/blob/main/docs/roadmaps/callable-ordinary-v2-execution.md`.
Projection into `.github` is asynchronous after authoritative merge/readback.

Remaining gaps are deliberate: `FS.GG.Coordination.Cli` `0.1.0` is published and read back from both designated
feeds, but no receiver selects it, no sandbox or production provider effect ran, no Q4/native external acceptance
exists, no continuous webhook host exists, and private roadmap telemetry is `not-configured` with native
collaboration usage interception unsupported. Those gaps block `.3c`/`.4` activation, not the completed producer
preparation and publication windows. GS2-09 discovery, migration
manifests/transforms/archive/rollback/rehearsal remain a separate pending feature, and OpenV2 and Q4 remain
unclaimed.
