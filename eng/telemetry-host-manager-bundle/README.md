# Telemetry Host manager bundle

This helper builds the framework-dependent telemetry Host manager into one deterministic
Actions artifact. It is a source and served-byte qualification boundary; it does not publish
a package, image, release, or durable public channel.

The F# boundary owns the closed archive semantics. It binds:

- the exact protected Coordination commit and tree;
- the manager project and lockfile;
- pinned .NET SDK 10.0.400;
- selected Microsoft.NETCore.App and hostfxr 10.0.12 closure;
- every file emitted by `dotnet publish`;
- `TelemetryHostManager.dll` as the entrypoint and the fixed runtime argv.

The verifier rejects missing, extra, changed, duplicate, traversal, linked, oversized, stale
source, and unsupported runtime inputs. The workflow uploads one archive plus its canonical
manifest and prepared receipt, downloads them into a fresh directory, compares the served
receipt bytes, and validates the downloaded archive. Actions retention is 90 days; root must
retain verified bytes under approved custody before expiration.

## Local check

Use the repository pinned SDK and installed runtime 10.0.12:

```console
dotnet restore eng/telemetry-host-manager/TelemetryHostManager.fsproj --locked-mode
dotnet restore eng/telemetry-host-manager-bundle/TelemetryHostManagerBundle.fsproj --locked-mode
dotnet restore eng/telemetry-host-manager-bundle/tests/TelemetryHostManagerBundle.Tests.fsproj --locked-mode
dotnet publish eng/telemetry-host-manager/TelemetryHostManager.fsproj -c Release --no-restore -o /tmp/telemetry-host-manager-publish
THMB_SOURCE_ROOT="$PWD" THMB_MANAGER_PUBLISH=/tmp/telemetry-host-manager-publish THMB_RUNTIME_ROOT=/usr/share/dotnet \
  dotnet run --project eng/telemetry-host-manager-bundle/tests/TelemetryHostManagerBundle.Tests.fsproj -c Release --no-restore
```

The pipeline is a single linear build, assemble, upload, fresh download, and verification
chain. Static workflow bindings and actual archive mutation tests provide the useful preflight.
There is no retry, shared CAS, fan-out, or new lifecycle state, so a separate Quint model would
add a parallel plan without covering another stateful risk.
