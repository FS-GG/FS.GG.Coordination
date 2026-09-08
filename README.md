# FS.GG.Coordination

FS.GG.Coordination is the new-only implementation of the GitHub substrate v2
coordination boundary. The repository is intentionally inert while its protocol
and qualification system are built.

The initial solution contains distinct assemblies for the protocol specification,
pure core, GitHub adapters, CLI host, inert App host, qualification contracts, and
tests. See [the solution-boundary architecture](docs/architecture/solution-boundary.md).

```bash
dotnet restore FS.GG.Coordination.sln
dotnet build FS.GG.Coordination.sln --no-restore
dotnet test FS.GG.Coordination.sln --no-build --no-restore
dotnet fsi eng/verify-dependencies.fsx -- --root .
```

The `Boundary qualification / boundary-qualification` hosted check executes the
same locked restore, Release build, tests, and dependency policy for every pull
request and push to `main`.

Future repository-owned Codex launches use the exact published telemetry adapter:

```bash
dotnet tool restore
eng/codex-exec.sh --assignment /absolute/private/attempt.json -- \
  --json --ephemeral -m MODEL "task"
```

The host provides the approved private SQLite store through `FSGG_TELEMETRY_STORE`.
Observation remains advisory and never changes the launched process's native result.
Exact-head CI population is retained with explicitly unknown step attribution until
repository-specific classification rules are admitted.
See [the prospective telemetry receiver contract](docs/architecture/telemetry-runtime-receiver.md).

No command, webhook listener, deployment, secret, subscription, or production
mutation authority is enabled by this scaffold.
Typed GitHub coordination substrate, qualification contracts, and fleet cutover tooling for FS-GG
