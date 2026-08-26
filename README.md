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

No command, webhook listener, deployment, secret, subscription, or production
mutation authority is enabled by this scaffold.
Typed GitHub coordination substrate, qualification contracts, and fleet cutover tooling for FS-GG
