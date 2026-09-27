# GS2-09.9 `/5` activation source window

Owner: FS.GG.Coordination. This work keeps the existing `GS2-09.9 — Qualify callable ordinary v2 execution` roadmap identity and the owner amendment in `evidence/github-substrate-v2/roadmap-amendments/GS2-09.9.json`. It creates no second roadmap unit or acceptance receipt.

## S1: internal executable boundary

`eng/callable_isolated_v2_runtime/` is the first executable source window. An internal composer supplies immutable source, artifact, workflow, target, request, run, approval, issuer, and journal coordinates; a protected active-key reader; separate native read/write and token adapters; and a durable journal path. The signed canonical Ed25519 grant binds every coordinate and expires within 30 minutes. Grant rejection precedes token lookup, attempt reservation, and POST.

The retained `/5` operator parses complete native GET pages, proves absent prestate, sends at most one POST, and classifies two matching complete poststate readings. S1 wraps its `run_pull_once` and `classify_pull_after_one_attempt` boundary. The bound request carries the exact canonical bytes from the plan, without a newline, and the writer receives those bytes. SQLite compares the expected parent generation and head against its stored parent in the same immediate transaction that commits `attempt_may_have_started` with `synchronous=FULL`. A separate connection rereads the committed row. Competing operations on one parent can win one reservation. The trusted key and expiry are checked before token lookup and again after token acquisition, immediately before POST. Each check samples the trusted clock after key and cryptographic work. The exact provider response or loss marker is persisted for recovery; a pending outcome after a crash cannot become success. Recovery rereads with that response and never resends. Incomplete or contradictory readback is `Unknown`.

## S2: protected adapter source

`adapters.py` composes the internal runtime from distinct protected configuration, grant, issuer/key, parent, clock, read, token and write roles. Its fixed GitHub API origin and immutable binding reject caller-selected endpoints, keys and ports. The adapter verifies the signed grant before establishing a stored parent, checks issuer and key custody, and rechecks grant, issuer, key, token and write-scope expiry at the send edge. It preserves a received provider refusal even if a later observer fails. A separate read-only recovery composition requires the exact stored parent.

These are source contracts with an injected `ProtectedAuthority`; no production authority implementation, credential or key store, protected parent provider or external HTTPS transport is installed by this window. The local SQLite bootstrap is not protected journal authority.

## S3: process qualification and remaining activation

Separate-interpreter tests interrupt execution before and after a possible POST, race eight processes on one parent, inspect the durable fence from a fresh process and count token and POST effects. They prove one-attempt recovery for the internal source runtime; the protected adapter tests exercise delayed scope and key readers, expiry, revocation and known provider refusal. This window has no installed production CLI route or accepted live `/5` receipt.

The next activation window must supply and qualify the actual protected authority, identities, runner, journal CAS/readback and exact installed artifact against the external provider. Historical `/4` acceptance and the existing `/5` contract, proposal and native operator retain their identities. Do not change activation state from source or synthetic process evidence.
