# V2-LANG-01.4 — bounded Microsoft Agent Framework trial

**Part:** V2-LANG-01, milestone .4, windows A–D. Protected Coordination main contains A–B through
PR #898 at `2ecab6c3473b085a720f7ffcf34362cba67c346d`. This successor prepares C–D. The accepted programme plan remains
[`FS-GG/.github`'s language-independent workspace amendment](https://github.com/FS-GG/.github/blob/main/docs/roadmaps/2026-09-29-language-independent-workspaces-and-agent-integration.md).

**Status:** A–B are delivered on protected Coordination main. C recovery semantics and the D rejection
disposition are prepared locally from that protected base. C–D protected delivery and readback remain pending,
so V2-LANG-01.4 is open. Publication, installation and operating activation are rejected by the D disposition.

**Telemetry:** attempt `v2-lang-framework-disposition-20260930` is not configured. No usage or bureaucracy
percentage is inferred.

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

## Recovery and adverse lifecycle disposition

Durable execution recovery remains owned by `ExecutionSessionCoordinator` and its journal. If cancellation,
a provider exception or an ambiguous provider response occurs after `LaunchAttemptRecorded`, a fresh facade
may bind the same exact intent to a coordinator over that journal. Before the original deadline, the
coordinator reconciles the existing provider attempt and never launches another attempt. A framework session
is not recovery evidence, and the facade still permits only one invocation per instance.

The 1.22.0 framework checkpoint path is refused for this trial. The graph's barrier join retains process-local
branch state, and a framework checkpoint does not bind that state to the durable execution journal or prove
provider-effect ownership. `AgentFrameworkWorkflow.start` therefore accepts only `Fresh`; a nonempty checkpoint
identity returns `agent-framework-workflow-checkpoint-replay-unsupported`, a malformed identity refuses, and no
executor runs. `AIAgent` session serialization and deserialization also remain explicitly unsupported. This is
a fail-closed disposition, not a durable replay implementation.

Cancellation reaches the coordinator/provider token. The test cancels only after the durable launch-attempt
event and provider entry, then recovers through a new coordinator/facade by reconciliation with one total
provider launch. A request at its exact deadline refuses before any journal or provider effect. Non-cancellation
exceptions crossing the coordinator call are reported as `agent-framework-execution-effect-unknown`; ambiguous
and still-unknown reconciliation results use the same classification and emit no projection. A later conclusive
reconciliation can return the candidate, still with `deliveryClaimed=false` and no additional launch. A workflow
branch exception is surfaced as the SDK's `WorkflowErrorEvent`; no workflow output or delivery claim is emitted.

## A–D windows

- [x] **A — exact SDK and execution-facade source trial.** Pin and call the real 1.22.0 abstractions,
  exercise actual non-streaming and streaming agent entry points, and preserve the existing neutral
  coordinator/provider outcome.
- [x] **B — fixed graph and bounded parallelism source trial.** Run the candidate, parallel inspection
  and deterministic verification, and barrier join through the real workflow runtime. Prove that a held
  branch does not block a separate execution work item and that graph completion never claims delivery.
- [x] **C — recovery and adverse lifecycle trial.** Define and test checkpoint/recovery ownership,
  cancellation propagation, deadline behavior, framework exceptions and unknown effects against the
  durable coordinator. Framework checkpoint replay is unsupported; exact durable coordinator reconciliation
  is the only admitted recovery path in this source trial.
- [x] **D — measured disposition prepared: reject production adoption.** The direct neutral coordinator
  remains the supported path. The trial adds runtime, dependency and maintenance cost, cannot use framework
  checkpoints for durable replay, and demonstrates no unmet product need that requires the facade or graph.
  The milestone remains open until this C–D result is delivered to and read back from protected main.

## D measurement and disposition

The bounded runtime comparison used Release builds on Linux x64, .NET SDK 10.0.400/runtime 10.0.12 and an
AMD Ryzen 9 7900. Each scenario ran in five fresh processes after 25 warm-up operations. The table reports
the median process result. Direct and facade success used 5,000 measured work items per process; recovery used
2,000; workflow used 500. Every operation constructed a fresh in-memory journal and deterministic provider
fixture returning the same candidate and usage. Allocation is the change in `GC.GetTotalAllocatedBytes(true)`
across the measured loop. These are local framework overhead measurements, not provider latency, capacity,
token usage or operating-cost evidence.

| Scenario | Median time/work item | Allocated bytes/work item | Compared with direct path |
| --- | ---: | ---: | --- |
| Neutral `ExecutionSessionCoordinator.Launch` | 6.63 µs | 4,616 | supported baseline |
| `BoundExecutionAgent.RunAsync` over the same coordinator | 16.79 µs | 10,352 | +10.16 µs and +5,736 bytes; 2.53× time |
| Additional fixed framework workflow | 210.94 µs | 108,749 | added after the execution projection |
| Direct ambiguous launch plus durable reconciliation | 9.95 µs | 5,440 | recovery baseline; one launch, one reconciliation |
| Two facade instances around that recovery | 35.76 µs | 16,608 | +25.81 µs and +11,168 bytes; 3.60× time |

The workflow result confirms the bounded fan-out/barrier behavior but contributes no execution authority,
durable scheduling or delivery capability. Combining the facade and fixed workflow would add their costs;
the provider call would normally dominate absolute elapsed time, but it does not remove the allocation,
dependency or ownership surface.

The maintained trial surface is 464 physical source lines and 685 physical test lines across four F# files,
with 13 focused tests, two direct SDK pins and a separate package lock. Against the existing Codex adapter,
the restored graph adds 18 package identities and 5,345,168 bytes of selected runtime assemblies. Adverse
lifecycle behavior still depends on the existing journal/coordinator for effect ownership and reconciliation;
the framework adds exception translation and fresh facade construction while its session and workflow
checkpoint replay remain fail-closed.

No current product requirement needs an `AIAgent` identity, framework message/session portability, or this
in-process two-branch graph. The neutral coordinator already performs the one authoritative launch and durable
reconciliation, and the two local candidate checks do not require a workflow runtime. The disposition is
therefore **reject production adoption** of Microsoft Agent Framework 1.22.0 for this path: do not publish,
install, activate or place it in generated workspaces. The optional source trial remains repository evidence.
A future trial requires a concrete unmet need and must remeasure the then-current SDK; this result does not
claim that later SDK versions have the same limits.

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
After registration in the owning UnitTests project, the focused native gate observed that same named
workflow test fail with exit 1 under the deliberate false delivery assertion. The assertion was restored,
and the registered AgentFramework filter passed 6/6. Window C extends that registered filter to 13 tests.
No economic savings claim is made without measurement.

Focused evidence covers:

- real `AIAgent.RunAsync` and `RunStreamingAsync` calls;
- baseline/adapter equality for candidate identity, head/tree and usage;
- wrong input/session/options/profile limits and duplicate invocation with no extra provider launch;
- the real fixed workflow graph, barrier join and explicit false delivery claim; and
- an independently completing work item while another graph branch is held;
- framework session and workflow checkpoint replay refusal before execution;
- cancellation after durable attempt followed by reconciliation with no second launch;
- provider exception and ambiguous-effect classification with conclusive later reconciliation;
- exact-deadline refusal before provider effect; and
- workflow exception emission with no workflow output.

## Unified section 9.9 workspace impact

There is no generated-workspace change in A–D. No SDD, Spec Kit, typed-SDD, none or product-language
family changes fresh creation, retained behavior, defaults or enabled runtime behavior. The source is an
optional build/test assembly and establishes no installed capability. A later effective change would
require an exact Coordination artifact publication, explicit receiver selection, clean-creation proof and
separate retained-upgrade handling. D rejects that adoption step for this trial. Source merge, installed
availability and operating activation remain separate facts.
