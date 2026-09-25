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
5. **Keep delivery separate.** Record a native process terminal only when the
   native source reports one. Closing an observation window is not process exit;
   it leaves a terminal gap if exit was not observed. Track usage coverage
   independently of issue delivery. The producer must not mint a native item
   outcome, change a board row, acquire a claim, or resume an orchestration
   route. Unsupported session hooks, missing assignment or an unverifiable Host
   receipt produce an incomplete-evidence verdict without a guessed zero.

The current runner's [`TelemetryFactBatches`](../../src/FS.GG.Coordination.Orchestration.Runner.Client/TelemetryFactBatches.fs)
illustrate compact `runtime-start`, `runtime-turn-usage`, `runtime-gap` and
`runtime-terminal` facts. Its [`TelemetryCliPublisher`](../../src/FS.GG.Coordination.Orchestration.Runner.Client/TelemetryCliPublisher.fs)
distinguishes applied from durably received and unknown submission outcomes.
Those types are useful source references, but the runner's WorkItem command and
executor authority do not apply to a direct session.

The dormant [`DirectSessionTelemetryFacts`](../../src/FS.GG.Coordination.Orchestration.Execution.Codex/DirectSessionTelemetryFacts.fs)
mapper prepares a completed-turn usage envelope from supplied native facts and
an exact workspace/item assignment. Its tests compare the output shape with the
runner fixture and refuse missing native IDs, invalid counters and mismatched
bindings. The mapper has no current-session event hook, authenticated assignment,
start or continuity journal, publisher, or applied Host receipt. Its output is
structural evidence only and is not an installed producer.

The dormant [`DirectSessionCorrelationHandoff`](../../src/FS.GG.Coordination.Orchestration.Execution.Codex/DirectSessionCorrelationHandoff.fs)
defines separate interfaces for an owner-approved assignment and a supported
current-session turn source. Its pure gate requires the expected workspace/item
scope, native session ID, prospective window challenge and thread to agree before
mapping usage. The interfaces have no trusted implementation or challenge issuer;
an arbitrary caller can still fabricate both records. Production qualification
must establish independent source and assignment authentication, prospective
challenge issuance, start and sequence continuity, and Host application.

The dormant [`DirectSessionProspectiveWindowGate`](../../src/FS.GG.Coordination.Orchestration.Execution.Codex/DirectSessionProspectiveWindowGate.fs)
adds an explicit prospective issuer and selected current-session source contract.
Its pure test model refuses expired or premature windows, repeated challenges,
foreign workspace/item assignments, changed challenges and source substitutions.
An accepted challenge consumes immutable test state only. There is no protected
challenge issuer, trusted clock, authenticated current-session source or durable
atomic replay ledger; resetting the test state would permit reuse. Those custody
components must be independently established before any live producer uses this
gate, and success here remains a structural result rather than capture evidence.

The dormant [`DirectSessionChallengeReservation`](../../src/FS.GG.Coordination.Orchestration.Execution.Codex/DirectSessionChallengeReservation.fs)
adds a typed durable compare-and-set port, keyed globally by challenge. It checks
the issued window with a trusted-clock read, reserves once, then reads the native
source and clock again before preparing a fact. A confirmed reservation survives
a later source or clock gap; an unknown store outcome cannot be retried as if it
were absent. The concurrent fake store proves the caller handles one winner and
replay, but is neither persistent nor an authenticated custody implementation.
No actual issuer, clock, current-session source or durable reservation store is
installed, and the prepared fact is never submitted by this module.

## Installed native source characterization

The installed Codex CLI 0.156.1 offers `codex exec --json` for a child process
launched by the existing adapter. [OpenAI's App Server protocol](https://learn.chatgpt.com/docs/app-server)
also streams `thread/tokenUsage/updated` notifications to a connected client
following its thread start or resume. The version-pinned generated schema has
`threadId`, `turnId`, and `tokenUsage.last` and `tokenUsage.total` breakdowns.
Those are usage snapshots, not an independently proven completed-turn counter
record. The notification has no source-connection authentication, per-event
cursor, or separate native session identity in its payload; those obligations
would belong to an authenticated subscribed transport and continuity journal.

The dormant [`CodexAppServerUsageProjection`](../../src/FS.GG.Coordination.Orchestration.Execution.Codex/CodexAppServerUsageProjection.fs)
parses only that notification from independently authored fixture bytes. It
requires exact expected thread and turn IDs, bounded nonnegative internally
consistent counters, and unique closed mapping keys. It refuses other methods
and transcript-shaped frames without exposing raw bytes. It does not convert
`last` or cumulative counts to `CodexTurnUsage`, and no code here subscribes to
this running private interactive thread. A supported, authorized connection
with prospective assignment and start/usage/terminal continuity is still the
missing live source boundary.

## Capability and evidence handoff

The missing interface is a supported, authenticated event stream for the
**current** Codex thread. The existing
[`CodexTurnProjection`](../../src/FS.GG.Coordination.Orchestration.Execution.Codex/CodexTurnProjection.fs)
can interpret a completed-turn JSON event, but its caller receives events only
from the Codex child it launched. The runner's
[`TelemetryTurnJournal`](../../src/FS.GG.Coordination.Orchestration.Runner.Client/TelemetryTurnJournal.fs)
then persists those observations under its executor command. Reusing either
type without a current-session event source and a separately authorized
assignment would make the identity and coverage claims circular.

A proposed direct-session adapter should expose three results before any event
submission:

| Result | Evidence the adapter must return | Refusal boundary |
| --- | --- | --- |
| Current-thread capability | Supported event-source version, native thread identity, observation cursor or explicit lack of one, and whether completed-turn counters are available | A process lookup, credential wrapper, transcript read or future-child launcher does not prove current-thread observation. |
| Assignment binding | Independently authorized workspace association and exact repository, WorkItem persistence ID, issue, attempt, generation, invocation, producer and prospective window | A prompt naming an issue, an inferred workspace, or a retrospective mapping cannot bind prior turns. |
| Event continuity | Ordered native starts, completed-turn IDs and counters, source timestamps, sequence and loss reports for the bound window | Missing cursor continuity, changed bytes under one native identity, or an unobserved start remains a gap. |

The private qualification packet should retain the assignment readback, source
capability result, start boundary, native event identities and counters,
digest-bound batch identity, authenticated applied Host receipt, and later
`item-detail/2` revision with the matched event identities. Each record must
name the same workspace, repository and WorkItem, with attempts and invocations
joined exactly. A `durably-received` result, an empty queue, or a readback made
before application is insufficient. Keep the packet private; a public review
may report only the verdict, opaque evidence identifiers and gaps.

The first implementation gate is the current-thread capability result. Until a
supported source can produce it, the producer must return `unsupported` and
must not create a synthetic prospective root, infer a turn from the interactive
transcript, or submit an ingest batch. This is a source contract for a future
adapter, not an API or executable currently present in this repository.

## Source-only acceptance scaffold

| Controlled input | Required verdict |
| --- | --- |
| Exact assignment, native thread/turn starts and completed-turn counters, matching applied receipt and private item readback | Complete for that prospective invocation window only |
| Credential wrapper and healthy workspace, with no native event hook | Unsupported; no captured-turn claim |
| Existing thread attached after earlier turns | Earlier coverage gap retained; only later turns may be evaluated |
| Native turn without exact workspace/item/attempt binding | Refused; no event submission |
| Duplicate turn identity with changed counters | Conflict; retain original evidence and mark coverage incomplete |
| Successful native exit with no completed-turn counters | Terminal observed; usage gap |
| Observation window closes without a native process exit | Window closed; process terminal remains unobserved |
| Native event stream reports a sequence gap or cannot prove cursor continuity | Retain the gap; no complete-window claim |
| Batch queued or durably received without applied Host receipt | Pending application; no completeness claim |
| Applied receipt for another batch, producer, workspace or item | Receipt mismatch; no completeness claim |
| Queue zero but private item readback omits the turn | Incomplete joined evidence |

A future test must exercise these cases against the real interface and Host
readback. This design does not authorize a synthetic ingest batch or a direct
session launch. End-to-end runner acceptance separately requires one genuine
WorkItem from an authenticated schedulable-board read and the ordinary runner's
admission, enrollment and permit checks.
