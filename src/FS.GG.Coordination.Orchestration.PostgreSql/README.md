# PostgreSQL orchestration adapter

This non-packable O0 adapter treats `fsgg_orchestration.event`, `stream`, and
`inbox` as the only authoritative domain history. An append locks the stream
and commits the command identity and all domain events in one serializable
Npgsql transaction. The pending-effect view is derived from ordered event rows.
It does not make the provider action atomic with that transaction: an unknown
provider result must be observed before retry.

`EventEnvelope` serializes the closed Core `Event` union using
`fsgg.orchestration.core-event-json/1`. Append and recovery decode every event,
derive its `EffectChange` from the replay state, compare that with the indexed
columns, and apply `Orchestration.evolve`. Unknown schema or serializer versions,
bad hashes, invalid JSON, impossible evolution, and mismatched effect metadata
fail closed. Schema version 2 is accepted by the outer row for upgrade testing,
but uses the same serializer identifier and codec; this is replay compatibility,
not an implemented payload upcast.

Akka.Persistence.Sql stores only optional runtime markers in its journal and
snapshot tables. The current adapter uses them for runtime compatibility and recovery
qualification; it does not implement a marker-based wake-up optimization. Losing or
staling them cannot authorize a domain
command or external effect: runtime recovery and expected-sequence ownership
come from the SPI tables, and replies occur only after the SPI transaction
commits. The plugin is configured without remoting/listeners and with an
isolated PostgreSQL search path.

Candidate bytes and metadata commit together. `Put` permits only
`application/vnd.git.bundle`, `application/zip`, and `application/zstd`, then
reads the `bytea` value back and verifies identity, size, and SHA-256 before it
returns a receipt. A caller-created receipt is never an input to this adapter.

Versions are locked locally: Akka 1.5.71, Akka.Persistence.Sql 1.5.70,
FSharp.SystemTextJson 1.4.36, and Npgsql 10.0.3. The design follows the
[Akka.NET persistence architecture](https://getakka.net/articles/persistence/architecture.html),
[Npgsql transaction guidance](https://www.npgsql.org/doc/basic-usage.html#transactions),
and the [PostgreSQL 18 `pg_dump` documentation](https://www.postgresql.org/docs/18/app-pgdump.html).

