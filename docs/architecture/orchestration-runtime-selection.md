# O0 orchestration runtime selection

Status: selected for the bounded O0 realization spike on 2026-09-10. This does not qualify a
production persistence adapter or activate an orchestration host.

## Decision

Select Akka.NET for the first runtime realization, while retaining PostgreSQL as the production
persistence choice that must be qualified separately. The result is close on implementation size and
favors Temporal for sequential workflow ergonomics. Akka wins this system-specific decision because the
accepted topology has many independently owned, interacting entities, already requires application-level
effect reconciliation, and can run the first single-node realization without adding a separately operated
workflow service. The decision does not authorize Akka.Cluster, remoting, a listener, or provider effects.

The experiment used `Akka.Persistence` 1.5.71 and `Temporalio` 1.18.0, the stable versions returned by
NuGet on the capture date ([Akka.Persistence](https://www.nuget.org/packages/Akka.Persistence/1.5.71),
[Temporalio](https://www.nuget.org/packages/Temporalio/1.18.0)). Exact dependency hashes are in the two
committed lock files.

## Common experiment

The executable fixture under `tests/Orchestration.RuntimeComparison` applies the same vocabulary and
outcome checks without importing or redefining the canonical protocol. It covers duplicate identity,
changed duplicate content where the runtime exposes the needed payload, actor or worker loss and resume,
an ambiguous provider completion that remains `observe-before-retry`, cancellation, application schema
versions 1 and 2, unsupported-version refusal, a deliberate idle interval, and two concurrent child
lifecycles where one cancels and one completes.

Akka persists `Accepted`, `EffectUnknown`, `EffectSettled`, and `Cancelled` events and rebuilds actor state
after termination. This matches Akka's documented event replay model, but its default in-memory journal is
explicitly non-durable ([persistence architecture](https://getakka.net/articles/persistence/architecture.html)).
Temporal starts the same lifecycle on its official auto-downloaded local dev server, stops the first
worker, resumes through a replacement worker, and replays the completed server history with
`WorkflowReplayer`. The SDK documents that the local test environment downloads and runs a real server
subprocess ([Temporal .NET SDK](https://github.com/temporalio/sdk-dotnet/tree/3b6b5be4480836615a611aa1beedce8788509b38#workflow-testing)).

Both implementations treat a missing provider response as unknown. Neither retries until a provider
observation says the effect did not complete. This is application code in both candidates. Akka's ordinary
messages are at-most-once, and its reliable-delivery facilities can redeliver duplicates
([delivery documentation](https://getakka.net/articles/actors/reliable-delivery.html)); Temporal activity
durability likewise cannot make an independently committed provider effect atomic with workflow history.

## Measurements

Measurements used repository-pinned .NET SDK 10.0.400 on Linux x64 at base commit
`5ac962e2b72eb9145163ae443d48e26b408e1059`. Package cache was warm. Temporal launched CLI 1.8.3,
server 1.31.2 and UI 2.50.1 with in-memory persistence. The aggregate and exact commands are in
`tests/Orchestration.RuntimeComparison/evidence.json`; raw output remains in
`/tmp/o0-runtime-comparison-runs.log`.

| Observation | Akka.NET | Temporal .NET |
|---|---:|---:|
| Median runtime startup, five runs | 88.8 ms | 150.9 ms |
| Median replay/resume, five runs | 3.2 ms | 8.9 ms |
| Executed assertions | 11 | 8 |
| Candidate-specific nonblank fixture lines | 119 | 90 |
| Direct package cache footprint | 3.96 MiB for Akka + Persistence | 327 MiB for all bundled Temporal runtimes |
| Additional local process | none | Temporal dev server |

The median end-to-end warm run was 1.513 seconds. A cache-warm forced restore took 0.643 seconds and a
Release build without restore took 1.474 seconds. The 14-nonblank-line F# interop project built in 1.244
seconds. These timings compare this fixture on one machine; they are scale-free smoke measurements, not
capacity or production latency results.

## Engineering and operating comparison

| Dimension | Akka.NET evidence | Temporal .NET evidence | Consequence here |
|---|---|---|---|
| Initial lifecycle | More event application and reply plumbing | Shorter workflow and signal code | Temporal is clearer for a sequential durable procedure. |
| Process recovery | Actor recreation replayed the journal in one actor system | A replacement worker resumed server-held history | Both abstractions support the needed shape; only Temporal crossed a worker boundary in this lab. |
| Versioning and replay | V1 and V2 events replayed; unknown input refused by custom code | Completed history passed `WorkflowReplayer`; input version gate was custom | Temporal supplies a strong replay test surface; both still require compatibility policy and fixtures. |
| Debugging | State follows explicit events and can be inspected at each apply step | Server history is a first-class trace, but requires server tooling | Temporal has the stronger workflow-history experience. |
| F# fit | Direct subclass/handler surface compiled | Workflow attributes and `Task` entry point compiled | Both are consumable; neither lifecycle was implemented fully in F#, so no deeper parity claim is made. |
| Evolution extension | Two commands shared one actor and required explicit per-child bookkeeping | Two child lifecycle workflows were isolated by runtime identity | Temporal reduced code for this narrow extension; actor hierarchy/supervision remains a better match for the accepted broader entity topology. |
| Operations | One application process in this lab; production still needs PostgreSQL and service supervision | Worker plus a separate Temporal service; this lab launched an additional server process | Temporal adds a service boundary and its own upgrade/backup/readiness surface. |
| External-effect recovery | Dedup, unknown state, observation and settlement were custom | Payload conflict, observation and settlement were custom | Neither removes the canonical inbox/outbox, provider idempotency, fencing or reconciliation work. |

Temporal's shorter fixture and history replay are meaningful. They do not offset its extra operated service
for the accepted single-node start, because the larger design is dominated by interacting durable work
items, sessions, reservations, operations and attempts rather than one long sequential workflow. Akka also
keeps policy reducers independent and provides the closer lifecycle/supervision vocabulary. If later
measurements show mostly sequential workflows or the organization adopts Temporal Cloud as an accepted
dependency, this decision should be revisited rather than generalized.

## Evidence limits and next gate

The Akka journal was in-memory, so actor termination is not CLR-process, machine, or durable-database crash
evidence. The Temporal local server also used in-memory persistence; worker shutdown was real, while server
crash/restart and durable recovery were not exercised. The 250 ms deliberate absence proves resumption after
an idle interval and does not simulate days of downtime. No PostgreSQL, full disk, network partition,
failover, backup restore, serializer migration, rolling deployment, or provider endpoint was present.

The next O0 slice may implement only the Akka.NET PostgreSQL adapter. It must freeze and test the exact Akka
plugin, serializer, PostgreSQL major/configuration, migrations and transaction behavior. Event streams stay
authoritative when the plugin cannot atomically commit custom outbox rows with events; pending outbox is then
derived and rebuildable from events. The local journal and Temporal dev-server results cannot qualify Main.
