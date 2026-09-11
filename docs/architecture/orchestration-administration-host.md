# Paused orchestration administration host

`FS.GG.Coordination.Orchestration.Host` is a separate, nonpackable executable boundary for explicit
initialization and migration-free runtime recovery. It does not change the permanently inert
`FS.GG.Coordination.App` boundary.

This first host boundary supports Linux x64 only. Its credential contract verifies an absolute regular file,
current effective-user ownership, owner-only mode, a non-symlink immediate parent without group or other
write access, a stable device/inode/mode/owner/size across open, and finite input size. Other platforms are
refused because they do not provide an equivalent qualified check here.

`init` is the only schema-writing startup command. It reads the PostgreSQL connection string from an
absolute owner-only file, applies the separately guarded root and pilot migrations, and prints the resulting
backup identity. `serve` requires that identity, a minimum generation fence, one permit identity, a stable
store identity, an absolute owner-only bearer-token file, and a loopback HTTP prefix. It never runs a
migration or creates an empty fallback store.

The host exposes unauthenticated process liveness and authenticated readiness, recovered pilot status,
pause, and revoke operations. Pause and revoke are durable pilot-domain commands bound to the configured
permit, its current sequence and pilot principal, and a caller-supplied stable command ID. Repeating an
identical command is handled by the existing PostgreSQL inbox; reusing its ID with changed bytes conflicts.
Recovery and readiness fail closed on unavailable, read-only, corrupt or incompatible storage, backup
identity mismatch, and generation-fence regression.

Every process start durably records the default-paused state and invalidates route readback before opening
the listener. Recovery of pilot-owned state after process replacement reports that fresh reconnect
readback is required. The operator surface continues to report `dispatchEnabled=false` and `mode=paused`.

The runner surface uses a distinct owner-only runner token in addition to Main's mutually authenticated TLS
boundary. It accepts only three closed messages: assignment poll, assignment acknowledgement, and durable
candidate return. Each message binds the immutable WorkItem persistence ID, route, attempt, runner, session,
principal, enrolled client fingerprint, generation, workflow revision, monotone client sequence, finite
enrollment expiry, and WorkItem deadline. The host derives the trusted principal from recovered enrollment;
the runner cannot select a provider identity. Assignment is released only for the exact dispatching
`DispatchRunner` intent. Candidate bytes are accepted only for the exact dispatching `StoreCandidate`
intent, are content-addressed in `ICandidateStore`, and are read back before the journal records the storage
effect and candidate receipt. Gaps, replay with changed bytes, pause, stale readback, stale generation,
expired authority, digest mismatch, and unknown storage outcome fail closed across restart.

The host still has no scheduler, GitHub credential, provider implementation, branch publication, pull-request
creation, or merge capability. The runner transport cannot call the other five hosted-writer operations and
does not widen the sealed provider adapter.

TLS termination and service lifecycle belong to Main's separately qualified deployment. The host accepts
only a loopback HTTP prefix so a deployment must keep its authenticated reverse-proxy boundary local.
Telemetry is neither referenced nor consulted for readiness, recovery, or control acceptance.

The source candidate exposes these concrete commands. They are not a deployment instruction until the
exact artifact and Main configuration have passed their independent gates:

```text
fsgg-coord-orchestration-host init --connection-file /absolute/private/postgresql-connection
fsgg-coord-orchestration-host serve --connection-file /absolute/private/postgresql-connection \
  --token-file /absolute/private/operator-token --runner-token-file /absolute/private/runner-token \
  --prefix http://127.0.0.1:5109/ \
  --store-id main-pilot --backup-identity <uuid-from-init> --minimum-generation-fence <n> \
  --permit-id <uuid> --pilot-principal <configured-pilot-principal>
```

An authenticated pause or revoke uses `POST /v1/pause` or `POST /v1/revoke` with
`fsgg.orchestration.host-control/1` JSON. The closed object includes `permitId`, `commandId`,
`expectedSequence`, `expectedGeneration`, `principalId`, `issuedAt`, `expiresAt`, and `reason`.
Unknown properties, oversized or truncated bodies, stale authority and identity reuse with changed bytes
are refused. `GET /v1/status` and `GET /health/ready` require the same bearer token; only process liveness
at `GET /health/live` is unauthenticated.

The separately released `fsgg-coord-orchestration-runner` executable is a least-capability HTTPS client. It
can post a prepared closed request to `/v1/runner/assignment`, `/v1/runner/ack`, or
`/v1/runner/candidate`; it has no database, operator, GitHub, provider, migration, or host-control code.
