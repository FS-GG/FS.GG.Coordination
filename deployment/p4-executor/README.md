# fdev P4 executor account

`provision-fdev.sh` creates the dedicated P4 executor on fdev. The account is fixed by the
[P4 amendment](../../docs/roadmaps/v2-lang-portable-integration.md#p4-facility-and-receiver-bound-grant-amendment--2026-10-30)
of the portable-integration plan. Root/fdev made fdev, not Home's image definition, the source of the account in
[FS-GG/.github#4730 decision 7](https://github.com/FS-GG/.github/issues/4730#issuecomment-6551301543). That
replaces the Home image request [FS-GG/.github#5078](https://github.com/FS-GG/.github/issues/5078).

```sh
sudo deployment/p4-executor/provision-fdev.sh --evidence DIR   # provision a fresh host, or no-op; then read back
sudo deployment/p4-executor/provision-fdev.sh --check          # read back only; changes nothing
```

## What it creates

| Item | Value |
| --- | --- |
| Account | `p4executor`, uid 32001, gid 32001, own group only, password locked, shell `/usr/bin/nologin`, home `/p4/runtime-v1/home` (not created by `useradd`) |
| Subordinate ids | `p4executor:2000000:65536` in both `/etc/subuid` and `/etc/subgid` |
| `provider_runtime.REQUIRED_DIRS` | `/p4` and its eight runtime and evidence directories, 32001:32001, mode 0700 |
| Receiver | `/srv/p4-receiver`, empty, 32001:32001, mode 0700 |
| Grant directory | `/etc/fsgg/portable-workspaces`, root:root, mode 0755, no extended ACL |

`useradd` runs with `-K SUB_UID_COUNT=0 -K SUB_GID_COUNT=0`, because fdev's `login.defs` would otherwise
allocate a range of its own choosing. The planned range is then added with `usermod --add-subuids/--add-subgids`.

The script does not create the runtime preparer's `containers.conf` or `storage.conf`. The resolver writes them,
and it refuses existing files whose bytes differ from what it generates.

The receiver is owned by the account because the resolver runs `git` as the account with system and global
configuration disabled. Without a `safe.directory` entry, git's ownership check would refuse a root-owned
checkout.

## Checks

`verify_p4executor.py` loads `tests/portable-workspace/trusted-provider/provider_runtime.py` and applies its own
functions. Its `preflight` and `collisions` modes print one JSON document each, and `readback` prints the
document recorded as evidence:

- **`preflight`**, before anything is created. `account_plan` must pass on the live `/etc/passwd`,
  `/etc/subuid` and `/etc/subgid`. This is the only point at which it can pass on the live files, because it
  refuses an existing name, uid or gid.
- **`collisions`**, when the account already exists. `account_plan` must pass with only `p4executor`'s own rows
  removed.
- **`readback`**, after provisioning:
  - On the live files, `account_plan` now refuses, because the identity exists. With only the account's own
    rows removed, it still passes, so the result collides with nothing else.
  - Measured through `runuser`, the way `collect_provider_capability.py` measures the account, `id -u`,
    `id -g` and `id -nG` must equal the plan, and the groups must be the account's name alone.
  - The account must be listed as a member of no other group, and its password must be locked. The password
    field is read but never printed.
  - `getsubids` must report exactly `2000000 65536` for uids and for gids.
  - Every `REQUIRED_DIRS` entry, and the receiver, must pass `capability()`'s directory predicate: a real
    directory at uid 32001 and gid 32001, mode `0700`.
  - `/etc/fsgg` and `/etc/fsgg/portable-workspaces` must pass the trusted enrollment source's directory
    predicate: not a link, owned by root, not writable by group or others, and no extended ACL.
  - No grant file exists.

The script refuses rather than repairs, and every check runs before the first change. Exactly two states pass:

- **fresh:** no account, group, uid, gid or subordinate row exists, `account_plan` passes, and no account-owned
  path exists. Everything is created, and only newly created paths are `chown`ed.
- **provisioned:** everything is already exactly as planned, and the run changes nothing. That covers the
  account's uid, gid, home, shell, sole group and locked password, both subordinate rows, and every directory's
  owner and mode.

Anything else is refused without a change: a partial or drifted account, a wrong or foreign-owned directory, a
link where a directory belongs, a non-empty `/srv/p4-receiver`, or an extended ACL.

## What it never does

It never writes `/etc/fsgg/portable-workspaces/python-hello-v1.json`. Installing the receiver-bound grant is a
separately admitted protected effect, which follows the candidate-facts and private-facts overlay steps and the
enforcing profile ([FS-GG/.github#4650](https://github.com/FS-GG/.github/issues/4650)). The script also runs no
container and creates no podman state.

## Persistence

On fdev, `/etc`, `/p4` and `/srv` live on the root disk, which a rebuild replaces. After any root-disk rebuild,
run the script again and record a fresh readback before anything relies on the account.
