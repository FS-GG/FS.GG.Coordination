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
- [x] **V2-CALL-01.3c — Adopt the exact callable artifact.** First installed behavior change. Separately authorize
  clean creation and retained upgrade as opt-in receiver paths, prove idempotency, explicit conflict, and no
  partial writes, and preserve coexistence with the legacy bridge. Both paths refuse effects before `OpenV2` and
  require class/receiver admission; no provider or lifecycle default changes implicitly. Completed by
  [`.github` PR #3539](https://github.com/FS-GG/.github/pull/3539), exact tested head
  `08b8daa5c98c590c12663a909c9e0d354bddae3a`, merge
  `587f46e15e1404dbe0dc1e9e6b47cf2861d7b502`: the canonical opt-in manifest pins
  `FS.GG.Coordination.Cli` 0.1.0 alongside `FS.GG.Coord.Cli` 0.90.0, and the receiver proof covers clean and
  retained installation, idempotency, conflicting-pin refusal before write, peer/lifecycle/operation
  preservation, scoped uninstall, isolated nuget.org resolution, installed invocation and pre-`OpenV2`
  refusal. All 43 current-head checks passed with two expected skips; production effects remain disabled.
- [x] **V2-CALL-01.4 — Qualify installed isolated-provider and native execution.** The three windows below
  completed in order. The live operation used released CLI 0.1.1 after the observed 0.1.0 App-token
  observation defect and explicit receiver adoption; the result is scoped to one synthetic operation.
  - [x] **V2-CALL-01.4a — Close recovery coverage and build the installed harness (routine).** Extend the Q3/Q6
    validator so its registered evidence includes production-runtime tests, not only the current domain tests.
    Cover unknown, proven-absent and applied effects; lost dispatch responses and journal acknowledgements;
    stale retry observations; durable intent across a fresh process; and native-completion reconciliation. Build
    a clean public-feed installation harness around the exact 0.1.0 CLI and the production GitHub-backed journal,
    using controlled HTTP before any live provider. Retain exact source, package, served, installed-command,
    request and journal hashes plus authoritative readback. A nonzero CLI exit or `AdvancePending` is an
    incomplete/refused result, never acceptance. This source window performs no external mutation. Completed by
    extending both registered commands over the production runtime suite and retaining the exact recovery map,
    public-feed installed-harness result, and still-unauthorized `.4b` operation proposal under
    `evidence/github-substrate-v2/gs2-09-9/` and `eng/`. The installed 0.1.0 bytes passed; no replacement version
    or product repair was required.
  - [x] **V2-CALL-01.4b — Admit one compatible isolated operation (separate protected authority).** GS2-09.9's
    current permission ceiling excludes provider mutation and external acceptance, so a later operation packet
    must bind the exact target, actor/credential authority, package and receiver revisions, source/base/check,
    epoch/policy/journal identities, allowed mutation, recovery and cleanup. Prefer a fresh intentionally public,
    synthetic-only disposable target, or use an entitled private target. The registered private sandbox's
    `required_status_checks` endpoint currently returns `403` because the required entitlement is absent; do not
    expose that repository, weaken protection, change billing, or reinterpret the refusal as absence.
    - [x] **Source preparation.** The digest-bound v2 proposal and guarded operator implement separate creation
      and identity-bound setup/execution/readback/cleanup phases. Checked-in state is
      `prepared-not-authorized`; every effect requires a short-lived protected grant, exact workflow/reviewer,
      GitHub App installation and fresh capability readback. Unknown create/setup/delete responses reconcile by
      exact readback, cleanup intent is durable before delete, and pending/nonzero CLI outcomes never accept.
      The preflight records zero compatible targets among 16 accessible repositories, the proposed target `404`,
      private required-status-checks `403`, and organization-installation plus actor-membership `403` as refusal
      facts only. No repository, settings, visibility, billing, credential, workflow, epoch, journal, provider or
      cleanup effect occurred; actual protected admission remains this milestone's next boundary.
    - [x] **Live-readiness source repair.** The v3 contract limits creation to its canonical method/path/body and
      persists a marker-bound attempt before POST. Authority, reviewer, App-installation, creation, setup,
      restricted execution and cleanup credentials are separate, expiring roles; admission binds the exact
      environment, run attempt, grant artifact and trusted workflow bytes. Operation progress retains immutable
      PR identity and exact installed plan bytes before dispatch, reconciles a lost CLI response from merged-PR
      and journal authority, protects the selected journal shard, and makes post-delete restart reachable only
      from retained evidence and delete intent. Actual UTC is rechecked at effect boundaries. The checked-in
      proposal remains `prepared-not-authorized`; this repair performs no live provider effect and does not close
      `.4b` or `.4c`. The historical `.github` authorization source remains undispatched and must be repinned in
      its separate executor window before it can issue a compatible v3 grant.
    - [x] **Artifact-envelope source repair.** The immutable v4 grant payload contains no server-assigned
      artifact coordinates. The executor supplies a separate exact artifact envelope; the operator independently
      reads the API identity, protected run and attempt, expiry and archive digest, downloads the archive, and
      accepts only one canonical grant payload whose digest matches the envelope. Wrong id/name/digest/run,
      expired, malformed, multi-file, swapped and replayed artifacts refuse before mutation. Public
      `validate-admission` and `execute` require the envelope explicitly. The `.github` authorization/executor
      sources must be repinned to this v4 contract and input shape before dispatch; `.4b` remains incomplete.
    - [x] **Live artifact-download source repair.** Protected executor runs `35407504607` and `35407577880`
      refused before provider dispatch: the first on a non-canonical payload digest supplied to the envelope and
      the second when Python forwarded the App authorization header across GitHub's signed cross-origin artifact
      redirect and received `401`. No checkpoint was emitted and the target remained absent. The operator now
      strips authorization and proxy-authorization on cross-origin redirects while refusing HTTPS downgrade;
      same-origin authentication, exact archive digest and canonical-payload validation remain intact. Fresh
      `.github` source must repin this repair and obtain new plans and grants; `.4b` and `.4c` remain incomplete.
  - [x] **V2-CALL-01.4c — Qualify installed native execution and acceptance.** Run the installed command under
    the admitted operation, obtain independent PR and sharded-journal readback, prove fresh-process no-op replay
    and interruption recovery without duplicate effect, clean up the disposable subject, and bind exact native
    acceptance to the observed identities. This evidence does not imply `OpenV2`, Q4, migration acceptance or a
    production default. The distinct `.4c` acceptance is bound by
    [`native-acceptance.json`](../../evidence/github-substrate-v2/gs2-09-9/native-acceptance.json) and its
    offline validator, which compare the interrupted `cli-intent` checkpoint with the final sealed receipt,
    exact installed plan, PR identity, settled sharded journal, fresh-process no-op replay and cleanup readback.
- [x] **V2-CALL-01.5 — Hand off exact callable readiness to GS2-09 migration.** Add durable evidence under
  `evidence/github-substrate-v2/gs2-09-9/` binding source/release and package/feed hashes, the adopting receiver
  revision, installed commands and schemas, interpreter identity and permission ceiling, preconditions,
  observation/decision/journal recovery behavior, native acceptance, and known limitations. Supply those exact
  identities to GS2-09 discovery/migration work while preserving separate manifest, transform, archive, rollback,
  omission and representative-rehearsal acceptance.
  - [x] **V2-CALL-01.5a — Seal the callable-readiness evidence packet.** Record and validate the exact protected
    source/release revisions, package and feed hashes, adopting receiver revision, installed command and schema
    identities, interpreter and permission ceiling, preconditions, observation/decision/journal recovery,
    `.4b`/`.4c` native acceptance, and known limitations under `evidence/github-substrate-v2/gs2-09-9/`.
    This protected Coordination delivery adds the independently checkable
    [`callable-readiness.json`](../../evidence/github-substrate-v2/gs2-09-9/callable-readiness.json) packet and
    public-identity validator without broadening the installed permission ceiling or relabeling `.4b`/`.4c`.
  - [x] **V2-CALL-01.5b — Hand the packet to GS2-09 discovery.** In a separate protected Coordination delivery,
    record the exact `.5a` packet identity, the callable runtime interface and recovery preconditions that
    discovery can consume, and the independently pending discovery, manifest, transform, archive, rollback,
    omission and representative-rehearsal gates. The independently checkable
    [`callable-discovery-handoff.json`](../../evidence/github-substrate-v2/gs2-09-9/callable-discovery-handoff.json)
    binds that receipt without broadening the installed permission ceiling. Completion proves receipt of the
    callable handoff only; it does not accept migration, `OpenV2`, Q4, or a production default.

Workspace and telemetry disposition: `.4a` added qualification source and an installed harness but changed no
provider or lifecycle default. Its original 0.1.0 freeze ended when the observed App-token defect required
version 0.1.1, protected publication and explicit receiver adoption; no bytes were substituted under 0.1.0.
Private roadmap telemetry for `.4a` is associated through the `.github` workspace; native collaboration
interception remains unsupported and usage remains unknown, not zero, and neither becomes acceptance evidence.

During `.4b` identity-bound execution, [run `35418940289`](https://github.com/FS-GG/.github/actions/runs/35418940289)
retained a `setup-intent` checkpoint and the installed 0.1.0 command refused planning with
`UnauthorizedObservation`. GitHub returned `permissions.push=false` for both scoped App tokens while the target
repository and PR remained readable. Producer version 0.1.1 repaired this App-token observation. At that
checkpoint, protected publication, explicit receiver repin, installed execution, native acceptance and cleanup
were pending; the later protected run and distinct `.4c` evidence below close those gates for one synthetic target.
The 0.1.1 release preflight at protected run `35433965498` retained candidate artifact `10581917023`.
Two independent hosted preparations and a local preparation with the same canonical source root and GitHub
`origin` produced byte-identical package SHA-256
`3072f67fa7ad19cc93240eff7b1b3003c12d07882ea9aa85710167273852bf7d` and identical manifests.
The earlier local proposal used a filesystem `origin`, omitted GitHub SourceLink metadata from six assemblies,
and therefore had different bytes; it is superseded, not published. Protected preparation and publication
now assert the exact remote before packing. The retained manifest still denies publication and tag authority;
the exact protected publisher remains a separate gate.

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

Completion evidence for `.4a`: both Q3/Q6 commands now execute the five domain and eleven production-runtime
controls. The clean-process harness installs the exact public `FS.GG.Coordination.Cli` 0.1.0 package, whose
nuget.org archive is `e7f440a2a1f94d51dbcdd7146494c97e6386f9dcc8034a028e3e851d364390e3`, and binds it to
candidate `ce318148d288051eaeb55ebb0e81bb0172d3194523c95ea9caeed5b5091a15cf`, source/tree
`1bd60a3e…`/`79de47c7…`, receiver merge `587f46e1…`, and installed command
`21d36ec3cdbb153453833ed57f2320f9f256a26f35aea52bb75c52a68efada04`. Five recovery scenarios cover three
lost journal acknowledgements, a lost dispatch response, native-completion reconciliation and fresh-process
no-op replay; nine independent negatives cover pre-open refusal, stale policy/source/base/check/epoch, wrong
subject, incomplete pagination and observer outage. Exact request sequence
`c30cd043a8a3a30bc9daffbb59bb0f6d9bfef997695da3d6d823d9a94597b317` and journal set
`520b08adbea6ee20b21b396f3200eeff7e970a6beaf81947942fd9f9af3cf968` are retained. In that `.4a` harness
every provider mutation was loopback-only; `.4b`, `.4c`, live acceptance, Q4, `OpenV2` and migration were pending.

Completion evidence for `.4b` source preparation and its live-readiness/artifact-envelope repairs: proposal
`6af4760f429379d6e4a20f3424ee70b33e21cce220930ac53c177755049bd78a` binds contract
`3ebf436e7e2efdf221b9b08f96b6d5216bbeb22053af26bd7cdd2d0d11ef561d`, operation source
`b5a20b2c511bf37833dac99c35eb1fa420f410f5b324fd26883e5af928cb145c`, package/receiver identities and
preflight `46c9af2263a8f686c59e90aacb79cc5466c2e1d3c6422221f86b1373fe5813eb`. The registered Q3 command runs
twenty-three offline tests plus independently compiled architecture controls covering canonical creation scope,
protected environment/run/artifact authority, separated credential roles, target, capability, phase, tamper,
expiry, unknown-response, durable plan/PR recovery, journal identity/protection, replay and cleanup refusals.
The prepared source performs zero live effects and grants no `.4b` admission or `.4c` acceptance.
The follow-up live artifact-download repair advances the current proposal, contract and operation-source digests
to `4b2bc6086bb57af82f9f1812a0e9b22933c406d0ac6276bc926f03dd4cd84f89`,
`828855bd5ba0455a1c5bb3d2e1fdad6ccef710fbf2c6205812a1b383a6e07d5c`, and
`392617a63fb622eb8d243f7973f1c74d63d13ac256aac2c86532cdeb51c1d5b2`; its focused suite runs twenty-four
offline tests including the redirect credential-boundary control. The two refused live runs performed no target,
journal, epoch, workflow, cleanup or external-acceptance effect.
The subsequent phase-two setup reached the installed runtime but exposed the installed CLI's single-credential
observation boundary: the execution role could prove delivery authorization but received `403` on protected-branch
readback, while the setup role completed provider reads but was refused as an unauthorized delivery observation.
The repaired contract grants the execution role only `administration: read`, keeps setup and cleanup mutation
authority separate, and uses the execution credential consistently for installed planning and both advance calls.
It continues to admit/migrate only the exact live creation receipt and `setup-intent` checkpoint. Current
proposal, contract, operation-source and coverage digests are
`8eb05b36647a5bafbf1053e01105c34bc86feb79e3a41e00e976041fbcbe296b`,
`ccd57e74293b2fb1443614fea6add54525f9f11c8d1126908180479ab5bc18a6`,
`2dca8907815c720086c761eff9283aaf682d5786b1c97668351679d452f5591d`, and
`ceae0963352ae3527cdfb23c1e5a80325ba938d5efc64f408e8a47c9cc732593`; the focused suite now runs twenty-five
offline tests. At that source checkpoint the target remained synthetic and pending protected recovery; `.4c`,
Q4 and migration acceptance remained outside that evidence.

Completion evidence for `.4b` and the separately assigned `.4c`: protected `.github` executor
[run `35631364282`](https://github.com/FS-GG/.github/actions/runs/35631364282), attempt 1 at receiver
`803556ff1e877d40af2dddbaff7e80f3241d8286`, completed successfully using installed CLI 0.1.1.
The retained prior `cli-intent` checkpoint is run `35619835923` artifact `10647264981`, archive SHA-256
`f98b32505b081a4c051d253f36e3a7d51e2fd1271a26596c18a7902b345a4318`. Final checkpoint
artifact `10654184892` and receipt artifact `10653844968` have archive SHA-256
`0f42981167fcfaa775c6257dfe421181b367f35f942c3d1a7f6621cfbde87529` and
`acd68a59dc8e7c7d4451d749184d80358b15f4f59ede47546f9c43b0d90150db`; their payloads are identical,
with canonical receipt seal `542dd22b24fd86b2216bcde443ba318826a9a37a122c82ea5890b11bf9081a41`.
The final sealed receipt binds original base `dffd58342bcbe4e2dcbd55f30afe624b316a5f80`, native plan
`617a0a5f4cf90145be0012a786d5ec79727da6fe56d982c601096910f1162f4a`, PR #1 merge
`00869036c9bf96f5cb4c783cfa91ac15504b673b`, settled sharded journal generation 3, operator PR and
journal API readback independent of the installed CLI, and fresh-process `AdvanceAlreadySettled` after the
retained interruption. [Main's immutable pre-cleanup readback](https://github.com/FS-GG/.github/blob/e71fb799b4780e53fbedffaa6c5815a9d7fc92d0/MAILBOX.md#L5511-L5513)
separately reports the merged PR and bound settled journal before the disposable target was deleted. The
operator source skipped a second installed advance after readback found the PR merged; the replay reported no
further effect. The offline validator checks retained and final sealed artifact identities; it does not refetch
the deleted target's PR or journal.
Cleanup settled for synthetic repository ID `1376575900` with HTTP 404 and a separate live 404 readback.
This single-operation evidence does not accept `OpenV2`, Q4, GS2-09 migration or a production default.

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

Remaining gaps are deliberate: `FS.GG.Coordination.Cli` 0.1.1 is published and the canonical `.github` receiver
selects it for the admitted singleton. One synthetic isolated external provider effect and native acceptance
completed under `.4b` and `.4c`; no production default, Q4 acceptance or continuous webhook host exists.
Native collaboration usage interception remains unsupported even though private roadmap telemetry is
workspace-associated. GS2-09 discovery, migration
manifests/transforms/archive/rollback/rehearsal remain a separate pending feature, and OpenV2 and Q4 remain
unclaimed.
