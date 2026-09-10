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

The process always reports `dispatchEnabled=false` and `mode=paused`. Recovery of pilot-owned state after
process replacement reports that fresh reconnect readback is required. There is no reconnect endpoint:
the host cannot synthesize authenticated provider evidence. It also has no runner dispatcher, scheduler,
provider writer, GitHub credential, candidate submission, PR creation or merge capability. A future
write-capable host remains gated by the canonical hosted-writer amendment and O2 activation evidence.

TLS termination and service lifecycle belong to Main's separately qualified deployment. The host accepts
only a loopback HTTP prefix so a deployment must keep its authenticated reverse-proxy boundary local.
Telemetry is neither referenced nor consulted for readiness, recovery, or control acceptance.

The source candidate exposes these concrete commands. They are not a deployment instruction until the
exact artifact and Main configuration have passed their independent gates:

```text
fsgg-coord-orchestration-host init --connection-file /absolute/private/postgresql-connection
fsgg-coord-orchestration-host serve --connection-file /absolute/private/postgresql-connection \
  --token-file /absolute/private/operator-token --prefix http://127.0.0.1:5109/ \
  --store-id main-pilot --backup-identity <uuid-from-init> --minimum-generation-fence <n> \
  --permit-id <uuid> --pilot-principal <configured-pilot-principal>
```

An authenticated pause or revoke uses `POST /v1/pause` or `POST /v1/revoke` with
`fsgg.orchestration.host-control/1` JSON. The closed object includes `permitId`, `commandId`,
`expectedSequence`, `expectedGeneration`, `principalId`, `issuedAt`, `expiresAt`, and `reason`.
Unknown properties, oversized or truncated bodies, stale authority and identity reuse with changed bytes
are refused. `GET /v1/status` and `GET /health/ready` require the same bearer token; only process liveness
at `GET /health/live` is unauthenticated.
