# GS2-08.2 initializer and monitor

This source window adds the missing operational machinery without performing a live operation. The initializer
binds Authority repository `1351660651`, fleet ref `refs/heads/fsgg/v2/journal/cutover/d5`, an immutable
`operating-v1` phase tag, desired policy, two-pass provider evidence, manifest/trust identities, cutover App and
installation, control issue `#2`, and a short-lived RSA-PSS authorization. Genesis uses an expected-absent lease;
the input pins the independent authorizer key ID and public-key digest, and the supplied public key must match both.
later tooling must use an exact expected-parent lease. Stored event/head/tree/commit bytes receive real Git object
IDs. The observation envelope is produced only after readback, has `parent: null` for generation one, and is never
substituted for stored journal bytes. Lost responses require exact reread; competing refs refuse.

The monitor is deliberately one-shot. Run the repaired capture first, then ingest it into a private host store:

The thin initializer CLI has distinct modes. `plan` consumes a signed authorization and public trust key and
emits only a sealed object plan. `apply` requires `--credential-fd N` with `N >= 3` and delegates the typed plan
to a separately reviewed transport executable; `verify` delegates readback without a credential. For example:

```bash
dotnet run --project src/FS.GG.Coordination.Cli -c Release --no-build -- \
  ledger-protection initialize plan --input input.json --authorization authorization.json \
  --public-key trusted-authorizer.pem --output plan.json
```

Generate the exact bytes that protected authorization must bind before requesting approval:

```bash
dotnet run --project src/FS.GG.Coordination.Cli -c Release --no-build -- \
  ledger-protection initialize payload --input input.json --output initializer-payload.json
sha256sum initializer-payload.json
```

`eng/github-ledger-operation.py derive` produces reviewable canonical `initial-manifest/v1`, `initial-trust/v1`,
and initializer-input bytes. The manifest is derived from the accepted GS2-08.1 receipt and epoch-wire digest,
exact source commit/tree, bound desired policy, fleet address, and the independent authorizer's canonical public-key
SPKI fingerprint. The trust document binds that manifest and the same public signer identity. It contains no private
key. Neither digest may be selected ad hoc.

Protected authorization is the manual-dispatch workflow in `FS-GG/.github` anchored by merge
`c00b4636688f95024b80c588d2410ca40e11f6e6`; its canonical workflow bytes have SHA-256
`a778801d66751c3890826b0ca015a81758f7733ff09e5b9c5bde55e1f86c3b8f`. The initializer binds both values.
The workflow's only job targets the existing `fleet-cutover` environment. Its artifact binds the exact Coordination source and canonical initializer-payload
SHA-256. `github-ledger-operation.py authorize` rereads the final Actions run and approval history, accepts only an
eligible configured reviewer, enforces the two-hour ceiling, and then uses a distinct authorizer key supplied on an
inherited descriptor to sign those exact bytes with RSA-PSS. The command also requires the same bounded operation ID
recorded by the protected receipt and the canonical SPKI SHA-256 of the supplied public key. General user authorization, a successful unrelated
run, or an artifact without native approval readback is insufficient.
The verifier also requires the workflow-run head to descend from that anchor and rereads the workflow file at the
run head; changed workflow bytes refuse authorization even when the environment approval itself succeeded.

The reviewed transport is `eng/github-ledger-initialization-transport.py`. It mints a repository-scoped cutover-App
token from an inherited key descriptor, never prints or retains the token, recomputes every Git object ID, refuses
competing refs, and independently rereads objects, branch, and tag. Verification is anonymous/read-only and refuses
a credential argument:

```bash
dotnet run --project src/FS.GG.Coordination.Cli -c Release --no-build -- \
  ledger-protection initialize apply --plan plan.json \
  --transport "$PWD/eng/github-ledger-initialization-transport.py" --credential-fd 4
dotnet run --project src/FS.GG.Coordination.Cli -c Release --no-build -- \
  ledger-protection initialize verify --plan plan.json \
  --transport "$PWD/eng/github-ledger-initialization-transport.py"
```

```bash
python3 eng/capture-github-ledger-protection.py --output "$private_dir/capture.json"
python3 eng/monitor-github-ledger-protection.py --store "$private_dir/monitor" \
  --capture-file "$private_dir/capture.json" --now "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
```

The store requires a mode-0700 non-symlink directory and uses SQLite WAL with FULL synchronization. It owns runs,
observations, checkpoints, incidents, an alert outbox and heartbeat; compressed raw captures are content addressed.
Unknown, incomplete, stale (>15 minutes), drift, corruption, quota exhaustion, or storage failure returns nonzero.
The monitor never repairs. An external timer may invoke it every five minutes, and an external watchdog must alert
when the heartbeat is older than 15 minutes. Installing that timer, alert destination, credentials, trust manifest,
or initializing live refs remains a later reviewed operation.

The version-1 database schema is owned by the monitor:

| Table | Identity | Purpose |
|---|---|---|
| `schema_info` | version | Refuse unsupported migrations. |
| `runs` | deterministic run ID | Start/finish, outcome, payload and finding for every invocation. |
| `observations` | payload SHA-256 | Capture time, raw/normalized set digests, continuity and content-addressed path. |
| `checkpoints` | singleton row | Last successful payload and normalized state used for drift comparison. |
| `incidents` | content-derived incident ID | Open/resolved failure state without duplicating identical incidents. |
| `outbox` | content-derived alert ID | Durable at-least-once handoff to a separately configured alert delivery worker. |
| `heartbeat` | singleton row | Latest run, time and final outcome for an external freshness watchdog. |

Concurrent invocations use SQLite's busy timeout, retry connection initialization, and serialize state changes with
`BEGIN IMMEDIATE`. Payload publication uses a unique temporary file followed by atomic replacement. A later operator
may use an external five-minute timer, but this source window intentionally does not install or enable one:

```ini
[Timer]
OnCalendar=*:0/5
Persistent=true
```

Use a wall-clock calendar trigger because a `Type=oneshot` service need not enter an active state that can anchor
`OnUnitActiveSec`. Stagger a separate watchdog at `OnCalendar=*:2/5` so it does not race the monitor's capture or
store transaction. The associated service must first create a fresh, complete capture and then invoke the one-shot
monitor. The watchdog must query `heartbeat.observed_at` and alert if it is more than 15 minutes old; successful
process exit alone does not prove monitoring freshness. Alert delivery updates `outbox.delivered_at` only after the
destination confirms delivery. Neither the monitor nor the watchdog performs automatic repair.

`eng/github-ledger-monitor-runner.py` is the systemd-independent runner boundary. Its mode-0600 private config names
an absolute source checkout, durable mode-0700 store, stable runner identity, explicit credential commands, and an
alert executable plus destination. `preview` performs no credential or provider access. An external scheduler
invokes `once` every 300 seconds and `watchdog` often enough to enforce the 900-second freshness ceiling. The runner
hands a sanitized alert envelope to the configured executable and acknowledges an outbox row only after exit zero.
Each invocation permits at most three complete two-pass capture attempts, separated by two seconds, so a transient
inconsistent provider listing does not create a monitoring gap. Every refused capture is retained as canonical-input
SHA-256-addressed private gzip evidence (at most 64 files). Three failed attempts still refuse the invocation; the
watchdog then exposes a persistent capture failure through the unchanged heartbeat rather than masking it.

The runner deliberately does not pretend an ephemeral container is durable. Installation evidence must name the
external scheduler/host, prove it survives container recreation, exercise the configured alert destination, and
record a `fsgg.github-ledger-external-runner-receipt/1` with `durable:true`, interval 300, watchdog 900, store ID,
runner ID, and `alertExercise:"delivered"`. Until that receipt exists, `MonitoringReady` remains unknown.

`LedgerOperationalEvidence.derive` is the only path from signed run evidence to SettingsApplied,
AppCustodyReady, FleetInitialized and MonitoringReady. Evidence binds exact inputs, authority, signer, run, time and
expiry, including the independently expected signer public-key digest; missing, duplicate, stale or invalid
signatures stay unknown. Caller-supplied booleans are not evidence.
