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

## Protected native collector installation

`install-native-collector` installs the owner-private sidecar consumed by the
Telemetry Host's `collect-native` command. Run it as the same operating-system
account that owns the Host configuration, collector credential, Codex home and
evidence directory. It accepts only a mode-`0600`
`fsgg.telemetry.host-config/2` file with one active `native-collector`
credential reference. Version 1 configuration is deliberately refused.

```sh
TelemetryHostManager install-native-collector \
  --host-config /absolute/private/host.json \
  --credential-reference roadmap-native-collector \
  --executable /absolute/reviewed/codex \
  --codex-home /absolute/private/codex-home \
  --evidence-root /absolute/private/native-collector-evidence \
  --provider openai --model gpt-6-sol --effort medium
```

The command checks ownership, exact private modes, symlink ancestry, executable
write/execute permissions, the selected scope and grant, and then atomically
creates `host.json.native-collector.json` plus an owner-private custody receipt.
An exact replay is idempotent. A changed executable, configuration, grant,
profile or installed sidecar refuses instead of rewriting custody. The receipt
contains hashes and non-secret authority identities; it never reads or emits
the credential bytes.

The command defaults to installation schema `/1` for existing operators. New
protected capture custody can opt into schema `/2` by pinning the reviewed
executable bytes explicitly:

```sh
TelemetryHostManager install-native-collector \
  --host-config /absolute/private/host.json \
  --credential-reference roadmap-native-collector \
  --executable /absolute/private/codex \
  --executable-sha256 LOWERCASE_SHA256 \
  --installation-version 2 \
  --codex-home /absolute/private/codex-home \
  --evidence-root /absolute/private/native-collector-evidence \
  --provider openai --model gpt-6-sol --effort medium
```

Version 2 requires the executable to be owned by the Host account or root and
to match the supplied SHA-256. The Codex home, evidence parent and evidence
root must be private, non-symlink descendants of the Host configuration's
private parent directory. Its sidecar adds only `ExecutableSha256` to the eight
version 1 fields. An existing version 1 sidecar cannot be promoted in place;
prepare prospective Host configuration and evidence paths, inspect them, and
then activate the new custody separately.

This source command does not activate the collector or authorize a model call.
Its receipt deliberately reports source verification, snapshot origin and
shared-cost completeness as `unknown`. Set
`FSGG_TELEMETRY_NATIVE_COLLECTOR_CONFIG` to the installed Host configuration
only after the separate installed readback verifies the exact Host executable,
sidecar, credential grant and private roots. A successful source fixture or
installation receipt is not live collection or qualified capture custody.

Publish the framework-dependent Linux executable with `dotnet publish -c
Release -o ABSOLUTE_STAGING_DIRECTORY`, then run its `install-manager --root
/opt/fs-gg/telemetry-host-manager --version VERSION` command as root. It
copies the required files into a new, root-owned version directory. Use that
absolute executable path for systemd guards and service-account operations.
Each manager update gets a new version directory; existing versions are never
overwritten.

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

For the first legacy Host update, stage a published manager directory on that
machine, then run one privileged invocation after the selector is merged:

```sh
sudo /ABSOLUTE/STAGED/TelemetryHostManager install-legacy-writer-guard \
  --host-id main-legacy --root /opt/fs-gg/telemetry-host-manager --version VERSION
```

The command requires the protected selector to name `main-legacy`, installs
the versioned manager, writes both systemd guards, reloads the units, and
checks the selector again. It leaves existing timers running. It refuses to
replace a version already installed; if a prior attempt installed only the
manager, inspect that version and finish with `install-guard-dropins` using
the installed executable. The staged directory and its hash receipt should
be retained until installation completes.

The supported host aliases are operator-assigned, public identifiers such as
`main-legacy` and `main-successor`. Do not put machine addresses, credentials,
private item identifiers, or telemetry payloads in the selector.

## Inert installation on a second machine

`prepare-inert` combines verified CLI engine installation, account creation,
reviewed file installation, and atomic Host release staging in one root
invocation. Supply
`--systemadmin-root`, `--systemadmin-commit`, `--package`, `--manifest`, `--journal`,
`--package-sha256`, `--manifest-sha256`, `--journal-sha256`, `--version`,
`--engine-package`, `--engine-sha256`, `--engine-manifest`,
`--engine-manifest-sha256`, `--engine-version`, and `--engine-root`.
It does not start or enable a unit. Each step can also be run separately as
described below when recovering a partial preparation.

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
3. Run `install-host-files --systemadmin-root ABSOLUTE_PATH --commit FULL_SHA`
   as root. It requires the exact reviewed SystemAdmin commit and clean
   Host source paths, then
   installs exact reviewed SystemAdmin wrapper, verifier, updater, and unit
   bytes into the service account's private home with that account as owner.
   It does not enable or start the units.
   For an existing installation, use `update-host-files` with the same exact
   source and commit options. It refuses absent, linked or foreign-owned
   targets and atomically replaces only this fixed file inventory. It also
   leaves all units disabled and stopped.
4. Run `stage-host-assets` as root with the exact package, manifest, journal,
   their three GitHub SHA-256 digests, version, and reviewed verifier path.
   It verifies the release and copies it into the account's private
   `releases/` directory without starting a service. Run
   `prepare-rootless-runtime` as root to enable the service account's user
   manager while leaving all telemetry units disabled. Run `build-host-image`
   as that account with the package,
   manifest, package SHA-256, exact runtime image digest, and versioned local
   image name. It pulls the exact digest-pinned runtime base before the
   reviewed offline build. Retain its image-ID receipt. Provision private TLS, producer,
   browser, deployment, and backup inputs through the operator's private
   channel. Restore a verified complete backup into a **new** state root;
   never initialize a blank store as a substitute for migration.
5. The installed Host unit remains disabled until its exact deployment file,
   schema-10 two-store preflight, authenticated readiness, backup recovery,
   and producer routing have been checked. The publisher, registry updater,
   and Pages units remain absent/inactive on the second machine during this
   preparation.

## Separate development dashboard

While the old Host remains the only public publisher, the second machine may
run its own Host on loopback with a newly enrolled development workspace and
private dashboard. Use a distinct TLS authority, producer credential, browser
principal, state root, and backup root. The development client must reach this
private endpoint and prove authenticated health before its repository is
activated prospectively. The new workspace may be initialized empty because it
is independent development data; it is not a restored copy of the old Host.
The SystemAdmin development-client procedure records a tested local Podman
route for that setup.

Keep every public publisher and registry updater absent or disabled on the
second machine. A later public Host migration must restore a verified old-Host
backup into a **different** state root, reconcile private identities, and pass
the switch gate below. The development store and its activation receipts are
not cross-host migration evidence.

For existing installations, `update-host --updater PATH --config PATH` invokes
the reviewed rootless Podman updater as the service account. Its own durable
transaction and schema-range checks decide whether an update may activate.
`migrate-host --updater PATH --config PATH --command-id ID
--expected-current-image sha256:... --target-qualified-release telemetry-host/vX.Y.Z`
passes an explicit schema 9→10 request to the rootless updater. The command
is only for a release whose old-backup restore and isolated rollback have
passed the disposable migration qualification; it does not relax the timer's
same-schema guard.
`retry-host --updater PATH --config PATH --command-id NEW_ID
--expected-current-image sha256:... --target-qualified-release telemetry-host/vX.Y.Z
--retry-failed-command OLD_ID` requests one reviewed same-schema retry after
the named earlier command settled `failed-rolled-back` or `recovered`. The
updater verifies the saved failed command, selected predecessor, qualified
candidate image and durable receipt before creating a new transaction and
backup. It keeps the earlier failure evidence. This command cannot request a
schema migration; automatic updates remain held until a reviewed retry runs.
`backup-stopped-host --operator PATH --deployment PATH --backup-id ID
--host-unit fsgg-telemetry-host-podman.service` invokes the reviewed wrapper
only after the service account's user unit is inactive. Neither command
reads or prints credentials.

## Publisher switch gate

The public `telemetry-data/host.json` ref has one writer. GitHub compare and
swap prevents stale commits but does not, by itself, establish a single
publisher across two hosts. Switching requires these ordered observations:

The manager's read-only `source-fence-status` command helps check the source
after its writers have been stopped:

```sh
sudo /opt/fs-gg/telemetry-host-manager/VERSION/TelemetryHostManager source-fence-status
```

It unions installed unit files and loaded units under the `fsgg-telemetry-*`
prefix in both the system and telemetry-service-account managers. It requires
the known publisher, registry updater, and Host units to be present, and checks
that every discovered unit is inactive. Services must be disabled, masked, or
static; timers, paths, and sockets must be disabled or masked. Any unreviewed
unit type refuses the command. It also requires the public `telemetry-data` ref
to remain unchanged across the readback. It prints the observed unit states
and exact public commit and exits nonzero when this inventory is not quiescent.
Its `activationAuthorized` field is always `false`: a zero exit only means this
instantaneous inventory preflight passed. Disabled services can still be
started manually, and this check cannot rule out an unrelated writer. The
stopped-source backup and recheck, private state transfer, target restore,
selector change, and cross-host activation proof below still need separate
review and physical evidence. Do not run this command in place of the old
Host's #3613 evening read-only item query.

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

## Prospective Responses collector installation

`install-responses-collector` writes closed installation schema 4 and its digest-bound receipt using the
existing Host private custody and verifier runtime/module/manifest arguments. This source route pins
`openai-responses/1`, the approved endpoint pair, model/effort and 8000 input / 1500 inclusive output / 60000
whole-phase limits. The provider credential reference/file is separate from the active native-collector
principal and grant. Installation checks file identity and custody without reading or hashing either
secret. Existing sidecars cannot be promoted or replaced.

The operator must supply exact private capability profile and actual no-model qualification result
paths and digests. Profiles are bounded to 64 KiB; product files must have positive lengths and
scenario results must use the exact sorted roster. A profile inventories complete disjoint installed directories and the actual
Host, Client, Core and Store assembly identities, plus fixed request policy, instruction/schema,
Responses verifier module and runtime manifest digests. The result joins the exact profile and code
with the closed nine-case fixture roster, evidence hashes, clean custody and original deadline.
Missing or authored example results do not qualify an installation. The Manager has no provider
request or credential-acquisition operation.

The independent verifier is distributed as the separately pinned programme module
`tools/learn_01_responses_source.py`. Its command is `verify-responses --capture CAPTURE
--telemetry-snapshot SNAPSHOT`; it performs offline replay only. The installed Host owns the original
remaining 60 seconds, verifier retirement, current grant and queue-claim checks. Installation leaves source
verification and snapshot origin unknown and activation unauthorized; genuine operation capture and
receiver acceptance are later gates. The prior AppServer verifier and v1-v3 receipts remain historical
variants and cannot establish Responses authority.
