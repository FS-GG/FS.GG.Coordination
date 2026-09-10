using System.Collections.Concurrent;
using System.Diagnostics;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Orchestration.RuntimeComparison;

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string scenario)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{scenario}: expected {expected}, got {actual}");
    }

    public static void True(bool actual, string scenario)
    {
        if (!actual)
            throw new InvalidOperationException($"{scenario}: expected true");
    }
}

public sealed record Submit(string CommandId, string Payload, int SchemaVersion);
public sealed record ProviderObserved(string CommandId, bool Completed);
public sealed record Cancel(string CommandId);
public sealed record Snapshot(string Status, string RecoveryAction, int Effects, int CompletedEffects);
public sealed record Accepted(string CommandId, string Payload, int SchemaVersion);
public sealed record EffectUnknown(string CommandId);
public sealed record EffectSettled(string CommandId);
public sealed record Cancelled(string CommandId);
public sealed record GetSnapshot;

public sealed class AkkaLifecycleActor : ReceivePersistentActor
{
    private readonly Dictionary<string, string> accepted = new(StringComparer.Ordinal);
    private readonly HashSet<string> unknown = new(StringComparer.Ordinal);
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private string status = "empty";

    public override string PersistenceId { get; }

    public AkkaLifecycleActor(string persistenceId)
    {
        PersistenceId = persistenceId;
        Recover<Accepted>(Apply);
        Recover<EffectUnknown>(Apply);
        Recover<EffectSettled>(Apply);
        Recover<Cancelled>(Apply);

        Command<Submit>(message =>
        {
            if (message.SchemaVersion is not (1 or 2))
            {
                Sender.Tell(new Status.Failure(new InvalidOperationException("unsupported-schema")));
                return;
            }
            if (accepted.TryGetValue(message.CommandId, out var existing))
            {
                Sender.Tell(existing == message.Payload ? "duplicate" : "conflict");
                return;
            }
            var replyTo = Sender;
            PersistAll<object>(
                [new Accepted(message.CommandId, message.Payload, message.SchemaVersion), new EffectUnknown(message.CommandId)],
                evt =>
                {
                    Apply(evt);
                    if (evt is EffectUnknown)
                        replyTo.Tell("accepted-observe-before-retry");
                });
        });
        Command<ProviderObserved>(message =>
        {
            var replyTo = Sender;
            if (!unknown.Contains(message.CommandId))
            {
                replyTo.Tell("no-unknown-effect");
                return;
            }
            if (!message.Completed)
            {
                replyTo.Tell("retry-authorized-after-observation");
                return;
            }
            Persist(new EffectSettled(message.CommandId), evt => { Apply(evt); replyTo.Tell("settled"); });
        });
        Command<Cancel>(message =>
        {
            var replyTo = Sender;
            Persist(new Cancelled(message.CommandId), evt => { Apply(evt); replyTo.Tell("cancelled"); });
        });
        Command<GetSnapshot>(_ => Sender.Tell(Current()));
    }

    private void Apply(object evt)
    {
        switch (evt)
        {
            case Accepted value:
                accepted[value.CommandId] = value.Payload;
                status = value.SchemaVersion == 1 ? "accepted-v1" : "accepted-v2";
                break;
            case EffectUnknown value:
                unknown.Add(value.CommandId);
                status = "effect-unknown";
                break;
            case EffectSettled value:
                unknown.Remove(value.CommandId);
                completed.Add(value.CommandId);
                status = "completed";
                break;
            case Cancelled value:
                unknown.Remove(value.CommandId);
                status = "cancelled";
                break;
        }
    }

    private Snapshot Current() => new(status, unknown.Count > 0 ? "observe-before-retry" : "none", accepted.Count, completed.Count);
}

public sealed record TemporalInput(string CommandId, string Payload, int SchemaVersion);
public sealed record TemporalResult(string Status, string RecoveryAction, int Effects, int CompletedEffects);

[Workflow("RuntimeComparisonLifecycle")]
public sealed class TemporalLifecycleWorkflow
{
    private bool providerObserved;
    private bool providerCompleted;
    private bool cancelled;

    [WorkflowRun]
    public async Task<TemporalResult> RunAsync(TemporalInput input)
    {
        if (input.SchemaVersion is not (1 or 2))
            throw new ApplicationFailureException("unsupported-schema", nonRetryable: true);

        await Workflow.WaitConditionAsync(() => providerObserved || cancelled);
        if (cancelled)
            return new("cancelled", "none", 1, 0);
        if (!providerCompleted)
            return new("retry-authorized-after-observation", "none", 1, 0);
        return new("completed", "none", 1, 1);
    }

    [WorkflowSignal]
    public Task ObserveProviderAsync(bool completed)
    {
        providerObserved = true;
        providerCompleted = completed;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task CancelAsync()
    {
        cancelled = true;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public TemporalResult Current() =>
        new(cancelled ? "cancelled" : "effect-unknown", providerObserved ? "none" : "observe-before-retry", 1, providerCompleted ? 1 : 0);
}

internal sealed record Measurement(string Runtime, double StartupMs, double ReplayMs, double DowntimeMs, int ScenarioChecks);

internal static class Program
{
    private static async Task<Measurement> RunAkkaAsync()
    {
        var startup = Stopwatch.StartNew();
        var config = ConfigurationFactory.ParseString("akka.persistence.journal.plugin = \"akka.persistence.journal.inmem\"");
        using var system = ActorSystem.Create("comparison", config);
        var actor = system.ActorOf(Props.Create(() => new AkkaLifecycleActor("work-item-42")), "work-item-42-a");
        startup.Stop();
        var checks = 0;

        Check.Equal("accepted-observe-before-retry", await actor.Ask<string>(new Submit("cmd-1", "same", 1)), "Akka submit"); checks++;
        Check.Equal("duplicate", await actor.Ask<string>(new Submit("cmd-1", "same", 1)), "Akka duplicate"); checks++;
        Check.Equal("conflict", await actor.Ask<string>(new Submit("cmd-1", "changed", 1)), "Akka conflicting duplicate"); checks++;
        Check.Equal("observe-before-retry", (await actor.Ask<Snapshot>(new GetSnapshot())).RecoveryAction, "Akka unknown effect"); checks++;

        var terminated = actor.GracefulStop(TimeSpan.FromSeconds(3));
        await terminated;
        var downtime = Stopwatch.StartNew();
        await Task.Delay(250);
        downtime.Stop();
        var replay = Stopwatch.StartNew();
        actor = system.ActorOf(Props.Create(() => new AkkaLifecycleActor("work-item-42")), "work-item-42-b");
        var recovered = await actor.Ask<Snapshot>(new GetSnapshot());
        replay.Stop();
        Check.Equal("observe-before-retry", recovered.RecoveryAction, "Akka actor restart replay"); checks++;
        Check.Equal("settled", await actor.Ask<string>(new ProviderObserved("cmd-1", true)), "Akka provider observation"); checks++;
        Check.Equal("accepted-observe-before-retry", await actor.Ask<string>(new Submit("cmd-2", "child-a", 2)), "Akka v2 child A"); checks++;
        Check.Equal("accepted-observe-before-retry", await actor.Ask<string>(new Submit("cmd-3", "child-b", 2)), "Akka v2 child B"); checks++;
        Check.Equal("cancelled", await actor.Ask<string>(new Cancel("cmd-2")), "Akka cancel child"); checks++;
        Check.True((await actor.Ask<Snapshot>(new GetSnapshot())).Effects == 3, "Akka concurrent child recovery"); checks++;
        try
        {
            await actor.Ask<object>(new Submit("cmd-bad", "bad", 99));
            throw new InvalidOperationException("Akka incompatible schema was accepted");
        }
        catch (InvalidOperationException exception) when (exception.Message == "unsupported-schema") { checks++; }
        await system.Terminate();
        return new("Akka.Persistence in-memory", startup.Elapsed.TotalMilliseconds, replay.Elapsed.TotalMilliseconds, downtime.Elapsed.TotalMilliseconds, checks);
    }

    private static async Task<Measurement> RunTemporalAsync()
    {
        var startup = Stopwatch.StartNew();
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        startup.Stop();
        var queue = $"runtime-comparison-{Guid.NewGuid():N}";
        var checks = 0;
        var options = new WorkflowOptions($"work-item-{Guid.NewGuid():N}", queue);
        WorkflowHandle<TemporalLifecycleWorkflow, TemporalResult> handle = null!;
        using var firstWorker = new TemporalWorker(environment.Client, new TemporalWorkerOptions(queue).AddWorkflow<TemporalLifecycleWorkflow>());
        await firstWorker.ExecuteAsync(async () =>
        {
            handle = await environment.Client.StartWorkflowAsync((TemporalLifecycleWorkflow workflow) => workflow.RunAsync(new("cmd-1", "same", 1)), options);
            await WaitUntilAsync(async () => (await handle.QueryAsync(workflow => workflow.Current())).RecoveryAction == "observe-before-retry");
            checks++;
            try
            {
                await environment.Client.StartWorkflowAsync((TemporalLifecycleWorkflow workflow) => workflow.RunAsync(new("cmd-1", "same", 1)), options);
                throw new InvalidOperationException("Temporal duplicate workflow ID was accepted");
            }
            catch (WorkflowAlreadyStartedException) { checks++; }
        });

        var downtime = Stopwatch.StartNew();
        await Task.Delay(250);
        downtime.Stop();
        var replay = Stopwatch.StartNew();
        using var secondWorker = new TemporalWorker(environment.Client, new TemporalWorkerOptions(queue).AddWorkflow<TemporalLifecycleWorkflow>());
        await secondWorker.ExecuteAsync(async () =>
        {
            await WaitUntilAsync(async () => (await handle.QueryAsync(workflow => workflow.Current())).RecoveryAction == "observe-before-retry");
            replay.Stop();
            checks++;
            await handle.SignalAsync(workflow => workflow.ObserveProviderAsync(true));
            Check.Equal("completed", (await handle.GetResultAsync()).Status, "Temporal observe before completion"); checks++;
            var replayResult = await new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<TemporalLifecycleWorkflow>())
                .ReplayWorkflowAsync(await handle.FetchHistoryAsync());
            Check.True(replayResult.ReplayFailure is null, "Temporal deterministic history replay"); checks++;

            var childA = await environment.Client.StartWorkflowAsync((TemporalLifecycleWorkflow workflow) => workflow.RunAsync(new("cmd-child-a", "child-a", 2)), new($"work-item-{Guid.NewGuid():N}", queue));
            var childB = await environment.Client.StartWorkflowAsync((TemporalLifecycleWorkflow workflow) => workflow.RunAsync(new("cmd-child-b", "child-b", 2)), new($"work-item-{Guid.NewGuid():N}", queue));
            await childA.SignalAsync(workflow => workflow.CancelAsync());
            await childB.SignalAsync(workflow => workflow.ObserveProviderAsync(true));
            Check.Equal("cancelled", (await childA.GetResultAsync()).Status, "Temporal cancellation"); checks++;
            Check.Equal("completed", (await childB.GetResultAsync()).Status, "Temporal concurrent child completion"); checks++;

            var badHandle = await environment.Client.StartWorkflowAsync((TemporalLifecycleWorkflow workflow) => workflow.RunAsync(new("cmd-bad", "bad", 99)), new($"work-item-{Guid.NewGuid():N}", queue));
            try
            {
                await badHandle.GetResultAsync();
                throw new InvalidOperationException("Temporal incompatible schema was accepted");
            }
            catch (WorkflowFailedException) { checks++; }
        });

        return new("Temporal local dev server", startup.Elapsed.TotalMilliseconds, replay.Elapsed.TotalMilliseconds, downtime.Elapsed.TotalMilliseconds, checks);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate())
            await Task.Delay(20, timeout.Token);
    }

    public static async Task<int> Main()
    {
        var results = new List<Measurement> { await RunAkkaAsync() };
        try
        {
            results.Add(await RunTemporalAsync());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"TEMPORAL_UNAVAILABLE {exception.GetType().Name}: {exception.Message}");
        }
        foreach (var result in results)
            Console.WriteLine($"MEASUREMENT runtime={result.Runtime};startup_ms={result.StartupMs:F1};replay_ms={result.ReplayMs:F1};downtime_ms={result.DowntimeMs:F1};checks={result.ScenarioChecks}");
        return results.Count == 2 ? 0 : 2;
    }
}
