# GitHub merge-group support

GS2-07.5 is a pure, repository-local qualification contract for GitHub merge
groups. An event never authorizes from event-time state alone. Each evaluation
must receive both the prior and freshly re-observed canonical base repository,
full `refs/heads/...` ref, exact SHA, observation revision, and freshness
deadline. Repository, ref, SHA, or revision disagreement is an explicit refusal.
It also receives freshly re-observed claim generation, review digest, candidate
head, dependency digest, release posture, and repository-settings digest.

The contract compares every observed value with the current value in the same
evaluation. A base update or force-push, even during concurrent work, changes
the SHA or revision and refuses the decision. This removes the old “remember
main now, compare later” race: successful qualification is scoped to the exact
merge-group head and current base observation that are sealed together.

Required checks use separate configured-expected and freshly observed aggregate
inventories. They must agree exactly. Every result must exist exactly once, must
have run for the `merge_group` event on the exact group head, and must have
succeeded. The complete result rows—not only their names—are sealed. Missing,
duplicate, pending, failed, wrong-event, and wrong-head results all fail closed.

Successful output is canonical JSON with a length-framed SHA-256 seal over all
authority-bearing fields. Public parsing and verification independently enforce
the complete grammar and semantics before accepting even a correctly recomputed
seal. Exact replay is byte-identical; replay after any new observation refuses.
The module performs no network, queue, settings, workflow, release, package, or
GitHub mutation and does not deploy a merge queue.
