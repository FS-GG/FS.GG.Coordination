module FS.GG.Coordination.Orchestration.Host.Tests.ChoreoProductionReplayTests

open System
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Pattern
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql

module F = HostTests.Fixture
module W = HostedWriterQuintReplayTests.Fixture

let token = CancellationToken.None
let now = DateTimeOffset.UtcNow

let clock =
    { new TimeProvider() with
        override _.GetUtcNow() = now
    }

/// External session facts only: all lifecycle decisions belong to the real coordinator.
type RecordingExecutionProvider() =
    let mutable launches = 0
    let mutable observation: SessionObservation option = None
    member _.Launches = launches

    interface IExecutionProvider with
        member _.ObserveReadiness _ =
            Task.FromResult
                {
                    Identity =
                        {
                            Provider = "choreo-recording"
                            AdapterVersion = "1"
                        }
                    Authentication = Authenticated "test"
                    SupportsResume = true
                    ObservedAt = now
                }

        member _.Launch(intent, _) =
            launches <- launches + 1

            let value: SessionObservation =
                {
                    Provider =
                        {
                            Provider = "choreo-recording"
                            AdapterVersion = "1"
                        }
                    Session = ProviderSessionReference.create "choreo-session" |> Result.defaultWith failwith
                    Resolved = { Model = None; Effort = None }
                    Lifecycle = Running
                    Output = []
                    LifecycleReferences = []
                    Usage =
                        {
                            Values = Map.empty
                            Cost = CostNotApplicable "test"
                        }
                    Candidate = None
                    ObservedAt = now
                }

            observation <- Some value
            Task.FromResult(LaunchStarted value)

        member _.Observe(_, _) =
            Task.FromResult(observation |> Option.map Ok |> Option.defaultValue (Error "absent"))

        member _.Reconcile(_, _) =
            Task.FromResult(observation |> Option.map Reconciled |> Option.defaultValue ConfirmedAbsent)

        member _.Cancel(_, _) = Task.FromResult CancelAccepted

let private append (store: IJournalStore) work command =
    task {
        let! recovered = HostedWriterJournal.recover store work token
        let state = recovered |> Result.defaultWith (sprintf "%A" >> failwith) |> _.State

        let envelope =
            {
                CommandId = Id.command (Guid.NewGuid())
                ProtocolVersion = Id.protocolVersion 1 0
                ExpectedRevision = state.Revision
                ExpectedGeneration = state.Generation
                PrincipalId = "pilot-route"
                SessionId = None
                IssuedAt = now
                ExpiresAt = now.AddMinutes 1.
                Command = command
            }

        let! result = HostedWriterJournal.decideAndAppend clock store work envelope token
        return result |> Result.defaultWith failwith |> fst |> _.Receipt
    }

let private requireAccepted (receipt: CommandReceipt) =
    Assert.True(
        receipt.Disposition = ReceiptDisposition.Accepted
        || receipt.Disposition = ReceiptDisposition.Duplicate,
        receipt.Detail
    )

let private recover (store: IJournalStore) work =
    task {
        let! recovered = HostedWriterJournal.recover store work token
        return recovered |> Result.defaultWith (sprintf "%A" >> failwith) |> _.State
    }

/// Shared by the in-memory and real PostgreSQL tests. The scenario and expected states
/// are loaded from Quint ITF; no F# transition function supplies the oracle.
let replay
    (store: IJournalStore)
    (executions: IExecutorCommandStore)
    (journal: IExecutionSessionJournal)
    scenarioId
    mutation
    =
    task {
        let originalStore = store

        let store =
            if mutation <> "journal" then
                store
            else
                { new IJournalStore with
                    member _.CheckReadiness ct = originalStore.CheckReadiness ct
                    member _.Recover(id, ct) = originalStore.Recover(id, ct)
                    member _.SaveSnapshot(value, ct) = originalStore.SaveSnapshot(value, ct)

                    member _.SaveProjectionCheckpoint(value, ct) =
                        originalStore.SaveProjectionCheckpoint(value, ct)

                    member _.Append(request, ct) =
                        let dispatch =
                            request.Events
                            |> List.exists (fun event ->
                                match EventEnvelope.tryDecode event.Payload with
                                | Ok(EffectDispatchStarted _) -> true
                                | _ -> false)

                        originalStore.Append(
                            (if dispatch then
                                 { request with
                                     ExpectedSequence = request.ExpectedSequence - 1L
                                 }
                             else
                                 request),
                            ct
                        )
                }

        let scenario = ChoreoTrace.load scenarioId
        let work = F.permit.SubjectId

        let! admitted =
            MainAdmissionPreparer.prepare
                clock
                store
                executions
                journal
                work
                "pilot-route"
                ({ F.preparationRequest () with
                    SelectedAt = now
                })
                (Text.Encoding.UTF8.GetBytes "bounded Choreo replay")
                token

        let preparation =
            admitted
            |> Result.defaultWith failwith
            |> MainRouteAdmission.decode work "pilot-route"
            |> Result.defaultWith failwith

        let route = preparation.Route

        let operations =
            [
                route.ClaimOperationId
                route.ProcessOperationId
                route.CandidateOperationId
                route.BranchOperationId
                route.PullRequestOperationId
                route.MergeOperationId
                route.ReadbackOperationId
            ]

        let artifact =
            { W.candidateArtifact with
                CandidateId = route.CandidateId
                RetainUntil = now.AddDays 30.
            }

        let receipt =
            { W.candidateReceipt with
                CandidateId = route.CandidateId
                VerifiedAt = now
            }

        let candidates =
            { new ICandidateStore with
                member _.Put(_, _) = Task.FromResult(Ok receipt)

                member _.Read(_, _) =
                    Task.FromResult(
                        Ok
                            {
                                Candidate = artifact
                                Bytes = Array.empty
                            }
                    )

                member _.Quarantine(_, _, _) = Task.FromResult(Ok())
                member _.CleanupUnreferenced(_, _, _) = Task.FromResult 0
            }

        let workflow =
            MainRouteWorkflow(clock, store, candidates, executions, journal, work, "pilot-route")

        let! prepared = workflow.Prepare(preparation, token)
        Assert.Equal(Ok(), prepared)
        use system = ActorSystem.Create("choreo-production-" + Guid.NewGuid().ToString("N"))
        let provider = RecordingExecutionProvider()
        let coordinator = ExecutionSessionCoordinator(provider, journal, clock)
        let actor = system.ActorOf(ExecutionSessionActor.Props coordinator)
        let requests = ResizeArray<GitHubRequest>()

        let githubExecutor =
            { new IGitHubRequestExecutor with
                member _.Send(request, _) =
                    requests.Add request
                    let head = artifact.HeadSha

                    let merged =
                        if mutation = "native" || scenarioId = "missing-native-readback" then
                            "null"
                        else
                            "\"2026-09-10T14:00:00Z\""

                    let mergeSha = W.oid "b"

                    let body =
                        $"[{{\"number\":42,\"node_id\":\"PR_node\",\"state\":\"closed\",\"merged_at\":{merged},\"merge_commit_sha\":\"{mergeSha}\",\"head\":{{\"sha\":\"{head}\",\"ref\":\"pilot\"}},\"base\":{{\"sha\":\"{mergeSha}\",\"ref\":\"main\",\"repo\":{{\"full_name\":\"FS-GG/.github\"}}}}}}]"

                    Task.FromResult(
                        Response
                            {
                                StatusCode = 200
                                Headers = Map.empty
                                Body = body
                                ETag = Some "readback"
                                RateBudget =
                                    {
                                        Limit = None
                                        Remaining = None
                                        ResetAt = None
                                        Cost = None
                                    }
                            }
                    )
            }

        let publisher =
            { new IGitCandidatePublisher with
                member _.Publish(_, _, _, _, _, _) = failwith "unexpected publication"
            }

        let github =
            GitHubRouteClient(
                githubExecutor,
                publisher,
                {
                    ApiRoot = Uri "https://api.github.test/"
                    Repository = "FS-GG/.github"
                    IssueNumber = 21
                    Principal = "pilot-route"
                    BaseRef = "main"
                    RoutineOperation = "source-change"
                    ClaimLease = TimeSpan.FromMinutes 30.
                },
                clock
            )

        let callbackPreparation =
            if mutation = "runner" then
                { preparation with
                    LaunchIntent =
                        { preparation.LaunchIntent with
                            Key =
                                { preparation.LaunchIntent.Key with
                                    Generation = 99L
                                }
                        }
                }
            else
                preparation

        let callbacks =
            MainProductionCallbacks(
                clock,
                actor,
                Unchecked.defaultof<RemoteExecutorProvider>,
                candidates,
                github,
                callbackPreparation
            )

        let counts = Array.zeroCreate<int> 7

        let mutable pending =
            TaskCompletionSource<Result<HostedWriterProviderReadback, string>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )

        let mutable dispatchStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable running: Task<MainEffectDriveResult> option = None
        let mutable observed: HostedWriterProviderReadback option = None
        let mutable reconciled: HostedWriterProviderReadback option = None
        let mutable advances = 0

        let hosted stage (intent: EffectIntent) =
            { W.hosted stage with
                OperationId = intent.OperationId
                RouteId = route.RouteId
                AttemptId = route.AttemptId
                CandidateId = route.CandidateId
                RepositoryNodeId = route.RepositoryNodeId
                Generation = route.Generation
                WorkflowRevision = route.WorkflowRevision
                ObservedAt = now
                ProviderResourceId =
                    match stage with
                    | 0 -> route.ClaimResourceId
                    | 1 -> string (Id.attemptValue route.AttemptId)
                    | 2 -> string (Id.candidateValue route.CandidateId)
                    | 4
                    | 5 -> "PR_node"
                    | _ -> route.BranchRef
            }

        let call stage route intent ct =
            task {
                counts[stage] <- counts[stage] + 1

                let! fact =
                    if stage = 1 then
                        task {
                            let! value = callbacks.Calls.DispatchRunner route intent ct in
                            return value |> Result.map HostedEffect
                        }
                    elif stage = 6 then
                        task {
                            let! value = callbacks.Calls.ReadNativeDelivery route intent ct in
                            return value |> Result.map NativeDelivery
                        }
                    else
                        Task.FromResult(Ok(HostedEffect(hosted stage intent)))

                observed <- fact |> Result.toOption
                dispatchStarted.TrySetResult() |> ignore
                return! pending.Task
            }

        let hostedCall stage route intent ct =
            task {
                let! value = call stage route intent ct

                return
                    value
                    |> Result.bind (function
                        | HostedEffect value -> Ok value
                        | _ -> Error "wrong readback kind")
            }

        let adapter =
            HostedWriterProviderAdapter.Create
                {
                    AcquireExternalClaim = hostedCall 0
                    DispatchRunner = hostedCall 1
                    StoreCandidate = hostedCall 2
                    PublishCandidateBranch = hostedCall 3
                    CreatePullRequest = hostedCall 4
                    MergePullRequest = hostedCall 5
                    ReadNativeDelivery =
                        fun route intent ct ->
                            task {
                                let! value = call 6 route intent ct

                                return
                                    value
                                    |> Result.bind (function
                                        | NativeDelivery value -> Ok value
                                        | _ -> Error "wrong readback kind")
                            }
                }

        let makeDriver () =
            MainEffectDriver(
                clock,
                store,
                work,
                "pilot-route",
                adapter,
                (fun _ _ _ -> Task.FromResult(reconciled |> Option.map Ok |> Option.defaultValue (Error "unknown"))),
                advance =
                    (fun _ intent _ ct ->
                        task {
                            advances <- advances + 1
                            return! workflow.Advance(preparation, intent, Some receipt, ct)
                        })
            )

        let mutable driver = makeDriver ()

        let stageOf (state: State) =
            operations
            |> List.takeWhile (fun id ->
                match Map.tryFind id state.Operations with
                | Some(OperationState.Settled(_, Applied _)) -> true
                | _ -> false)
            |> List.length

        let mutable stage = 0
        let mutable last: MainEffectDriveResult option = None

        try
            for milestone in scenario.Milestones.Tail do
                let context = $"{scenarioId} raw[{milestone.StateIndex}] {milestone.QuintAction}"

                match milestone.Action with
                | "recordIntent" -> () // The real workflow durably records the next intent during continuation.
                | "dispatch" ->
                    pending <- TaskCompletionSource<_>(TaskCreationOptions.RunContinuationsAsynchronously)
                    dispatchStarted <- TaskCompletionSource<_>(TaskCreationOptions.RunContinuationsAsynchronously)
                    running <- Some(driver.Drive(operations[stage], token))

                    let! first =
                        Task
                            .WhenAny(dispatchStarted.Task :> Task, running.Value :> Task)
                            .WaitAsync(TimeSpan.FromSeconds 15.)

                    if first = (running.Value :> Task) && not dispatchStarted.Task.IsCompleted then
                        failwith $"{context}: dispatch refused {running.Value.Result}"

                    do! dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds 10.)
                | "observeApplied" ->
                    let fact =
                        observed
                        |> Option.defaultWith (fun () -> failwith (context + " provider fact unavailable"))

                    let fact =
                        match fact, mutation with
                        | HostedEffect value, "generation" ->
                            HostedEffect
                                { value with
                                    Generation = Id.generation 99L
                                }
                        | HostedEffect value, "operation" ->
                            HostedEffect
                                { value with
                                    OperationId = Id.operation (Guid.NewGuid())
                                }
                        | HostedEffect value, "candidate" ->
                            HostedEffect
                                { value with
                                    CandidateId = Id.candidate (Guid.NewGuid())
                                }
                        | HostedEffect value, "repository" ->
                            HostedEffect
                                { value with
                                    RepositoryNodeId = "foreign"
                                }
                        | _ -> fact

                    pending.SetResult(Ok fact)
                    let! result = running.Value.WaitAsync(TimeSpan.FromSeconds 10.)
                    last <- Some result

                    if mutation = "" then
                        Assert.True(
                            (match result with
                             | EffectCompleted _ -> true
                             | _ -> false),
                            $"{context}: {result}"
                        )
                | "loseResponse" ->
                    pending.SetResult(Error "github-timeout-unknown")
                    let! result = running.Value.WaitAsync(TimeSpan.FromSeconds 10.)
                    last <- Some result
                    // Reconstruct the actual driver across the ambiguity boundary.
                    driver <- makeDriver ()
                | "reconcileApplied" ->
                    reconciled <- observed
                    let before = counts[stage]
                    let! result = driver.Drive(operations[stage], token)
                    last <- Some result
                    Assert.Equal(before, counts[stage])
                | "reconcileAbsent" ->
                    let! state = recover store work

                    let intent =
                        match state.Operations[operations[stage]] with
                        | NeedsObservation(i, _) -> i
                        | _ -> failwith context

                    reconciled <-
                        Some(
                            HostedEffect
                                { hosted stage intent with
                                    Exists = false
                                    CandidateHeadSha = None
                                    ResultSha = None
                                    ProviderResourceId = intent.ResourceId
                                }
                        )

                    let before = advances
                    let! result = driver.Drive(operations[stage], token)
                    last <- Some result
                    Assert.True((advances = before), context + ": absence must not advance the workflow")

                    Assert.True(
                        match result with
                        | EffectProvenAbsent _ -> true
                        | _ -> false
                    )

                    let callsBefore = counts[stage]
                    let! duplicateAbsence = driver.Drive(operations[stage], token)

                    Assert.True(
                        match duplicateAbsence with
                        | EffectProvenAbsent _ -> true
                        | _ -> false
                    )

                    Assert.Equal(callsBefore, counts[stage])
                    Assert.Equal(before, advances)
                | "retryProvenAbsent" ->
                    let! accepted = append store work (AuthorizeEffectRetry operations[stage])
                    requireAccepted accepted
                | "restartPaused" ->
                    let! accepted = append store work (RecordStartupPause "choreo-restart")
                    requireAccepted accepted
                    driver <- makeDriver ()
                    let! refused = append store work Resume
                    Assert.Equal(ReceiptDisposition.Rejected, refused.Disposition)
                | "recoverJournal" ->
                    let! state = recover store work
                    Assert.False(state.ReadbackCurrent)
                    let! refused = driver.Drive(operations[stage], token)
                    Assert.Equal(EffectDriveRefused "effect-authority-not-current", refused)
                    Assert.Equal(0, counts |> Array.sum)
                | "reconnect" ->
                    let! accepted = append store work (RecordHostedRouteReadback preparation.Readback)
                    requireAccepted accepted
                    let! state = recover store work
                    Assert.True(state.ReadbackCurrent)

                    Assert.True(
                        match state.Control with
                        | Paused _ -> true
                        | _ -> false
                    )
                | "resume" ->
                    let! accepted = append store work Resume
                    requireAccepted accepted
                | "rejectDuplicateResponse" ->
                    let readback =
                        match observed.Value with
                        | HostedEffect value -> value
                        | _ -> failwith context

                    let! refused =
                        append store work (RecordHostedEffectReadback(operations[0], readback))

                    Assert.Equal(ReceiptDisposition.Rejected, refused.Disposition)
                    Assert.Equal(1, counts[0])
                | "rejectStaleGeneration"
                | "rejectWrongIdentity" ->
                    let! state = recover store work

                    let intent =
                        match state.Operations[operations[0]] with
                        | IntentRecorded value -> value
                        | _ -> failwith context

                    let fact = hosted 0 intent

                    let invalid =
                        if milestone.Action = "rejectStaleGeneration" then
                            { fact with
                                Generation = Id.generation 99L
                            }
                        else
                            { fact with
                                OperationId = Id.operation (Guid.NewGuid())
                            }

                    let! refused = append store work (RecordHostedEffectReadback(operations[0], invalid))
                    Assert.Equal(ReceiptDisposition.Rejected, refused.Disposition)
                    Assert.Equal(0, counts |> Array.sum)
                | action -> failwith $"unbound production trace action {action}"

                let! state = recover store work
                stage <- stageOf state

                let paused =
                    match state.Control with
                    | Paused _ -> true
                    | _ -> false

                Assert.Equal(milestone.Snapshot.Paused, paused)
                Assert.Equal(milestone.Snapshot.AuthorityFresh, state.ReadbackCurrent)

                Assert.True(
                    (stage = milestone.Snapshot.Stage),
                    $"{context}: expected completed stage {milestone.Snapshot.Stage}, actual {stage}; result={last}"
                )

                if milestone.Action = "dispatch" then
                    Assert.True(
                        (match state.Operations[operations[stage]] with
                         | Dispatching _ -> true
                         | _ -> false),
                        context
                    )

                if milestone.Action = "loseResponse" then
                    Assert.True(
                        (match state.Operations[operations[stage]] with
                         | NeedsObservation _ -> true
                         | _ -> false),
                        context
                    )

            if scenarioId = "missing-native-readback" then
                let! state = recover store work
                Assert.Empty(state.NativeDeliveryReadbacks)

                let intent =
                    match state.Operations[operations[6]] with
                    | IntentRecorded value -> value
                    | _ -> failwith "missing native intent"

                let! missing = callbacks.Calls.ReadNativeDelivery route intent token
                Assert.Equal(Error "github-native-delivery-not-observed", missing)

            if scenarioId = "happy-path" then
                Assert.Equal(1, provider.Launches)

                let! reconnected =
                    actor.Ask<CoordinationResult>(
                        box (Reconnect preparation.LaunchIntent.Key),
                        TimeSpan.FromSeconds 10.
                    )

                Assert.True(
                    match reconnected with
                    | SessionAdvanced _
                    | SessionDuplicate _ -> true
                    | _ -> false
                )

                let wrong =
                    { preparation.LaunchIntent with
                        Key =
                            { preparation.LaunchIntent.Key with
                                Generation = 99L
                            }
                    }

                let! rejected =
                    actor.Ask<CoordinationResult>(box (Launch wrong), TimeSpan.FromSeconds 10.)

                Assert.Equal(SessionRefused "execution-key-conflict", rejected)
                Assert.Equal(1, provider.Launches)
                Assert.NotEmpty requests

            return stage
        finally
            pending.TrySetResult(Error "test-disposed") |> ignore
            system.Terminate().GetAwaiter().GetResult()
    }

let runMemory scenario mutation =
    task {
        let store = F.MemoryJournal()
        let execution = F.MemoryExecutor(fun () -> store.State)
        return! replay store execution execution scenario mutation
    }

[<Theory>]
[<InlineData("happy-path")>]
[<InlineData("lost-applied")>]
[<InlineData("proven-absent-retry")>]
[<InlineData("restart-gates")>]
[<InlineData("duplicate-response")>]
[<InlineData("stale-generation")>]
[<InlineData("wrong-identity")>]
[<InlineData("missing-native-readback")>]
let ``Quint milestones drive production workflow effect pump and execution actor`` scenario =
    task {
        let! _ = runMemory scenario ""
        return ()
    }

[<Theory>]
[<InlineData("generation")>]
[<InlineData("operation")>]
[<InlineData("candidate")>]
[<InlineData("repository")>]
let ``production readback mutations diverge at the first Quint settlement`` mutation =
    task {
        let! error =
            Assert.ThrowsAnyAsync<Exception>(fun () -> runMemory "happy-path" mutation :> Task)

        Assert.Contains("raw[9] hostSettles", error.Message)
    }

[<Fact>]
let ``stale journal sequence refuses before the Quint dispatch reaches a provider`` () =
    task {
        let! error =
            Assert.ThrowsAnyAsync<Exception>(fun () -> runMemory "happy-path" "journal" :> Task)

        Assert.Contains("raw[4] journalRecordsDispatch", error.Message)
    }

[<Fact>]
let ``missing native provider evidence diverges at its first real callback`` () =
    task {
        let! error =
            Assert.ThrowsAnyAsync<Exception>(fun () -> runMemory "happy-path" "native" :> Task)

        Assert.Contains("raw[63] hostSettles", error.Message)
    }

[<Fact>]
let ``crossed runner generation diverges at the Quint process settlement`` () =
    task {
        let! error =
            Assert.ThrowsAnyAsync<Exception>(fun () -> runMemory "happy-path" "runner" :> Task)

        Assert.Contains("raw[18] hostSettles", error.Message)
    }
