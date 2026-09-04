# GitHub narrow reconciliation

GS2-07.2 adds a pure qualification contract for turning supported GitHub event or command observations into scheduling intent. It does not subscribe to webhooks, call GitHub, or mutate a production queue.

The closed event inventory is issue, relation, Project, repository, ruleset, run/check, release, and installation. Each observation is normalized to repository, source revision, event kind, subject identity, and positive subject revision. Repository plus normalized subject is length-framed and hashed into the scheduling key. Duplicate and reordered observations converge to one entry per subject while retaining its newest relevant revision.

The plan seal binds the repository, source revision, complete supported-event inventory, ordered queue entries, queue receipts, deduplication dispositions, and the exclusive writer boundary `fresh-observe → reduce → sealed-plan → apply → verify`. Events and commands can request scheduling only. Direct writes, altered routes, incomplete writer chains, altered seals, cross-scope subjects, stale revisions, and replay containing new work refuse explicitly.

`eng/validate-github-narrow-reconciliation.fsx` executes distinct generated and independently authored adversarial controls against the pure contract and retained evidence. It also proves canonical Quint bytes are unchanged and scans the contract surface for network, production-queue, and GitHub-mutation capabilities.
