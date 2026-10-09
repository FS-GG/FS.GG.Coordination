# LEARN-01.2 — Actor-owned admission and native observation

Status: A1–A3 source implementation prepared; production composition and exact-head hosted qualification pending. LEARN-01.2 and installed acceptance LEARN-01.4 remain open. Source, publication, installed adoption, native usage coverage and process cleanup are separate outcomes.

This window follows [.github LEARN §1.1](https://github.com/FS-GG/.github/blob/d01650c9/docs/roadmaps/2026-09-12-095059-stable-policy-orchestration-and-statistical-learning.md#11-telemetry-acceptance-at-actor-owned-dispatch--2026-10-09). The [executor observation](learn-01-executor-observation.md), [context shadow](learn-01-context-shadow.md) and [installed window](learn-01-installed-window.md) retain their existing accepted work. O0–O3 and LEARN-01.3 are not reopened.

## Existing authority and additive source window

Production already runs through `MainHostComposition`, `ExecutionSessionActor`, `ExecutionSessionCoordinator`, the PostgreSQL execution journal and `RemoteExecutorProvider`. The runner launches the local provider at the remote endpoint. This change preserves that chain and its existing execution, retry and cancellation rules. It adds no actor, coordinator, store, execution event stream or migration. Telemetry receipts cannot authorize execution or establish process cleanup.

- [ ] **A1 — Bind prospective identity and declared causality before launch.** `MainAdmissionPreparer.prepareCausal` constructs neutral immutable metadata from canonical WorkItem and parent-route history and persists it in the existing route before launch intent. Original/member identity, assignment/attempt/generation, invocation/root/parent, relation and admitted time are bound together. Declared purpose, dependency provenance and an optional explicit retry-of annotation remain data; follow-up is not automatically interpreted as retry. Unresolvable or compacted parent history refuses the qualified path.
- [ ] **A2 — Recover committed expected admissions.** `PostgreSqlExecutionStore.ReadCommittedAdmissions` reads bounded pages of immutable routes joined to their first committed intent. Staged routes alone are excluded. The shared pure projector supplies existing expected-dispatch, invocation-lineage, runtime-admission and event-time facts. `TelemetryAdmissionBridge` queues exact replayable bytes through the existing immutable publisher and retries at startup. Queue/store failure or partial pagination reports a coverage gap.
- [ ] **A3 — Join existing native facts and qualify the production boundary.** The runner retains its launch marker, native observer and private turn journal. Qualified commands use the admitted original/invocation identities for native process/thread/turn/usage facts and suppress duplicate runner admission population. Exact causal binding equality participates in replay. Old commands retain their existing admission producer and identity algorithms. Native lifecycle, cancellation request, process terminal, missing usage and cleanup remain distinct observations.

The checkboxes require qualification and protected integration, not just implemented source. This is one coupled source window in Coordination; no parallel executor implementation is introduced.

## Compatibility and bounds

Historical public F# record constructors, decoders and serialized bytes remain unchanged. Separate qualified route `/3` and command `/5` envelopes contain the old DTO and bounded causal metadata. Nested historical digest, outer envelope digest and canonical lineage joins are checked independently. Old receivers refuse the new schemas before effect; metadata is never silently discarded to claim qualified execution.

Causal metadata is at most 4 KiB with at most 16 dependency declarations. The combined control frame retains the existing 32 KiB ceiling. Unknown purpose is `unclassified`; unknown dependency coverage remains explicit. Duplicate dependencies, missing provenance, ambiguous retry, foreign member/original joins, generation contradiction and oversized metadata refuse. Historical routes and learning assignments are not retrofitted.

The preparation descriptor retains its compatible inner route. Workflow rebind retrieves the exact committed envelope independently. Command persistence, authenticated relay, local transport, readiness, launch, observation, content read and settlement use the combined codec. Runner responses settle the whole qualified command digest; launch markers retain the qualified bytes. Duplicate launch remains reconciliation-only even when local provider state is absent.

The committed census caps pages at 64 rows, validates canonical cursor form and retains partial coverage on continuation pages. SQL guards bound route and event bytes before allocation, in addition to the existing database payload constraints. Route/event digest, schema, intent key, generation, input and admission time must agree. Recovery time cannot change fact identity or payload. A complete bounded read snapshot is not evidence of a closed invocation graph or complete native counters.

## Qualification

Focused compilation covers the Protocol, Host, Runner and PostgreSQL test consumers. Five in-memory admission controls cover declared lineage/refusal, deterministic projection, actual Host preparer ordering and immutable outbox recovery. A pure runner control verifies exact historical admission bytes and native event identity joins.

The existing production Main composition theory has both historical and qualified cases. It crosses the real Host actor, PostgreSQL journal, remote provider, authenticated relay, packaged runner and local synthetic provider. Its transport interrupts the runner and loses a recovered response to exercise durable reconciliation across seven native effects. The qualified case also checks staged-route exclusion, independent restart replay, bounded pagination, invalid cursor/page refusal, existing payload size constraints and corrupt-route refusal/restoration before the production path proceeds.

Actual PostgreSQL/process execution requires a fresh disposable cluster, isolated sockets and independently owned child cleanup. Synthetic providers and loopback fixtures do not establish installed provider acceptance. Required hosted checks, semantic/formal selection, coherent validation and exact-source integration remain mandatory; local test success does not waive them. No authority, retry, budget state transition, canonical Quint model, process supervision, authentication or custody change is selected here.

## Later evidence-gated work

Purpose and dependency declarations are durably retained, but current ingest/1 fact kinds do not project them as an executable causal graph. Receiver projection requires a compatible explicit producer/receiver contract before emitting new fact kinds. Wait observations must originate at the owning provider, transport, queue or dependency boundary and retain open intervals after interruption. Critical-path reduction follows qualified graph, clock and coverage semantics.

Publication must bind exact qualified Host/runner artifacts and any changed telemetry producer/receiver bytes. Installed adoption separately verifies serving bytes, protected configuration, grants and receiving installation; no automatic upgrade or experiment enrollment follows from this source change. Fresh workspace creation and existing-workspace upgrade require published artifacts and preservation of authored files, journal/route bytes, pending effects and outbox recovery. No Templates or other materializer change is selected without an actual dependency.

Native usage requires authentic completed-turn counters, with requested, selected and observed model/effort kept separate. Missing usage, built-in collaboration coverage or descendant cleanup stays unknown. LEARN-01.4 activation, the current/focused experiment and later learning decisions retain their existing owners and prerequisites.
