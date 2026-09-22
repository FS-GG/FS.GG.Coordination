# Telemetry Host manager

This F# console application prepares and maintains a telemetry Host without
silently creating a second public dashboard writer. It is an operator tool in
`eng/`, separate from the callable `fsgg-coordination` CLI and its production
dependency boundary.

Build with the repository's pinned .NET SDK:

```sh
dotnet restore eng/telemetry-host-manager/TelemetryHostManager.fsproj --locked-mode
dotnet build eng/telemetry-host-manager/TelemetryHostManager.fsproj --no-restore
dotnet run --project eng/telemetry-host-manager/TelemetryHostManager.fsproj -- status
```

The current public writer is selected by
`FS-GG/.github/main:telemetry-dashboard/active-publisher.json`. `guard` reads
the protected `main` commit and then the selector at that exact commit. It
returns 0 only for the selected host. Missing, malformed, or unreachable
control refuses the run. For the initial deployment, merge the selector with
the legacy Host named, verify `guard --host-id main-legacy` on that Host, then
install its `ExecCondition` drop-ins on both the member-v3 publisher and the
member registry updater. This sequence keeps the current publisher active
while the inert selector is introduced. A second Host may collect telemetry, build images, and stage
projections while its publisher stays inactive.

The supported host aliases are operator-assigned, public identifiers such as
`main-legacy` and `main-successor`. Do not put machine addresses, credentials,
private item identifiers, or telemetry payloads in the selector.

## Inert installation on a second machine

1. Verify the exact public Host release with `verify-host-release`. Provide
   the release's GitHub SHA-256 and the reviewed SystemAdmin
   `telemetry_host_release.py` verifier. `install-engine` verifies the coherent
   CLI package SHA-256, extracts only safe `tools/net10.0/any/` entries to a
   new immutable version directory, and checks its installed `--version`.
   Existing version directories are never overwritten.
2. Run `create-host-account` as root. It creates
   `fsgg-telemetry-podman` with a host-assigned UID/GID and subordinate ID
   range, plus private empty state/config/backup directories. This avoids
   assuming the legacy Host's numeric 953/954 identities, which are already
   assigned to other accounts on the successor machine.
3. Run `install-host-files --systemadmin-root ABSOLUTE_PATH` as root. It
   installs exact reviewed SystemAdmin wrapper, verifier, updater, and unit
   bytes into the service account's private home with that account as owner.
   It does not enable or start the units.
4. Run `stage-host-assets` as root with the exact package, manifest, journal,
   their three GitHub SHA-256 digests, version, and reviewed verifier path.
   It verifies the release and copies it into the account's private
   `releases/` directory without starting a service. Run `build-host-image`
   as that account with the package,
   manifest, package SHA-256, exact runtime image digest, and versioned local
   image name. Retain its image-ID receipt. Provision private TLS, producer,
   browser, deployment, and backup inputs through the operator's private
   channel. Restore a verified complete backup into a **new** state root;
   never initialize a blank store as a substitute for migration.
5. The installed Host unit remains disabled until its exact deployment file,
   schema-10 two-store preflight, authenticated readiness, backup recovery,
   and producer routing have been checked. The publisher, registry updater,
   and Pages units remain absent/inactive on the second machine during this
   preparation.

For existing installations, `update-host --updater PATH --config PATH` invokes
the reviewed rootless Podman updater as the service account. Its own durable
transaction and schema-range checks decide whether an update may activate.
`backup-stopped-host --operator PATH --deployment PATH --backup-id ID
--host-unit fsgg-telemetry-host-podman.service` invokes the reviewed wrapper
only after the service account's user unit is inactive. Neither command
reads or prints credentials.

## Publisher switch gate

The public `telemetry-data/host.json` ref has one writer. GitHub compare and
swap prevents stale commits but does not, by itself, establish a single
publisher across two hosts. Switching requires these ordered observations:

1. Qualify the original item and each member on the existing physical Host.
   For [`.github#3613`](https://github.com/FS-GG/.github/issues/3613), the
   two-member `item-detail/2` verdict is still the first gate. Preserve the
   existing publisher activation and success/intent receipts.
2. Stop the old registry updater, stage, publisher, and Host writers. Require
   each service inactive, each recurrence timer stopped, no unresolved
   publisher intent, and a fresh exact public commit. Take a quiesced,
   verified two-store and private-config backup. A source Host that cannot
   be positively fenced blocks the switch.
3. Restore on the target with its host-specific service identity and run
   schema, completeness, privacy, time/token, and public-row preservation
   checks. The target publisher needs a reviewed cross-host activation proof
   bound to the stopped source, transferred state, target identity, and the
   current public commit. The legacy same-host member-v3 proof is **not** a
   cross-host proof and must not be copied or relabeled.
4. With the old writer still stopped, change the protected selector by PR,
   incrementing `generation` and naming the target. Verify the merge and both
   hosts' guard results from that exact commit. Recheck the public ref and
   target projection before one target publisher service run. Enable target
   recurrence only after its success receipt, `telemetry-data` commit, Pages
   run, and deployed `hostRevision` all agree.

Before any target write, a failed preparation can resume the source only after
its original state, selector, and public ref still match. After a possible
target write, keep both recurrence timers off until durable intent and the
public ref are reconciled. This app currently prepares releases, observes
state, installs the fail-closed writer guard, and invokes the existing Host
updater/backup operations. Cross-host backup transport, private state restore,
and publisher activation need their own reviewed implementation and physical
Host evidence before an actual switch.
