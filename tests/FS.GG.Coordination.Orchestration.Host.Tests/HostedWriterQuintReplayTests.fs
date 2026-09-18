module FS.GG.Coordination.Orchestration.Host.Tests.HostedWriterQuintReplayTests

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Pattern
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol
open FsQuint
open Xunit

module Fixture =
    let now = DateTimeOffset.Parse "2026-09-10T19:00:00Z"
    let guid (value: string) = Guid.Parse value
    let sha character = String.replicate 64 character
    let oid character = String.replicate 40 character
    let workItem = WorkItemIdentity.create "MDU6SXNzdWUx" 7L "I_writer_replay" 11L
    let attempt = Id.attempt (guid "10000000-0000-0000-0000-000000000011")
    let session = Id.session (guid "11000000-0000-0000-0000-000000000011")
    let candidate = Id.candidate (guid "20000000-0000-0000-0000-000000000011")

    let operations =
        [ 1..7 ]
        |> List.map (fun value -> Id.operation (guid $"30000000-0000-0000-0000-{value:D12}"))

    let route =
        {
            RouteId = guid "40000000-0000-0000-0000-000000000011"
            WorkItemId = workItem
            JobClass = "routine-documentation-delivery"
            AttemptId = attempt
            CandidateId = candidate
            RepositoryNodeId = "repository-1"
            BranchRef = "refs/heads/fsgg/pilot/replay"
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
            SelectedAt = now.AddMinutes(-1.)
        }

    let snapshot =
        {
            ProjectId = Id.project (guid "50000000-0000-0000-0000-000000000011")
            WorkItemId = workItem
            WorkflowRevision = route.WorkflowRevision
            CanonicalSha256 = sha "b"
            BoardMembershipIds = []
            CapturedAt = now.AddMinutes(-2.)
        }

    let budget =
        {
            Schema = "fsgg.coordination.subscription-execution-budget/2"
            AttemptLimit = 1
            MaximumRuntime = TimeSpan.FromMinutes 30.
            ExecutionDeadline = now.AddMinutes 25.
            DeliveryDeadline = now.AddHours 2.
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
            ReservationId = Id.reservation (guid "60000000-0000-0000-0000-000000000011")
            Generation = route.Generation
            ExpiresAt = now.AddMinutes 20.
            RequiredClaimIds = Set.singleton route.ClaimResourceId
        }

    let runner =
        {
            RunnerId = Id.runner (guid "70000000-0000-0000-0000-000000000011")
            PrincipalId = "pilot"
            FingerprintSha256 = sha "c"
            Generation = route.Generation
            ExpiresAt = now.AddMinutes 20.
        }

    let initialEvents =
        [
            SubscriptionWorkAdmitted(snapshot, budget)
            GenerationAdvanced route.Generation
            ReservationCreated reservation
            HostedRouteSelected route
        ]

    let kinds =
        [
            AcquireExternalClaim
            DispatchRunner
            StoreCandidate
            PublishCandidateBranch
            CreatePullRequest
            MergePullRequest
            ReadNativeDelivery
        ]

    let operation stage = operations[stage]

    let operationName stage =
        [|
            "op-claim"
            "op-process"
            "op-candidate"
            "op-branch"
            "op-pr"
            "op-merge"
            "op-readback"
        |][stage]

    let resource stage =
        match kinds[stage] with
        | AcquireExternalClaim -> route.ClaimResourceId
        | DispatchRunner -> string (Id.attemptValue route.AttemptId)
        | StoreCandidate -> string (Id.candidateValue route.CandidateId)
        | _ -> route.BranchRef

    let intent stage =
        {
            OperationId = operation stage
            Kind = kinds[stage]
            Generation = route.Generation
            WorkflowRevision = route.WorkflowRevision
            ResourceId = resource stage
            PayloadSha256 = sha "a"
        }

    let hosted stage =
        let kind = kinds[stage]

        {
            OperationId = operation stage
            RouteId = route.RouteId
            AttemptId = route.AttemptId
            CandidateId = route.CandidateId
            RepositoryNodeId = route.RepositoryNodeId
            ProviderResourceId =
                (match kind with
                 | CreatePullRequest
                 | MergePullRequest -> "PR_node"
                 | _ -> resource stage)
            CandidateHeadSha =
                (match kind with
                 | StoreCandidate
                 | PublishCandidateBranch
                 | CreatePullRequest
                 | MergePullRequest -> Some(oid "a")
                 | _ -> None)
            ResultSha =
                (match kind with
                 | StoreCandidate -> Some(sha "d")
                 | PublishCandidateBranch -> Some(oid "a")
                 | MergePullRequest -> Some(oid "b")
                 | _ -> None)
            ProviderRevision = $"provider-revision-{stage}"
            Generation = route.Generation
            WorkflowRevision = route.WorkflowRevision
            ObservedAt = now
            Exists = true
        }

    let native () =
        {
            OperationId = route.ReadbackOperationId
            RouteId = route.RouteId
            AttemptId = route.AttemptId
            CandidateId = route.CandidateId
            RepositoryNodeId = route.RepositoryNodeId
            PullRequestNodeId = "PR_node"
            CandidateHeadSha = oid "a"
            ObservedPullRequestHeadSha = oid "a"
            MergeCommitSha = oid "b"
            ProviderRevision = "provider-revision-6"
            Generation = route.Generation
            WorkflowRevision = route.WorkflowRevision
            ObservedAt = now
            Merged = true
        }

    let routeReadback =
        {
            RouteId = route.RouteId
            WorkItemId = workItem
            RepositoryNodeId = route.RepositoryNodeId
            ProviderRevision = "route-reconnected"
            EvidenceSha256 = sha "e"
            Generation = route.Generation
            WorkflowRevision = route.WorkflowRevision
            ObservedAt = now
        }

    let candidateArtifact =
        let contentKey = sha "d"

        {
            CandidateId = candidate
            BaselineSha = oid "0"
            HeadSha = oid "a"
            TreeSha = oid "c"
            ManifestSha256 = sha "f"
            ContentSha256 = sha "d"
            MediaType = "application/vnd.fsgg.runner-candidate+zip"
            SizeBytes = 32L
            RetainUntil = now.AddDays 30.
            Location = ContentAddressedObject($"sha256/{contentKey}")
        }

    let candidateReceipt =
        {
            CandidateId = candidate
            ContentSha256 = sha "d"
            ManifestSha256 = sha "f"
            SizeBytes = 32L
            Location = candidateArtifact.Location
            StoreId = "replay-store"
            StoreSchemaVersion = 2
            StorageReceiptSha256 = sha "9"
            VerifiedAt = now
        }

type private FixedClock() =
    inherit TimeProvider()
    override _.GetUtcNow() = Fixture.now

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
                PayloadSha256 = SHA256.HashData payload |> Convert.ToHexString |> _.ToLowerInvariant()
                EffectChange =
                    (match eventValue with
                     | EffectIntentRecorded intent -> IntentAdded intent
                     | EffectSettled(id, _) -> Settled id
                     | _ -> NoEffect)
                RecordedAt = Fixture.now
            })

    let commands = Dictionary<CommandId, string * int64>()

    member _.DecodedEvents =
        events
        |> List.map (fun stored -> EventEnvelope.tryDecode stored.Payload |> Result.defaultWith failwith)

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

type private ActorRuntime =
    {
        Actor: IActorRef
        Projection: QuintReplayState
    }

type private ApplyReplay = ApplyReplay of QuintReplayStep

let private integer value = QuintReplayValue.Integer(string value)
let private boolean value = QuintReplayValue.Boolean value
let private textValue value = QuintReplayValue.Text value

let private stateValue
    stage
    operationId
    operationStatus
    paused
    readbackCurrent
    restarted
    freshReadback
    adapterClaimedComplete
    unknownObserved
    =
    let progressed threshold = stage >= threshold

    let draft: QuintReplayState =
        {
            Identity = String.replicate 64 "0"
            Bindings =
                [
                    "state",
                    QuintReplayValue.Record
                        [
                            "activeAssignments",
                            integer (
                                if stage = 7 then
                                    0
                                else if paused && stage = 0 && operationStatus = "none" && not restarted then
                                    0
                                else
                                    1
                            )
                            "adapterClaimedComplete", boolean adapterClaimedComplete
                            "attemptId", textValue "attempt-1"
                            "branchPublished", boolean (progressed 4)
                            "budgetLimit", integer 1
                            "budgetUsed",
                            integer (
                                if paused && stage = 0 && operationStatus = "none" && not restarted then
                                    0
                                else
                                    1
                            )
                            "candidateDurable", boolean (progressed 3)
                            "candidateId", textValue "candidate-1"
                            "capacity", integer 1
                            "claimCurrent", boolean (progressed 1)
                            "currentGeneration", integer 1
                            "deliveryOpen", boolean true
                            "evidenceAttemptId", textValue (if stage > 0 then "attempt-1" else "")
                            "evidenceCandidateId", textValue (if stage > 0 then "candidate-1" else "")
                            "evidenceGeneration", integer (if stage > 0 then 1 else 0)
                            "evidenceRepositoryId", textValue (if stage > 0 then "repository-1" else "")
                            "evidenceRouteId", textValue (if stage > 0 then "route-1" else "")
                            "executionOpen", boolean true
                            "freshReadback", boolean freshReadback
                            "jobClass", textValue "routine-documentation-delivery"
                            "mergeObserved", boolean (progressed 6)
                            "nativeReadbackObserved", boolean (progressed 7)
                            "operationId", textValue operationId
                            "operationStatus", textValue operationStatus
                            "paused", boolean paused
                            "permitGeneration", integer 1
                            "pullRequestObserved", boolean (progressed 5)
                            "readbackCurrent", boolean readbackCurrent
                            "repositoryId", textValue "repository-1"
                            "restarted", boolean restarted
                            "routeId", textValue "route-1"
                            "sameOperationRetried", boolean false
                            "stage", integer stage
                            "subjectId", textValue "MDU6SXNzdWUx"
                            "unknownObserved", boolean unknownObserved
                        ]
                ]
        }

    { draft with
        Identity =
            QuintReplay.stateFingerprint draft
            |> Result.defaultWith (fun error -> failwithf "%A" error)
    }

let private appendCommand (store: IJournalStore) command =
    task {
        let! recovered =
            HostedWriterJournal.recover store Fixture.workItem CancellationToken.None

        let state =
            (recovered |> Result.defaultWith (fun failures -> failwithf "%A" failures)).State

        let envelope =
            {
                CommandId = Id.command (Guid.NewGuid())
                ProtocolVersion = Id.protocolVersion 1 0
                ExpectedRevision = state.Revision
                ExpectedGeneration = state.Generation
                PrincipalId = "quint-replay"
                SessionId = None
                IssuedAt = Fixture.now
                ExpiresAt = Fixture.now.AddMinutes 1.
                Command = command
            }

        let! appended =
            HostedWriterJournal.decideAndAppend (FixedClock()) store Fixture.workItem envelope CancellationToken.None

        match appended with
        | Ok(decision, _) when
            decision.Receipt.Disposition = ReceiptDisposition.Accepted
            || decision.Receipt.Disposition = ReceiptDisposition.Duplicate
            ->
            return ()
        | Ok(decision, _) -> return failwith $"{decision.Receipt.Disposition}: {decision.Receipt.Detail}"
        | Error reason -> return failwith reason
    }

let private createAdapter onDispatch =
    let hosted stage _ _ _ =
        onDispatch stage
        Task.FromResult(Ok(Fixture.hosted stage))

    HostedWriterProviderAdapter.Create
        {
            AcquireExternalClaim = hosted 0
            DispatchRunner = hosted 1
            StoreCandidate = hosted 2
            PublishCandidateBranch = hosted 3
            CreatePullRequest = hosted 4
            MergePullRequest = hosted 5
            ReadNativeDelivery =
                fun _ _ _ ->
                    onDispatch 6
                    Task.FromResult(Ok(Fixture.native ()))
        }

let private reconcileReadback stage =
    match Fixture.kinds[stage] with
    | ReadNativeDelivery -> NativeDelivery(Fixture.native ())
    | _ -> HostedEffect(Fixture.hosted stage)

let private stageOf (state: State) =
    Fixture.operations
    |> List.takeWhile (fun operationId ->
        state.Operations
        |> Map.tryFind operationId
        |> Option.exists (function
            | OperationState.Settled(_, Applied _) -> true
            | _ -> false))
    |> List.length

let private project (store: MemoryStore) faultyNative =
    task {
        let! recovered =
            HostedWriterJournal.recover (store :> IJournalStore) Fixture.workItem CancellationToken.None

        let state =
            (recovered |> Result.defaultWith (fun failures -> failwithf "%A" failures)).State

        let stage = stageOf state

        let operationId, operationStatus =
            if stage = 7 then
                "", "none"
            else
                match Map.tryFind (Fixture.operation stage) state.Operations with
                | None -> "", "none"
                | Some(IntentRecorded _) -> Fixture.operationName stage, "intent"
                | Some(Dispatching _) -> Fixture.operationName stage, "dispatching"
                | Some(NeedsObservation _) -> Fixture.operationName stage, "unknown"
                | Some(OperationState.Settled _) -> "", "none"

        let events = store.DecodedEvents

        let restarted =
            events
            |> List.exists (function
                | StartupPausedEvent _ -> true
                | _ -> false)

        let fresh =
            events
            |> List.exists (function
                | HostedRouteReadbackAccepted _ -> true
                | _ -> false)

        let unknown =
            events
            |> List.exists (function
                | EffectObservationRequired _ -> true
                | _ -> false)

        let paused =
            match state.Control with
            | Paused _ -> true
            | _ -> false

        let projectedStage = if faultyNative && stage = 7 then 6 else stage

        return
            stateValue
                projectedStage
                operationId
                operationStatus
                paused
                state.ReadbackCurrent
                restarted
                fresh
                false
                unknown
    }

type private HostedWriterReplayActor(faultyNative: bool) =
    inherit UntypedActor()
    let store = MemoryStore Fixture.initialEvents
    let journal = store :> IJournalStore
    let dispatchCounts = Array.zeroCreate Fixture.kinds.Length

    let adapter =
        createAdapter (fun stage -> dispatchCounts[stage] <- dispatchCounts[stage] + 1)

    let mutable pending: HostedWriterProviderReadback option = None

    override this.OnReceive message =
        let replyTo = this.Sender
        let self = this.Self

        let work =
            task {
                match message with
                | :? ApplyReplay as envelope ->
                    let (ApplyReplay step) = envelope

                    match step.Action with
                    | "manualStart" -> do! appendCommand journal Resume
                    | "recordIntent" ->
                        let! current =
                            HostedWriterJournal.recover journal Fixture.workItem CancellationToken.None

                        let stage =
                            current
                            |> Result.defaultWith (fun failures -> failwithf "%A" failures)
                            |> _.State
                            |> stageOf

                        do! appendCommand journal (RecordEffectIntent(Fixture.intent stage))
                    | "dispatch" ->
                        let! current =
                            HostedWriterJournal.recover journal Fixture.workItem CancellationToken.None

                        let stage =
                            current
                            |> Result.defaultWith (fun failures -> failwithf "%A" failures)
                            |> _.State
                            |> stageOf

                        do! appendCommand journal (MarkEffectDispatching(Fixture.operation stage))

                        let! result =
                            adapter.Dispatch(Fixture.route, Fixture.intent stage, CancellationToken.None)

                        pending <- Some(result |> Result.defaultWith failwith)
                    | "loseResponse" ->
                        let! current =
                            HostedWriterJournal.recover journal Fixture.workItem CancellationToken.None

                        let stage =
                            current
                            |> Result.defaultWith (fun failures -> failwithf "%A" failures)
                            |> _.State
                            |> stageOf

                        do!
                            appendCommand
                                journal
                                (ObserveEffect(Fixture.operation stage, Unknown "github-timeout-unknown"))

                        pending <- None
                    | "observeApplied"
                    | "reconcileApplied" ->
                        let! current =
                            HostedWriterJournal.recover journal Fixture.workItem CancellationToken.None

                        let stage =
                            current
                            |> Result.defaultWith (fun failures -> failwithf "%A" failures)
                            |> _.State
                            |> stageOf

                        let observed =
                            match step.Action, pending with
                            | "observeApplied", Some value -> value
                            | "observeApplied", None -> failwith "provider-response-is-not-pending"
                            | "reconcileApplied", None ->
                                if dispatchCounts[stage] <> 1 then
                                    failwith
                                        $"expected exactly one provider dispatch before reconciliation, got {dispatchCounts[stage]}"

                                // The provider already applied the effect before its response was lost.
                                // Reconciliation observes that fact and must not dispatch the effect again.
                                reconcileReadback stage
                            | "reconcileApplied", Some _ ->
                                failwith "reconciliation-cannot-consume-a-pending-dispatch-response"
                            | action, _ -> failwith $"unsupported observation action: {action}"

                        pending <- None

                        match observed with
                        | HostedEffect readback ->
                            do! appendCommand journal (RecordHostedEffectReadback(Fixture.operation stage, readback))

                            if stage = 0 then
                                do!
                                    appendCommand
                                        journal
                                        (ObserveClaim
                                            {
                                                ClaimId = Fixture.route.ClaimResourceId
                                                Generation = Fixture.route.Generation
                                                WorkflowRevision = Fixture.route.WorkflowRevision
                                                ObservedAt = Fixture.now
                                            })

                                do!
                                    appendCommand
                                        journal
                                        (StartAttempt(Fixture.attempt, Fixture.session, Fixture.runner))
                            elif stage = 2 then
                                do!
                                    appendCommand
                                        journal
                                        (RecordCandidate(Fixture.candidateArtifact, Fixture.candidateReceipt))
                        | NativeDelivery readback ->
                            do! appendCommand journal (RecordNativeDeliveryReadback(Fixture.operation stage, readback))
                            do! appendCommand journal (ObserveAttempt(Fixture.attempt, Completed))
                    | "restartPaused" -> do! appendCommand journal (RecordStartupPause "quint-restart")
                    | "reconnect" -> do! appendCommand journal (RecordHostedRouteReadback Fixture.routeReadback)
                    | "resume" -> do! appendCommand journal Resume
                    | action -> failwith $"unbound Quint action: {action}"

                    let! projection = project store faultyNative
                    return Ok projection
                | _ -> return Error "replay-actor-message-refused"
            }

        work.ContinueWith(fun (completed: Task<Result<QuintReplayState, string>>) ->
            if completed.IsCompletedSuccessfully then
                replyTo.Tell(completed.Result, self)
            else
                replyTo.Tell(Error(completed.Exception.GetBaseException().Message), self))
        |> ignore

let private actorProps faultyNative =
    Props.Create(typeof<HostedWriterReplayActor>, [| box faultyNative |])

let private driver (system: ActorSystem) faultyNative initial : ReplayDriver<ActorRuntime ref> =
    {
        Initialize =
            fun _ _ ->
                Task.FromResult(
                    Ok(
                        ref
                            {
                                Actor = system.ActorOf(actorProps faultyNative)
                                Projection = initial
                            }
                    )
                )
        Apply =
            fun step runtime token ->
                task {
                    token.ThrowIfCancellationRequested()

                    let! result =
                        runtime.Value.Actor.Ask<Result<QuintReplayState, string>>(
                            ApplyReplay step,
                            TimeSpan.FromSeconds 10.
                        )

                    return
                        result
                        |> Result.map (fun projection ->
                            runtime.Value <-
                                { runtime.Value with
                                    Projection = projection
                                })
                }
        Observe = fun runtime _ -> Task.FromResult(Ok runtime.Value.Projection)
        Cleanup =
            fun runtime _ ->
                system.Stop(runtime.Value.Actor)
                Task.FromResult(Ok())
    }

let private runReplay faultyNative scenarioId =
    task {
        use system = ActorSystem.Create($"hosted-writer-quint-{Guid.NewGuid():N}")
        let replayTrace = (ChoreoTrace.load scenarioId).Replay

        let! result =
            Replay.run
                (TimeSpan.FromSeconds 60.)
                System.Threading.CancellationToken.None
                (driver system faultyNative replayTrace.Initial)
                replayTrace

        do! system.Terminate()
        return result
    }

[<Fact>]
let ``Akka hosted writer replays the genuine Quint Choreo happy path`` () =
    task {
        match! runReplay false "happy-path" with
        | {
              Outcome = ReplayOutcome.Equivalent
              CleanupFailure = None
          } -> ()
        | result -> Assert.Fail($"expected equivalent replay, got %A{result}")
    }

[<Fact>]
let ``Akka hosted writer reconciliation does not redispatch after a lost response`` () =
    task {
        match! runReplay false "lost-applied" with
        | {
              Outcome = ReplayOutcome.Equivalent
              CleanupFailure = None
          } -> ()
        | result -> Assert.Fail($"expected equivalent recovery replay, got %A{result}")
    }

[<Fact>]
let ``missing native readback projection diverges at exact Quint action`` () =
    task {
        let trace = (ChoreoTrace.load "happy-path").Replay

        match! runReplay true "happy-path" with
        | {
              Outcome = ReplayOutcome.Diverged(step, action, actualSource, path, _, _)
              CleanupFailure = None
          } ->
            Assert.Equal(trace.Steps.Length, step)
            Assert.Equal(Some "observeApplied", action)
            Assert.Equal(Some(trace.Steps |> List.last |> _.Source), actualSource)
            Assert.StartsWith("$/bindings/", path)
        | result -> Assert.Fail($"expected exact native-readback divergence, got %A{result}")
    }
