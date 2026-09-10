# Private PostgreSQL qualification

Run from this directory with installed PostgreSQL 18.6 binaries:

```sh
bash run-private-postgres.sh --logger 'console;verbosity=minimal'
```

On GitHub `ubuntu-latest`, use the exact SDK selected by `global.json` from
`PATH` and the pinned multi-architecture PostgreSQL image:

```sh
FSGG_PG_MODE=docker DOTNET_EXE=dotnet bash run-private-postgres.sh \
  --logger 'console;verbosity=minimal'
```

The image is `postgres:18.6@sha256:4ef4dbc939d61acea57712655ddb4b4ab27419c913f94cca0cd57cb3ea3c2280`.
Host, port, username, container name, cluster root, and SDK executable are
configurable through `FSGG_PG_HOST`, `FSGG_PG_PORT`, `FSGG_PG_USERNAME`,
`FSGG_PG_CONTAINER`, `FSGG_PG_ROOT`, and `DOTNET_EXE`. Docker mode maps TCP only
to loopback and uses `docker kill`/`docker start` for the process-loss case;
binary mode retains the private Unix-socket cluster and `pg_ctl` controls.

The script creates an unprivileged temporary PostgreSQL 18.6 cluster on a Unix
socket at port 55439 with `fsync`, `synchronous_commit`, and `full_page_writes`
enabled. It creates only `orchestration_o0`, runs the locked Release build and
tests with `/tmp/fsgg-dotnet-10.0.400/dotnet`, and stops that private cluster.

On 2026-09-10, the Debug build completed without warnings or errors in 2.43 s.
Sixteen tests passed against PostgreSQL 18.6 in a fresh-cluster Release run
that took 24.353 s including cluster initialization, restore, build, and stop. They exercised
migration/readiness refusal, atomic append/deduplication/CAS under concurrent
owners, semantic effect derivation, snapshot bounds, corrupt/unknown event
refusal, candidate byte readback/quarantine/cleanup, immediate database process
termination and restart recovery, Akka marker recovery across ActorSystems,
same-codec v1/v2 row replay, lost-response retry, and logical backup restore.

This is laboratory evidence from one local filesystem. It does not qualify the
production database, storage hardware, power-loss behavior, network partitions,
full-disk handling, PostgreSQL failover, or physical/PITR backup. The `pg_dump`
test is a logical backup restored into a second isolated database; it verifies
generation fencing, pending-effect reconciliation, retained candidate bytes,
and absence of an artifact acknowledged after the backup. Recover, append, and
candidate acceptance enforce the database identity/generation/schema gate on
each transaction; callers cannot bypass it by omitting a readiness probe. A full
Core-decided lifecycle roundtrips private identities, budgets, attempts, and an
artifact proven through the candidate store. Two live ActorSystems with the same
persistence ID store only one sequence-one marker; this demonstrates the SQL
unique sequence limit, not exclusive actor ownership. The immediate-stop test demonstrates recovery of a transaction whose
commit had already returned; it does not resolve an ambiguous commit response.
Akka marker recovery does not prove exclusive actor ownership. The PostgreSQL
expected-sequence CAS admits at most one competing append, while readiness and
generation fences must still run before external effects.
