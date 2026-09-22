# Prospective telemetry runtime receiver

Coordination selects the published `FS.GG.Coord.Cli` `0.91.4` dotnet tool through the repository-root
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

The launcher owns expected dispatch and lineage facts, but it cannot authorize a cross-item original/member
relationship from a private assignment. For distinct roadmap member items that share one canonical original,
first merge each exact member/original mapping into the protected
`.github/docs/coordination/telemetry-original-item-assignments.json` registry. Before each member's root
launch, invoke the protected work-roadmap adapter and require the exact applied result:

```console
python3 /path/to/protected/.github/tools/roadmap-telemetry.py \
  population-only --feature <feature> --item <member> --original-item <original>
# {"schema":"fsgg.telemetry.original-binding-result/1","status":"applied"}
```

The operation requires a receipt-scoped workspace, distinct member and original identities, exactly one matching
registry assignment, and read access to the immutable protected `.github/main` revision. It persists private
mode-`0600` retry state before submission and replays the exact batch after an unknown response. A changed retry,
malformed state, unavailable protected revision, or missing or duplicate registry entry is a refusal. An
unapplied or unknown receipt cannot return the applied result and retains the exact pending batch for retry. The
successful operation publishes only the deterministic open `budget-population` fact; it does not create a second
expected dispatch. A conflicting source population can be retained as evidence, but the store keeps the member
open. The private root assignment's `itemId`
must equal that member, and descendants retain the member through the inherited launcher context. Do not also
invoke the roadmap adapter's `begin`, `started`, or `finish` operations for launcher-owned invocations.

A member becomes complete only after its native item outcome is settled and every launcher-owned expected
dispatch for that member has one joined lineage and terminal. A canonical two-member original is therefore
complete only when both protected population facts are present and both distinct member items meet that rule.
Missing authorization, a non-applied population receipt, conflicting original claims, or an unsettled dispatch
keeps the affected member and the shared original open.

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

A `runtime-terminal` with outcome `completed` describes the observed Codex process only. It does not assert that
the work item was delivered, close the whole-item population, or create a `native-item-outcome`. Those facts require
the separately corroborated routine or orchestration delivery readback for the same item. Consequently, a private
`item-detail/2` read can correctly show terminal native usage while its item outcome and population remain missing.
When a protected roadmap mapping has already opened a canonical member population, the same process completion
leaves that population open; it becomes completed only after the machine delivery outcome is joined.

`tests/telemetry-runtime-receiver/run.sh` restores the exact public package, checks its installed version, executes
the real packaged adapter around a controlled Codex process, proves exact argument forwarding, injects observer
loss, verifies unchanged native success and failure statuses, and checks that process completion does not fabricate
delivery or whole-item completion.
