# Direct-session telemetry producer boundary (proposal)

Status: source-only design, 2026-09-25. No direct interactive session producer is
installed or activated by this document.

## Existing observation paths

The [repository launcher](telemetry-runtime-receiver.md) calls the packaged
`telemetry runtime codex-exec` adapter. That adapter starts a future Codex child,
binds a private assignment before launch, observes its JSON event stream, and
preserves its native exit status. The [orchestration runner](../../src/FS.GG.Coordination.Orchestration.Runner.Client/README.md)
has a separate `executor-stdio` path: its observer journals native completed
turns and queues compact telemetry batches for the selected WorkItem. Neither
path attaches to a Codex process that this repository did not launch.

The approved `fdev-telemetry exec` wrapper supplies private telemetry credentials
to a command. It does not, by itself, bind the current interactive Codex thread
to a WorkItem, observe native turns, or submit turn facts. Running a direct
`codex --yolo` session through that credential wrapper would therefore leave the
same observation gap. A healthy Host and an empty workspace queue establish
transport readiness, not a captured turn or an applied batch.

## Proposed producer interface

A direct-session producer needs an explicit host-supported observation interface
for the **current** interactive session. Its exact API and packaging remain to be
selected. A future implementation must satisfy these boundaries before it can
claim coverage:

1. **Bind before the first observed turn.** Consume a private, owner-approved
   assignment containing the exact workspace, repository, WorkItem persistence
   ID, issue identity, attempt, generation, invocation, producer and observation
   window. Verify the live native thread identity against that assignment. Reject
   absent, stale, conflicting or duplicate bindings. An already running session
   can begin only a new prospective window; earlier turns remain a gap.
2. **Read native facts.** Receive native thread and turn starts and each
   `turn.completed` counter from a supported authenticated Codex event surface.
   Preserve the native thread ID, native turn ID where supplied, invocation-local
   sequence, input, cached input, output, reasoning, total, provider/model
   identity, timestamp and counter provenance. Unknown counters and missing
   native identities remain explicit gaps. Never derive a turn from elapsed time,
   a session total, the current transcript, or a model's text answer.
3. **Persist one bounded observation journal.** Key records by the exact
   workspace, item, attempt, invocation and native turn identity. Record
   prospective activation, expected dispatch and lineage before any start;
   journal observations before publishing them. Exact retries are idempotent;
   changed bytes under one identity refuse. Keep prompts, reasoning content,
   tool output, diffs, paths and raw event frames out of telemetry batches.
4. **Settle through the Host.** Submit digest-bound batches using the selected
   workspace association. Retain a batch while its outcome is queued,
   durably received or unknown. Count it as applied only after an authenticated
   Host receipt or receipt readback matches that batch, producer, workspace and
   item. Then verify the joined facts through private `item-detail/2`, including
   its revision and observation time. Queue zero alone cannot establish this
   match. Any publication loss stays a coverage gap even if a later retry applies.
5. **Keep delivery separate.** Record a native process terminal and usage
   coverage independently of issue delivery. The producer must not mint a native
   item outcome, change a board row, acquire a claim, or resume an orchestration
   route. Unsupported session hooks, missing assignment or an unverifiable Host
   receipt produce an incomplete-evidence verdict without a guessed zero.

The current runner's [`TelemetryFactBatches`](../../src/FS.GG.Coordination.Orchestration.Runner.Client/TelemetryFactBatches.fs)
illustrate compact `runtime-start`, `runtime-turn-usage`, `runtime-gap` and
`runtime-terminal` facts. Its [`TelemetryCliPublisher`](../../src/FS.GG.Coordination.Orchestration.Runner.Client/TelemetryCliPublisher.fs)
distinguishes applied from durably received and unknown submission outcomes.
Those types are useful source references, but the runner's WorkItem command and
executor authority do not apply to a direct session.

## Source-only acceptance scaffold

| Controlled input | Required verdict |
| --- | --- |
| Exact assignment, native thread/turn starts and completed-turn counters, matching applied receipt and private item readback | Complete for that prospective invocation window only |
| Credential wrapper and healthy workspace, with no native event hook | Unsupported; no captured-turn claim |
| Existing thread attached after earlier turns | Earlier coverage gap retained; only later turns may be evaluated |
| Native turn without exact workspace/item/attempt binding | Refused; no event submission |
| Duplicate turn identity with changed counters | Conflict; retain original evidence and mark coverage incomplete |
| Successful native exit with no completed-turn counters | Terminal observed; usage gap |
| Batch queued or durably received without applied Host receipt | Pending application; no completeness claim |
| Applied receipt for another batch, producer, workspace or item | Receipt mismatch; no completeness claim |
| Queue zero but private item readback omits the turn | Incomplete joined evidence |

A future test must exercise these cases against the real interface and Host
readback. This design does not authorize a synthetic ingest batch or a direct
session launch. End-to-end runner acceptance separately requires one genuine
WorkItem from an authenticated schedulable-board read and the ordinary runner's
admission, enrollment and permit checks.
