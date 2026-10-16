# Coherent candidate admission

The Optimistic workflow admits a pull request's immutable head when the PR is open for
review. An `opened` or `synchronize` event on a ready PR, and `ready_for_review` on a
draft, starts classification followed by a candidate-bound CI profile. A draft PR update
creates only a skipped workflow record: `prepare` is skipped and both `always()`
aggregates also skip. Bootstrap qualification still gives source work compile, test,
security and package feedback.

This boundary keeps stacked draft heads from consuming formal runner slots before an
integration owner selects a delivery candidate. It does not cancel, replace or reuse
any coherent obligation already admitted. Converting an active PR back to draft leaves
its accepted run in place. `workflow_dispatch` with an exact candidate/base tuple,
merge-group, protected-main push and nightly pending-candidate recovery retain their
existing paths; an owner can explicitly qualify a draft head through dispatch.

## Early source qualification

Before Bootstrap releases its execution jobs, the existing `reuse-decision` job runs
[`source-preflight.sh`](../../eng/bootstrap-gates/source-preflight.sh) on the execute
route. Whole-tree reuse retains its existing authentic complete donor validation.
The same script replaces restore/build in coherent `shared-build`; `formal-prepare`
now depends on successful `shared-build`. A failed or incomplete preflight blocks
those dependent workloads. Diagnostic and terminal aggregates retain their existing
failure collection rules and cannot turn missing qualification into success.

The script requires an exact candidate HEAD and clean tracked files, restores the
solution in locked mode, and compiles the full Release solution with warnings as
errors. It then runs two focused CI contract cohorts and the existing allocation,
executor, inert custody and transport cohorts against those binaries. Each cohort
needs a fresh nonzero passing TRX census;
transport additionally needs the existing exact callback-case census and pinned
source-only callback fixtures. Compilation precedes fixture preparation. Failed or
invalid cohort outcomes block later launches because healthy test-host completion
and cleanup are then unknown. The attempt preserves its first cause separately from
retention failures. Each attempt has fresh evidence, an eight-minute process-group
deadline and a ten-minute job budget. There is no retry. Diagnostics upload separately from successful build
artifacts; stage transitions retain unexecuted and prerequisite-blocked coverage.

The CI cohorts select the authored Bootstrap and coherent launch-order mutations,
healthy projection/contract witnesses, workflow recovery and identity-path checks,
and the inert orchestration controls. They select fixed methods in the two existing
built test assemblies, excluding their FSI, shell and formal fixtures. A hosted
attempt passed the original 193-test runtime preflight but later failed six unit
cases because mutation fixtures omitted the validator's initial Bootstrap contract
and an existing assertion expected an obsolete comment. The repaired fixtures prove
an unmutated positive witness before each mutation, and these selected CI contracts
now run before runtime cohorts or callback source preparation. No full suite is
added to the early gate.

The coherent build artifact remains candidate-named and limited to the current run.
The preflight script and callback/census inputs participate in qualification-plan
identity. This introduces no binary cache or donor receipt. Full required compiler,
recovery, package, security and coherent checks remain authoritative and unchanged.

Run `python3 -I -B eng/tests/source-preflight/test_source_preflight.py` for inert
orchestration controls. These cover success, failed restore/compile, missing tools or
inputs, wrong candidate, dirty tracked files, timeout, invalid TRX and exact callback
case rejection without invoking CLR or fetching source. BootstrapCi and the coherent
generator inspect the actual projected job boundaries and success dependencies;
mutation tests remove dependencies and bypass success to exercise refusal. Hosted
normal CI must still verify generator byte equality and actual compile/tests.

The pilot uses static dependency checks and shell controls rather than a new formal
model: the change adds literal ordering, with no new scheduler or reuse semantics.
Observed baseline feedback was about 3–4 minutes for compile plus these cohorts,
versus about 15 minutes for the full compiler gate on one prior candidate. Bootstrap
adds that early build cost on successful execute routes. Coherent reuses its existing
build but serializes formal preparation after it. The added CI selection reuses
compiled assemblies; the inert control portion measured about five seconds locally,
while its two hosted test-host startups and complete added latency remain unmeasured.
The existing eight-minute script and ten-minute job bounds remain unchanged.
Successful overhead, cold/warm variance, failure frequency and billed savings remain
unmeasured. Existing run
timings should measure first diagnosis, added successful latency and prevented job
launches; no positive aggregate savings are claimed.

## Two qualification profiles

`full` is the default for every event. The `scoped` pilot applies only to a ready PR at
its exact candidate head when classification returns `reused` with a validated authentic,
complete, unexpired **full** prior aggregate. The recorded base must match the PR event's
base SHA. The exact base-to-head diff must contain
only modifications to `README.md` or
`src/FS.GG.Coordination.Cli/ObserverViewCommand.fs`. The latter is a read-only CLI
view over exported observer events; scoped qualification still runs unit, architecture,
security, package and recovery partitions. Additions, deletions, renames, unknown paths,
stale bases, `current` classification and malformed inputs select `full`.

The scoped matrix runs the formal base shard and five nonformal partitions. At aggregate,
the producer reads the prior run and full artifact again, compares all seven semantic
and binding identities, and recomputes the canonical `reused` selection. Full runs
retain all formal shards and the six-partition aggregate. A scoped result uses the distinct
`scoped-aggregate-receipt/1` schema and `scoped-aggregate-*` artifact name. Prior reuse
discovery and nightly recovery accept only `coherent-aggregate-*` full evidence, so a
scoped pass cannot donate full evidence or stop a later full recovery. Protected-main,
merge-group, scheduled and explicit dispatch events always select `full`.

The [plan](../../eng/optimistic-qualification-plan.json) defines the audited paths and
execution set. The [selector](../../eng/optimistic-profile.py) checks event, disposition,
donor binding and exact diff; the generated workflow consumes its matrix. The generator
and BootstrapCi provide a static preflight over the actual workflow projection. The
[profile fixtures](../../eng/test-optimistic-profile.py) test admission and fail-closed
cases, and the [aggregate fixture](../../eng/test-optimistic-scoped-aggregate.sh) checks
missing and forged receipts and full-donor rejection. This pilot has no hosted cost or
latency measurement yet; savings are an expectation from omitting nineteen formal
shards, not an observed result. A new formal preflight model would duplicate the bounded
static matrix and receipt checks without a demonstrated additional defect class.

An owner can reverse the admission rule by restoring the previous trigger and guards
through a reviewed PR. Previously accepted runs finish under their original candidate
identity regardless of later policy changes.
