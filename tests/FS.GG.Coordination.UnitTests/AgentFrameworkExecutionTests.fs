module FS.GG.Coordination.AgentFrameworkExecutionTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Xunit
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.AgentFramework

let private input = "execute the already-bound candidate"
let private now = DateTimeOffset.Parse "2026-09-30T10:00:00Z"
let private candidateId = Guid.Parse "30000000-0000-0000-0000-000000000104"
let private key =
    {
        AssignmentId = Guid.Parse "10000000-0000-0000-0000-000000000104"
        AttemptId = Guid.Parse "20000000-0000-0000-0000-000000000104"
        Generation = 4L
    }

let private digest (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value)
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private profile =
    {
        AgentId = "fsgg-bounded-execution"
        ExactInput = input
        ExactKey = key
        ExactWorkspace = "/tmp/fsgg-agent-framework-trial"
        ExactModel = Some "gpt-6-sol"
        ExactEffort = Some "medium"
        ExactDeadline = now.AddMinutes 2.
        ExactMaximumRuntime = TimeSpan.FromMinutes 2.
        ExactMaximumAttempts = 1
        ExactRecordedAt = now
    }

let private intent =
    {
        Schema = ExecutionProtocol.launchSchema
        Key = key
        InputDigest = digest input
        Workspace = profile.ExactWorkspace
        Requested =
            {
                Model = profile.ExactModel
                Effort = profile.ExactEffort
            }
        Limits =
            {
                Deadline = profile.ExactDeadline
                MaximumRuntime = profile.ExactMaximumRuntime
                MaximumAttempts = profile.ExactMaximumAttempts
            }
        RecordedAt = profile.ExactRecordedAt
    }

type private FixedClock() =
    inherit TimeProvider()
    override _.GetUtcNow() = now

type private AtClock(value: DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow() = value

type private MemoryJournal() =
    let gate = obj()
    let mutable events: SessionEvent list = []

    member _.Events = lock gate (fun () -> events)

    interface IExecutionSessionJournal with
        member _.ReadAttempt(_, _, _) =
            lock gate (fun () ->
                Task.FromResult(
                    if List.isEmpty events then
                        None
                    else
                        Some
                            {
                                Revision = int64 events.Length
                                Events = events
                            }
                ))

        member _.AppendAttempt(_, _, expectedRevision, eventValue, _) =
            lock gate (fun () ->
                if expectedRevision <> int64 events.Length then
                    Task.FromResult AppendConflict
                else
                    events <- events @ [ eventValue ]
                    Task.FromResult Appended)

let private observation =
    {
        Provider =
            {
                Provider = "Codex"
                AdapterVersion = "codex-subscription-exec/1"
            }
        Session =
            match ProviderSessionReference.create "codex-session-104" with
            | Ok value -> value
            | Error reason -> failwith reason
        Resolved =
            {
                Model = profile.ExactModel
                Effort = profile.ExactEffort
            }
        Lifecycle = Succeeded
        Output = []
        LifecycleReferences = []
        Usage =
            {
                Values =
                    Map
                        [ "input_tokens", UsageKnown(11L, "tokens", "fixture")
                          "output_tokens", UsageKnown(7L, "tokens", "fixture") ]
                Cost = CostNotApplicable "subscription"
            }
        Candidate =
            Some
                {
                    CandidateId = candidateId
                    HeadSha = String.replicate 40 "a"
                    TreeSha = String.replicate 40 "b"
                }
        ObservedAt = now
    }

type private CountingProvider() =
    let mutable launches = 0

    member _.Launches = Volatile.Read(&launches)

    interface IExecutionProvider with
        member _.ObserveReadiness(_) =
            Task.FromResult
                {
                    Identity = observation.Provider
                    Authentication = Authenticated "fixture"
                    SupportsResume = true
                    ObservedAt = now
                }

        member _.Launch(_, _) =
            Interlocked.Increment(&launches) |> ignore
            Task.FromResult(LaunchStarted observation)

        member _.Observe(_, _) = Task.FromResult(Ok observation)
        member _.Reconcile(_, _) = Task.FromResult(Reconciled observation)
        member _.Cancel(_, _) = Task.FromResult CancelAccepted

type private ScriptedProvider
    (
        launchBehavior: CancellationToken -> Task<LaunchResult>,
        initialReconcile: ReconcileResult
    ) =
    let mutable launches = 0
    let mutable reconciles = 0
    let mutable reconcileResult = initialReconcile

    member _.Launches = Volatile.Read(&launches)
    member _.Reconciles = Volatile.Read(&reconciles)

    member _.ReconcileResult
        with set value = reconcileResult <- value

    interface IExecutionProvider with
        member _.ObserveReadiness(_) =
            Task.FromResult
                {
                    Identity = observation.Provider
                    Authentication = Authenticated "fixture"
                    SupportsResume = true
                    ObservedAt = now
                }

        member _.Launch(_, cancellationToken) =
            Interlocked.Increment(&launches) |> ignore
            launchBehavior cancellationToken

        member _.Observe(_, _) = Task.FromResult(Ok observation)

        member _.Reconcile(_, _) =
            Interlocked.Increment(&reconciles) |> ignore
            Task.FromResult reconcileResult

        member _.Cancel(_, _) = Task.FromResult CancelAccepted

let private coordinator () =
    let provider = CountingProvider()
    let value = ExecutionSessionCoordinator(provider, MemoryJournal(), FixedClock())
    value, provider

let private getResult =
    function
    | Ok value -> value
    | Error reason -> failwithf "unexpected error: %s" reason

let private binding () = BoundAgentFrameworkLaunch.create profile intent |> getResult

let private decode (value: string) = AgentFrameworkExecutionProjection.decode value |> getResult

[<Fact>]
let ``actual AIAgent RunAsync preserves baseline candidate and usage`` () =
    task {
        let baselineCoordinator, baselineProvider = coordinator ()
        let! baselineResult = baselineCoordinator.Launch(intent, CancellationToken.None)
        let baseline = AgentFrameworkExecutionProjection.ofCoordinationResult baselineResult |> getResult

        let adapterCoordinator, adapterProvider = coordinator ()
        let agent = BoundExecutionAgent(adapterCoordinator, binding ())
        let! session = agent.CreateSessionAsync().AsTask()
        let! response = agent.RunAsync(input, session, null, CancellationToken.None)
        let projected = decode response.Text

        Assert.Equal(baseline.CandidateId, projected.CandidateId)
        Assert.Equal(baseline.HeadSha, projected.HeadSha)
        Assert.Equal(baseline.TreeSha, projected.TreeSha)
        Assert.Equal<AgentFrameworkUsageProjection>(baseline.Usage, projected.Usage)
        Assert.False(projected.DeliveryClaimed)
        Assert.Equal(1, baselineProvider.Launches)
        Assert.Equal(1, adapterProvider.Launches)
    }

[<Fact>]
let ``actual AIAgent streaming preserves bounded outcome`` () =
    task {
        let value, provider = coordinator ()
        let agent = BoundExecutionAgent(value, binding ())
        let! session = agent.CreateSessionAsync().AsTask()
        let enumerator = agent.RunStreamingAsync(input, session, null, CancellationToken.None).GetAsyncEnumerator()
        let! present = enumerator.MoveNextAsync().AsTask()
        Assert.True(present)
        let projected = decode enumerator.Current.Text
        let! finished = enumerator.MoveNextAsync().AsTask()
        do! enumerator.DisposeAsync().AsTask()

        Assert.False(finished)
        Assert.Equal(candidateId.ToString("D"), projected.CandidateId)
        Assert.False(projected.DeliveryClaimed)
        Assert.Equal(1, provider.Launches)
    }

[<Fact>]
let ``wrong input session options and duplicate invocation never add a launch`` () =
    task {
        let value, provider = coordinator ()
        let agent = BoundExecutionAgent(value, binding ())
        let! session = agent.CreateSessionAsync().AsTask()

        let! wrongInput =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                agent.RunAsync("different", session, null, CancellationToken.None) :> Task)

        Assert.Equal("agent-framework-input-mismatch", wrongInput.Message)
        Assert.Equal(0, provider.Launches)

        let otherValue, _ = coordinator ()
        let otherAgent = BoundExecutionAgent(otherValue, binding ())
        let! foreignSession = otherAgent.CreateSessionAsync().AsTask()

        let! wrongSession =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                agent.RunAsync(input, foreignSession, null, CancellationToken.None) :> Task)

        Assert.Equal("agent-framework-session-identity-mismatch", wrongSession.Message)
        Assert.Equal(0, provider.Launches)

        let! wrongOptions =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                agent.RunAsync(input, session, AgentRunOptions(), CancellationToken.None) :> Task)

        Assert.Equal("agent-framework-options-refused", wrongOptions.Message)
        Assert.Equal(0, provider.Launches)

        let! _ = agent.RunAsync(input, session, null, CancellationToken.None)

        let! duplicate =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                agent.RunAsync(input, session, null, CancellationToken.None) :> Task)

        Assert.Equal("agent-framework-duplicate-invocation-refused", duplicate.Message)
        Assert.Equal(1, provider.Launches)
    }

[<Fact>]
let ``binding refuses changed request identity and profile limits`` () =
    let cases =
        [ { intent with Key = { intent.Key with Generation = 5L } }
          { intent with InputDigest = digest "different" }
          { intent with Workspace = "/tmp/different" }
          { intent with Requested = { intent.Requested with Model = Some "different" } }
          { intent with Limits = { intent.Limits with Deadline = intent.Limits.Deadline.AddSeconds 1. } }
          { intent with Limits = { intent.Limits with MaximumAttempts = 2 } }
          { intent with Limits = { intent.Limits with MaximumRuntime = TimeSpan.FromMinutes 3. } }
          { intent with RecordedAt = intent.RecordedAt.AddSeconds 1. } ]

    for candidate in cases do
        Assert.Equal(Error "agent-framework-launch-binding-mismatch", BoundAgentFrameworkLaunch.create profile candidate)

[<Fact>]
let ``framework session checkpoint and replay are refused`` () =
    task {
        let value, provider = coordinator ()
        let agent = BoundExecutionAgent(value, binding ())
        let! session = agent.CreateSessionAsync().AsTask()

        let! serializeError =
            Assert.ThrowsAsync<NotSupportedException>(fun () ->
                agent.SerializeSessionAsync(session, null, CancellationToken.None).AsTask() :> Task)

        use document = JsonDocument.Parse "{}"

        let! deserializeError =
            Assert.ThrowsAsync<NotSupportedException>(fun () ->
                agent.DeserializeSessionAsync(document.RootElement, null, CancellationToken.None).AsTask() :> Task)

        Assert.Equal("agent-framework-session-serialization-refused", serializeError.Message)
        Assert.Equal("agent-framework-session-deserialization-refused", deserializeError.Message)
        Assert.Equal(0, provider.Launches)
    }

[<Fact>]
let ``cancelled launch recovers only by durable coordinator reconciliation`` () =
    task {
        let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let journal = MemoryJournal()

        let provider =
            ScriptedProvider(
                (fun cancellationToken ->
                    task {
                        started.TrySetResult() |> ignore
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return LaunchAmbiguous "unreachable"
                    }),
                Reconciled observation
            )

        let firstCoordinator =
            ExecutionSessionCoordinator(provider, journal, FixedClock())

        let firstAgent = BoundExecutionAgent(firstCoordinator, binding ())
        let! firstSession = firstAgent.CreateSessionAsync().AsTask()
        use cancellation = new CancellationTokenSource()
        let firstRun = firstAgent.RunAsync(input, firstSession, null, cancellation.Token)
        do! started.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        cancellation.Cancel()
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> firstRun :> Task)

        Assert.Equal(1, provider.Launches)
        Assert.Contains(journal.Events, function | LaunchAttemptRecorded(1, _) -> true | _ -> false)

        let recoveryCoordinator =
            ExecutionSessionCoordinator(provider, journal, FixedClock())

        let recoveryAgent = BoundExecutionAgent(recoveryCoordinator, binding ())
        let! recoverySession = recoveryAgent.CreateSessionAsync().AsTask()
        let! recovered = recoveryAgent.RunAsync(input, recoverySession, null, CancellationToken.None)
        let projected = decode recovered.Text

        Assert.Equal(candidateId.ToString("D"), projected.CandidateId)
        Assert.False(projected.DeliveryClaimed)
        Assert.Equal(1, provider.Launches)
        Assert.Equal(1, provider.Reconciles)
    }

[<Fact>]
let ``provider exception becomes unknown effect and durable reconciliation does not relaunch`` () =
    task {
        let journal = MemoryJournal()

        let provider =
            ScriptedProvider(
                (fun _ -> Task.FromException<LaunchResult>(ApplicationException "fixture-provider-crash")),
                Reconciled observation
            )

        let firstAgent =
            BoundExecutionAgent(ExecutionSessionCoordinator(provider, journal, FixedClock()), binding ())

        let! firstSession = firstAgent.CreateSessionAsync().AsTask()

        let! unknown =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                firstAgent.RunAsync(input, firstSession, null, CancellationToken.None) :> Task)

        Assert.Equal("agent-framework-execution-effect-unknown", unknown.Message)
        Assert.IsType<ApplicationException>(unknown.InnerException) |> ignore
        Assert.Equal(1, provider.Launches)

        let recoveryAgent =
            BoundExecutionAgent(ExecutionSessionCoordinator(provider, journal, FixedClock()), binding ())

        let! recoverySession = recoveryAgent.CreateSessionAsync().AsTask()
        let! recovered = recoveryAgent.RunAsync(input, recoverySession, null, CancellationToken.None)
        Assert.Equal(candidateId.ToString("D"), (decode recovered.Text).CandidateId)
        Assert.Equal(1, provider.Launches)
        Assert.Equal(1, provider.Reconciles)
    }

[<Fact>]
let ``ambiguous effect stays unknown until durable reconciliation succeeds`` () =
    task {
        let journal = MemoryJournal()

        let provider =
            ScriptedProvider(
                (fun _ -> Task.FromResult(LaunchAmbiguous "provider-response-lost")),
                ReconcileUnknown "provider-still-unknown"
            )

        let runAgent () =
            task {
                let agent =
                    BoundExecutionAgent(ExecutionSessionCoordinator(provider, journal, FixedClock()), binding ())

                let! session = agent.CreateSessionAsync().AsTask()
                return agent, session
            }

        let! firstAgent, firstSession = runAgent ()

        let! firstUnknown =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                firstAgent.RunAsync(input, firstSession, null, CancellationToken.None) :> Task)

        Assert.Equal(
            "agent-framework-execution-effect-unknown:provider-response-lost",
            firstUnknown.Message
        )

        let! secondAgent, secondSession = runAgent ()

        let! secondUnknown =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                secondAgent.RunAsync(input, secondSession, null, CancellationToken.None) :> Task)

        Assert.Equal(
            "agent-framework-execution-effect-unknown:provider-still-unknown",
            secondUnknown.Message
        )

        Assert.Equal(1, provider.Launches)
        provider.ReconcileResult <- Reconciled observation
        let! thirdAgent, thirdSession = runAgent ()
        let! recovered = thirdAgent.RunAsync(input, thirdSession, null, CancellationToken.None)

        Assert.Equal(candidateId.ToString("D"), (decode recovered.Text).CandidateId)
        Assert.False((decode recovered.Text).DeliveryClaimed)
        Assert.Equal(1, provider.Launches)
        Assert.Equal(2, provider.Reconciles)
    }

[<Fact>]
let ``expired bound request refuses before provider effect`` () =
    task {
        let provider = CountingProvider()

        let value =
            ExecutionSessionCoordinator(provider, MemoryJournal(), AtClock(profile.ExactDeadline))

        let agent = BoundExecutionAgent(value, binding ())
        let! session = agent.CreateSessionAsync().AsTask()

        let! expired =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                agent.RunAsync(input, session, null, CancellationToken.None) :> Task)

        Assert.Equal("execution-budget-expired", expired.Message)
        Assert.Equal(0, provider.Launches)
    }
