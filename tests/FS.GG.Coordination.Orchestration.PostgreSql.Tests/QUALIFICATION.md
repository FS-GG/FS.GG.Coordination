# Private PostgreSQL qualification

Run from this directory:

```sh
bash run-private-postgres.sh --logger 'console;verbosity=minimal'
```

The script creates an unprivileged temporary PostgreSQL 18.6 cluster on a Unix
socket at port 55439 with `fsync`, `synchronous_commit`, and `full_page_writes`
enabled. It creates only `orchestration_o0`, runs the locked Release build and
tests with `/tmp/fsgg-dotnet-10.0.400/dotnet`, and stops that private cluster.

On 2026-09-10, the Debug build completed without warnings or errors in 2.43 s.
Eleven tests passed against PostgreSQL 18.6 in 3.72 s after build. They exercised
migration/readiness refusal, atomic append/deduplication/CAS under concurrent
owners, semantic effect derivation, snapshot bounds, corrupt/unknown event
refusal, candidate byte readback/quarantine/cleanup, immediate database process
termination and restart recovery, and Akka marker recovery across ActorSystems.

This is laboratory evidence from one local filesystem. It does not qualify the
production database, storage hardware, power-loss behavior, network partitions,
full-disk handling, PostgreSQL failover, physical/PITR backup, or `pg_dump`
restore. The immediate-stop test demonstrates recovery of a transaction whose
commit had already returned; it does not resolve an ambiguous commit response.
Akka marker recovery does not prove exclusive actor ownership. The PostgreSQL
expected-sequence CAS admits at most one competing append, while readiness and
generation fences must still run before external effects.

