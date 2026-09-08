# Prospective telemetry runtime receiver

Coordination selects the published `FS.GG.Coord.Cli` `0.87.0` dotnet tool through the repository-root
manifest. `eng/codex-exec.sh` is the repository-owned launch and dispatch boundary for future Codex work:

```bash
dotnet tool restore
eng/codex-exec.sh --assignment /absolute/private/attempt.json -- \
  --json --ephemeral -m MODEL "task"
```

The root assignment is a private mode-`0600` `fsgg.telemetry.codex-assignment/1` document. The host supplies
the approved private store through `FSGG_TELEMETRY_STORE`; the repository neither embeds a host path nor owns a
database process. The installed adapter creates prospective activation, expected-dispatch, invocation-lineage,
event-time and terminal observations. Descendants call the same entrypoint without an assignment and inherit the
closed invocation context; `--relation follow-up` identifies a follow-up instead of the default child relation.

The receiver configuration in `.fsgg/telemetry-runtime.json` is an explicit capability declaration. It permits
only future repository-owned `codex-exec` launches. It does not discover historical sessions, scan transcripts,
import an existing database, start a hosted daemon, mutate a provider, or claim coverage for platform-native
`collaboration.spawn_agent` calls.

`.fsgg/telemetry-ci-attribution.json` also activates the packaged exact-head CI observer. Its initial empty rule
set is deliberate: native run, attempt, job, step and check population is retained, while activity classification
stays explicitly unknown until exact workflow/job/step rules are measured and adopted. Unknown classification is
not redistributed or guessed.

Telemetry is advisory. The adapter tees native output and returns the native Codex exit status. Publication loss,
an unavailable store, a full inbox, writer contention or reconciliation failure remains a diagnostic and cannot
turn native success into failure or hide native failure. Invalid private assignment or missing exact tool bytes is
a pre-launch refusal, so no native delivery has occurred in that case.

`tests/telemetry-runtime-receiver/run.sh` restores the exact public package, checks its installed version, executes
the real packaged adapter around a controlled Codex process, proves exact argument forwarding, injects observer
loss, and verifies unchanged native success and failure statuses.
