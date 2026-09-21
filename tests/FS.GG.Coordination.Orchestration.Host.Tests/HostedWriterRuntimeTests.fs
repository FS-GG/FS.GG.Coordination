module FS.GG.Coordination.Orchestration.Host.Tests.HostedWriterRuntimeTests

open System
open System.Collections.Generic
open System.IO
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

module private Fixture =
    let now = DateTimeOffset.Parse "2026-09-10T19:00:00Z"
    let guid (value: string) = Guid.Parse value
    let workItem = WorkItemIdentity.create "R_writer" 7L "I_writer" 11L
    let attempt = Id.attempt (guid "10000000-0000-0000-0000-000000000001")
    let candidate = Id.candidate (guid "20000000-0000-0000-0000-000000000001")

    let operations =
        [ 1..7 ]
        |> List.map (fun value -> Id.operation (guid ($"30000000-0000-0000-0000-{value:D12}")))

    let route =
        {
            RouteId = guid "40000000-0000-0000-0000-000000000001"
            WorkItemId = workItem
            JobClass = "routine-documentation-delivery"
            AttemptId = attempt
            CandidateId = candidate
            RepositoryNodeId = "R_repo"
            BranchRef = "refs/heads/fsgg/pilot/docs"
            ClaimResourceId = "claim-1"
            ClaimOperationId = operations[0]
            ProcessOperationId = operations[1]
            CandidateOperationId = operations[2]
            BranchOperationId = operations[3]
            PullRequestOperationId = operations[4]
            MergeOperationId = operations[5]
            ReadbackOperationId = operations[6]
            Generation = Id.generation 1L
            WorkflowRevision = Id.revision 8L
            SelectedAt = now.AddMinutes -1.
        }

    let intent kind operationId =
        let resource =
            match kind with
            | AcquireExternalClaim -> route.ClaimResourceId
            | DispatchRunner -> Id.attemptValue route.AttemptId |> string
            | StoreCandidate -> Id.candidateValue route.CandidateId |> string
            | PublishCandidateBranch
            | CreatePullRequest
            | MergePullRequest
            | ReadNativeDelivery -> route.BranchRef
            | _ -> "outside"

        {
            OperationId = operationId
            Kind = kind
            Generation = route.Generation
            WorkflowRevision = route.WorkflowRevision
            ResourceId = resource
            PayloadSha256 = String.replicate 64 "a"
        }

    let hosted (intentValue: EffectIntent) =
        {
            OperationId = intentValue.OperationId
            RouteId = route.RouteId
            AttemptId = route.AttemptId
            CandidateId = route.CandidateId
            RepositoryNodeId = route.RepositoryNodeId
            ProviderResourceId = "provider-resource"
            CandidateHeadSha = None
            ResultSha = None
            ProviderRevision = "provider-revision"
            Generation = route.Generation
            WorkflowRevision = route.WorkflowRevision
            ObservedAt = now
            Exists = true
        }

    let native (intentValue: EffectIntent) =
        {
            OperationId = intentValue.OperationId
            RouteId = route.RouteId
            AttemptId = route.AttemptId
            CandidateId = route.CandidateId
            RepositoryNodeId = route.RepositoryNodeId
            PullRequestNodeId = "PR_node"
            CandidateHeadSha = String.replicate 40 "a"
            ObservedPullRequestHeadSha = String.replicate 40 "a"
            MergeCommitSha = String.replicate 40 "b"
            ProviderRevision = "provider-revision"
            Generation = route.Generation
            WorkflowRevision = route.WorkflowRevision
            ObservedAt = now
            Merged = true
        }

    let executorCommand commandId =
        let unsigned =
            {
                Schema = ExecutorWire.commandSchemaV2
                CommandId = commandId
                BodySha256 = ""
                Kind = "reconcile"
                WorkItemPersistenceId = WorkItemIdentity.persistenceId workItem
                RouteOperationId = Id.operationValue route.ProcessOperationId
                AssignmentId = Id.operationValue route.ProcessOperationId
                AttemptId = Id.attemptValue attempt
                CandidateId = Id.candidateValue candidate
                Generation = 1L
                ExpectedRevision = 1L
                RecordedAt = now
                Deadline = now.AddMinutes 30.
                MaximumRuntimeSeconds = 1800L
                MaximumAttempts = 1
                Workspace = "pilot"
                WorkspaceManifestSha256 = String.replicate 64 "a"
                RequestedModel = null
                RequestedEffort = null
                InputDigest = String.replicate 64 "b"
                ExecutorBinding = "executor-1"
                ProviderSessionReference = null
                ArtifactDigest = null
                ContentOffset = 0L
                ContentLength = 0
                ParentAttemptId = Nullable()
                ParentGeneration = Nullable()
                TelemetryRelation = null
            }

        { unsigned with
            BodySha256 = ExecutorWire.commandV2Digest unsigned
        }

type private FixedClock(now: DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow() = now

type private FixedHttpHandler(send: HttpRequestMessage * CancellationToken -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, cancellationToken) = send (request, cancellationToken)

[<Fact>]
let ``sealed adapter exposes exactly the seven route-bound operations`` () =
    task {
        let invoked = ResizeArray<EffectKind>()

        let hosted kind (_: HostedRoutePlan) (intentValue: EffectIntent) (_: CancellationToken) =
            invoked.Add kind
            Task.FromResult(Ok(Fixture.hosted intentValue))

        let native (_: HostedRoutePlan) (intentValue: EffectIntent) (_: CancellationToken) =
            invoked.Add ReadNativeDelivery
            Task.FromResult(Ok(Fixture.native intentValue))

        let adapter =
            HostedWriterProviderAdapter.Create
                {
                    AcquireExternalClaim = hosted AcquireExternalClaim
                    DispatchRunner = hosted DispatchRunner
                    StoreCandidate = hosted StoreCandidate
                    PublishCandidateBranch = hosted PublishCandidateBranch
                    CreatePullRequest = hosted CreatePullRequest
                    MergePullRequest = hosted MergePullRequest
                    ReadNativeDelivery = native
                }

        let kinds =
            [
                AcquireExternalClaim, Fixture.route.ClaimOperationId
                DispatchRunner, Fixture.route.ProcessOperationId
                StoreCandidate, Fixture.route.CandidateOperationId
                PublishCandidateBranch, Fixture.route.BranchOperationId
                CreatePullRequest, Fixture.route.PullRequestOperationId
                MergePullRequest, Fixture.route.MergeOperationId
                ReadNativeDelivery, Fixture.route.ReadbackOperationId
            ]

        for kind, operationId in kinds do
            let! result =
                adapter.Dispatch(Fixture.route, Fixture.intent kind operationId, CancellationToken.None)

            Assert.True(Result.isOk result)

        Assert.Equal<EffectKind list>(kinds |> List.map fst, List.ofSeq invoked)
        let outside = Fixture.intent ReleaseExternalClaim Fixture.route.ClaimOperationId
        let! refused = adapter.Dispatch(Fixture.route, outside, CancellationToken.None)
        Assert.Equal(Error "effect-is-not-bound-to-selected-hosted-route", refused)
        let wrong = Fixture.intent MergePullRequest Fixture.route.ClaimOperationId
        let! mismatched = adapter.Dispatch(Fixture.route, wrong, CancellationToken.None)
        Assert.Equal(Error "effect-is-not-bound-to-selected-hosted-route", mismatched)

        let staleReadbackCalls =
            {
                AcquireExternalClaim =
                    fun route intentValue _ ->
                        Task.FromResult(
                            Ok
                                { Fixture.hosted intentValue with
                                    RouteId = Guid.NewGuid()
                                }
                        )
                DispatchRunner = hosted DispatchRunner
                StoreCandidate = hosted StoreCandidate
                PublishCandidateBranch = hosted PublishCandidateBranch
                CreatePullRequest = hosted CreatePullRequest
                MergePullRequest = hosted MergePullRequest
                ReadNativeDelivery = native
            }

        let staleAdapter = HostedWriterProviderAdapter.Create staleReadbackCalls

        let! stale =
            staleAdapter.Dispatch(
                Fixture.route,
                Fixture.intent AcquireExternalClaim Fixture.route.ClaimOperationId,
                CancellationToken.None
            )

        Assert.Equal(Error "provider-readback-is-not-bound-to-selected-hosted-route", stale)
    }

[<Fact>]
let ``work item append binds deterministic event ids and effect metadata`` () =
    let operationId = Fixture.route.ClaimOperationId
    let intent = Fixture.intent AcquireExternalClaim operationId
    let commandId = Id.command (Fixture.guid "50000000-0000-0000-0000-000000000001")

    let envelope =
        {
            CommandId = commandId
            ProtocolVersion = Id.protocolVersion 1 0
            ExpectedRevision = initial.Revision
            ExpectedGeneration = initial.Generation
            PrincipalId = "pilot"
            SessionId = None
            IssuedAt = Fixture.now
            ExpiresAt = Fixture.now.AddMinutes 1.
            Command = RecordEffectIntent intent
        }

    let receipt =
        {
            CommandId = commandId
            BodySha256 = canonicalEnvelopeSha256 envelope
            Disposition = Accepted
            Revision = Id.revision 2L
            ProtocolVersion = Id.protocolVersion 1 0
            Detail = "fixture"
        }

    let decision =
        {
            Events =
                [
                    EffectIntentRecorded intent
                    EffectSettled(operationId, Applied "revision")
                    CommandRecorded receipt
                ]
            Effects = [ intent ]
            Receipt = receipt
        }

    let persistenceId = WorkItemIdentity.persistenceId Fixture.workItem

    let first =
        HostedWriterJournal.appendRequest persistenceId Fixture.now 0L initial envelope decision

    let replay =
        HostedWriterJournal.appendRequest persistenceId Fixture.now 0L initial envelope decision

    Assert.Equal(first, replay)

    Assert.Equal<EffectChange list>(
        [ IntentAdded intent; Settled operationId; NoEffect ],
        first.Events |> List.map _.EffectChange
    )

    Assert.All(first.Events, fun stored -> Assert.NotEqual(Guid.Empty, stored.EventId))
    Assert.Equal(canonicalEnvelopeSha256 envelope, first.Inbox.BodySha256)

type private FixedStore(recovery: RecoveryResult) =
    interface IJournalStore with
        member _.CheckReadiness _ = Task.FromResult(Ok())
        member _.Recover(_, _) = Task.FromResult(Ok recovery)
        member _.Append(_, _) = Task.FromResult(InvalidAppend "unused")
        member _.SaveSnapshot(_, _) = Task.FromResult(Ok())
        member _.SaveProjectionCheckpoint(_, _) = Task.FromResult(Ok())

type private MemoryStore(initialEvents: Event list) =
    let persistenceId = WorkItemIdentity.persistenceId Fixture.workItem

    let mutable events =
        initialEvents
        |> List.mapi (fun index eventValue ->
            let payload = EventEnvelope.encode eventValue

            {
                PersistenceId = persistenceId
                Sequence = int64 (index + 1)
                EventId = Guid.NewGuid()
                SchemaVersion = 1
                SerializerVersion = EventEnvelope.serializerVersion
                Payload = payload
                PayloadSha256 =
                    System.Security.Cryptography.SHA256.HashData payload
                    |> Convert.ToHexString
                    |> _.ToLowerInvariant()
                EffectChange =
                    (match eventValue with
                     | EffectIntentRecorded intent -> IntentAdded intent
                     | EffectSettled(id, _) -> EffectChange.Settled id
                     | _ -> NoEffect)
                RecordedAt = Fixture.now
            })

    let commands = Dictionary<CommandId, string * int64>()

    interface IJournalStore with
        member _.CheckReadiness _ = Task.FromResult(Ok())

        member _.Recover(_, _) =
            let state =
                events
                |> List.map (fun stored -> EventEnvelope.tryDecode stored.Payload |> Result.defaultWith failwith)
                |> replay

            let unsettled =
                state.Operations
                |> Map.toList
                |> List.choose (fun (_, value) ->
                    match value with
                    | IntentRecorded i
                    | Dispatching i
                    | NeedsObservation(i, _) -> Some i
                    | _ -> None)

            Task.FromResult(
                Ok
                    {
                        Events = events
                        Snapshot = None
                        UnsettledEffects = unsettled
                        RequiresExternalReconciliation = not unsettled.IsEmpty
                    }
            )

        member _.Append(request, _) =
            match commands.TryGetValue request.Inbox.CommandId with
            | true, (digest, sequence) when digest = request.Inbox.BodySha256 -> Task.FromResult(Duplicate sequence)
            | true, _ -> Task.FromResult Conflict
            | _ when request.ExpectedSequence <> int64 events.Length ->
                Task.FromResult(WrongExpectedSequence(int64 events.Length))
            | _ ->
                events <- events @ request.Events
                let tail = int64 events.Length
                commands[request.Inbox.CommandId] <- (request.Inbox.BodySha256, tail)
                Task.FromResult(Appended tail)

        member _.SaveSnapshot(_, _) = Task.FromResult(Ok())
        member _.SaveProjectionCheckpoint(_, _) = Task.FromResult(Ok())

[<Fact>]
let ``startup pause is established before work item admission`` () =
    task {
        let store = MemoryStore [] :> IJournalStore

        let! result =
            HostedWriterJournal.persistStartupPause
                (FixedClock Fixture.now)
                store
                Fixture.workItem
                "pilot"
                CancellationToken.None

        Assert.True(Result.isOk result)

        let! recovered =
            HostedWriterJournal.recover store Fixture.workItem CancellationToken.None

        let state =
            (match recovered with
             | Ok value -> value
             | Error failures -> failwithf "%A" failures)
                .State

        Assert.Equal(None, state.WorkItemId)
        Assert.Equal(Paused "process-startup", state.Control)
    }

let private activeClaimEvents () =
    let snapshot =
        {
            ProjectId = Id.project (Guid.NewGuid())
            WorkItemId = Fixture.workItem
            WorkflowRevision = Fixture.route.WorkflowRevision
            CanonicalSha256 = String.replicate 64 "b"
            BoardMembershipIds = []
            CapturedAt = Fixture.now.AddMinutes(-2.)
        }

    let budget =
        {
            Schema = "fsgg.coordination.subscription-execution-budget/2"
            AttemptLimit = 1
            MaximumRuntime = TimeSpan.FromMinutes 30.
            ExecutionDeadline = Fixture.now.AddMinutes 25.
            DeliveryDeadline = Fixture.now.AddHours 2.
            Usage = TokensUnknown "not-reported"
            Cost =
                {
                    InvocationState = "not-applicable"
                    InvocationProvenance = "subscription"
                    BroaderAttributionState = "unknown"
                    BroaderAttributionProvenance = "unattributed"
                }
        }

    let reservation =
        {
            ReservationId = Id.reservation (Guid.NewGuid())
            Generation = Fixture.route.Generation
            ExpiresAt = Fixture.now.AddMinutes 20.
            RequiredClaimIds = Set.empty
        }

    let intent = Fixture.intent AcquireExternalClaim Fixture.route.ClaimOperationId

    [
        SubscriptionWorkAdmitted(snapshot, budget)
        GenerationAdvanced Fixture.route.Generation
        ReservationCreated reservation
        HostedRouteSelected Fixture.route
        ResumedEvent
        EffectIntentRecorded intent
    ],
    intent

[<Fact>]
let ``legacy subscription event cannot gain a delivery deadline during replay`` () =
    let events, _ = activeClaimEvents ()
    let current = EventEnvelope.encode events.Head
    Assert.True(EventEnvelope.tryDecode current |> Result.isOk)
    let json = Encoding.UTF8.GetString current

    let legacy =
        Text.RegularExpressions.Regex
            .Replace(json, ",?\"deliveryDeadline\":\"[^\"]+\"", "")
            .Replace(
                "fsgg.coordination.subscription-execution-budget/2",
                "fsgg.coordination.subscription-execution-budget/1"
            )
        |> Encoding.UTF8.GetBytes

    Assert.True(EventEnvelope.tryDecode legacy |> Result.isError)

[<Fact>]
let ``duplicate durable command returns original accepted receipt after response loss`` () =
    task {
        let events, _ = activeClaimEvents ()
        let store = MemoryStore events :> IJournalStore
        let commandId = Id.command (Guid.Parse "70000000-0000-0000-0000-000000000001")

        let envelope =
            {
                CommandId = commandId
                ProtocolVersion = Id.protocolVersion 1 0
                ExpectedRevision = Id.revision (int64 events.Length)
                ExpectedGeneration = Fixture.route.Generation
                PrincipalId = "pilot"
                SessionId = None
                IssuedAt = Fixture.now
                ExpiresAt = Fixture.now.AddMinutes 1.
                Command = MarkEffectDispatching Fixture.route.ClaimOperationId
            }

        let! first =
            HostedWriterJournal.decideAndAppend
                (FixedClock Fixture.now)
                store
                Fixture.workItem
                envelope
                CancellationToken.None

        let! replayed =
            HostedWriterJournal.decideAndAppend
                (FixedClock Fixture.now)
                store
                Fixture.workItem
                envelope
                CancellationToken.None

        let firstDecision, _ = Result.defaultWith failwith first
        let replayDecision, _ = Result.defaultWith failwith replayed
        Assert.Equal(ReceiptDisposition.Accepted, firstDecision.Receipt.Disposition)
        Assert.Equal(firstDecision.Receipt, replayDecision.Receipt)
    }

[<Fact>]
let ``historical hosted absence survives durable append and restart`` () =
    task {
        let events, intent = activeClaimEvents ()

        let historicalEvents =
            events
            @ [
                EffectDispatchStarted intent.OperationId
                EffectObservationRequired(intent.OperationId, "process-launch-not-observed")
                RevokedEvent "generation-replaced"
                GenerationAdvanced(Id.generation 2L)
            ]

        let store = MemoryStore historicalEvents :> IJournalStore
        let commandId = Id.command (Guid.Parse "70010000-0000-0000-0000-000000000001")

        let envelope =
            {
                CommandId = commandId
                ProtocolVersion = Id.protocolVersion 1 0
                ExpectedRevision = Id.revision (int64 historicalEvents.Length)
                ExpectedGeneration = Id.generation 2L
                PrincipalId = "pilot"
                SessionId = None
                IssuedAt = Fixture.now
                ExpiresAt = Fixture.now.AddMinutes 1.
                Command = ObserveEffect(intent.OperationId, ProvenAbsent)
            }

        let! first =
            HostedWriterJournal.decideAndAppend
                (FixedClock Fixture.now)
                store
                Fixture.workItem
                envelope
                CancellationToken.None

        let firstDecision, _ = Result.defaultWith failwith first
        Assert.Equal(ReceiptDisposition.Accepted, firstDecision.Receipt.Disposition)
        Assert.Equal("historical-hosted-effect-absence-settled", firstDecision.Receipt.Detail)

        let! replayed =
            HostedWriterJournal.decideAndAppend
                (FixedClock Fixture.now)
                store
                Fixture.workItem
                envelope
                CancellationToken.None

        let replayDecision, _ = Result.defaultWith failwith replayed
        Assert.Equal(firstDecision.Receipt, replayDecision.Receipt)

        let! recovered =
            HostedWriterJournal.recover store Fixture.workItem CancellationToken.None

        let recoveredResult =
            match recovered with
            | Ok value -> value
            | Error failures -> failwithf "%A" failures

        Assert.Equal(OperationState.Settled(intent, ProvenAbsent), recoveredResult.State.Operations[intent.OperationId])
        Assert.False(recoveredResult.RequiresExternalReconciliation)
    }

[<Fact>]
let ``paused restart after delivery expiry reconciles dispatch without repeating provider mutation`` () =
    task {
        let events, intent = activeClaimEvents ()

        let store =
            MemoryStore(
                events
                @ [ EffectDispatchStarted intent.OperationId; StartupPausedEvent "restart" ]
            )
            :> IJournalStore

        let mutable writes = 0
        let mutable reconciles = 0

        let hosted =
            {
                OperationId = intent.OperationId
                RouteId = Fixture.route.RouteId
                AttemptId = Fixture.route.AttemptId
                CandidateId = Fixture.route.CandidateId
                RepositoryNodeId = Fixture.route.RepositoryNodeId
                ProviderResourceId = Fixture.route.ClaimResourceId
                CandidateHeadSha = None
                ResultSha = None
                ProviderRevision = "claim-revision"
                Generation = Fixture.route.Generation
                WorkflowRevision = Fixture.route.WorkflowRevision
                ObservedAt = Fixture.now
                Exists = true
            }

        let call _ _ _ =
            writes <- writes + 1
            Task.FromResult(Ok hosted)

        let adapter =
            HostedWriterProviderAdapter.Create
                {
                    AcquireExternalClaim = call
                    DispatchRunner = call
                    StoreCandidate = call
                    PublishCandidateBranch = call
                    CreatePullRequest = call
                    MergePullRequest = call
                    ReadNativeDelivery = fun _ _ _ -> Task.FromResult(Error "unused")
                }

        let reconcile _ _ _ =
            reconciles <- reconciles + 1
            Task.FromResult(Ok(HostedEffect hosted))

        let driver =
            MainEffectDriver(FixedClock(Fixture.now.AddHours 2.), store, Fixture.workItem, "pilot", adapter, reconcile)

        let! result = driver.Drive(intent.OperationId, CancellationToken.None)

        match result with
        | EffectCompleted _ -> ()
        | other -> failwithf "%A" other

        Assert.Equal(0, writes)
        Assert.Equal(1, reconciles)
    }

[<Fact>]
let ``pending preflight remains undispatched then lost response is reconcile only`` () =
    task {
        let events, intent = activeClaimEvents ()
        let store = MemoryStore events :> IJournalStore
        let mutable preflights = 0
        let mutable writes = 0
        let mutable reconciles = 0

        let preflight _ _ _ =
            preflights <- preflights + 1

            Task.FromResult(
                if preflights = 1 then
                    Error "github-required-checks-not-green"
                else
                    Ok()
            )

        let mutate _ _ _ =
            writes <- writes + 1
            Task.FromResult(Error "github-timeout-unknown")

        let unused _ _ _ = Task.FromResult(Error "unused")

        let adapter =
            HostedWriterProviderAdapter.Create
                {
                    AcquireExternalClaim = mutate
                    DispatchRunner = unused
                    StoreCandidate = unused
                    PublishCandidateBranch = unused
                    CreatePullRequest = unused
                    MergePullRequest = unused
                    ReadNativeDelivery = fun _ _ _ -> Task.FromResult(Error "unused")
                }

        let reconcile _ _ _ =
            reconciles <- reconciles + 1
            Task.FromResult(Error "github-native-delivery-not-observed")

        let driver =
            MainEffectDriver(
                FixedClock Fixture.now,
                store,
                Fixture.workItem,
                "pilot",
                adapter,
                reconcile,
                preflight = preflight
            )

        let! pending = driver.Drive(intent.OperationId, CancellationToken.None)
        Assert.Equal(EffectDriveRefused "github-required-checks-not-green", pending)
        Assert.Equal(0, writes)
        let! ambiguous = driver.Drive(intent.OperationId, CancellationToken.None)

        match ambiguous with
        | EffectNeedsExternalReconciliation _ -> ()
        | other -> failwithf "%A" other

        Assert.Equal(1, writes)
        let! afterRestart = driver.Drive(intent.OperationId, CancellationToken.None)

        match afterRestart with
        | EffectNeedsExternalReconciliation _ -> ()
        | other -> failwithf "%A" other

        Assert.Equal(1, writes)
        Assert.Equal(1, reconciles)
    }

[<Fact>]
let ``expired delivery authority refuses before external preflight`` () =
    task {
        let events, intent = activeClaimEvents ()
        let store = MemoryStore events :> IJournalStore
        let mutable preflights = 0

        let preflight _ _ _ =
            preflights <- preflights + 1
            Task.FromResult(Ok())

        let unused _ _ _ = Task.FromResult(Error "unused")

        let adapter =
            HostedWriterProviderAdapter.Create
                {
                    AcquireExternalClaim = unused
                    DispatchRunner = unused
                    StoreCandidate = unused
                    PublishCandidateBranch = unused
                    CreatePullRequest = unused
                    MergePullRequest = unused
                    ReadNativeDelivery = fun _ _ _ -> Task.FromResult(Error "unused")
                }

        let expired =
            MainEffectDriver(
                FixedClock(Fixture.now.AddHours 2.),
                store,
                Fixture.workItem,
                "pilot",
                adapter,
                (fun _ _ _ -> Task.FromResult(Error "unused")),
                preflight = preflight
            )

        let! refused = expired.Drive(intent.OperationId, CancellationToken.None)
        Assert.Equal(EffectDriveRefused "effect-authority-not-current", refused)
        Assert.Equal(0, preflights)
    }

[<Fact>]
let ``HTTP GitHub executor honors case-insensitive rate reset before another request`` () =
    task {
        let mutable calls = 0

        use handler =
            new FixedHttpHandler(fun _ ->
                task {
                    calls <- calls + 1
                    let response = new HttpResponseMessage(HttpStatusCode.OK)
                    response.Headers.TryAddWithoutValidation("x-rAtElImIt-ReMaInInG", "0") |> ignore

                    response.Headers.TryAddWithoutValidation(
                        "X-RATELIMIT-RESET",
                        DateTimeOffset.UtcNow.AddMinutes(1.).ToUnixTimeSeconds().ToString()
                    )
                    |> ignore

                    response.Content <- new StringContent("{}")
                    return response
                })

        use client = new HttpClient(handler)

        let executor =
            HttpGitHubRequestExecutor(client, "fixture-token", 1024) :> IGitHubRequestExecutor

        let request =
            Rest
                {
                    Method = RestMethod.Get
                    Uri = Uri "https://api.github.test/rate"
                    Headers = Map.empty
                    Body = None
                    ApiVersion = FS.GG.Coordination.GitHub.ApiVersion.required
                    Idempotency = FS.GG.Coordination.GitHub.IdempotencyClass.ReplaySafe
                }

        let! first = executor.Send(request, CancellationToken.None)

        match first with
        | Response response -> Assert.Equal(Some 0, response.RateBudget.Remaining)
        | other -> failwithf "%A" other

        use cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds 50.)
        let! second = executor.Send(request, cancelled.Token)
        Assert.Equal(TimedOut, second)
        Assert.Equal(1, calls)
    }

[<Fact>]
let ``HTTP GitHub executor honors mixed-case Retry-After on throttling`` () =
    task {
        let mutable calls = 0

        use handler =
            new FixedHttpHandler(fun _ ->
                task {
                    calls <- calls + 1
                    let response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    response.Headers.TryAddWithoutValidation("rEtRy-AfTeR", "60") |> ignore
                    response.Content <- new StringContent("{}")
                    return response
                })

        use client = new HttpClient(handler)

        let executor =
            HttpGitHubRequestExecutor(client, "fixture-token", 1024) :> IGitHubRequestExecutor

        let request =
            Rest
                {
                    Method = RestMethod.Get
                    Uri = Uri "https://api.github.test/retry"
                    Headers = Map.empty
                    Body = None
                    ApiVersion = FS.GG.Coordination.GitHub.ApiVersion.required
                    Idempotency = FS.GG.Coordination.GitHub.IdempotencyClass.ReplaySafe
                }

        let! first = executor.Send(request, CancellationToken.None)

        match first with
        | Response response -> Assert.Equal(429, response.StatusCode)
        | other -> failwithf "%A" other

        use cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds 50.)
        let! second = executor.Send(request, cancelled.Token)
        Assert.Equal(TimedOut, second)
        Assert.Equal(1, calls)
    }

[<Fact>]
let ``Main verifies complete executor bundle before durable candidate readback`` () =
    task {
        let bytes = System.Text.Encoding.UTF8.GetBytes "immutable git bundle"
        let digest = RunnerWire.sha256 bytes
        let candidateId = Id.candidateValue Fixture.candidate

        let unsigned =
            {
                Schema = ExecutorWire.artifactManifestSchema
                CommandId = Guid.NewGuid()
                CandidateId = candidateId
                BaselineObjectId = String.replicate 40 "a"
                HeadObjectId = String.replicate 40 "b"
                TreeObjectId = String.replicate 40 "c"
                BundleSha256 = digest
                BundleSizeBytes = int64 bytes.Length
                ManifestSha256 = ""
                ChunkBytes = bytes.Length
            }

        let manifest =
            { unsigned with
                ManifestSha256 = ExecutorWire.artifactManifestDigest unsigned
            }

        let chunk =
            {
                Schema = ExecutorWire.artifactContentSchema
                CommandId = manifest.CommandId
                CandidateId = candidateId
                BundleSha256 = digest
                Offset = 0L
                Final = true
                ContentBase64 = Convert.ToBase64String bytes
            }

        let mutable stored = None

        let store =
            { new ICandidateStore with
                member _.Put(value, _) =
                    stored <- Some value

                    Task.FromResult(
                        Ok
                            {
                                CandidateId = value.Candidate.CandidateId
                                ContentSha256 = value.Candidate.ContentSha256
                                ManifestSha256 = value.Candidate.ManifestSha256
                                SizeBytes = value.Candidate.SizeBytes
                                Location = value.Candidate.Location
                                StoreId = "main"
                                StoreSchemaVersion = 1
                                StorageReceiptSha256 = String.replicate 64 "d"
                                VerifiedAt = Fixture.now
                            }
                    )

                member _.Read(_, _) = Task.FromResult(Ok stored.Value)
                member _.Quarantine(_, _, _) = Task.FromResult(Ok())
                member _.CleanupUnreferenced(_, _, _) = Task.FromResult 0
            }

        let readback =
            {
                Frames =
                    [
                        ExecutorWire.encodeArtifactManifest manifest
                        ExecutorWire.encodeArtifactContent chunk
                    ]
            }

        let! accepted =
            RemoteCandidatePipeline.store
                store
                candidateId
                manifest.BaselineObjectId
                (Fixture.now.AddDays 1.)
                readback
                CancellationToken.None

        Assert.True(Result.isOk accepted)

        let! truncated =
            RemoteCandidatePipeline.store
                store
                candidateId
                manifest.BaselineObjectId
                (Fixture.now.AddDays 1.)
                {
                    Frames = [ ExecutorWire.encodeArtifactManifest manifest ]
                }
                CancellationToken.None

        Assert.Equal(Error "executor-artifact-content-refused", truncated)
    }

[<Fact>]
let ``Host relay binds duplicate commands and replays completion after response loss`` () =
    task {
        let relay = HostExecutorRelay(2, 1024 * 1024)
        let transport = relay :> IAuthenticatedExecutorTransport
        let command = Fixture.executorCommand (Guid.NewGuid())
        let frames = [ ExecutorWire.encodeCommandV2 command ]
        let first = transport.Exchange(frames, CancellationToken.None)
        let duplicate = transport.Exchange(frames, CancellationToken.None)
        let! polled = relay.Poll(CancellationToken.None)

        Assert.Equal(
            Some
                {
                    CommandId = command.CommandId
                    Frames = frames
                },
            polled
        )

        let changed =
            { command with
                ExpectedRevision = 2L
                BodySha256 = ""
            }
            |> fun value ->
                { value with
                    BodySha256 = ExecutorWire.commandV2Digest value
                }

        let! conflict =
            transport.Exchange([ ExecutorWire.encodeCommandV2 changed ], CancellationToken.None)

        Assert.Equal(Error "executor-relay-command-identity-conflict", conflict)

        let response =
            [
                ExecutorWire.encodeOperationOutcome
                    {
                        Schema = ExecutorWire.operationOutcomeSchema
                        CommandId = command.CommandId
                        BodySha256 = command.BodySha256
                        Operation = "reconcile"
                        Disposition = "unknown"
                        ProviderSessionReference = null
                        Reason = "fixture"
                        ObservedAt = Fixture.now
                    }
            ]

        Assert.Equal(Ok(), relay.Complete(command.CommandId, response))
        Assert.Equal(Ok { Frames = response }, first.Result)
        Assert.Equal(Ok { Frames = response }, duplicate.Result)
        Assert.Equal(Ok(), relay.Complete(command.CommandId, response))
        let! replayed = transport.Exchange(frames, CancellationToken.None)
        Assert.Equal(Ok { Frames = response }, replayed)

        Assert.Equal(
            Error "executor-relay-response-identity-conflict",
            relay.Complete(command.CommandId, [| 1uy |] :: [])
        )
    }

[<Fact>]
let ``Host relay cancellation leaves original request available for reconciliation`` () =
    task {
        let relay = HostExecutorRelay(2, 1024 * 1024)
        let command = Fixture.executorCommand (Guid.NewGuid())
        let frames = [ ExecutorWire.encodeCommandV2 command ]
        use cancelled = new CancellationTokenSource()

        let waiting =
            (relay :> IAuthenticatedExecutorTransport).Exchange(frames, cancelled.Token)

        cancelled.Cancel()
        let! result = waiting
        Assert.Equal(Error "executor-relay-response-unknown", result)
        Assert.Equal(1, relay.PendingCount)
        let! polled = relay.Poll(CancellationToken.None)

        Assert.Equal(
            Some
                {
                    CommandId = command.CommandId
                    Frames = frames
                },
            polled
        )
    }

let private localRunnerFixture (mode: string) =
    let root = Directory.CreateTempSubdirectory("local-executor-transport-").FullName

    for name in [ "repository"; "workspaces"; "inputs"; "state"; "artifacts" ] do
        Directory.CreateDirectory(Path.Combine(root, name)) |> ignore

    File.WriteAllText(Path.Combine(root, "state/mode"), mode)
    let runner = Path.Combine(root, "runner.py")

    let script =
        [
            "#!/usr/bin/python3"
            "import json, os, struct, sys, time"
            "args=dict(zip(sys.argv[2::2],sys.argv[3::2]))"
            "state=args['--state-root']; count_path=os.path.join(state,'starts')"
            "open(os.path.join(state,'args.json'),'w').write(json.dumps(args,sort_keys=True))"
            "count=(int(open(count_path).read()) if os.path.exists(count_path) else 0)+1"
            "open(count_path,'w').write(str(count)); mode=open(os.path.join(state,'mode')).read().strip()"
            "if mode=='exit-once' and count==1: sys.exit(17)"
            "while True:"
            "    header=sys.stdin.buffer.read(4)"
            "    if not header: break"
            "    if len(header)!=4: sys.exit(18)"
            "    size=struct.unpack('>i',header)[0]; payload=sys.stdin.buffer.read(size)"
            "    if len(payload)!=size: sys.exit(19)"
            "    value=json.loads(payload)"
            "    if value.get('schema')!='fsgg.orchestration.executor-command/2': continue"
            "    mode=open(os.path.join(state,'mode')).read().strip()"
            "    if mode=='sleep': time.sleep(30)"
            "    command_id=value['commandId']"
            "    if mode=='stale': command_id='ffffffff-ffff-ffff-ffff-ffffffffffff'"
            "    if mode.startswith('artifact'):"
            "        artifact=open(os.path.join(state,'artifact.json'),'rb').read()"
            "        sys.stdout.buffer.write(struct.pack('>i',len(artifact))+artifact); sys.stdout.buffer.flush()"
            "    response={'schema':'fsgg.orchestration.executor-operation-outcome/1','commandId':command_id,'bodySha256':value['bodySha256'],'operation':'reconcile','disposition':'unknown','providerSessionReference':None,'observedAt':'2026-09-14T00:00:00+00:00','reason':'fixture'}"
            "    encoded=json.dumps(response,separators=(',',':')).encode()"
            "    if mode=='malformed': encoded=b'{'"
            "    sys.stdout.buffer.write(struct.pack('>i',len(encoded))+encoded); sys.stdout.buffer.flush()"
        ]
        |> String.concat "\n"

    File.WriteAllText(runner, script + "\n")
    File.SetUnixFileMode(runner, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    root,
    runner,
    {
        RunnerExecutable = runner
        RepositoryRoot = Path.Combine(root, "repository")
        WorkspaceRoot = Path.Combine(root, "workspaces")
        InputRoot = Path.Combine(root, "inputs")
        StateRoot = Path.Combine(root, "state")
        ArtifactRoot = Path.Combine(root, "artifacts")
        CodexExecutable = "/bin/false"
        ExecutorBinding = "fixture-executor"
        Telemetry = None
    }

[<Fact>]
let ``local executor child exchanges bounded frames and survives sequential commands`` () =
    task {
        let root, _, configuration = localRunnerFixture "ok"
        use transport = new LocalExecutorTransport(configuration)
        let first = Fixture.executorCommand (Guid.NewGuid())

        let! firstResult =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange([ ExecutorWire.encodeCommandV2 first ], CancellationToken.None)

        Assert.True(Result.isOk firstResult, sprintf "%A" firstResult)
        let second = Fixture.executorCommand (Guid.NewGuid())

        let! secondResult =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange([ ExecutorWire.encodeCommandV2 second ], CancellationToken.None)

        Assert.True(Result.isOk secondResult)
        Assert.Equal("1", File.ReadAllText(Path.Combine(root, "state/starts")))
        Assert.True(transport.IsRunning)
    }

[<Fact>]
let ``local executor forwards exact telemetry binding arguments`` () =
    task {
        let root, _, baseConfiguration = localRunnerFixture "ok"
        let telemetry =
            {
                Executable = "/usr/local/bin/fsgg-coord-engine"
                Config = "/etc/fs-gg/telemetry/workspace.json"
                CredentialFile = "/run/secrets/telemetry-orchestration-credential"
                CertificateAuthorityFile = "/etc/fs-gg/telemetry/ca.crt"
                Outbox = "/srv/runner-state/telemetry-outbox"
                BindingDigest = String.replicate 64 "a"
                Repository = "FS-GG/.github"
            }

        use transport = new LocalExecutorTransport({ baseConfiguration with Telemetry = Some telemetry })
        let command = Fixture.executorCommand (Guid.NewGuid())
        let! result =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange([ ExecutorWire.encodeCommandV2 command ], CancellationToken.None)
        Assert.True(Result.isOk result)

        use document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "state/args.json")))
        let args = document.RootElement
        Assert.Equal(telemetry.Executable, args.GetProperty("--telemetry-executable").GetString())
        Assert.Equal(telemetry.Config, args.GetProperty("--telemetry-config").GetString())
        Assert.Equal(telemetry.CredentialFile, args.GetProperty("--telemetry-credential-file").GetString())
        Assert.Equal(telemetry.CertificateAuthorityFile, args.GetProperty("--telemetry-ca-file").GetString())
        Assert.Equal(telemetry.Outbox, args.GetProperty("--telemetry-outbox").GetString())
        Assert.Equal(telemetry.BindingDigest, args.GetProperty("--telemetry-binding-digest").GetString())
        Assert.Equal(telemetry.Repository, args.GetProperty("--telemetry-repository").GetString())
    }

[<Fact>]
let ``local executor child restarts after exit and refuses stale or malformed output`` () =
    task {
        let root, _, configuration = localRunnerFixture "exit-once"
        use transport = new LocalExecutorTransport(configuration)
        let command = Fixture.executorCommand (Guid.NewGuid())

        let! failed =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange([ ExecutorWire.encodeCommandV2 command ], CancellationToken.None)

        Assert.Equal(Error "executor-child-exited", failed)

        let! recovered =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange([ ExecutorWire.encodeCommandV2 command ], CancellationToken.None)

        Assert.True(Result.isOk recovered, sprintf "%A" recovered)
        Assert.Equal("2", File.ReadAllText(Path.Combine(root, "state/starts")))
        File.WriteAllText(Path.Combine(root, "state/mode"), "stale")

        let! stale =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange(
                    [ ExecutorWire.encodeCommandV2 (Fixture.executorCommand (Guid.NewGuid())) ],
                    CancellationToken.None
                )

        Assert.Equal(Error "executor-child-stale-or-malformed-frame-refused", stale)
        File.WriteAllText(Path.Combine(root, "state/mode"), "malformed")

        let! malformed =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange(
                    [ ExecutorWire.encodeCommandV2 (Fixture.executorCommand (Guid.NewGuid())) ],
                    CancellationToken.None
                )

        Assert.Equal(Error "executor-child-stale-or-malformed-frame-refused", malformed)
    }

[<Fact>]
let ``local executor accepts a bound historical artifact before the current terminal frame`` () =
    task {
        let root, _, configuration = localRunnerFixture "artifact"
        use transport = new LocalExecutorTransport(configuration)
        let command = Fixture.executorCommand (Guid.NewGuid())

        let workspace =
            {
                Schema = ExecutorWire.workspaceManifestSchema
                Workspace = "pilot"
                RepositoryBinding = "FS-GG/.github"
                BaselineObjectId = String.replicate 40 "a"
                AllowedPaths = [| "docs/item.md" |]
                Validations = [| "git-diff-check" |]
                InputDigest = command.InputDigest
            }

        let unsigned =
            {
                Schema = ExecutorWire.artifactManifestSchema
                CommandId = Guid.NewGuid()
                CandidateId = command.CandidateId
                BaselineObjectId = workspace.BaselineObjectId
                HeadObjectId = String.replicate 40 "b"
                TreeObjectId = String.replicate 40 "c"
                BundleSha256 = String.replicate 64 "d"
                BundleSizeBytes = 741L
                ManifestSha256 = ""
                ChunkBytes = 1024
            }

        let artifact =
            { unsigned with
                ManifestSha256 = ExecutorWire.artifactManifestDigest unsigned
            }

        File.WriteAllBytes(Path.Combine(root, "state/artifact.json"), ExecutorWire.encodeArtifactManifest artifact)

        let request =
            [
                ExecutorWire.encodeWorkspaceManifest workspace
                ExecutorWire.encodeCommandV2 command
            ]

        let! result =
            (transport :> IAuthenticatedExecutorTransport).Exchange(request, CancellationToken.None)

        let frames = result |> Result.defaultWith failwith |> _.Frames
        Assert.Equal(2, frames.Length)
        Assert.Equal(artifact, frames[0] |> ExecutorWire.parseArtifactManifest |> Result.defaultWith failwith)

        let terminal =
            frames[1] |> ExecutorWire.parseOperationOutcome |> Result.defaultWith failwith

        Assert.Equal(command.CommandId, terminal.CommandId)
        Assert.Equal(command.BodySha256, terminal.BodySha256)
    }

[<Theory>]
[<InlineData(true, false)>]
[<InlineData(false, true)>]
let ``local executor refuses a historical artifact outside the current candidate and baseline``
    wrongCandidate
    wrongBaseline
    =
    task {
        let root, _, configuration = localRunnerFixture "artifact"
        use transport = new LocalExecutorTransport(configuration)
        let command = Fixture.executorCommand (Guid.NewGuid())

        let workspace =
            {
                Schema = ExecutorWire.workspaceManifestSchema
                Workspace = "pilot"
                RepositoryBinding = "FS-GG/.github"
                BaselineObjectId = String.replicate 40 "a"
                AllowedPaths = [| "docs/item.md" |]
                Validations = [| "git-diff-check" |]
                InputDigest = command.InputDigest
            }

        let unsigned =
            {
                Schema = ExecutorWire.artifactManifestSchema
                CommandId = Guid.NewGuid()
                CandidateId =
                    (if wrongCandidate then
                         Guid.NewGuid()
                     else
                         command.CandidateId)
                BaselineObjectId =
                    (if wrongBaseline then
                         String.replicate 40 "e"
                     else
                         workspace.BaselineObjectId)
                HeadObjectId = String.replicate 40 "b"
                TreeObjectId = String.replicate 40 "c"
                BundleSha256 = String.replicate 64 "d"
                BundleSizeBytes = 741L
                ManifestSha256 = ""
                ChunkBytes = 1024
            }

        let artifact =
            { unsigned with
                ManifestSha256 = ExecutorWire.artifactManifestDigest unsigned
            }

        File.WriteAllBytes(Path.Combine(root, "state/artifact.json"), ExecutorWire.encodeArtifactManifest artifact)

        let request =
            [
                ExecutorWire.encodeWorkspaceManifest workspace
                ExecutorWire.encodeCommandV2 command
            ]

        let! result =
            (transport :> IAuthenticatedExecutorTransport).Exchange(request, CancellationToken.None)

        Assert.Equal(Error "executor-child-stale-or-malformed-frame-refused", result)
    }

[<Fact>]
let ``local executor cancellation terminates child and permits clean restart`` () =
    task {
        let root, _, configuration = localRunnerFixture "sleep"

        use transport =
            new LocalExecutorTransport(configuration, shutdownTimeout = TimeSpan.FromMilliseconds 200.)

        use deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds 150.)
        let command = Fixture.executorCommand (Guid.NewGuid())

        let! cancelled =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange([ ExecutorWire.encodeCommandV2 command ], deadline.Token)

        Assert.True((cancelled = Error "executor-child-exchange-cancelled"), sprintf "%A" cancelled)
        Assert.False(transport.IsRunning)
        File.WriteAllText(Path.Combine(root, "state/mode"), "ok")

        let! recovered =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange(
                    [ ExecutorWire.encodeCommandV2 (Fixture.executorCommand (Guid.NewGuid())) ],
                    CancellationToken.None
                )

        Assert.True(Result.isOk recovered)
    }

[<Fact>]
let ``local executor refuses oversized exchanges and clean shutdown terminates its child`` () =
    task {
        let _, _, configuration = localRunnerFixture "ok"
        let transport = new LocalExecutorTransport(configuration)

        let oversized =
            List.replicate 65 (ExecutorWire.encodeCommandV2 (Fixture.executorCommand (Guid.NewGuid())))

        let! refused =
            (transport :> IAuthenticatedExecutorTransport).Exchange(oversized, CancellationToken.None)

        Assert.Equal(Error "executor-child-request-bounds-refused", refused)

        let! accepted =
            (transport :> IAuthenticatedExecutorTransport)
                .Exchange(
                    [ ExecutorWire.encodeCommandV2 (Fixture.executorCommand (Guid.NewGuid())) ],
                    CancellationToken.None
                )

        Assert.True(Result.isOk accepted)
        Assert.True(transport.IsRunning)
        (transport :> IDisposable).Dispose()
        Assert.False(transport.IsRunning)
    }

[<Fact>]
let ``work item recovery refuses an untyped snapshot instead of inventing state`` () =
    task {
        let persistenceId = WorkItemIdentity.persistenceId Fixture.workItem

        let snapshot =
            {
                PersistenceId = persistenceId
                Sequence = 4L
                SchemaVersion = 1
                Payload = [| 1uy |]
                PayloadSha256 = String.replicate 64 "a"
            }

        let store =
            FixedStore
                {
                    Events = []
                    Snapshot = Some snapshot
                    UnsettledEffects = []
                    RequiresExternalReconciliation = false
                }

        let! recovered =
            HostedWriterJournal.recover store Fixture.workItem CancellationToken.None

        Assert.Equal(Error [ CorruptRecord(persistenceId, 4L) ], recovered)
    }

[<Fact>]
let ``work item recovery replays typed events and retains unsettled provider work`` () =
    task {
        let persistenceId = WorkItemIdentity.persistenceId Fixture.workItem
        let intent = Fixture.intent AcquireExternalClaim Fixture.route.ClaimOperationId

        let snapshot =
            {
                ProjectId = Id.project (Fixture.guid "60000000-0000-0000-0000-000000000001")
                WorkItemId = Fixture.workItem
                WorkflowRevision = Fixture.route.WorkflowRevision
                CanonicalSha256 = String.replicate 64 "b"
                BoardMembershipIds = []
                CapturedAt = Fixture.now
            }

        let budget =
            {
                TokenLimit = 100L
                RuntimeSecondsLimit = 100L
                CostMicrosLimit = 100L
                Deadline = Fixture.now.AddMinutes 30.
            }

        let commandId = Id.command (Fixture.guid "60000000-0000-0000-0000-000000000002")

        let envelope =
            {
                CommandId = commandId
                ProtocolVersion = Id.protocolVersion 1 0
                ExpectedRevision = initial.Revision
                ExpectedGeneration = initial.Generation
                PrincipalId = "pilot"
                SessionId = None
                IssuedAt = Fixture.now
                ExpiresAt = Fixture.now.AddMinutes 1.
                Command = Admit(snapshot, budget)
            }

        let receipt =
            {
                CommandId = commandId
                BodySha256 = canonicalEnvelopeSha256 envelope
                Disposition = Accepted
                Revision = Id.revision 3L
                ProtocolVersion = Id.protocolVersion 1 0
                Detail = "fixture"
            }

        let decision =
            {
                Events =
                    [
                        WorkAdmitted(snapshot, budget)
                        EffectIntentRecorded intent
                        CommandRecorded receipt
                    ]
                Effects = []
                Receipt = receipt
            }

        let stored =
            HostedWriterJournal.appendRequest persistenceId Fixture.now 0L initial envelope decision

        let store =
            FixedStore
                {
                    Events = stored.Events
                    Snapshot = None
                    UnsettledEffects = [ intent ]
                    RequiresExternalReconciliation = true
                }

        let! recovered =
            HostedWriterJournal.recover store Fixture.workItem CancellationToken.None

        match recovered with
        | Error failures -> failwithf "typed work item recovery failed: %A" failures
        | Ok result ->
            Assert.Equal(Some Fixture.workItem, result.State.WorkItemId)
            Assert.Equal(IntentRecorded intent, result.State.Operations[intent.OperationId])
            Assert.Equal<EffectIntent list>([ intent ], result.UnsettledEffects)
            Assert.True(result.RequiresExternalReconciliation)
    }

type private QueuedGitHub(outcomes: FS.GG.Coordination.GitHub.TransportOutcome list) =
    let queue = Queue<FS.GG.Coordination.GitHub.TransportOutcome>(outcomes)
    let requests = ResizeArray<FS.GG.Coordination.GitHub.GitHubRequest>()
    member _.Requests = requests |> Seq.toList

    interface IGitHubRequestExecutor with
        member _.Send(request, _) =
            requests.Add request

            Task.FromResult(
                if queue.Count = 0 then
                    FS.GG.Coordination.GitHub.NetworkFailure
                else
                    queue.Dequeue()
            )

type private FixedPublisher(result: Result<string, string>) =
    interface IGitCandidatePublisher with
        member _.Publish(_, _, _, _, _, _) = Task.FromResult result

let private response body =
    FS.GG.Coordination.GitHub.Response
        {
            StatusCode = 200
            Headers = Map.empty
            Body = body
            ETag = Some "fixture-etag"
            RateBudget =
                {
                    Limit = None
                    Remaining = None
                    ResetAt = None
                    Cost = None
                }
        }

let private responseStatus status body =
    FS.GG.Coordination.GitHub.Response
        {
            StatusCode = status
            Headers = Map.empty
            Body = body
            ETag = Some "fixture-etag"
            RateBudget =
                {
                    Limit = None
                    Remaining = None
                    ResetAt = None
                    Cost = None
                }
        }

let private githubTarget =
    {
        ApiRoot = Uri "https://api.github.test/"
        Repository = "FS-GG/.github"
        IssueNumber = 3419
        Principal = "pilot-worker"
        BaseRef = "main"
        RoutineOperation = "internal-docs"
        ClaimLease = TimeSpan.FromMinutes 30.
    }

let private pr state mergedAt head mergeSha =
    let merged =
        match mergedAt with
        | Some value -> $"\"{value}\""
        | None -> "null"

    let merge =
        match mergeSha with
        | Some value -> $"\"{value}\""
        | None -> "null"

    let baseSha = String.replicate 40 "b"
    $"[{{\"number\":42,\"node_id\":\"PR_node\",\"state\":\"{state}\",\"body\":\"<!-- fsgg:routine-development/v1 head={head} operation=internal-docs -->\",\"merged_at\":{merged},\"merge_commit_sha\":{merge},\"head\":{{\"sha\":\"{head}\",\"ref\":\"pilot\"}},\"base\":{{\"ref\":\"main\",\"sha\":\"{baseSha}\",\"repo\":{{\"full_name\":\"FS-GG/.github\"}}}}}}]"

let private prDetail head =
    $"{{\"draft\":false,\"mergeable\":true,\"mergeable_state\":\"clean\",\"head\":{{\"sha\":\"{head}\"}},\"base\":{{\"ref\":\"main\"}}}}"

let private routinePolicy =
    let bytes =
        Encoding.UTF8.GetBytes
            "{\"schema\":\"fsgg.routine-development-policy/v1\",\"allowedOperations\":[\"source-change\",\"internal-docs\"]}"

    $"{{\"content\":\"{Convert.ToBase64String bytes}\"}}"

[<Fact>]
let ``GitHub route recovery reads fresh immutable repository and issue identity`` () =
    task {
        let executor =
            QueuedGitHub[response "{\"id\":7,\"node_id\":\"R_writer\"}"
                         response "{\"number\":11,\"node_id\":\"I_writer\"}"]

        let target = { githubTarget with IssueNumber = 11 }

        let client =
            GitHubRouteClient(executor, FixedPublisher(Ok(String.replicate 40 "a")), target, FixedClock Fixture.now)

        let route =
            { Fixture.route with
                RepositoryNodeId = "R_writer"
            }

        let! result = client.ReadHostedRoute(route, CancellationToken.None)
        let readback = result |> Result.defaultWith failwith
        Assert.Equal(route.RouteId, readback.RouteId)
        Assert.Equal(route.Generation, readback.Generation)
        Assert.Equal(Fixture.now, readback.ObservedAt)
        Assert.Equal(64, readback.EvidenceSha256.Length)
        Assert.Equal(2, executor.Requests.Length)

        let requestedUris =
            executor.Requests
            |> List.map (function
                | Rest value -> value.Uri.AbsoluteUri
                | GraphQL _ -> failwith "expected REST recovery reads")

        Assert.Equal<string list>(
            [ "https://api.github.test/repos/FS-GG/.github"
              "https://api.github.test/repos/FS-GG/.github/issues/11" ],
            requestedUris
        )

        let mismatch =
            QueuedGitHub[response "{\"id\":8,\"node_id\":\"R_other\"}"
                         response "{\"number\":11,\"node_id\":\"I_writer\"}"]

        let mismatchClient =
            GitHubRouteClient(mismatch, FixedPublisher(Ok(String.replicate 40 "a")), target, FixedClock Fixture.now)

        let! refused = mismatchClient.ReadHostedRoute(route, CancellationToken.None)
        Assert.Equal(Error "github-route-identity-mismatch", refused)
    }

[<Fact>]
let ``GitHub route recovery preserves legacy journal identity with database ID in number slot`` () =
    task {
        let executor =
            QueuedGitHub[response "{\"id\":7,\"node_id\":\"R_writer\"}"
                         response "{\"number\":11,\"node_id\":\"I_writer\"}"]

        let target = { githubTarget with IssueNumber = 11 }
        let client =
            GitHubRouteClient(executor, FixedPublisher(Ok(String.replicate 40 "a")), target, FixedClock Fixture.now)

        let legacyId = WorkItemIdentity.create "R_writer" 7L "I_writer" 5518537537L
        let route = { Fixture.route with RepositoryNodeId = "R_writer"; WorkItemId = legacyId }
        let! result = client.ReadHostedRoute(route, CancellationToken.None)
        let readback = result |> Result.defaultWith failwith
        Assert.Equal(legacyId, readback.WorkItemId)
        Assert.Equal(WorkItemIdentity.persistenceId legacyId, WorkItemIdentity.persistenceId readback.WorkItemId)

        let wrongIssue =
            QueuedGitHub[response "{\"id\":7,\"node_id\":\"R_writer\"}"
                         response "{\"number\":12,\"node_id\":\"I_writer\"}"]

        let wrongClient =
            GitHubRouteClient(wrongIssue, FixedPublisher(Ok(String.replicate 40 "a")), target, FixedClock Fixture.now)

        let! refused = wrongClient.ReadHostedRoute(route, CancellationToken.None)
        Assert.Equal(Error "github-route-identity-mismatch", refused)
    }

[<Fact>]
let ``GitHub claim covers the immutable delivery window and expired ownership refuses`` () =
    task {
        let operation = Guid.Parse "80000000-0000-0000-0000-000000000001"
        let session = operation.ToString "N"

        let marker =
            $"<!-- fsgg:claim worker=pilot-worker lease=91 renewed=1 session={session} -->"

        let comments =
            $"[{{\"id\":1,\"updated_at\":\"2026-09-10T19:00:00Z\",\"body\":\"{marker}\"}}]"

        let executor =
            QueuedGitHub[response "[]"
                         response "{}"
                         response comments]

        let client =
            GitHubRouteClient(
                executor,
                FixedPublisher(Ok(String.replicate 40 "a")),
                githubTarget,
                FixedClock Fixture.now
            )

        let! acquired =
            client.AcquireClaim("claim-1", operation, Fixture.now.AddMinutes 90.5, CancellationToken.None)

        Assert.True(Result.isOk acquired)
        let posted = executor.Requests[1]

        let postedBody =
            match posted with
            | Rest(value: RestRequest) -> defaultArg value.Body ""
            | GraphQL _ -> failwith "expected REST claim mutation"

        Assert.Contains("lease=91", postedBody)

        let tooLong = QueuedGitHub[]

        let tooLongClient =
            GitHubRouteClient(
                tooLong,
                FixedPublisher(Ok(String.replicate 40 "a")),
                githubTarget,
                FixedClock Fixture.now
            )

        let beyondDelivery = (Fixture.now.AddHours 2.).AddTicks 1L

        let! refused =
            tooLongClient.AcquireClaim("claim-1", operation, beyondDelivery, CancellationToken.None)

        Assert.Equal(Error "github-claim-delivery-deadline-refused", refused)
        Assert.Empty tooLong.Requests

        let expired =
            $"[{{\"id\":1,\"updated_at\":\"2026-09-10T18:29:00Z\",\"body\":\"<!-- fsgg:claim worker=pilot-worker lease=30 renewed=1 session={session} -->\"}}]"

        let expiredExecutor = QueuedGitHub[response expired]

        let expiredClient =
            GitHubRouteClient(
                expiredExecutor,
                FixedPublisher(Ok(String.replicate 40 "a")),
                githubTarget,
                FixedClock Fixture.now
            )

        let! absent = expiredClient.ReadClaim("claim-1", operation, CancellationToken.None)
        Assert.Equal(Error "github-claim-not-observed", absent)
    }

[<Fact>]
let ``failed candidate recovery requires exact external absence`` () =
    task {
        let operation = Guid.Parse "80000000-0000-0000-0000-000000000001"
        let absent =
            QueuedGitHub[response "[]"; responseStatus 404 "{}"; response "[]"]

        let client =
            GitHubRouteClient(absent, FixedPublisher(Ok(String.replicate 40 "a")), githubTarget, FixedClock Fixture.now)

        let! claim = client.ReadClaimAbsent("claim-1", operation, CancellationToken.None)
        let! branch = client.ReadBranchAbsent("refs/heads/pilot", CancellationToken.None)
        let! pull = client.ReadPullRequestAbsent("refs/heads/pilot", CancellationToken.None)
        Assert.Equal(Ok(), claim)
        Assert.Equal(Ok(), branch)
        Assert.Equal(Ok(), pull)

        let present =
            QueuedGitHub[response "[]"; response "{}"; response "[]"]

        let presentClient =
            GitHubRouteClient(present, FixedPublisher(Ok(String.replicate 40 "a")), githubTarget, FixedClock Fixture.now)

        let! _ = presentClient.ReadClaimAbsent("claim-1", operation, CancellationToken.None)
        let! refused = presentClient.ReadBranchAbsent("refs/heads/pilot", CancellationToken.None)
        Assert.Equal(Error "github-branch-still-present", refused)

        let crossBase = QueuedGitHub[response "[{\"base\":{\"ref\":\"other\"}}]"]
        let crossBaseClient =
            GitHubRouteClient(crossBase, FixedPublisher(Ok(String.replicate 40 "a")), githubTarget, FixedClock Fixture.now)

        let! crossBaseRefusal = crossBaseClient.ReadPullRequestAbsent("refs/heads/pilot", CancellationToken.None)
        Assert.Equal(Error "github-pull-request-still-present", crossBaseRefusal)

        match crossBase.Requests with
        | [ Rest request ] -> Assert.DoesNotContain("base=", request.Uri.Query)
        | _ -> failwith "expected one all-base PR census"
    }

[<Fact>]
let ``terminal snapshot binds old attempt and refuses altered or nonterminal bytes`` () =
    let directory = Directory.CreateTempSubdirectory("terminal-evidence-").FullName

    try
        let assignment = Guid.NewGuid()
        let attempt = Guid.NewGuid()
        let intent: FS.GG.Coordination.Orchestration.Execution.LaunchIntent =
            {
                Schema = "fsgg.orchestration.execution-launch/2"
                Key = { AssignmentId = assignment; AttemptId = attempt; Generation = 1L }
                InputDigest = String.replicate 64 "a"
                Workspace = "pilot"
                Requested = { Model = None; Effort = None }
                Limits = { Deadline = Fixture.now; MaximumRuntime = TimeSpan.FromMinutes 30.; MaximumAttempts = 1 }
                RecordedAt = Fixture.now.AddMinutes -30.
            }

        let final = Encoding.UTF8.GetBytes "{\"status\":\"completed\",\"summary\":\"done\"}"
        let stdout = Encoding.UTF8.GetBytes "{\"type\":\"turn.started\"}\n{\"type\":\"turn.completed\"}\n"
        let sha (bytes: byte array) =
            System.Security.Cryptography.SHA256.HashData bytes
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()

        let manifest terminal (stdoutBytes: byte array) =
            JsonSerializer.Serialize
                {|
                    schema = "fsgg.orchestration.terminal-evidence/1"
                    assignmentId = string assignment
                    attemptId = string attempt
                    generation = 1L
                    finalSha256 = sha final
                    stdoutSha256 = sha stdoutBytes
                    finalBytes = final.LongLength
                    stdoutBytes = stdoutBytes.LongLength
                    capturedAt = Fixture.now
                    processTerminationObserved = terminal
                |}

        File.WriteAllBytes(Path.Combine(directory, "final.json"), final)
        File.WriteAllBytes(Path.Combine(directory, "stdout.jsonl"), stdout)
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest true stdout)

        Assert.True(MainTerminalEvidence.verify directory intent Fixture.now |> Result.isOk)

        let wrongIntent =
            { intent with
                Key = { intent.Key with AttemptId = Guid.NewGuid() }
            }

        Assert.Equal(
            Error "terminal-evidence-manifest-refused",
            MainTerminalEvidence.verify directory wrongIntent Fixture.now
        )

        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest false stdout)
        Assert.Equal(
            Error "terminal-evidence-manifest-refused",
            MainTerminalEvidence.verify directory intent Fixture.now
        )

        let ambiguous = Encoding.UTF8.GetBytes "{\"type\":\"turn.started\"}\n{\"type\":\"turn.failed\"}\n"
        File.WriteAllBytes(Path.Combine(directory, "stdout.jsonl"), ambiguous)
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest true ambiguous)
        Assert.Equal(
            Error "terminal-evidence-turn-ambiguous",
            MainTerminalEvidence.verify directory intent Fixture.now
        )
    finally
        Directory.Delete(directory, true)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``expired stored candidate resumes once after exact rejected continuation`` preExtended =
    task {
        let now = Fixture.now
        let route = Fixture.route
        let work = route.WorkItemId
        let deadline = now.AddMinutes -1.
        let deliveryDeadline = now.AddHours 1.
        let retention = deliveryDeadline.AddDays 30.
        let bytes = Encoding.UTF8.GetBytes "exact-candidate"
        let contentSha = RunnerWire.sha256 bytes
        let candidate =
            { CandidateId = route.CandidateId
              BaselineSha = String.replicate 40 "a"
              HeadSha = String.replicate 40 "b"
              TreeSha = String.replicate 40 "c"
              ManifestSha256 = String.replicate 64 "d"
              ContentSha256 = contentSha
              MediaType = "application/vnd.fsgg.runner-candidate+zip"
              SizeBytes = int64 bytes.Length
              RetainUntil = deadline
              Location = ContentAddressedObject($"sha256/{contentSha}") }
        let oldReceipt =
            { CandidateId = candidate.CandidateId
              ContentSha256 = candidate.ContentSha256
              ManifestSha256 = candidate.ManifestSha256
              SizeBytes = candidate.SizeBytes
              Location = candidate.Location
              StoreId = "fixture"
              StoreSchemaVersion = 1
              StorageReceiptSha256 = String.replicate 64 "e"
              VerifiedAt = deadline }
        let intent = Fixture.intent StoreCandidate route.CandidateOperationId
        let readback =
            { Fixture.hosted intent with
                ProviderResourceId = string (Id.candidateValue route.CandidateId)
                CandidateHeadSha = Some candidate.HeadSha
                ResultSha = Some candidate.ContentSha256 }
        let budget =
            { Schema = "fsgg.coordination.subscription-execution-budget/2"
              AttemptLimit = 1
              MaximumRuntime = TimeSpan.FromMinutes 30.
              ExecutionDeadline = deadline
              DeliveryDeadline = deliveryDeadline
              Usage = TokensUnknown "fixture"
              Cost =
                { InvocationState = "not-applicable"
                  InvocationProvenance = "fixture"
                  BroaderAttributionState = "unknown"
                  BroaderAttributionProvenance = "fixture" } }
        let snapshot =
            { ProjectId = Id.project (Guid.NewGuid())
              WorkItemId = work
              WorkflowRevision = route.WorkflowRevision
              CanonicalSha256 = String.replicate 64 "f"
              BoardMembershipIds = []
              CapturedAt = route.SelectedAt }
        let rejectedId =
            MainRouteWorkflowIdentity.commandId work route.RouteId route.AttemptId route.Generation "record-candidate"
        let rejected =
            { CommandId = rejectedId
              BodySha256 = String.replicate 64 "0"
              Disposition = ReceiptDisposition.Rejected
              Revision = Id.revision 1L
              ProtocolVersion = Id.protocolVersion 1 0
              Detail = "candidate-not-recoverable-or-invalid" }
        let events =
            [ SubscriptionWorkAdmitted(snapshot, budget)
              GenerationAdvanced route.Generation
              HostedRouteSelected route
              EffectIntentRecorded intent
              HostedEffectReadbackAccepted readback
              EffectSettled(intent.OperationId, Applied readback.ProviderRevision)
              CommandRecorded rejected ]
        let store = MemoryStore(events) :> IJournalStore
        let mutable current =
            { Candidate =
                (if preExtended then { candidate with RetainUntil = retention } else candidate)
              Bytes = bytes }
        let mutable currentReceipt =
            if preExtended then
                { oldReceipt with
                    StorageReceiptSha256 = String.replicate 64 "1"
                    VerifiedAt = now }
            else
                oldReceipt
        let mutable extensions = 0
        let candidateStore =
            { new ICandidateStore with
                member _.Put(value, _) =
                    Assert.Equal(current.Candidate, value.Candidate)
                    Assert.Equal<byte array>(current.Bytes, value.Bytes)
                    Task.FromResult(Error(Existing currentReceipt))
                member _.Read(_, _) = Task.FromResult(Ok current)
                member _.Quarantine(_, _, _) = failwith "unexpected quarantine"
                member _.CleanupUnreferenced(_, _, _) = failwith "unexpected cleanup"
              interface ICandidateRetentionExtension with
                member _.ExtendRetention(value, receipt, requested, _) =
                    Assert.Equal(current, value)
                    Assert.Equal(currentReceipt, receipt)
                    Assert.Equal(retention, requested)
                    extensions <- extensions + 1
                    current <- { current with Candidate = { candidate with RetainUntil = requested } }
                    currentReceipt <-
                        { receipt with
                            StorageReceiptSha256 = String.replicate 64 "1"
                            VerifiedAt = now }
                    Task.FromResult(Ok currentReceipt) }
        let binding: ExecutorRouteBinding =
            { Schema = "fixture"
              BindingSha256 = intent.PayloadSha256
              WorkItemPersistenceId = WorkItemIdentity.persistenceId work
              RouteId = route.RouteId
              RouteOperationId = Guid.Empty
              ProcessOperationId = Guid.Empty
              AssignmentId = Guid.Empty
              AttemptId = Id.attemptValue route.AttemptId
              CandidateId = Id.candidateValue route.CandidateId
              Generation = Id.generationValue route.Generation
              RepositoryBinding = "fixture"
              BaselineObjectId = candidate.BaselineSha
              PromptDigest = String.replicate 64 "2"
              WorkspaceManifestSha256 = String.replicate 64 "3"
              ExecutorBinding = "fixture"
              ParentAttemptId = Nullable()
              ParentGeneration = Nullable()
              TelemetryRelation = "" }
        let launch: FS.GG.Coordination.Orchestration.Execution.LaunchIntent =
            { Schema = "fsgg.orchestration.execution-launch/2"
              Key =
                { AssignmentId = Guid.NewGuid()
                  AttemptId = Id.attemptValue route.AttemptId
                  Generation = Id.generationValue route.Generation }
              InputDigest = String.replicate 64 "4"
              Workspace = "fixture"
              Requested = { Model = None; Effort = None }
              Limits =
                { Deadline = deadline
                  MaximumRuntime = TimeSpan.FromMinutes 30.
                  MaximumAttempts = 1 }
              RecordedAt = now.AddMinutes -30. }
        let preparation: MainRoutePreparation =
            { Snapshot = snapshot
              Budget = budget
              Reservation = Unchecked.defaultof<_>
              Route = route
              Readback = Unchecked.defaultof<_>
              Runner = Unchecked.defaultof<_>
              SessionId = Unchecked.defaultof<_>
              LaunchIntent = launch
              Binding = binding
              InputManifest = Unchecked.defaultof<_>
              InputBytes = [||]
              WorkspaceManifest = Unchecked.defaultof<_>
              ExecutionReservation = Unchecked.defaultof<_> }
        let clock =
            { new TimeProvider() with
                override _.GetUtcNow() = now }
        let workflow =
            MainRouteWorkflow(clock, store, candidateStore, Unchecked.defaultof<_>, Unchecked.defaultof<_>, work, "pilot-worker")
        let! recovered = workflow.RecoverContinuation(preparation, CancellationToken.None)
        Assert.Equal(Ok(), recovered)
        let! state = HostedWriterJournal.recover store work CancellationToken.None
        let state = state |> Result.defaultWith (sprintf "%A" >> failwith) |> _.State
        Assert.Equal(Some current.Candidate, Map.tryFind route.CandidateId state.Candidates)
        Assert.True(state.Operations.ContainsKey route.BranchOperationId)
        Assert.Equal((if preExtended then 0 else 1), extensions)
        let beforeRetry = state.Revision
        let! retry = workflow.RecoverContinuation(preparation, CancellationToken.None)
        Assert.Equal(Ok(), retry)
        let! afterRetry = HostedWriterJournal.recover store work CancellationToken.None
        Assert.Equal(beforeRetry, (afterRetry |> Result.defaultWith (sprintf "%A" >> failwith) |> _.State).Revision)
        Assert.Equal((if preExtended then 0 else 1), extensions)
    }

[<Fact>]
let ``absent candidate control allows only same-command partial retry`` () =
    let route = Fixture.route
    let intent = Fixture.intent StoreCandidate route.CandidateOperationId
    let control: MainRouteControl =
        {
            CommandId = Guid.NewGuid()
            ExpectedSequence = 23L
            ExpectedGeneration = Id.generationValue route.Generation
            PrincipalId = "pilot-worker"
            IssuedAt = Fixture.now.AddMinutes -1.
            ExpiresAt = Fixture.now.AddMinutes 1.
            Reason = "candidate-touch-set-refused"
            Action = "settle-absent-candidate"
        }

    let pending =
        { initial with
            Revision = Id.revision 23L
            Generation = route.Generation
            Operations = Map.ofList [ route.CandidateOperationId, NeedsObservation(intent, "candidate-unknown") ]
        }

    let marker = MainAbsentCandidateControl.digest control
    Assert.Equal(Ok marker, MainAbsentCandidateControl.authorize control route pending Fixture.now)

    let expired = { control with ExpiresAt = Fixture.now }
    Assert.Equal(
        Error "absent-candidate-control-stale",
        MainAbsentCandidateControl.authorize expired route pending Fixture.now
    )

    let readback =
        { Fixture.hosted intent with
            ProviderRevision = marker
            ObservedAt = Fixture.now
            Exists = false
        }

    let afterFirstStage =
        { pending with
            Revision = Id.revision 24L
            Generation = Id.generation 2L
            Operations = Map.ofList [ route.CandidateOperationId, OperationState.Settled(intent, ProvenAbsent) ]
            HostedEffectReadbacks = Map.ofList [ route.CandidateOperationId, readback ]
        }

    Assert.Equal(
        Ok marker,
        MainAbsentCandidateControl.authorize control route afterFirstStage (Fixture.now.AddMinutes 5.)
    )

    let different = { control with CommandId = Guid.NewGuid() }
    Assert.Equal(
        Error "absent-candidate-retry-identity-refused",
        MainAbsentCandidateControl.authorize different route afterFirstStage (Fixture.now.AddMinutes 5.)
    )

let runFailedCandidateRecoveryFixture partialFirstStage (storeFactory: Event list -> Task<IJournalStore>) =
    task {
        let now = Fixture.now
        let route = { Fixture.route with RepositoryNodeId = "R_writer" }
        let assignment = Id.operationValue route.ProcessOperationId
        let attemptId = Id.attemptValue route.AttemptId
        let launch: FS.GG.Coordination.Orchestration.Execution.LaunchIntent =
            {
                Schema = "fsgg.orchestration.execution-launch/2"
                Key = { AssignmentId = assignment; AttemptId = attemptId; Generation = 1L }
                InputDigest = String.replicate 64 "a"
                Workspace = "pilot"
                Requested = { Model = None; Effort = None }
                Limits = { Deadline = now.AddMinutes -1.; MaximumRuntime = TimeSpan.FromMinutes 30.; MaximumAttempts = 1 }
                RecordedAt = now.AddMinutes -30.
            }

        let budget =
            {
                Schema = "fsgg.coordination.subscription-execution-budget/2"
                AttemptLimit = 1
                MaximumRuntime = TimeSpan.FromMinutes 30.
                ExecutionDeadline = now.AddMinutes -2.
                DeliveryDeadline = now.AddMinutes -1.
                Usage = TokensUnknown "provider-unknown"
                Cost =
                    {
                        InvocationState = "not-applicable"
                        InvocationProvenance = "subscription-session"
                        BroaderAttributionState = "unknown"
                        BroaderAttributionProvenance = "not-attributed"
                    }
            }

        let reservation: FS.GG.Coordination.Orchestration.Pilot.SubscriptionReservation =
            {
                Schema = "fsgg.subscription.reservation/1"
                ReservationId = Guid.NewGuid()
                AssignmentId = assignment
                AttemptId = attemptId
                Generation = 1L
                ExpectedRevision = 0L
                ReservedAt = now.AddMinutes -30.
                Deadline = now.AddMinutes -1.
                MaximumRuntimeSeconds = 1800L
                AttemptLimit = 1
            }

        let preparation: MainRoutePreparation =
            {
                Snapshot = Unchecked.defaultof<_>
                Budget = budget
                Reservation = Unchecked.defaultof<_>
                Route = route
                Readback = Unchecked.defaultof<_>
                Runner = Unchecked.defaultof<_>
                SessionId = Unchecked.defaultof<_>
                LaunchIntent = launch
                Binding = Unchecked.defaultof<_>
                InputManifest = Unchecked.defaultof<_>
                InputBytes = [||]
                WorkspaceManifest = Unchecked.defaultof<_>
                ExecutionReservation = reservation
            }

        let claimIntent = Fixture.intent AcquireExternalClaim route.ClaimOperationId
        let processIntent = Fixture.intent DispatchRunner route.ProcessOperationId
        let candidateIntent = Fixture.intent StoreCandidate route.CandidateOperationId
        let readback intent resource =
            { Fixture.hosted intent with
                RepositoryNodeId = route.RepositoryNodeId
                ProviderResourceId = resource
                ObservedAt = now
            }

        let runner =
            {
                RunnerId = Id.runner (Guid.NewGuid())
                PrincipalId = "pilot-worker"
                FingerprintSha256 = String.replicate 64 "a"
                Generation = route.Generation
                ExpiresAt = now.AddMinutes 1.
            }

        let attempt =
            {
                AttemptId = route.AttemptId
                SessionId = Id.session (Guid.NewGuid())
                Runner = runner
                Generation = route.Generation
                StartedAt = now.AddMinutes -20.
                Status = AttemptStatus.Active
            }

        let events =
            [
                SubscriptionWorkAdmitted(
                    {
                        ProjectId = Id.project (Guid.NewGuid())
                        WorkItemId = route.WorkItemId
                        WorkflowRevision = route.WorkflowRevision
                        CanonicalSha256 = String.replicate 64 "b"
                        BoardMembershipIds = []
                        CapturedAt = now.AddMinutes -30.
                    },
                    budget
                )
                GenerationAdvanced route.Generation
                ReservationCreated
                    {
                        ReservationId = Id.reservation (Guid.NewGuid())
                        Generation = route.Generation
                        ExpiresAt = now.AddMinutes -1.
                        RequiredClaimIds = set [ route.ClaimResourceId ]
                    }
                HostedRouteSelected route
                EffectIntentRecorded claimIntent
                EffectDispatchStarted claimIntent.OperationId
                HostedEffectReadbackAccepted(readback claimIntent route.ClaimResourceId)
                EffectSettled(claimIntent.OperationId, Applied "provider-revision")
                ClaimObserved
                    {
                        ClaimId = route.ClaimResourceId
                        Generation = route.Generation
                        WorkflowRevision = route.WorkflowRevision
                        ObservedAt = now.AddMinutes -20.
                    }
                AttemptStarted attempt
                EffectIntentRecorded processIntent
                EffectDispatchStarted processIntent.OperationId
                HostedEffectReadbackAccepted(readback processIntent (string attemptId))
                EffectSettled(processIntent.OperationId, Applied "provider-revision")
                EffectIntentRecorded candidateIntent
                EffectDispatchStarted candidateIntent.OperationId
                EffectObservationRequired(candidateIntent.OperationId, "candidate-unknown")
                PausedEvent "old-route-recovery"
            ]

        let control: MainRouteControl =
            {
                CommandId = Guid.NewGuid()
                ExpectedSequence = Id.revisionValue (replay events).Revision
                ExpectedGeneration = 1L
                PrincipalId = "pilot-worker"
                IssuedAt = now.AddMinutes -1.
                ExpiresAt = now.AddMinutes 1.
                Reason = "candidate-touch-set-refused"
                Action = "settle-absent-candidate"
            }

        let seededEvents =
            if partialFirstStage then
                let firstReadback =
                    { readback candidateIntent candidateIntent.ResourceId with
                        ProviderRevision = MainAbsentCandidateControl.digest control
                        Exists = false
                    }

                events
                @ [ HostedEffectReadbackAccepted firstReadback
                    EffectSettled(candidateIntent.OperationId, ProvenAbsent) ]
            else
                events

        let! store = storeFactory seededEvents
        let candidateStore =
            { new ICandidateStore with
                member _.Put(_, _) = failwith "unexpected candidate mutation"
                member _.Read(_, _) = Task.FromResult(Error "candidate-not-found")
                member _.Quarantine(_, _, _) = failwith "unexpected quarantine"
                member _.CleanupUnreferenced(_, _, _) = failwith "unexpected cleanup"
            }

        let mutable releases = 0
        let commands =
            { new IExecutorCommandStore with
                member _.BindRoute(_, _) = failwith "unexpected route bind"
                member _.ReadRoute(_, _, _) = failwith "unexpected route read"
                member _.FindAttemptBySession(_, _) = failwith "unexpected session read"
                member _.StageInput(_, _, _) = failwith "unexpected input stage"
                member _.ReadInput(_, _) = failwith "unexpected input read"
                member _.StageWorkspaceManifest(_, _) = failwith "unexpected workspace stage"
                member _.ReadWorkspaceManifest(_, _) = failwith "unexpected workspace read"
                member _.PersistCommand(_, _) = failwith "unexpected executor command"
                member _.ReadPending(_, _) = failwith "unexpected pending read"
                member _.SettleCommand(_, _, _) = failwith "unexpected executor settlement"
                member _.ReserveSubscription(_, _, _, _) = failwith "unexpected reservation"
                member _.SettleSubscription(_, _, _) = failwith "unexpected subscription settlement"
                member _.ReleaseSubscription(_, _, _, _) =
                    releases <- releases + 1
                    Task.FromResult(if releases = 1 then SubscriptionReleased else SubscriptionReleaseDuplicate)
                member _.ReadSubscription(_, _) = failwith "unexpected subscription read"
            }

        let observation: FS.GG.Coordination.Orchestration.Execution.SessionObservation =
            {
                Provider = { Provider = "Codex"; AdapterVersion = "fixture" }
                Session =
                    FS.GG.Coordination.Orchestration.Execution.ProviderSessionReference.create "codex-thread:fixture"
                    |> Result.defaultWith failwith
                Resolved = { Model = None; Effort = None }
                Lifecycle = FS.GG.Coordination.Orchestration.Execution.SessionLifecycle.OutcomeUnknown
                Output = []
                LifecycleReferences = []
                Usage =
                    {
                        Values = Map.empty
                        Cost = FS.GG.Coordination.Orchestration.Execution.CostUnknown "fixture"
                    }
                Candidate = None
                ObservedAt = now.AddMinutes -2.
            }

        let journal =
            { new FS.GG.Coordination.Orchestration.Execution.IExecutionSessionJournal with
                member _.ReadAttempt(_, _, _) =
                    Task.FromResult(
                        Some
                            {
                                Revision = 3L
                                Events =
                                    [
                                        FS.GG.Coordination.Orchestration.Execution.LaunchIntentRecorded launch
                                        FS.GG.Coordination.Orchestration.Execution.ObservationRecorded observation
                                        FS.GG.Coordination.Orchestration.Execution.CancelRequested(now.AddMinutes -1.)
                                    ]
                            }
                    )
                member _.AppendAttempt(_, _, _, _, _) = failwith "unexpected execution append"
            }

        let response status body =
            Response
                {
                    StatusCode = status
                    Headers = Map.empty
                    Body = body
                    ETag = Some "fixture"
                    RateBudget = { Limit = None; Remaining = None; ResetAt = None; Cost = None }
                }

        let githubExecutor =
            QueuedGitHub
                [
                    response 200 "{\"id\":7,\"node_id\":\"R_writer\"}"
                    response 200 "{\"number\":11,\"node_id\":\"I_writer\"}"
                    response 404 "{}"
                    response 200 "[]"
                    response 200 "[]"
                    response 200 "{\"id\":7,\"node_id\":\"R_writer\"}"
                    response 200 "{\"number\":11,\"node_id\":\"I_writer\"}"
                    response 404 "{}"
                    response 200 "[]"
                    response 200 "[]"
                ]

        let github =
            GitHubRouteClient(
                githubExecutor,
                FixedPublisher(Ok(String.replicate 40 "a")),
                { githubTarget with IssueNumber = 11 },
                FixedClock now
            )

        let directory = Directory.CreateTempSubdirectory("full-recovery-").FullName

        try
            let final = Encoding.UTF8.GetBytes "{\"status\":\"completed\"}"
            let stdout = Encoding.UTF8.GetBytes "{\"type\":\"turn.started\"}\n{\"type\":\"turn.completed\"}\n"
            let sha (bytes: byte array) =
                System.Security.Cryptography.SHA256.HashData bytes
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()

            File.WriteAllBytes(Path.Combine(directory, "final.json"), final)
            File.WriteAllBytes(Path.Combine(directory, "stdout.jsonl"), stdout)
            File.WriteAllText(
                Path.Combine(directory, "manifest.json"),
                JsonSerializer.Serialize
                    {|
                        schema = "fsgg.orchestration.terminal-evidence/1"
                        assignmentId = string assignment
                        attemptId = string attemptId
                        generation = 1L
                        finalSha256 = sha final
                        stdoutSha256 = sha stdout
                        finalBytes = final.LongLength
                        stdoutBytes = stdout.LongLength
                        capturedAt = now
                        processTerminationObserved = true
                    |}
            )

            let! result =
                MainProductionAdmission.SettleAbsentCandidateCore(
                    FixedClock now,
                    store,
                    candidateStore,
                    commands,
                    journal,
                    route.WorkItemId,
                    "pilot-worker",
                    github,
                    directory,
                    preparation,
                    control,
                    CancellationToken.None
                )

            Assert.True(Result.isOk result, sprintf "%A" result)
            let! after = HostedWriterJournal.recover store route.WorkItemId CancellationToken.None
            let settled = after |> Result.defaultWith (fun failures -> failwithf "%A" failures)
            Assert.True(settled.State.Reservation.IsNone)
            Assert.Empty settled.State.ExternalClaims
            Assert.Empty settled.State.RecoveryObligations
            Assert.Equal(1, releases)
            Assert.Equal(
                Some(OperationState.Settled(candidateIntent, ProvenAbsent)),
                Map.tryFind candidateIntent.OperationId settled.State.Operations
            )
            Assert.Equal(
                AttemptStatus.ReconciledAbsent "candidate-deliverable-absent:candidate-touch-set-refused",
                settled.State.Attempts[route.AttemptId].Status
            )

            let! repeated =
                MainProductionAdmission.SettleAbsentCandidateCore(
                    FixedClock(now.AddMinutes 5.),
                    store,
                    candidateStore,
                    commands,
                    journal,
                    route.WorkItemId,
                    "pilot-worker",
                    github,
                    directory,
                    preparation,
                    control,
                    CancellationToken.None
                )

            Assert.True(Result.isOk repeated, sprintf "%A" repeated)
            let! afterRepeat = HostedWriterJournal.recover store route.WorkItemId CancellationToken.None
            let repeatedState = afterRepeat |> Result.defaultWith (fun failures -> failwithf "%A" failures)
            Assert.Equal(settled.Sequence, repeatedState.Sequence)
            Assert.Equal(2, releases)
            Assert.All(githubExecutor.Requests, fun request ->
                match request with
                | Rest value -> Assert.Equal(RestMethod.Get, value.Method)
                | GraphQL _ -> failwith "unexpected GraphQL")
        finally
            Directory.Delete(directory, true)
    }

[<Fact>]
let ``failed candidate recovery settles exact journal state without provider mutation`` () =
    runFailedCandidateRecoveryFixture false (fun events -> Task.FromResult(MemoryStore events :> IJournalStore))

[<Fact>]
let ``failed candidate recovery resumes after durable stage-one lost response`` () =
    runFailedCandidateRecoveryFixture true (fun events -> Task.FromResult(MemoryStore events :> IJournalStore))

[<Fact>]
let ``GitHub route refuses competing canonical claim marker`` () =
    task {
        let operation = Guid.Parse "80000000-0000-0000-0000-000000000001"

        let comments =
            $"[{{\"id\":1,\"updated_at\":\"2026-09-10T19:00:00Z\",\"body\":\"<!-- fsgg:claim worker=other lease=30 renewed=1 session={operation:N} -->\"}}]"

        let executor = QueuedGitHub[response comments]

        let client =
            GitHubRouteClient(
                executor,
                FixedPublisher(Ok(String.replicate 40 "a")),
                githubTarget,
                FixedClock Fixture.now
            )

        let! result = client.AcquireClaim("claim-1", operation, CancellationToken.None)
        Assert.Equal(Error "github-claim-held-by-competitor", result)
        Assert.Single(executor.Requests) |> ignore
    }

[<Fact>]
let ``GitHub route recognizes canonical claim metadata without stealing ownership`` () =
    task {
        let operation = Guid.Parse "80000000-0000-0000-0000-000000000001"

        let comments =
            $"[{{\"id\":1,\"updated_at\":\"2026-09-10T19:00:00Z\",\"body\":\"<!-- fsgg:claim worker=other lease=30 renewed=1 session={operation:N} prev=Ready pathRepo=FS-GG.github agentContract=v1 -->\"}}]"

        let executor = QueuedGitHub[response comments]

        let client =
            GitHubRouteClient(
                executor,
                FixedPublisher(Ok(String.replicate 40 "a")),
                githubTarget,
                FixedClock Fixture.now
            )

        let! result = client.AcquireClaim("claim-1", operation, CancellationToken.None)
        Assert.Equal(Error "github-claim-held-by-competitor", result)
        Assert.Single(executor.Requests) |> ignore
    }

[<Fact>]
let ``GitHub route refuses missing branch readback and unmerged delivery`` () =
    task {
        let head = String.replicate 40 "a"

        let executor =
            QueuedGitHub[FS.GG.Coordination.GitHub.Response
                             {
                                 StatusCode = 404
                                 Headers = Map.empty
                                 Body = "{}"
                                 ETag = None
                                 RateBudget =
                                     {
                                         Limit = None
                                         Remaining = None
                                         ResetAt = None
                                         Cost = None
                                     }
                             }

                         response (pr "open" None head None)]

        let client =
            GitHubRouteClient(executor, FixedPublisher(Ok head), githubTarget, FixedClock Fixture.now)

        let! publication =
            client.PublishBranch("refs/heads/pilot", None, head, [| 1uy |], Guid.NewGuid(), CancellationToken.None)

        Assert.Equal(Error "github-status-404", publication)
        let! delivery = client.ReadDelivery("refs/heads/pilot", head, CancellationToken.None)
        Assert.Equal(Error "github-native-delivery-not-observed", delivery)
    }

[<Fact>]
let ``GitHub route adopts one exact closed pull request without reopening or duplicating it`` () =
    task {
        let head = String.replicate 40 "a"
        let executor = QueuedGitHub[response (pr "closed" None head None)]

        let client =
            GitHubRouteClient(
                executor,
                FixedPublisher(Error "publisher-must-not-run"),
                githubTarget,
                FixedClock Fixture.now
            )

        let! result =
            client.CreatePullRequest("refs/heads/pilot", head, Guid.NewGuid(), CancellationToken.None)

        Assert.Equal(Ok("PR_node", head), result)
        let request = Assert.Single executor.Requests

        match request with
        | Rest value ->
            Assert.Equal(RestMethod.Get, value.Method)
            Assert.EndsWith("/pulls", value.Uri.AbsolutePath)
            Assert.Contains("state=all", value.Uri.Query)
        | _ -> failwith "expected one read-only REST census"
    }

[<Fact>]
let ``GitHub native delivery facts retain exact PR base head merge and occurrence`` () =
    task {
        let head = String.replicate 40 "a"
        let merge = String.replicate 40 "c"
        let occurred = "2026-09-20T10:15:00Z"
        let executor = QueuedGitHub[response (pr "closed" (Some occurred) head (Some merge))]

        let client =
            GitHubRouteClient(
                executor,
                FixedPublisher(Error "publisher-must-not-run"),
                githubTarget,
                FixedClock Fixture.now
            )

        let! result = client.ReadDeliveryFacts("refs/heads/pilot", head, CancellationToken.None)

        match result with
        | Error reason -> failwith reason
        | Ok facts ->
            Assert.Equal(42, facts.Number)
            Assert.Equal("PR_node", facts.NodeId)
            Assert.Equal("main", facts.BaseRef)
            Assert.Equal(String.replicate 40 "b", facts.BaseSha)
            Assert.Equal(head, facts.HeadSha)
            Assert.Equal(merge, facts.MergeCommitSha)
            Assert.Equal(DateTimeOffset.Parse occurred, facts.MergedAt)
    }

[<Fact>]
let ``native delivery batch has stable item identity and authoritative merge fields`` () =
    let client =
        GitHubRouteClient(
            QueuedGitHub[],
            FixedPublisher(Error "publisher-must-not-run"),
            githubTarget,
            FixedClock Fixture.now
        )

    let publisher =
        FS.GG.Coordination.Orchestration.Runner.Client.TelemetryCliPublisher
            {
                Executable = "/unused/cli"
                Config = "/unused/config"
                CredentialFile = "/unused/credential"
                CertificateAuthorityFile = "/unused/ca"
                Outbox = "/unused/outbox"
                Repository = "FS-GG/.github"
                BindingDigest = String.replicate 64 "a"
            }

    let bridge = TelemetryOutcomeBridge("FS-GG/.github", client, publisher)
    let readback = Fixture.native (Fixture.intent ReadNativeDelivery Fixture.route.ReadbackOperationId)
    let facts =
        {
            Number = 42
            NodeId = readback.PullRequestNodeId
            BaseRef = "main"
            BaseSha = String.replicate 40 "d"
            HeadSha = readback.CandidateHeadSha
            MergeCommitSha = readback.MergeCommitSha
            MergedAt = Fixture.now.AddMinutes -2.
            Revision = "fixture-etag"
        }

    let name, bytes = bridge.CreateBatch(Fixture.route, readback, facts)
    let repeatedName, repeatedBytes = bridge.CreateBatch(Fixture.route, readback, facts)
    Assert.Equal(name, repeatedName)
    Assert.Equal<byte array>(bytes, repeatedBytes)
    use document = JsonDocument.Parse bytes
    let root = document.RootElement
    Assert.Equal("fsgg.telemetry.ingest/1", root.GetProperty("schema").GetString())
    Assert.Equal(2, root.GetProperty("eventCount").GetInt32())
    let outcome = root.GetProperty("events")[0]
    Assert.Equal("native-item-outcome", outcome.GetProperty("kind").GetString())
    Assert.Equal(WorkItemIdentity.persistenceId Fixture.route.WorkItemId, outcome.GetProperty("itemId").GetString())
    Assert.Equal("orchestration-delivery", outcome.GetProperty("sourceKind").GetString())
    Assert.Equal("delivered", outcome.GetProperty("codeDelivery").GetString())
    Assert.Equal(42, outcome.GetProperty("prNumber").GetInt32())
    Assert.Equal(facts.BaseSha, outcome.GetProperty("baseSha").GetString())
    Assert.Equal(facts.MergeCommitSha, outcome.GetProperty("mergeCommit").GetString())
    Assert.Equal(facts.MergedAt, outcome.GetProperty("occurredAt").GetDateTimeOffset())
    let population = root.GetProperty("events")[1]
    Assert.Equal("budget-population", population.GetProperty("kind").GetString())
    Assert.Equal("completed", population.GetProperty("state").GetString())
    Assert.Equal("native-item", population.GetProperty("sourceKind").GetString())
    Assert.Equal(outcome.GetProperty("itemId").GetString(), population.GetProperty("originalItemId").GetString())

[<Fact>]
let ``Host startup drain applies a pending outcome without route admission`` () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "fsgg-outcome-drain-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore

        try
            let file name (contents: string) mode =
                let path = Path.Combine(root, name)
                File.WriteAllText(path, contents)
                File.SetUnixFileMode(path, mode)
                path

            let privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
            let executable = file "cli" "#!/bin/sh\nprintf 'applied\\n'\n" (privateMode ||| UnixFileMode.UserExecute)
            let config = file "workspace.json" "{}" privateMode
            let _ = file "workspace.json.lock" "" privateMode
            let credential = file "credential" "fixture-secret" privateMode
            let ca = file "ca.crt" "fixture" privateMode
            let outbox = Path.Combine(root, "outbox")

            let publisher =
                FS.GG.Coordination.Orchestration.Runner.Client.TelemetryCliPublisher
                    {
                        Executable = executable
                        Config = config
                        CredentialFile = credential
                        CertificateAuthorityFile = ca
                        Outbox = outbox
                        Repository = "FS-GG/.github"
                        BindingDigest = String.replicate 64 "a"
                    }

            let queued = publisher.Queue("batch-pending-outcome", Encoding.UTF8.GetBytes "fixture-batch")
            Assert.True(Result.isOk queued)
            let github =
                GitHubRouteClient(
                    QueuedGitHub[],
                    FixedPublisher(Error "publisher-must-not-run"),
                    githubTarget,
                    FixedClock Fixture.now
                )

            let bridge = TelemetryOutcomeBridge("FS-GG/.github", github, publisher)
            use stopping = new CancellationTokenSource()
            let drain = bridge.DrainUntilCancelled stopping.Token
            let marker = Path.Combine(outbox, "applied", "batch-pending-outcome.sha256")
            let mutable attempts = 0

            while not (File.Exists marker) && attempts < 100 do
                attempts <- attempts + 1
                do! Task.Delay 50

            stopping.Cancel()
            do! drain
            Assert.True(File.Exists marker)
            Assert.Equal(0, publisher.PendingCount)
        finally
            Directory.Delete(root, true)
    }

[<Fact>]
let ``GitHub merge refuses incomplete required checks before mutation`` () =
    task {
        let head = String.replicate 40 "a"
        let protection = "{\"checks\":[{\"context\":\"compiler-and-tests\"}]}"

        let checks =
            $"{{\"check_runs\":[{{\"id\":1,\"head_sha\":\"{head}\",\"name\":\"routine-eligibility\",\"status\":\"completed\",\"conclusion\":\"success\"}}]}}"

        let executor =
            QueuedGitHub[response (pr "open" None head None)
                         response (prDetail head)
                         response routinePolicy
                         response protection
                         response checks]

        let client =
            GitHubRouteClient(executor, FixedPublisher(Ok head), githubTarget, FixedClock Fixture.now)

        let! result =
            client.Merge("refs/heads/pilot", head, Guid.NewGuid(), CancellationToken.None)

        Assert.Equal(Error "github-required-checks-not-green", result)
        Assert.Equal(5, executor.Requests.Length)
    }

[<Fact>]
let ``GitHub routine docs accepts policy and latest exact-head eligibility only`` () =
    task {
        let head = String.replicate 40 "a"

        let checks =
            $"{{\"check_runs\":[{{\"id\":1,\"head_sha\":\"{head}\",\"name\":\"routine-eligibility\",\"status\":\"completed\",\"conclusion\":\"failure\"}},{{\"id\":2,\"head_sha\":\"{head}\",\"name\":\"routine-eligibility\",\"status\":\"completed\",\"conclusion\":\"success\"}}]}}"

        let executor =
            QueuedGitHub[response (pr "open" None head None)
                         response (prDetail head)
                         response routinePolicy
                         responseStatus 404 "{}"
                         response checks]

        let client =
            GitHubRouteClient(executor, FixedPublisher(Ok head), githubTarget, FixedClock Fixture.now)

        let! accepted =
            client.CheckProtectedHead("refs/heads/pilot", head, CancellationToken.None)

        match accepted with
        | Ok(42, "PR_node", "fixture-etag") -> ()
        | other -> failwithf "%A" other

        Assert.Equal(5, executor.Requests.Length)
    }

[<Fact>]
let ``GitHub routine docs refuses newer failed eligibility and unsupported operation`` () =
    task {
        let head = String.replicate 40 "a"

        let checks =
            $"{{\"check_runs\":[{{\"id\":1,\"head_sha\":\"{head}\",\"name\":\"routine-eligibility\",\"status\":\"completed\",\"conclusion\":\"success\"}},{{\"id\":2,\"head_sha\":\"{head}\",\"name\":\"routine-eligibility\",\"status\":\"completed\",\"conclusion\":\"failure\"}}]}}"

        let executor =
            QueuedGitHub[response (pr "open" None head None)
                         response (prDetail head)
                         response routinePolicy
                         responseStatus 404 "{}"
                         response checks]

        let client =
            GitHubRouteClient(executor, FixedPublisher(Ok head), githubTarget, FixedClock Fixture.now)

        let! refused =
            client.CheckProtectedHead("refs/heads/pilot", head, CancellationToken.None)

        Assert.Equal(Error "github-required-checks-not-green", refused)

        let unsupported =
            { githubTarget with
                RoutineOperation = "source-change"
            }

        let unsupportedExecutor = QueuedGitHub[]

        let unsupportedClient =
            GitHubRouteClient(unsupportedExecutor, FixedPublisher(Ok head), unsupported, FixedClock Fixture.now)

        let! profile =
            unsupportedClient.CheckProtectedHead("refs/heads/pilot", head, CancellationToken.None)

        Assert.Equal(Error "github-routine-profile-unsupported", profile)
        Assert.Empty(unsupportedExecutor.Requests)
    }
