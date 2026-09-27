# GS2-09.9 `/5` activation source window

Owner: FS.GG.Coordination. This work keeps the existing `GS2-09.9 — Qualify callable ordinary v2 execution` roadmap identity and the owner amendment in `evidence/github-substrate-v2/roadmap-amendments/GS2-09.9.json`. It creates no second roadmap unit or acceptance receipt.

## S1: internal executable boundary

`eng/callable_isolated_v2_runtime/` is the first executable source window. An internal composer supplies immutable source, artifact, workflow, target, request, run, approval, issuer, and journal coordinates; a protected active-key reader; separate native read/write and token adapters; and a durable journal path. The signed canonical Ed25519 grant binds every coordinate and expires within 30 minutes. Grant rejection precedes token lookup, attempt reservation, and POST.

The retained `/5` operator parses complete native GET pages, proves absent prestate, sends at most one POST, and classifies two matching complete poststate readings. S1 wraps its `run_pull_once` and `classify_pull_after_one_attempt` boundary. SQLite commits `attempt_may_have_started` with `synchronous=FULL` before POST. A separate connection rereads the committed row. Competition can win one reservation; recovery rereads and never resends. Incomplete or contradictory readback is `Unknown`.

This source window has no installed production CLI route, endpoint selector, or caller-selected key/port flags. S2 must wire qualified protected readers, custody and issuer adapters. S3 must qualify the installed release, separate-process interruption and competition, and external effect counters before changing activation state. Historical `/4` acceptance and the existing `/5` contract, proposal, and native operator retain their identities.
