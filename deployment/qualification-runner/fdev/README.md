# fdev native-window launcher (provider `fdev-kvm-qualification-v1`)

This is M0 source for [.github#4778](https://github.com/FS-GG/.github/issues/4778) and
[#4650](https://github.com/FS-GG/.github/issues/4650). The owning plan is .github
`docs/coordination/2026-10-30-native-chain-rehost-fdev.md`. Merging this source starts no
window or guest, and it selects no operation, clock, claim or credential. M1 is a separate
Root selection.

## Custody and what the evidence never claims

fdev is the only security zone, so protection inside it is cooperative
([owner ruling](https://github.com/FS-GG/.github/issues/4778#issuecomment-6519522997)). The
claim, hashes, clocks and readbacks detect accidents and keep the record coherent. They are
not tamper-proof against agents or fdev root.

Every evidence document (`fsgg.fdev-native-window-evidence/1`) carries these constant fields,
and `validate-evidence` refuses any other value:

- `independence: {fromFdev: false, fromHome: false}`
- `custody: cooperative`
- `tamperProof: false`
- `hostKind: fdev-agent-vm-drained-window`
- `ioSharedWithHostCoTenants: true`

`sharedCodexHost` is computed, never supplied. It is `false` only when all four exclusivity
checks pass ([shared-host ruling](https://github.com/FS-GG/.github/issues/4778#issuecomment-6527516917)).

**I/O correspondence is never asserted by fdev.** The evidence reports `not-established`
until Home-observed window evidence is supplied. That input is
`fsgg.fdev-native-home-io-evidence/1`, and `attach-home-io` applies it. It must contain:

- `iostat -dx` samples covering the window from entry to exit;
- Home's statement that the scheduled writer was stopped and no interactive workload ran;
- fdev-side `io.stat` and PSI at both ends;
- cap readbacks at window entry and exit.

Only then does the status become `window-evidence-captured-awaiting-root`. `acceptedByRoot`
stays `false`: Root decides satisfaction
([storage ruling](https://github.com/FS-GG/.github/issues/4778#issuecomment-6526677331)).

## Commands

```sh
# Read-only. Exit 0 only when every row is Supported.
python3 -I -B fdev_native.py preflight [--selection S.json] \
    [--host-partition-df home-df.json --host-partition-max-age 3600] \
    [--image noble.img] [--privileged-census] [--no-github]

# Dry run: preflight verdict plus the exact effect plan. Executes and writes nothing.
python3 -I -B fdev_native.py launch --dry-run --selection S.json

# The window (M1+, under a Root selection), run inside its own bounded scope:
systemd-run --user --scope --unit=fsgg-native-<op>-<step>-launcher.scope \
    -p RuntimeMaxSec=<sum of step clocks + 600> -p MemoryMax=1G -p TasksMax=128 -- \
    python3 -I -B fdev_native.py launch --selection S.json

# Recovery after a crashed window: remove the recorded caps (idempotent).
python3 -I -B fdev_native.py window-exit --manifest /srv/v3-pool/native/runs/<op>/caps-manifest.json
```

`--privileged-census` runs the read-only `census` subcommand under `sudo -n`. That is the
only way to see other users' open files on `/srv/v3-pool`. Without it, unreadable processes
leave `pool.no-data-writers` Unknown.

## Inputs

- **Selection** (`fsgg.fdev-native-window-selection/1`), bound by Root for each window:
  - operation id and step, plus the Root comment on #4778 that names the operation;
  - clocks (pre-start, useful, retire, collector, collector retire, enclosing);
  - `ioCaps` as `rbps`, `wbps`, `riops` and `wiops` (no default is guessed);
  - `exemptCgroups`: the Root session and the readback actor;
  - `rootDrainAttestation`: a URL;
  - the stock image path, verified against the
    `noble/acquired-input-lock.json` pin;
  - read-only disks with digests and an optional NoCloud seed directory;
  - the Home df reading and its maximum age.
- **Home df** (`fsgg.fdev-native-home-df/1`): the raw output of
  `df -B1 --output=size,used,avail,itotal,iused,iavail` on the shared HDD partition, plus
  `measuredAt` and the citing `source` URL. It is an input and is never inferred. A reading
  older than the selection's bound refuses. This is the capacity re-read hook: the launcher
  re-reads it, pool free space, `MemAvailable` and exclusivity at window entry, after the
  claim.

## Preconditions (each fails closed; Unknown is never success)

| Check | Rule |
|---|---|
| `selection.valid` | Schema, clocks summing within the enclosing bound, exempt sessions, attestation, stock-image lock path, Home df shape |
| `selection.operation-not-retired` | 83a08, 69db, 2ae4 and d2f2, plus any id sharing those short prefixes, refuse |
| `selection.root-comment-binds-operation` | The operation id appears in a Root-authored comment on #4778 |
| `claim.absent` | `/srv/v3-pool/native/claims/<op>` does not exist. The claim is `O_CREAT\|O_EXCL\|O_NOFOLLOW` and refuses forever. |
| `kvm.device`, `kvm.nested` | `/dev/kvm` is a read-write character device and nested virtualisation is on |
| `cgroup.v2-controllers`, `cgroup.cpuset-cpus`, `cgroup.user-io-delegated` | cgroup v2 with `cpuset cpu io memory pids`, CPUs 8-11 available, `io` delegated to the user manager |
| `systemd.version`, `tools.present`, `privilege.sudo-noninteractive` | systemd 250+, the tools, `sudo -n` (cooperative hygiene) |
| `exclusive.lanes-drained` | Every non-exempt lane unit of developer, fsgg-store-dev and fsgg-learn-qual holds only idle shells |
| `exclusive.exempt-sessions-quiet` | No build server or compiler runs inside an exempt session. In-process subagent work is not cgroup-observable, so Root attests the drain. |
| `exclusive.no-other-native-unit`, `exclusive.no-v3-pilot` | No other `fsgg-native-*` unit in any manager, and no active `v3-storage@*` |
| `memory.available` | `MemAvailable` ≥ 26 GiB (Root decision 3) |
| `pool.mounted-identity`, `pool.free-space`, `pool.no-data-writers` | `/srv/v3-pool` is label `systemadmin-v3`, has the run's need plus 64 GiB free, and no writer holds files on it |
| `commit-root.ready` | `/srv/bulk/native-chain` is 0700 and owned by developer, or is absent and creatable |
| `io.hdd-devices-resolved` | Each HDD-backed mount resolves to a whole-disk major:minor |
| `io.caps-bound`, `io.no-stale-caps` | Root bound the caps, and no earlier window left a cap behind |
| `services.cap-targets-resolved` | The store and LEARN units (and any discovered SC2 unit) resolve to a manager |
| `capacity.host-partition` | The Home df input is present, consistent, fresh, and holds the need plus 64 GiB |
| `image.stock-digest` | The stock image hashes to the lock pin |
| `dotnet.runtime` | 10.0.12 is installed (M1 prerequisite) |

Launch adds these checks:

- `scope.bounded`: the front end runs in its own scope with finite limits.
- `io.cap-readback`: kernel `io.max` equals the bound caps on every running target.
- `clock.pre-start`: the guest starts within the pre-start cut, on fdev monotonic time.
- `clock.enclosing`: the GitHub `Date` header minus the start witness, plus the remaining
  reserved clocks, stays within the enclosing bound.

## Window effects, in order

1. Create the claim, then post the start witness comment on #4778.
2. Re-read capacity and exclusivity.
3. Create the run directory, then write the caps manifest.
4. Apply `io.max` caps through `systemctl set-property --runtime`.
   - Targets are the drained lane units and the fsgg-store, LEARN and SC2 service units, on
     the HDD-backed devices only.
   - Never the whole `user-1000.slice`, an exempt session or the launcher (Root decision
     6527516917).
   - Developer lanes use the user manager. Other users go through
     `sudo -n systemctl --user --machine=<user>@.host`. System units use `sudo -n systemctl`.
5. Set the slice properties: `fsgg-native.slice` `AllowedCPUs=8-11 MemoryMin=18G
   MemoryMax=19G TasksMax=512`, and `fsgg-native-collect.slice` `MemoryMax=1G`.
6. Verify the stock image and the read-only disk digests.
7. Prepare `root.raw`: `fallocate` it in full, then `qemu-img convert -n --target-is-zero`
   into it.
8. Check the pre-start cut and the enclosing bound.
9. Start the guest: a `systemd-run` transient system service in `fsgg-native.slice`, run as
   developer.
   - Unit properties: `Type=exec`, `ExitType=cgroup`, `RemainAfterExit=yes`, `Restart=no`,
     `CollectMode=inactive`, `OOMPolicy=kill`, `KillMode=control-group`,
     `RuntimeMaxSec=<useful>`, `TimeoutStopSec=<retire>`, `MemoryMax`, `TasksMax`.
   - `ExecStop` sends QMP `system_powerdown`, then waits for `$MAINPID`.
   - QEMU options:
     - `-nic none`, `-mem-prealloc`, a fresh `-uuid`;
     - every disk with `cache.direct`, `aio=native`, `discard=ignore` and
       `detect-zeroes=off`;
     - a read-only `vvfat` CIDATA seed.
   - No `MemoryOOMGroup` is set.
10. Observe the guest's terminal state, bounded by useful + retire + 60 s.
11. Run the collector unit in `fsgg-native-collect.slice`. It hashes `root.raw` and writes
    the closed packet.
12. Window exit, on every path including failures:
    1. remove the caps and read back their absence;
    2. revert the two native slices;
    3. take the after-window `io.stat` and PSI;
    4. list the lane units that appeared uncapped during the window;
    5. write the evidence document.

Retirement, deletion of the run disks, and the independent readback stay with Root and the
readback actor, as the plan describes.
