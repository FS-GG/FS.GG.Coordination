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

The first installed behavior change is `.3`; `.1` and `.2` change source and repository-local qualification only.
Clean creation and retained upgrade are separate opt-in paths, and both must refuse before `OpenV2`. No provider
or lifecycle default changes here.

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
- [ ] **V2-CALL-01.3 — Publish and adopt the exact callable artifact.** First installed behavior change; separately
  authorize publication, immutable artifact verification, clean creation, retained upgrade, receiver selection,
  and pre-`OpenV2` refusal.
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

## §9.9 impact and observation gaps

This owner amendment adds GS2-09.9 as the callable prerequisite to representative rehearsal while leaving
GS2-09.1–.8 migration scope unchanged. The eventual Unified index row points to
`https://github.com/FS-GG/FS.GG.Coordination/blob/main/docs/roadmaps/callable-ordinary-v2-execution.md`.
Projection into `.github` is asynchronous after authoritative merge/readback.

Remaining gaps are deliberate: no package is published, no receiver selects this source, no sandbox or production
provider effect ran, no Q4/native external acceptance exists, no continuous webhook host exists, and private
roadmap telemetry is `not-configured` with native collaboration usage interception unsupported. Those gaps block
`.3`/`.4` activation, not this repository-local source and recovery window.
