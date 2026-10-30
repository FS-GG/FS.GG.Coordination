# Forward Execution pair publication route

Status: **complete dormant source route with inert controls; live effects
disabled**. The committed admission (`eng/execution-pair-forward-admission.json`)
is `disabled`: no version is selected, every live-effect prerequisite is absent and
every credential-bearing job fails before a credential exists. The original
`0.3.0` operation, its admission, publisher, attempts, ordinals 1–5 and
historical Unknowns are neither read nor changed by this route; a repository
control pins the original admission and publisher bytes.

This route publishes a **newly selected** coherent
`FS.GG.Execution.Contracts`/`FS.GG.Execution.Processes` pair, never `0.3.0`.
`0.3.1` is only the committed proposal. The final version is selected after
authoritative occupancy and must be strictly newer than every observed feed
version and `execution/v*` tag, including `0.3.0`.

## One source cut, one rule

`eng/callable-cli-release.fsx --profile execution-pair-forward --version V`
prepares and verifies the pair with the existing member inspection, deterministic
double-pack and coherent Contracts dependency checks, overriding only
`PackageVersion`. The original `execution-pair` profile still accepts exactly
`0.3.0`, and the forward profile refuses it; the two version rules are disjoint
(`forwardVersionPattern` in the script equals `FORWARD_VERSION_PATTERN` in
`eng/execution_pair_forward.py`, checked by a control, and the shared harness
reuses the module rule).

The existing prepare workflow selects the profile from `version`. A forward
candidate, its installed load and its four receiver cases run at **one exact
source**: the executor must equal candidate source A. A publisher B (including a
later resume publisher) must descend from A with A's tree; within the exact
*publisher closure* (the forward library and CLI, the installed-harness and
readback helpers, the tag ruleset, `global.json` and the publish workflow) it may
differ from A only by the admission file. The same rule covers anything Python or
dotnet could resolve by name (top-level and `eng/` Python files, `.pth`, NuGet and
MSBuild configuration). The publisher also runs hermetically: every workflow
invocation is `python3 -I -B` and dotnet runs from a clean directory outside the
checkout. Unrelated repository history therefore cannot strand a partly published
pair, and a control scans the publisher sources and workflow to prove the closure
is exactly what they read.

## Forward receivers

The four receiver cases keep their repositories, fixture populations and both
rollback receivers. The two fixed original S1 receivers (`FS.GG.SDD` `d0bb4059`,
`FsQuint` `708587f2`) cannot receive a forward pair: they pin the pair at exactly
`[0.3.0]` and restore in locked mode, so a forward candidate feed cannot satisfy them.
A forward selection therefore names its own S1 receiver commits in
`selection.receiverSources`:

```json
{"sdd-s1": {"commit": "<40 hex>"},
 "fsquint-s1": {"commit": "<40 hex>", "coreLocks": {"<the seven FsQuint lock paths>": "<sha256>"}}}
```

The value must be an object (null is refused), and neither commit may be any of the four
fixed original receivers. `coreLocks` replaces the seven pinned
FsQuint SDK-Core source-lock digests (the same seven paths). The selection is part
of the content identity, and the maintained four-receiver join checks every observed
receiver commit and lock digest against it. The original pair still joins only the
fixed original table.

The packed bytes embed `RepositoryCommit` = source A, so the receiver lock content
hashes exist only after A is fixed. Order: prepare the candidate at A; create each
receiver commit by changing only the pair version pin and its regenerated locks
against those exact candidate packages; run `execution-pair-consumers` at A with
those commits and their trees as `consumer_sources` (`sdd_source`, `sdd_tree`,
`fsquint_source`, `fsquint_tree`); record both in the selection. `main` must stay
at A from candidate preparation through the receiver run.

## Admission states

| State | Meaning |
|---|---|
| `disabled` | no selection (committed state) |
| `selected` | complete reviewable selection; only the read-only `preflight` may run |
| `enabled` | additionally every `liveEffects` prerequisite (environment, ledger App, NuGet Trusted Publishing, occupancy fence, old-writer fence, operation admission) and the shared `boundIntentFence` carry retained evidence |

`enabled` is refused unless all of that is present. Root establishes those facts;
this route never creates them.

## Thirteen ordered durable effects

The protected ordered-effect journal engine is the unchanged shared helper pinned
by commit and per-file SHA-256 in the admission (`sharedHelper`); there is no
second journal algorithm. Order, one effect at a time:

1. `github:` Contracts, 2. `github:` Processes (GitHub Packages, byte-exact readback)
3. `nuget:` Contracts, 4. `nuget:` Processes (identical retained inputs; nuget.org may add only its repository signature)
5. `tag` (lightweight, candidate source), 6. `draft` release
7–12. assets: both packages, manifest, consumer qualification, readback, publication journal
13. `promote` (non-latest)

Each effect is observed before and after its single send; a verified effect is
re-observed on every transition. Generated assets are pure functions of
authenticated retained inputs, so a recovery invocation reproduces them byte for
byte. They are not part of the journal content identity.

## Credential-isolated jobs

| Job | May | May not |
|---|---|---|
| `preflight` | read candidate, receivers, occupancy, policy (administration **write** App token: GitHub returns ruleset `bypass_actors` only to write-access callers; a read token yields an incomplete document that is refused), journal destination | any write |
| `packages` | ledger App token, GitHub Packages write, NuGet Trusted Publishing; effects 1–4 | tag, release, contents write |
| `public-load` | anonymous public package restore/build/load, no secrets | any credential or write |
| `finalize` | ledger App token, contents write, policy read (same administration-write App token as preflight); effects 5–13 | package or NuGet credential |

`finalize` starts only from a journal whose package window is verified and only
with the successful `public-load` job and receipt artifact of the same run. A
stage originates only effects whose credential it holds; the fresh per-effect
admission refuses the rest before any intent exists, and `ready()` proves every
send is possible (credentials, retained bytes, staged assets) before the first
intent, because an intent that cannot be sent is a permanent Unknown.

## Continuing a stopped publication

`mode=publish` starts a fresh journal. After any stop, `mode=resume` runs a
`--resume` preflight (candidate, receivers and tag policy are re-authenticated; unoccupied
cells are *not* required because the engine re-observes every journal effect) and
publishes a `plan` from the shared journal alone: `packages` (package window
incomplete), `finalize` (window verified; `public-load` re-runs in the new run) or
`complete`. Jobs run only when the plan names them, so no effect whose intent is
recorded is ever resent; a stale `packages` job against an advanced journal is
refused by the engine.

## Guards

* **Preflight**: candidate archive authenticated through the maintained
  extractor; four distinct receiver cases joined to the exact candidate
  (`pair_consumers`); strict occupancy (four cells, version indexes, tag list, tag,
  release; a full page or unreadable response never means absence); complete tag
  policy through the maintained predicate (ruleset 21633398); journal unused.
  The first irreversible job repeats the receiver, occupancy and candidate joins.
* **Per-effect admission**: exact publisher run (path, event, attempt 1, actor,
  `main` head equals the publisher) before every intent, dispatch and settlement;
  a fresh policy read before the tag.
* **Send fence**: a send is legal only for the intent this invocation just wrote,
  once. An intent inherited from a prior invocation is never a permit.
* **No automatic mutation retries**: a lost, refused or unapplied send leaves the
  effect in `intent`; later invocations only observe. There is no workflow retry
  and a rerun (attempt greater than 1) is refused.

## Verification

```text
PYTHONDONTWRITEBYTECODE=1 python3 tests/portable-workspace/release/test_execution_pair_forward.py
PYTHONDONTWRITEBYTECODE=1 python3 tests/portable-workspace/release/test_execution_pair_release.py
dotnet fsi eng/validate-github-immutable-execution-pins.fsx
```

The forward controls run the exact pinned engine against an in-memory GitHub,
feed and journal; every guard has a paired causal control that removes that
single guard and shows the harm. They also prepare a real forward candidate and
verify its reproducibility. Set `FSGG_SHARED_HELPER_ROOT` to a checkout of the
pinned helper commit to avoid the (digest-verified) fetch.

## Known limits

* A pre-existing **draft** release for the selected tag is invisible to the
  read-only preflight and package jobs (drafts need push access). It is detected at
  the `draft` effect, whose job holds contents write, before any release write;
  the packages already published remain valid and the operator resolves the draft.
* A GitHub Packages 404 is trusted as absence only when the public feed also has no
  history for that package; otherwise the listing is refused as ambiguous. A package
  whose only history is on GitHub Packages and invisible to the workflow token is
  still read as absent: the push would then fail and consume the journal id, but
  nothing public is published.
* `main` must equal the publisher on every intent, dispatch and settlement, so the
  protected environment should serialize merges to `main` during a run.
* The `execution-pair-forward` environment must be created and protected by root
  before any dispatch; a dispatch would otherwise create it unprotected.
* The ledger App must be one of the two integrations in the Authority main-writer
  ruleset (`authority_protection` pins them); the forward environment therefore
  needs that existing App's credentials, or a ruleset change in `FS-GG/.github`.

## Not established here

Actual publication, downloaded-only adoption and rollback, Linux containment,
the forward S1 receiver commits in `FS.GG.SDD` and `FsQuint` (created for each
candidate), hosted receiver runs of the forward candidate, the protected environment, App
installations, Trusted Publishing policy, occupancy and old-writer fencing, and any
operation admission remain with the parent obligations. Nothing here sends, tags,
publishes or dispatches.

The shared bound-intent fence landed in `FS-GG/.github#4805` and is pinned by the
admission (`sharedHelper.boundIntentFence`, evidence is the pinned engine file's own
SHA-256). The route stays `disabled`; it still uses its invocation-local `SendFence`
and does not yet pass `fence=` to the shared engine.
