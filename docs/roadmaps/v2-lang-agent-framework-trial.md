# V2-LANG-01.4 — bounded Microsoft Agent Framework trial

**Part:** V2-LANG-01, milestone .4, windows A–D. This branch contains only the first source window,
A–B. The accepted programme plan remains
[`FS-GG/.github`'s language-independent workspace amendment](https://github.com/FS-GG/.github/blob/main/docs/roadmaps/2026-09-29-language-independent-workspaces-and-agent-integration.md).

**Status:** A–B source and focused local tests are prepared from protected Coordination main
`125a6ba3ac019b4c4f6be57db04b958555f73409`. Merge, C recovery semantics, D decision evidence,
publication, installation and adoption remain pending. V2-LANG-01.4 is open.

**Telemetry:** not configured for this dispatch. No usage or bureaucracy percentage is inferred.

## Trial boundary

The optional assembly composes the accepted execution path:

```text
CodexExecution.coordinator
  -> ExecutionSessionCoordinator
  -> CodexExecutionProvider
  -> one previously bound LaunchIntent
```

`BoundExecutionAgent` is a Microsoft Agent Framework `AIAgent` facade over an injected existing
coordinator. Its binding checks the exact execution key, workspace, input digest, model, effort, deadline,
recorded time, maximum runtime and single-attempt limit before a framework call can reach the coordinator. The facade accepts one exact user message from
its own session, refuses caller options, and permits one invocation. Wrong input, a foreign session,
changed profile fields, supplied options and duplicate invocation do not launch a provider. The facade
does not create a launch request, select a provider, grant authority, approve a candidate or deliver it.

Both real `AIAgent.RunAsync` and `AIAgent.RunStreamingAsync` return the same bounded candidate and usage
projection as the neutral coordinator baseline. Every projection sets `deliveryClaimed` to false.
Session serialization and deserialization are refused so a framework session cannot become a portable
execution capability.

The real workflow SDK graph is fixed:

```text
candidate -> candidate inspection ------\
          -> deterministic verification ---> barrier join -> trial result
```

The inspection and verification branches receive the same candidate projection. The typed barrier join
requires one result from each named branch, rejects foreign work-item identity and duplicate branch
results, and emits a trial result with no delivery claim. A test holds the inspection branch for one work
item while a separately built and executed graph completes another work item, then releases the held
branch. This proves independence between those in-process trial executions; it does not establish durable
recovery, distributed scheduling or production concurrency.

## A–D windows

- [x] **A — exact SDK and execution-facade source trial.** Pin and call the real 1.22.0 abstractions,
  exercise actual non-streaming and streaming agent entry points, and preserve the existing neutral
  coordinator/provider outcome.
- [x] **B — fixed graph and bounded parallelism source trial.** Run the candidate, parallel inspection
  and deterministic verification, and barrier join through the real workflow runtime. Prove that a held
  branch does not block a separate execution work item and that graph completion never claims delivery.
- [ ] **C — recovery and adverse lifecycle trial.** Define and test checkpoint/recovery ownership,
  cancellation propagation, deadline behavior, framework exceptions and unknown effects against the
  durable coordinator. No recovery, cancellation or unknown-effect claim follows from A–B.
- [ ] **D — measured disposition.** Measure the trial against the existing supported baseline and record
  adopt, defer or reject. Include dependency/runtime cost, failure behavior, maintenance surface and an
  actual unmet need. A–B alone makes no adoption decision.

## Upstream identity and measured package footprint

The isolated project pins direct packages `Microsoft.Agents.AI.Abstractions` 1.22.0 and
`Microsoft.Agents.AI.Workflows` 1.22.0. The implementation was checked against the shipped net10.0 XML
API and binaries plus Microsoft's [1.22.0 NuGet package](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/1.22.0)
and [workflow builder and execution documentation](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/builder-and-execution).

Against the existing Codex adapter project's restored package set, the isolated project adds 18 package
identities and 18 selected runtime assemblies totaling 5,345,168 bytes. The two downloaded direct package
archives are 671,487 bytes for Abstractions and 2,110,984 bytes for Workflows. The added packages include
`Microsoft.Agents.AI` 1.22.0, Microsoft.Extensions.AI 10.10.0, OpenTelemetry.Api 1.18.0,
Microsoft.ML.Tokenizers 2.0.0 and Google.Protobuf 3.30.2. The lock file records the complete resolved graph;
no unrelated project or central package version is upgraded. These figures measure restored/runtime
footprint only and make no build-time or operating-cost savings claim.

## Pipeline preflight and verification

The adapter stays a separate optional assembly. Its two new test modules are registered into the existing
`FS.GG.Coordination.UnitTests` project rather than a separate test project. That is a simple compile/test
membership change: the existing bootstrap gate and coherent unit partition already execute UnitTests, so
no new workflow, job or model is warranted. This avoids a defect where a separately built project could
remain silently unexecuted.

A deliberate local fault changed the graph's false delivery assertion to expect true. The focused runner
observed the named test failure and exited 1; the fault was then reverted and all six focused tests passed.
Before acceptance, registration in the owning UnitTests project must repeat the known-failure observation
and restore check through the native gate. No economic savings claim is made without measurement.

Focused evidence covers:

- real `AIAgent.RunAsync` and `RunStreamingAsync` calls;
- baseline/adapter equality for candidate identity, head/tree and usage;
- wrong input/session/options/profile limits and duplicate invocation with no extra provider launch;
- the real fixed workflow graph, barrier join and explicit false delivery claim; and
- an independently completing work item while another graph branch is held.

## Unified section 9.9 workspace impact

There is no generated-workspace change in A–B. No SDD, Spec Kit, typed-SDD, none or product-language
family changes fresh creation, retained behavior, defaults or enabled runtime behavior. The source is an
unregistered optional assembly and establishes no installed capability. A later effective change would
require an exact Coordination artifact publication, explicit receiver selection, clean-creation proof and
separate retained-upgrade handling. C and D remain prerequisites to any adopt decision. Source merge,
installed availability and operating activation remain separate facts.
