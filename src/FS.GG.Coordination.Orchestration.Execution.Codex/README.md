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

`CodexExecution.supervisedActorProps` composes the concrete provider, neutral durable coordinator,
and thin Akka actor. The caller must provide a digest-addressed input reader, candidate inspector,
and durable journal. The production Host still needs to bind those interfaces to its PostgreSQL
journal and assignment workspace before activation.

Environment allow-listing is process hygiene only. The child remains a cooperative, same-user
runner with the user's filesystem and network authority; stronger credential isolation remains
deferred. Claude, OpenCode, and DeepSeek remain separate future adapters over the neutral contract.
