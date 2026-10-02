# Telemetry Host manager bundle

This helper builds the framework-dependent telemetry Host manager into one deterministic
Actions artifact. It is a source and served-byte qualification boundary; it does not publish
a package, image, release, or durable public channel.

The F# boundary owns two explicit formats. `bundle/1` and `bundle-prepared/1` keep
their original shapes, reconstruction behavior, and `assemble`/`verify` CLI grammar for
historical artifacts. Hardened production uses `bundle/2`, `bundle-prepared/2`, and the
closed `assemble-v2`/`verify-v2` grammar. Version dispatch never treats one format as the
other. The v2 boundary binds:

- the exact protected Coordination commit, tree, manager project and lockfile;
- a separate build SDK from `mcr.microsoft.com/dotnet/sdk` at linux/amd64 manifest
  `sha256:1aabdb4843de1c426d3676bf1220bc040e540f82a765320b3eb2c693e8d0a7dd`,
  config `sha256:690de8d26a94a08b03190ccabf1906ac4025172267a1584255f062869caf8242`,
  SDK 10.0.400 and canonical tree SHA-256
  `c51a26bcd972e5f1b2944a912ca57cab9878fa88a2d8110fa0300c54ba0afcb0`;
- the target `mcr.microsoft.com/dotnet/aspnet` linux/amd64 manifest
  `sha256:ed6a2d26633ddcd3d42a1d9f9866214ecbbc11ba6ac5e0e843da02c13da24072`,
  config `sha256:d84f2a8aca8b8dbf142dd6bb1ffa7a1c051085c55bf36fc2f7fa1b1f17820932`
  and canonical hardened tree SHA-256
  `ead4ece42719198be9607d18415e428e3a6fcaadf50b88dc6e93894c47bec4c2`;
- the complete target `/usr/share/dotnet` inventory, including hostfxr,
  Microsoft.NETCore.App 10.0.12 and Microsoft.AspNetCore.App 10.0.12;
- every file emitted by `dotnet publish`, `TelemetryHostManager.dll` as the entrypoint,
  and fixed target argv rooted at `/usr/share/dotnet`.

The workflow pulls both OCI inputs by manifest digest and checks their config digest and
linux/amd64 platform. It extracts two target trees independently. Each private target tree is
normalized to directories `0555`, `dotnet` `0555`, and all other files `0444` before the F#
producer sees it. The build uses only the separately extracted SDK executable. Assembly runs
in a disposable, network-disabled namespace with the target tree mounted at its final
`/usr/share/dotnet` path. Verification reconstructs the same canonical bundle/2 manifest from
the second target tree and requires exact path, byte and mode equality.

The selected target contains 337 files and 109,735,192 bytes after normalization. The selected
SDK contains 4,907 files and 640,105,059 bytes. These inventory digests are part of the closed
selection, so a caller cannot attach the approved OCI label to changed extracted bytes. Changing
an image, platform, config, SDK executable/tree, target path, file, or mode requires a reviewed
source change to this producer.

The v2 verifier also rejects missing, extra, changed, duplicate, traversal, linked, oversized,
stale-source and unsupported-runtime inputs. The archive contains the manager payload and
manifest. It does not duplicate the target runtime because the immutable OCI manifest plus the
canonical inventory reacquires the exact tree. Actions retention is 90 days; root must retain
verified manager bytes under approved custody before expiration.

## Focused local check

Supply two independently extracted copies of the selected target and one extracted copy of the
selected SDK. Normalize both target copies as described above, then run:

```console
dotnet restore eng/telemetry-host-manager/TelemetryHostManager.fsproj --locked-mode
dotnet restore eng/telemetry-host-manager-bundle/tests/TelemetryHostManagerBundle.Tests.fsproj --locked-mode
dotnet publish eng/telemetry-host-manager/TelemetryHostManager.fsproj -c Release --no-restore -o /tmp/telemetry-host-manager-publish
dotnet build eng/telemetry-host-manager-bundle/tests/TelemetryHostManagerBundle.Tests.fsproj -c Release --no-restore
THMB_SOURCE_ROOT="$PWD" \
THMB_MANAGER_PUBLISH=/tmp/telemetry-host-manager-publish \
THMB_RUNTIME_ROOT=/tmp/target-runtime-a \
THMB_VERIFY_RUNTIME_ROOT=/tmp/target-runtime-b \
THMB_SDK_ROOT=/tmp/build-sdk \
THMB_SDK_EXECUTABLE=/tmp/build-sdk/dotnet \
  /tmp/build-sdk/dotnet exec --fx-version 10.0.11 \
  eng/telemetry-host-manager-bundle/tests/bin/Release/net10.0/TelemetryHostManagerBundle.Tests.dll
```

The focused suite runs the positive producer and fresh-tree verifier and refuses stale digest,
wrong base identity, platform, root and source, changed SDK or target bytes, changed hostfxr,
missing files, writable `0777` target content and changed modes before any manifest or archive is
written. A changed SDK executable fixture proves the executable is never invoked before its
complete tree is admitted. The suite also creates and verifies an actual legacy v1 bundle,
checks that a decoded v1 prepared document reaches the later runtime mismatch predicate, and
checks cross-version refusals. It retains the existing archive mutation coverage. This selection is stateless input
validation, so it introduces no lifecycle state or parallel formal model.
