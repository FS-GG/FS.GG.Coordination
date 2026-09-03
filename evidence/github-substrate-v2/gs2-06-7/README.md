# GS2-06.7 workflow-selection qualification

`corpus.json` is the complete versioned qualification input for the pure Q3
selector and distinct Q7 fleet-safety projection. `independent-expectations.json`
is authored separately and binds one independently named mutation fixture to
every required control. Neither validator publishes workflows or mutates fleet
state.

The production-consumable implementation is the pure `WorkflowSelection` Core
API and the `workflow-select` CLI command. The sealed runtime fixtures exercise
arbitrary mixed file/non-file input and merge-group recomputation against the
queued head, current base, and current settings. The repository-owned reusable
workflow and composite action call that CLI and expose stable aggregate outputs;
they do not enable or edit a fleet receiver.

`observed-workflow-runs.json` retains eight completed GitHub Actions runs and
their exact job identities/times for every registered fleet repository. It was
captured read-only through the recorded REST queries on 2026-09-03, is complete
for the recorded 2026-08-20 through 2026-09-03 window, and is compacted below
the Git evidence ceiling without dropping any field used by independent Q7
recomputation. Q7 derives workflow/job fan-out, billed minutes, queue time, and
p50/p95 completion from those raw rows and rejects missing, stale, duplicated,
or forged evidence. Reviewed targets are projections, not claims that a
selective production rollout already ran.

`deletion-ledger.json` records zero deletions because this repair is additive.
The scheduled sentinel runs the full suite, compares every expected obligation,
and emits an explicit fleet-disable decision on a missed obligation. It records
the decision only; production workflow/settings mutation and GS2-06.8 remain
out of scope. The original accepted receipt is immutable and any superseding
repair receipt is created and indexed only after protected merge and fresh
independent qualification.
