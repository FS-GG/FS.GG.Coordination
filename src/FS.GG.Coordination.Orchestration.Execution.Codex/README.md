# Codex subscription execution adapter

This adapter binds the provider-neutral execution contract to the pinned Codex CLI 0.154.0
subscription session. It invokes the stable non-interactive surface as `codex exec`, supplies the
prompt only on standard input, selects the workspace with `-C`, requests JSONL with `--json`, and
captures the final structured response with `--output-schema` and `--output-last-message`. A known
Codex thread can be expressed with `codex exec ... resume <thread-id> -`, but recovery never resumes
an ambiguous spawn automatically.

The structured response reports only a completed edit result and a bounded summary. Input and
candidate identities remain owned by the accepted launch request and runner candidate inspector;
optional legacy identity-shaped response fields are treated as non-authoritative text.

Readiness requires both the exact CLI version and `codex login status` reporting a ChatGPT login.
The provider-reported model is not present in Codex exec JSONL, so the resolved selection is the
locally selected CLI model/effort, not a claim about backend routing. Complete token fields are
normalized with JSONL provenance; missing or malformed usage stays unknown. Subscription execution
has no per-invocation price here, so cost is not applicable rather than zero.

`DirectSessionTelemetryFacts` is a dormant, pure mapper for a separately observed direct-session
completed turn. It requires an exact supplied assignment and native turn ID, and prepares the
existing `fsgg.telemetry.ingest/1` usage shape with a workspace/item correlation sidecar. It does
not observe the current interactive session, authenticate the assignment, submit a batch, or prove
an applied Host receipt. The [direct-session boundary](../../docs/architecture/direct-session-telemetry-producer.md)
describes those remaining requirements.

`DirectSessionCorrelationHandoff` adds explicit assignment and current-turn source interfaces. It
requires two independently supplied records to agree on the exact assignment, native session,
prospective window challenge and thread before calling the mapper. No trusted implementation of
either interface is installed, so its successful structural result is not capture evidence.

`DirectSessionProspectiveWindowGate` models the next source boundary: an issued challenge is valid
for at most five minutes, may prepare one turn, and must match the selected current-session source.
Its ledger is immutable test state, not durable atomic replay custody. The issuer, trusted clock,
source authentication and persistent one-use store have no installed implementation.

`DirectSessionChallengeReservation` defines a typed atomic insert-if-absent store port keyed by the
challenge across scopes. The source is read only after a matching reservation receipt. A store
error has an unknown effect and must not be retried blindly; a source or clock gap after a confirmed
reservation burns the challenge. Concurrent fake-store tests exercise this contract, but no durable
store or trusted clock implementation is installed.

`CodexAppServerUsageProjection` is a read-only parser for the installed CLI 0.156.1 app-server v2
`thread/tokenUsage/updated` notification shape. It preserves the notification's exact thread and
turn IDs and its separate `last` and cumulative snapshots from independently authored fixture
bytes. It never treats either snapshot as completed-turn usage.

`CodexAppServerContinuity` is a dormant state reducer over an authenticated subscription binding
and prospectively observed frames. It requires the exact assignment scope, turn and transport,
then a contiguous local ordinal with `turn/started`, zero or more usage updates, and a terminal
`turn/completed`. A missing start, sequence gap, changed source, duplicate or malformed frame, or
disconnect before terminal permanently records a gap. The local ordinal is a proposed transport
journal sidecar, not an App Server cursor, so it cannot establish upstream notification
completeness. A terminal observation does not yield completed-turn usage. No authenticated
subscription to this interactive thread or transport-journal implementation is installed.

`CodexAppServerJournal` adds a dormant atomic append port for that future transport journal. Each
request carries the exact subscription binding, predecessor receipt, ordinal and immutable frame
bytes (or a disconnect fact). A matching append receipt permits the in-memory continuity reducer
to advance. Local frame and sequence refusals are retained as terminal gap entries. A duplicate,
conflicting or uncertain store outcome permanently halts that window.
The fake store tests retention and one winner under concurrent append attempts. No durable store,
recovery reader, authenticated live transport or current-session source is installed, so this
contract does not prove upstream notification completeness or session capture.

`CodexAppServerJournalRecovery` verifies a future store-issued sealed snapshot against the separately
bound subscription. It checks the declared count and head, every predecessor and ordinal, the
immutable frame digest, and the reducer's event order. Its result is either a provisional terminal
status or an explicit gap; it never produces completed-turn usage. The read port requires an
authenticated, transactionally complete seal. No seal issuer, trusted recovery source, or durable
store implementation is installed. A supplied JSON file cannot establish journal completeness.

`CodexAppServerCurrentSubscription` links a prospective issued challenge to a future authenticated
current-session App Server subscription. It reserves the challenge atomically before reading the
native source, then checks the exact workspace/item, session, challenge, thread, selected adapter,
transport and subscription time against a later trusted clock read. A confirmed reservation stays
burned on a source or clock gap. This is a structural handoff only: no trusted native current-session
source, issuer, clock, durable reservation store or transport implementation is installed. The
source must later retain the first `turn/started` notification in the journal; this gate does not
prove that a selected turn began after subscription or produce completed-turn usage.

`CodexAppServerFirstStart` adds that next structural check. After the one-use subscription
reservation, a future authenticated journal reader must provide the first append receipt and its
observation time. The gate checks the same native session and challenge, a first entry with no
predecessor, exact binding and connection, canonical bytes and digest, and a native `turn/started`
for the selected thread and turn after subscription. Source, clock and receipt failures burn the
reservation. No trusted journal reader or native transport is installed; the result does not
establish a complete turn, usage or an applied Host receipt.

`CodexAppServerCorrelatedTerminal` links the prospective first-start receipt to an authenticated
sealed journal read. The first receipt must match exactly, and the existing recovery reducer must
verify the complete chain through a terminal notification. It returns only a provisional terminal
status and usage-update count for the exact workspace/item and native turn. App Server `last` and
cumulative token snapshots do not provide completed-turn usage, so this result cannot feed the
telemetry mapper or prove a Host receipt. No trusted sealed reader or native capture path is
installed.

`CodexAppServerUsageTruth` emits a canonical workspace/item/session/thread/turn correlation with
an explicit no-usage verdict. In the pinned App Server v2 0.156.1 schema, `turn/completed` carries
turn status but no usage; `thread/tokenUsage/updated` carries `last` and cumulative snapshots. The
schema's internal raw-response completion describes one upstream Responses API completion, not a
turn aggregate. A matching `codex exec` child turn is a different session provenance. The policy
keeps all three evidence classes out of completed-turn telemetry and refuses candidates with
foreign IDs. A future native per-turn usage contract and trusted current-session source are still
required before this correlation can yield usage or an applied Host receipt.

`CodexExecution.supervisedActorProps` composes the concrete provider, neutral durable coordinator,
and thin Akka actor. The caller must provide a digest-addressed input reader, candidate inspector,
and durable journal. The production Host still needs to bind those interfaces to its PostgreSQL
journal and assignment workspace before activation.

Environment allow-listing is process hygiene only. The child remains a cooperative, same-user
runner with the user's filesystem and network authority; stronger credential isolation remains
deferred. Claude, OpenCode, and DeepSeek remain separate future adapters over the neutral contract.
