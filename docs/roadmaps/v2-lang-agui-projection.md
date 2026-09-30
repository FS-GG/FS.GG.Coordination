# V2-LANG-01.3 — AG-UI read-only work-item projection

**Part:** V2-LANG-01, window .3. This bounded plan implements only an optional read-only projection in
Coordination. The accepted programme plan remains
[`FS-GG/.github`'s language-independent workspace amendment](https://github.com/FS-GG/.github/blob/main/docs/roadmaps/2026-09-29-language-independent-workspaces-and-agent-integration.md).

**Status:** source-qualified on the local implementation branch; merge and any later publication or
receiver adoption remain pending.

**Disposition:** adopt the optional read-only adapter at source level. Defer an HTTP endpoint, package
publication, receiver activation and any write or approval interface until a selected receiver has a
measured need. `RUN_FINISHED` means this projection read completed; it does not mean the work item was
verified or delivered.

## Contract and authority

The existing durable work-item journal and its replay function remain authoritative. The adapter reads
`IJournalStore.Recover`, validates persistence identity and sequence continuity, decodes the existing
event envelope and replays the existing Core state. It emits exactly three AG-UI events over SSE:
`RUN_STARTED`, `STATE_SNAPSHOT` and `RUN_FINISHED`.

The snapshot identifies its authority as `durable-work-item-replay` and exposes bounded state: journal
sequence, generation, workflow revision, control state and aggregate attempt/recovery/readback facts.
It emits no command, tool, message, approval, effect or state-delta event. No AG-UI event is fed back
into Core. The module has no endpoint registration and is absent from the Host startup path.

Exact duplicate journal rows collapse without duplicate UI events. A reconnect cursor receives a fresh
verified snapshot. A sequence gap produces a truthful partial snapshot without revision, generation or
delivery claims. Conflicting duplicates, a foreign persistence identity, corrupt events, a stale expected
generation, an impossible cursor and observer/store loss refuse before SSE is returned.

## Tested upstream identity and cost

The trial pins `AGUI.Abstractions` and `AGUI.Formatting` to exact NuGet version `1.0.0`. That release
implements AG-UI protocol `1.0`; its package metadata binds the source to upstream commit
`f08ccf853497914ef70a5f0197af45bb74915bc2`. The official release and .NET SDK layout are documented at:

- <https://github.com/ag-ui-protocol/ag-ui/releases>
- <https://github.com/ag-ui-protocol/ag-ui/tree/f08ccf853497914ef70a5f0197af45bb74915bc2/sdks/dotnet>
- <https://docs.ag-ui.com/concepts/events>

The implementation adds 299 lines in one optional projection module. Qualification adds 188 lines of
F# and a 27-line Python client. The two direct packages add one new transitive package,
`Microsoft.Extensions.AI.Abstractions` 10.6.0. `AGUI.Server`, protobuf, ASP.NET endpoint code and agent
framework packages are absent. The dependency cost is acceptable for this Host-scoped source adapter;
activation remains deferred because this window measured protocol fit rather than receiver demand.

## Acceptance evidence

The focused Host tests prove:

- one durable work item reaches a Python client through the SDK's `SseEventStreamFormatter`;
- the Python client reads protocol version `1.0`, the complete state snapshot and the explicit false
  delivery claim;
- exact duplicates converge and reconnect returns the current verified snapshot;
- gaps expose partial state, while stale generation and invalid cursors refuse;
- observer loss and conflicting duplicate rows return no projection bytes.

Run:

```console
dotnet test tests/FS.GG.Coordination.Orchestration.Host.Tests/FS.GG.Coordination.Orchestration.Host.Tests.fsproj --no-restore
```

## Workspace impact

This source window changes no generated workspace, installed receiver, default or enabled service.
Product repositories acquire no .NET or AG-UI dependency. A later receiver would require an exact
Coordination artifact publication, explicit endpoint composition, authentication and separate clean and
retained adoption proof. A write or approval interface requires a new revision-bound command contract
and its own bounded roadmap window.
