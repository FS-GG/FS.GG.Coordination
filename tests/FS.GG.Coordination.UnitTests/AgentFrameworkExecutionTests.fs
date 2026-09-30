module FS.GG.Coordination.AgentFrameworkExecutionTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
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

type private MemoryJournal() =
    let gate = obj()
    let mutable events: SessionEvent list = []

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

type private CountingProvider() =
    let mutable launches = 0

    let observation =
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
