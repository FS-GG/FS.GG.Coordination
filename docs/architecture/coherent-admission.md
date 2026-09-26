# Coherent candidate admission

The Optimistic workflow admits a pull request's immutable head when the PR is open for
review. An `opened` or `synchronize` event on a ready PR, and `ready_for_review` on a
draft, starts classification followed by the complete coherent run. A draft PR update
creates only a skipped workflow record: `prepare` is skipped and both `always()`
aggregates also skip. Bootstrap qualification still gives source work compile, test,
security and package feedback.

This boundary keeps stacked draft heads from consuming formal runner slots before an
integration owner selects a delivery candidate. It does not cancel, replace or reuse
any coherent obligation already admitted. Converting an active PR back to draft leaves
its accepted run in place. `workflow_dispatch` with an exact candidate/base tuple,
merge-group, protected-main push and nightly pending-candidate recovery retain their
existing paths; an owner can explicitly qualify a draft head through dispatch.

The plan is `eng/optimistic-qualification-plan.json`; the template and generated YAML
are checked by `eng/generate-optimistic-validation-workflow.fsx`. The generator checks
the admission event, draft guard and both aggregate guards. This is a static preflight:
the change adds no queue, lock or retry state, and the existing fanout fixture still
checks the costly dependency graph. A new model would not improve the event/guard
check enough to justify its maintenance. The first hosted draft PR and its transition
to ready provide provider-side acceptance evidence; local checks alone cannot prove
GitHub event semantics.

An owner can reverse the admission rule by restoring the previous trigger and guards
through a reviewed PR. Previously accepted runs finish under their original candidate
identity regardless of later policy changes.
