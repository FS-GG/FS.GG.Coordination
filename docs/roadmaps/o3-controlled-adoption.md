# O3 controlled adoption

Feature: `standalone-telemetry-host-and-orchestration`

Items: `O3-01` through `O3-03`

Attempt: `o3-source-20260916-1`

O3 extends the accepted serial Host to two explicit project/subject identities while retaining one durable
ordinary subscription slot. The Host remains one-subject-at-a-time. Canonical ownership is the immutable
repository database ID plus issue number; project-board items are projections and cannot create a second owner.

This source window does not add a multi-project scheduler or service, a second executor, a new policy registry,
an automatic queue, a production fake provider, a global recovery-slot claim, or simultaneous model execution.
`PostgreSqlExecutionStore.ReserveSubscription` locks and counts active ordinary reservations globally.
Its `recoveryCapacity` argument validates the accepted configuration but does not create a second global counter.
`ProjectOrchestrator` recovery capacity remains local to each project.

## Accepted plan and evidence

- [x] **O3-01 — shared capacity and canonical ownership.** A real PostgreSQL race uses distinct project and
  subject identities against ordinary capacity 1. Exactly one reservation succeeds. Restart retains capacity
  and idempotency; changed bytes conflict; unknown accounting and a mismatched release retain the slot; the
  exact release lets the refused immutable request enter. Observer/Core tests prove changed board projection IDs
  retain one canonical owner and recovery reservations remain project-local. Existing Core authority tests retain
  the deadline, unknown-outcome, command-body, generation, revision, and immutable attempt-history boundaries.

- [x] **O3-02 — transient observation failure and scoped recovery.** Timeout, rate-limit, and incomplete
  pagination observations produce no project observation and cannot erase the last durable membership. A later
  complete observation reconciles the same canonical subject. Existing production-composition PostgreSQL and
  Host tests retain the required route/claim readback gate, unknown-mutation observation path, wrong-subject and
  changed-admission refusal, displaced/expired claim refusal, exact admission and candidate history, startup
  pause, fresh reconnect readback, and separately authenticated resume. The added two-project Core test changes
  A's recovery reservations while proving B's journal-independent generation, ownership, and reservations are
  byte-for-byte unchanged. Recovery/reconciliation remains available while each project's ordinary allocation is
  occupied; it does not bypass the global subscription slot for another model invocation.

- [x] **O3-03 — prepare adoption.** The existing routine source route and protected-main container-bundle
  workflow are the only delivery paths. The workflow accepts the exact protected-main merge SHA as
  `expected_sha`, builds Host and runner from that commit and source tree, assembles one canonical archive, uploads
  it without recompression, downloads it into a fresh directory, and verifies the served bytes. Focused evidence
  is this roadmap, the PostgreSQL/Observer/Core tests, and
  [the O3-04 installed-adoption preview](../operations/o3-04-installed-adoption-preview.md). This source window
  does not dispatch that workflow or perform the installed operation.

## Retained authority and impact

The unchanged model/native/reboot basis is `.github` PR 3515 at
`cf840ff9f760d2fdebcdca84caf0ae24f75b8dfa`, Coordination PR 397 at
`1d3b683d804cb369914a3fbb29757c36c533e130`, SystemAdmin PR 108 at
`9046d2b3`, and terminal mailbox commit `aed85031815706ebe5675311004c23bde9aceb3b`.
O3 does not repeat those formal scenarios, call another model, or perform another physical reboot.

There is no SDD, Templates, generated-workspace, or default-workspace change. O3-03 changes source evidence and
artifact preparation only. O3-04 is the first installed behavior change and remains explicit and opt-in. A fresh
store adoption and an existing-store adoption are separate operations and require separate readback. Telemetry
coverage remains `not-configured`; native collaboration usage is `native-collaboration-usage-unsupported`.
No usage, cost, or overhead is inferred.
