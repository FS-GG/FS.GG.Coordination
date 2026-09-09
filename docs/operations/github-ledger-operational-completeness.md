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
OnBootSec=5m
OnUnitActiveSec=5m
Persistent=true
```

The associated service must first create a fresh, complete capture and then invoke the one-shot monitor. A separate
watchdog must query `heartbeat.observed_at` and alert if it is more than 15 minutes old; successful process exit alone
does not prove monitoring freshness. Alert delivery updates `outbox.delivered_at` only after the destination confirms
delivery. Neither the monitor nor the watchdog performs automatic repair.

`LedgerOperationalEvidence.derive` is the only path from signed run evidence to SettingsApplied,
AppCustodyReady, FleetInitialized and MonitoringReady. Evidence binds exact inputs, authority, signer, run, time and
expiry, including the independently expected signer public-key digest; missing, duplicate, stale or invalid
signatures stay unknown. Caller-supplied booleans are not evidence.
