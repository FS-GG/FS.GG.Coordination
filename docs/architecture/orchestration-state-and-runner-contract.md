# Orchestration state and runner contract

O0 adds source-level orchestration state and persistence contracts. It does not install a host, open a
listener, enroll a production runner, mutate GitHub, deploy to Main, or authorize the O1/O2 pilot. The
existing `FS.GG.Coordination.App` project remains an inert, non-packable library.

## Authority and version boundary

The generated Coordination protocol remains the canonical source for command, event, stream, mutation,
receipt and observation vocabulary. `FS.GG.Coordination.Core.Orchestration` refines that vocabulary into
runtime state and total reducers. It does not add a second protocol generator or edit generated artifacts.

The first production persistence profile is frozen to .NET SDK 10.0.400, Akka.NET 1.5.71,
Akka.Persistence.Sql 1.5.70, Npgsql 10.0.3 and PostgreSQL 18.6. PostgreSQL is separate from telemetry
SQLite. Akka.Cluster, Akka.Remote, multi-node failover and a public host are outside O0.

The bounded runtime comparison is retained in
[orchestration-runtime-selection.md](orchestration-runtime-selection.md). Both candidates ran the same
failure lifecycle. Akka.NET was selected for the accepted many-entity topology and smaller host boundary;
Temporal's shorter sequential workflow implementation and stronger history tooling remain recorded. The
comparison used local/in-memory stores and is not PostgreSQL or Main durability evidence.

## Stable identities and receipts

A canonical WorkItem is identified by immutable repository database identity plus its repository-local
issue number. GitHub global node IDs are retained as opaque readback facts, including legacy padded Base64
and current formats, but are excluded from the persistence key so an ID-format migration, repository
rename or repository transfer does not create a second WorkItem. Project-board membership is a projection;
adding or removing the same issue from boards cannot create or delete its durable owner.

Project, operation, command, attempt, session, reservation, candidate and runner IDs are separate types.
Only reducers advance the monotone ownership generation. Commands can carry an expected generation but
cannot choose the next one. Canonical command bytes use length framing, normalized time and numeric forms,
stable collection ordering and the WorkItem persistence identity. The Core computes the SHA-256 before
deduplication. Reusing an ID with identical bytes is idempotent; changed bytes conflict.

Receipts distinguish accepted, duplicate, conflict and rejected outcomes and bind the resulting workflow
revision. A transport acknowledgement, actor mailbox message, console message or telemetry row is not a
command receipt.

## Reducer and execution rules

The WorkItem reducer accepts an explicit decision time. Wall-clock reads and provider calls stay outside
the reducer. Admission persists a finite token, runtime, cost and deadline budget. Checked arithmetic
refuses overflow. Pause, cancellation, revocation, budget use, reservations, claims, attempts, candidates,
operation state and command receipts all survive replay.

Write-capable execution requires all of these at effect release, not only when an attempt record is made:

- running control state and unexpired budget;
- current ownership generation and workflow revision;
- an unexpired local reservation;
- every required external claim observed at the same generation and workflow revision;
- no unresolved compensation obligation; and
- one current active attempt for runner dispatch.

Partial multi-touch acquisition followed by reservation loss retains each acquired claim as a recovery
obligation. A failed compensation does not consume the project recovery reserve or enable another normal
assignment. Project-level scheduling keeps a configured portion of capacity available for recovery work.

A lost heartbeat records an unknown attempt outcome. It never marks the attempt failed or permits a new
attempt. Replacement becomes eligible only after a terminal runner observation or explicit reconciliation.
Attempt history and IDs remain immutable. Likewise, an unknown provider effect moves the same operation to
`NeedsObservation`; it cannot be dispatched again until external readback proves it absent. A proven-absent
result authorizes retry under the same operation ID, followed by the ordinary current-authority checks.

Session opening, client-sequence acceptance, server-sequence advancement and closure are WorkItem events.
Their replayed state rejects duplicate and gapped client sequences and preserves both cursors. Runner
enrollment binds the stable runner ID, authenticated principal, specification fingerprint, generation and
finite expiry. Every runner message is also bound to current control, readback, budget and deadline state.
Restart or reconnect does not mint a new budget, generation, session, or sequence.

## Persistence and recovery

`OrchestrationPersistence.IJournalStore` defines one transaction that admits/deduplicates the inbox command
and appends its event rows under an expected sequence. A new command with no events is invalid. An identical
duplicate may contain no new events and returns the original terminal sequence. An ID/content conflict and
a wrong expected sequence are distinct results.

Effect intent and settlement metadata are fields of the authoritative event row. Pending outbox work is
derived by folding rows in sequence: an intent opens or reopens its operation and a later settlement closes it.
No separately committed outbox row is presented as atomic with actor persistence. Snapshots are disposable and may be saved only at or behind the journal head.
Projection checkpoints are independent read-side progress.

Readiness fails closed for unavailable/read-only/capacity-limited storage, corrupt records, unknown event or
serializer versions, a snapshot ahead of its journal, interrupted migration, incompatible downgrade and an
old backup awaiting reconciliation. Recovery does not dispatch until generations, revocations and unsettled
external effects have been reconciled. Telemetry availability is not an input to readiness or replay.

## Candidate survival

An accepted candidate binds candidate ID, baseline/head/tree Git identities, manifest and content SHA-256,
media type, exact byte count, retention and a durable location. Accepted media are limited to Git bundles,
ZIP and Zstandard payloads, with a 100 MiB maximum and at most 90 days of requested retention. Archive paths
must be relative, separator-normalized and free of dot segments, drive prefixes and traversal.

For non-Git artifacts, the PostgreSQL profile stores content-addressed bytes and metadata before returning a
storage receipt. For Git candidates, only an owner-qualified `refs/fsgg/candidates/` ref bound to the exact
commit may qualify. The reducer accepts candidate metadata only with the matching qualified storage receipt.
Interrupted uploads remain unreferenced and unacknowledged; duplicate identical uploads return the existing
receipt; conflicting bytes refuse. Missing or moved acknowledged objects are quarantined rather than
silently treated as delivered. Cleanup is bounded and touches only expired unreferenced uploads.

## Trust and activation limit

The initial runner remains a trusted cooperating principal. Generation and revocation fence operations that
pass through this supported route. They do not revoke an independently held broad GitHub credential and do
not establish hostile-runner containment. O0 therefore grants no pilot authority.

O2 remains blocked on the external prerequisites recorded by the standalone roadmap: authoritative
OperatingV2 (or a separately accepted sequencing decision), an accepted hosted-writer amendment, Main's
qualified deployment profile, separated credential custody, selected pilot scope and startup policy,
qualified artifact/runner/container routes, and operation-specific provider atomicity evidence. Source and
local PostgreSQL qualification do not satisfy those activation gates.
