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
