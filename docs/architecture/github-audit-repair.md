# GitHub scheduled-audit repair

GS2-07.3 adds a pure repository-local qualification contract for complete scheduled audits. The input binds an exact source revision, sorted repository scope, cursor, complete pagination, event history, and authoritative observations classified as dropped delivery, preview gap, external repository, or schema drift.

Audit observations and event history compile to the same length-framed repository/subject scheduling key. The newest revision wins, an audit-only subject remains schedulable when delivery history is absent, and an event-plus-audit subject records convergence. Audit discovery never writes derived state: it can only schedule the shared `fresh-observe > reduce > sealed-plan > apply > verify` reconciler.

The contract has no network, scheduler, webhook, production queue, or production mutation dependency. Exact replay is byte-identical. Incomplete scope or pages, stale cursor or revision, altered observation/classification/routing, omitted repair classes, direct writes, unsealed or altered plans, and changed replay all fail closed.
