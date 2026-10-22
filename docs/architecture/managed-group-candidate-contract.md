# Managed-group candidate contract

`fsgg.managed-group-contract/1` is the canonical supplier contract that the Templates
browser caller binds at its Chromium support/runtime join
([FS-GG/.github#4710](https://github.com/FS-GG/.github/issues/4710), C-J1). It is
delivered by [#1046](https://github.com/FS-GG/FS.GG.Coordination/issues/1046) under
[#1026](https://github.com/FS-GG/FS.GG.Coordination/issues/1026). This page records
the inventory behind it, the design choice, the exact contract and what remains open.
Source qualification here is not hosted Templates runtime qualification, publication
or installed acceptance.

## Inventory

**Shared process owner.** `eng/shared_process_owner.py` owns one direct child by
numeric process group. It reads PID/start/group from `/proc`, follows descendants by
parent links, refuses when a known descendant leaves the group
(`observed descendant escaped owned process group`) and calls cleanup
`observed-empty-group` only when the group and escapes are empty. RSS is sampled; there
is no hard whole-tree containment. Its bytes are hash-bound by the bootstrap
qualification plan and the choreo diagnostics harness, so this contract does not
change it.

**BCL sessions.** `FS.GG.Execution.Processes` (V2-PROC-01.3) wraps `System.Diagnostics.Process`.
Its README states that stable birth identity, descendant observation/containment,
aggregate limits, whole-workload emptiness, reaping and owner-survival cleanup refuse
before launch. `BestEffortProcessTree` traverses the retained BCL tree and cannot find
re-parented or detached descendants.

**#1026 containment evidence.** #1026 keeps "Linux containment" as a separate open
remainder. Its accepted source slices (#976/#977/#1029/#1031/#1039/#1045) concern
publication, trace capture and admission; none supplies descendant containment. The
progress report of 2026-10-14 mentions an earlier managed-group checkpoint
`5f7094bf7c9dc437f0bffb372993312e00367d60`; it does not exist in any FS-GG repository or
local clone checked, so nothing is reused from it.

**Templates caller.** At protected `f5fbd06e` (still current in
`.github/workflows/fable-reference-public.yml`), the hosted WASM job is disabled by
literal `if: false` and `main()` raises
`managed-group candidate/profile contract not yet bound`. The provisioning code takes an
injected `execute(argv, cwd, env) -> stdout` and runs the Rust installer, `dotnet`,
Fable, `npm ci` through a `#!/bin/sh` wrapper and a Node script that validates browsers.
Stock Playwright 1.63 starts browsers and the Vite web server in **detached process
groups**, which the strict shared owner must refuse. The caller already pins source,
archive and tool digests, builds a fresh closed environment, gives each phase an
absolute monotonic deadline with 30 s cleanup reserves, and refuses work under an
active `SIGALRM` watchdog only during acquisition. It needs: an API, a profile schema,
a source identity to pin, an executable/birth/root/argv/retirement profile, stable
descendant containment and closed refusal semantics.

## Design choice

Containment is a **delegated cgroup v2 leaf**, birth identity is a **pidfd**, and
retirement is the kernel's **`cgroup.kill`**.

| Option | Contains setsid/double-fork | Retires atomically | Available on hosted ubuntu runner | Available in the local dev container |
|---|---|---|---|---|
| Process group (current owner) | No: refused | No | Yes | Yes |
| BCL tree traversal | No | No | Yes | Yes |
| Child subreaper + `/proc` walk | Observable, not contained | No (fork race) | Yes | Yes |
| PID + user namespace | Yes | Yes (init death) | Unknown: Ubuntu 24.04 may restrict unprivileged user namespaces | Yes |
| **Delegated cgroup v2** | **Yes** | **Yes (`cgroup.kill`)** | **Yes: systemd PID 1 and non-interactive sudo** | **No: refused** |

The consumer runs on GitHub-hosted Ubuntu, which has systemd as PID 1, cgroup v2 and
non-interactive sudo, so a transient unit with `Delegate=yes` is available. A cgroup
also enforces `pids.max` and `memory.max` instead of sampling, and its membership does
not change identity the way a PID namespace does. The local development container
(catatonit PID 1, root-owned cgroup namespace root, no sudo) cannot delegate a cgroup,
so the owner refuses there with `containment-unavailable: delegated-root-invalid`. That
is the intended behavior, not a fallback. The module is a sibling of the shared owner,
not an edit of it, and keeps the same receipt vocabulary.

## Contract

Source identity: repository `FS-GG/FS.GG.Coordination`, path
`eng/managed_group_owner.py`, the merge commit of the delivering PR, and that file's
SHA-256. The module is one file using only the standard library (Python 3.10+ on Linux), so one
`(commit, path, sha256)` pin is its complete closure. Each receipt repeats the owner
path and SHA-256 it ran.

API:

- `probe(delegated_root=None) -> dict`: reports whether containment can be established, without
  changing anything. `delegated_root` defaults to the caller's own cgroup (or its parent when
  the caller is already in `fsgg-supervisor`).
- `validate_profile(profile) -> (profile, sha256)`: checks the closed schema below.
- `run(profile, *, env, deadline, out_dir, delegated_root=None, control=None) -> receipt`:
  either returns a receipt with `outcome: succeeded`, raises `Refused` (`code`, `detail`,
  `receipt`) with nothing launched, or raises `GroupFailed` (`receipt`) after a launch
  attempt. `deadline` is the caller's absolute `time.monotonic()`. `control` is a
  test hook and must be `None` in production.
- CLI: `managed_group_owner.py probe` (exit 0 or 3) and
  `managed_group_owner.py run --profile FILE --out-dir DIR --deadline-seconds N`.

Profile `fsgg.managed-group-profile/1` uses closed keys at every level:

| Field | Meaning |
|---|---|
| `name` | `[a-z0-9][a-z0-9.-]{0,63}` |
| `root` | Absolute, symlink-free private root. `cwd` and `out_dir` must be inside it |
| `executable` | `{path, sha256}`. The path must be absolute, symlink-free and a regular executable file. It is opened `O_NOFOLLOW`, hashed from the open descriptor and exec'd through that descriptor (`/proc/self/fd/N`), so the hashed bytes are the bytes run |
| `leaderImages` | 1–8 SHA-256 values that the leader's `/proc/<pid>/exe` may show at birth (the ELF itself, or the interpreter plus exec successors for a script wrapper) |
| `argv` | Exact argument vector, 1–256 entries, no control characters |
| `cwd` | Absolute, symlink-free directory inside `root` |
| `environmentKeys` | Sorted, exact key set of `env`. Values are not bound |
| `containment` | `cgroup2-delegated` |
| `limits` | `pidsMax` (null or 1–65536), `memoryMaxBytes` (null or at least 1 MiB; also sets `memory.swap.max=0`), `stdoutBytes`, `stderrBytes` (exact caps) |
| `retirement` | `onLeaderExit` (`require-empty` or `retire-members`), `leaderExitSettleSeconds` (0–30), `termGraceSeconds` (0–60, less than cleanup), `cleanupSeconds` (0.1–120) |

The profile's identity is the SHA-256 of its canonical JSON, recorded as `profileSha256`.

Lifecycle:

1. **Refusals before launch.** `profile-invalid`, `out-dir-invalid`, `root-invalid`,
   `deadline-invalid`, `deadline-exhausted`, `owner-not-single-threaded`,
   `owner-alarm-active`, `environment-mismatch`, `cwd-invalid`, `executable-invalid`,
   `executable-identity-mismatch`, `containment-unavailable:<probe reason>`,
   `delegated-root-not-exclusive`, `limit-unenforceable:<controller>`. A refusal
   receipt is written once `out_dir` exists.
2. **Root and leaf.** The delegated root must be a writable `domain` cgroup that
   contains only the owner process and no other child cgroups. The owner moves itself
   to `fsgg-supervisor`, enables any requested controllers and creates a fresh leaf
   `fsgg-mg-<invocationId>`.
3. **Birth.** The forked child joins the leaf and sets `no_new_privs` before exec. The
   leader is the owner's unreaped child, so `pidfd_open` binds that exact birth. The
   receipt records PID, start ticks, pidfs inode, cgroup, `NoNewPrivs`, cmdline and the
   SHA-256 of `/proc/<pid>/exe`. An image outside `leaderImages`, an unobservable image
   of a live leader, a leader outside the leaf, or a leader without `no_new_privs` fails.
   A leader that exits before observation is recorded as `unobserved-leader-exited`;
   no image claim is made.
4. **Supervision.** Every 50 ms the owner records leaf members by (PID, start ticks)
   and reads the delegated root. Session or group detachment stays inside the leaf and
   is not an escape. A process anywhere in the delegated root outside the leaf (other
   than the owner in `fsgg-supervisor`), or a known member alive outside the root, is
   an **escape**: the run fails with `contained member escaped managed group` and the
   process is killed. Output goes through pipes with exact byte caps.
5. **Leader exit.** After the settle window, live members either fail the run
   (`require-empty`: `leader exited with live contained members`) or are recorded and
   retired (`retire-members`). A non-zero exit or signal fails the run. Reaching the
   deadline fails with `original deadline exhausted`.
6. **Retirement (always).** The owner sends SIGTERM to leaf members and SIGKILL to
   escapes, each through a pidfd whose start ticks are re-checked after opening. After
   `termGraceSeconds` it writes `cgroup.kill`, waits for `populated 0`, reaps the
   leader, drains streams to EOF and removes the leaf and any cgroups members created,
   retrying EBUSY. Everything is bounded by `min(stop, deadline) + cleanupSeconds`.
7. **Result.** `cleanup` is `observed-empty-removed`, `observed-empty-remove-failed` or
   `unknown`. `custody` is `contained`, `escaped-retired`, `escaped-unretired` or
   `no-child`. Anything other than `observed-empty-removed` fails the run even if the
   command succeeded (`cleanup unqualified: …`). Hook and report failures never replace
   the first cause; they go to `additionalFailures`. `receipt.json`, `stdout.log` and
   `stderr.log` are written to `out_dir`.

Boundaries that this contract does not claim:

- Supervisor-death cleanup belongs to the enclosing unit (`KillMode=control-group`),
  and the receipt says so (`ownerDeathCleanup: delegated-unit-not-claimed-by-owner`).
- Containment holds against unprivileged members. `no_new_privs` and the unit's
  `NoNewPrivileges=yes` stop members from gaining privilege through sudo or setuid.
- Members can use a pidfd to signal only processes they could already signal; stock
  Playwright's own negative-PID group kill stays the caller's accepted residual.
- A member that forks and exits faster than the 50 ms census is retired by
  `cgroup.kill` but may be missing from `members.sample`. `observed` counts sightings,
  not every birth.
- The owner must be single-threaded during `run` (it uses `preexec_fn`) and must not
  run under an active `ITIMER_REAL`.

## Hosted bootstrap

The caller's step runs its Python in a fresh delegated unit, as the runner user:

```bash
sudo -n systemd-run --quiet --wait --pipe --collect --service-type=exec \
  --unit="fsgg-mg-templates-${RUN_ID}-${RUN_ATTEMPT}" \
  -p Delegate=yes -p User="$(id -un)" -p Group="$(id -gn)" -p NoNewPrivileges=yes \
  -p KillMode=control-group -p TimeoutStopSec=5 -p TasksAccounting=yes -p MemoryAccounting=yes \
  -p RuntimeMaxSec=2400 -p WorkingDirectory="$PWD" \
  --setenv=EVENT="$EVENT" … -- /usr/bin/python3 - <<'HOSTED_WASM'
```

`eng/tests/managed-group-owner/delegated-unit.sh` is the maintained form of this
recipe, and the CI controls use it. Environment variables must be passed explicitly
with `--setenv`. Where the host has no systemd or no non-interactive sudo, `probe()`
refuses.

## What the Templates caller binds

1. Pin `OWNER = (FS-GG/FS.GG.Coordination, <merge commit>, eng/managed_group_owner.py, <sha256>)`.
   Fetch it through the existing closed `get()` (host `raw.githubusercontent.com`),
   check the SHA-256 and import it with `importlib.util.spec_from_file_location`.
2. In `main()`, before acquisition, call `probe()` and refuse if it is unavailable.
   Record the probe result.
3. Replace the injected `execute(argv, cwd, env)` with a function that builds one
   profile per call: `executable` = `argv[0]` with its reviewed SHA-256 (the caller
   already pins or derives these), `leaderImages` (for the `npm` wrapper, the
   `/bin/sh` target plus the Node binary), exact `argv`, `cwd`, `environmentKeys = sorted(env)`,
   `root = <job root>`, `require-empty`, `cleanupSeconds = BUDGET["cleanup"]`. Call
   `run(profile, env=env, deadline=phase_deadline(...), out_dir=<fresh dir under root>)`
   and return `stdout.log`. Retain every receipt. On `Refused` or `GroupFailed`, keep
   the receipt and raise its first cause.
4. Run the browser custody pilot and installed C as one managed group per Playwright
   invocation. Detached browser and Vite groups are contained members. Use
   `require-empty` so a stock shutdown that leaves members behind is a recorded failure,
   not a silent retirement.
5. Lifting `if: ${{ false }}`, the inline refusal and the upload gate stays with C-J1
   review and the root's admission. This contract grants no runtime authority.

## Qualification

`eng/tests/managed-group-owner/run-ci.sh` runs in the required `compiler-and-tests`
gate:

- **Refusal controls** (8) run everywhere and verify that nothing is launched.
- **Containment controls** (14) run in a fresh delegated unit and are required on
  GitHub-hosted runners. They cover success with birth binding, `no_new_privs` and
  exact streams; a setsid/double-fork descendant under both policies; escape to a
  sibling cgroup and to the supervisor leaf; early non-zero and zero exits; a deadline
  kill of a SIGTERM-ignoring tree; a real EBUSY removal failure that is reported, not
  hidden; a hook failure that does not replace the first cause; output caps; a
  non-admitted leader image; refusal for a non-exclusive root; `pids.max`
  enforcement; and descendants started by a BCL `System.Diagnostics.Process` session
  (`dotnet fsi`) that detach with setsid.
- **Owner-death control.** The owner SIGKILLs itself mid-run, and the delegated unit
  must still retire the SIGTERM-ignoring member.
- **Mutation proof** (`mutation_proof.py`): 19 single-fragment mutants. Each must fail
  its named controls while the unmutated owner passes them. Four refusal mutants also
  run locally.

Unknowns: hosted Templates runtime behavior (browser support libraries, stock shutdown
and the 27-case journey); the performance cost of hashing large leader images at birth;
and cgroup behavior on hosts other than GitHub-hosted Ubuntu. These stay with
C-J1/#4710 and the #1026 remainder.
