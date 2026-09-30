module AgentFrameworkDMeasurement

open System
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Agents.AI.Workflows
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.AgentFramework

let now = DateTimeOffset.Parse "2026-09-30T10:00:00Z"
let input = "execute the already-bound candidate"

let digest (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value)
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

type FixedClock() =
    inherit TimeProvider()
    override _.GetUtcNow() = now

type MemoryJournal() =
    let mutable events: SessionEvent list = []

    interface IExecutionSessionJournal with
        member _.ReadAttempt(_, _, _) =
            Task.FromResult(
                if List.isEmpty events then None
                else Some { Revision = int64 events.Length; Events = events })

        member _.AppendAttempt(_, _, expectedRevision, eventValue, _) =
            if expectedRevision <> int64 events.Length then
                Task.FromResult AppendConflict
            else
                events <- events @ [ eventValue ]
                Task.FromResult Appended

type FixedProvider(observation: SessionObservation) =
    interface IExecutionProvider with
        member _.ObserveReadiness(_) =
            Task.FromResult
                { Identity = observation.Provider
                  Authentication = Authenticated "measurement"
                  SupportsResume = true
                  ObservedAt = now }
        member _.Launch(_, _) = Task.FromResult(LaunchStarted observation)
        member _.Observe(_, _) = Task.FromResult(Ok observation)
        member _.Reconcile(_, _) = Task.FromResult(Reconciled observation)
        member _.Cancel(_, _) = Task.FromResult CancelAccepted

type AmbiguousProvider(observation: SessionObservation) =
    let mutable launches = 0
    let mutable reconciles = 0
    member _.Launches = launches
    member _.Reconciles = reconciles
    interface IExecutionProvider with
        member _.ObserveReadiness(_) =
            Task.FromResult
                { Identity = observation.Provider
                  Authentication = Authenticated "measurement"
                  SupportsResume = true
                  ObservedAt = now }
        member _.Launch(_, _) =
            launches <- launches + 1
            Task.FromResult(LaunchAmbiguous "measurement-response-lost")
        member _.Observe(_, _) = Task.FromResult(Ok observation)
        member _.Reconcile(_, _) =
            reconciles <- reconciles + 1
            Task.FromResult(Reconciled observation)
        member _.Cancel(_, _) = Task.FromResult CancelAccepted

let case index =
    let key =
        { AssignmentId = Guid(0, 0s, 0s, [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; byte (index >>> 8); byte index |])
          AttemptId = Guid(1, 0s, 0s, [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; byte (index >>> 8); byte index |])
          Generation = int64 index }
    let profile =
        { AgentId = "fsgg-bounded-execution"
          ExactInput = input
          ExactKey = key
          ExactWorkspace = "/tmp/fsgg-agent-framework-trial"
          ExactModel = Some "gpt-6-sol"
          ExactEffort = Some "medium"
          ExactDeadline = now.AddMinutes 2.
          ExactMaximumRuntime = TimeSpan.FromMinutes 2.
          ExactMaximumAttempts = 1
          ExactRecordedAt = now }
    let intent =
        { Schema = ExecutionProtocol.launchSchema
          Key = key
          InputDigest = digest input
          Workspace = profile.ExactWorkspace
          Requested = { Model = profile.ExactModel; Effort = profile.ExactEffort }
          Limits =
            { Deadline = profile.ExactDeadline
              MaximumRuntime = profile.ExactMaximumRuntime
              MaximumAttempts = profile.ExactMaximumAttempts }
          RecordedAt = profile.ExactRecordedAt }
    let candidateId = Guid(2, 0s, 0s, [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; byte (index >>> 8); byte index |])
    let observation =
        { Provider = { Provider = "Codex"; AdapterVersion = "measurement/1" }
          Session = ProviderSessionReference.create $"measurement-{index}" |> Result.defaultWith failwith
          Resolved = { Model = profile.ExactModel; Effort = profile.ExactEffort }
          Lifecycle = Succeeded
          Output = []
          LifecycleReferences = []
          Usage =
            { Values = Map [ "input_tokens", UsageKnown(11L, "tokens", "measurement"); "output_tokens", UsageKnown(7L, "tokens", "measurement") ]
              Cost = CostNotApplicable "subscription" }
          Candidate = Some { CandidateId = candidateId; HeadSha = String.replicate 40 "a"; TreeSha = String.replicate 40 "b" }
          ObservedAt = now }
    profile, intent, observation

let direct index =
    task {
        let _, intent, observation = case index
        let coordinator = ExecutionSessionCoordinator(FixedProvider(observation), MemoryJournal(), FixedClock())
        let! result = coordinator.Launch(intent, CancellationToken.None)
        match AgentFrameworkExecutionProjection.ofCoordinationResult result with
        | Ok projection when projection.CandidateId = observation.Candidate.Value.CandidateId.ToString("D") -> return ()
        | value -> return failwithf "unexpected direct result %A" value
    }

let adapter index =
    task {
        let profile, intent, observation = case index
        let coordinator = ExecutionSessionCoordinator(FixedProvider(observation), MemoryJournal(), FixedClock())
        let binding = BoundAgentFrameworkLaunch.create profile intent |> Result.defaultWith failwith
        let agent = BoundExecutionAgent(coordinator, binding)
        let! session = agent.CreateSessionAsync().AsTask()
        let! response = agent.RunAsync(input, session, null, CancellationToken.None)
        match AgentFrameworkExecutionProjection.decode response.Text with
        | Ok projection when projection.CandidateId = observation.Candidate.Value.CandidateId.ToString("D") -> return ()
        | value -> return failwithf "unexpected adapter result %A" value
    }

let workflow index =
    task {
        let _, _, observation = case index
        let projected =
            { Lifecycle = "succeeded"
              CandidateId = observation.Candidate.Value.CandidateId.ToString("D")
              HeadSha = observation.Candidate.Value.HeadSha
              TreeSha = observation.Candidate.Value.TreeSha
              Usage = [| { Name = "input_tokens"; Kind = "known"; Value = Nullable 11L; UnitName = "tokens"; Provenance = "measurement" } |]
              DeliveryClaimed = false }
        let candidate = { WorkItemId = $"measurement-{index}"; Projection = projected }
        let! started = AgentFrameworkWorkflow.start Fresh candidate (fun _ _ -> Task.CompletedTask) CancellationToken.None
        let stream = started |> Result.defaultWith failwith
        let enumerator = stream.WatchStreamAsync(CancellationToken.None).GetAsyncEnumerator()
        let mutable found = false
        let mutable reading = true
        while reading do
            let! present = enumerator.MoveNextAsync().AsTask()
            if present then
                match enumerator.Current with
                | :? WorkflowOutputEvent as output ->
                    match output.Data with
                    | :? (AgentFrameworkWorkflowResult option) as value -> found <- value.IsSome
                    | _ -> ()
                | _ -> ()
            else reading <- false
        do! enumerator.DisposeAsync().AsTask()
        do! stream.DisposeAsync().AsTask()
        if not found then failwith "workflow output missing"
    }

let directRecovery index =
    task {
        let _, intent, observation = case index
        let journal = MemoryJournal()
        let provider = AmbiguousProvider(observation)
        let first = ExecutionSessionCoordinator(provider, journal, FixedClock())
        let! initial = first.Launch(intent, CancellationToken.None)
        match initial with
        | SessionNeedsReconciliation _ -> ()
        | value -> failwithf "unexpected direct ambiguity %A" value
        let recovery = ExecutionSessionCoordinator(provider, journal, FixedClock())
        let! recovered = recovery.Launch(intent, CancellationToken.None)
        match AgentFrameworkExecutionProjection.ofCoordinationResult recovered with
        | Ok projection when projection.CandidateId = observation.Candidate.Value.CandidateId.ToString("D") && provider.Launches = 1 && provider.Reconciles = 1 -> return ()
        | value -> return failwithf "unexpected direct recovery %A" value
    }

let adapterRecovery index =
    task {
        let profile, intent, observation = case index
        let journal = MemoryJournal()
        let provider = AmbiguousProvider(observation)
        let binding = BoundAgentFrameworkLaunch.create profile intent |> Result.defaultWith failwith
        let first = BoundExecutionAgent(ExecutionSessionCoordinator(provider, journal, FixedClock()), binding)
        let! firstSession = first.CreateSessionAsync().AsTask()
        let mutable unknown = false
        try
            let! _ = first.RunAsync(input, firstSession, null, CancellationToken.None)
            ()
        with :? InvalidOperationException as exceptionValue when exceptionValue.Message.StartsWith("agent-framework-execution-effect-unknown") ->
            unknown <- true
        if not unknown then failwith "adapter ambiguity was not unknown"
        let recovery = BoundExecutionAgent(ExecutionSessionCoordinator(provider, journal, FixedClock()), binding)
        let! recoverySession = recovery.CreateSessionAsync().AsTask()
        let! response = recovery.RunAsync(input, recoverySession, null, CancellationToken.None)
        match AgentFrameworkExecutionProjection.decode response.Text with
        | Ok projection when projection.CandidateId = observation.Candidate.Value.CandidateId.ToString("D") && provider.Launches = 1 && provider.Reconciles = 1 -> return ()
        | value -> return failwithf "unexpected adapter recovery %A" value
    }

let run name iterations operation =
    task {
        for index in 1 .. 25 do
            do! operation index
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        let allocationBefore = GC.GetTotalAllocatedBytes(true)
        let stopwatch = Stopwatch.StartNew()
        for index in 1000 .. 1000 + iterations - 1 do
            do! operation index
        stopwatch.Stop()
        let allocated = GC.GetTotalAllocatedBytes(true) - allocationBefore
        let payload =
            {| scenario = name
               iterations = iterations
               elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds
               microsecondsPerOperation = stopwatch.Elapsed.TotalMicroseconds / float iterations
               allocatedBytes = allocated
               allocatedBytesPerOperation = float allocated / float iterations |}
        printfn "%s" (JsonSerializer.Serialize payload)
    }

[<EntryPoint>]
let main arguments =
    let scenario = arguments |> Array.tryHead |> Option.defaultValue "all"
    let iterations = arguments |> Array.tryItem 1 |> Option.map int |> Option.defaultValue 1000
    match scenario with
    | "direct" -> run "direct-coordinator" iterations direct
    | "adapter" -> run "agent-facade" iterations adapter
    | "workflow" -> run "fixed-workflow" iterations workflow
    | "direct-recovery" -> run "direct-recovery" iterations directRecovery
    | "adapter-recovery" -> run "agent-facade-recovery" iterations adapterRecovery
    | _ ->
        task {
            do! run "direct-coordinator" iterations direct
            do! run "agent-facade" iterations adapter
            do! run "fixed-workflow" iterations workflow
        }
    |> fun taskValue -> taskValue.GetAwaiter().GetResult()
    0
