module FS.GG.Coordination.Orchestration.Host.Tests.HostTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.Pilot
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

module Fixture =
    let now = DateTimeOffset.Parse "2026-09-10T15:00:00Z"
    let permitId = Guid.Parse "62000000-0000-0000-0000-000000000001"

    let permit =
        {
            SchemaVersion = 1
            PermitId = permitId
            SubjectId = WorkItemIdentity.create "R_host" 8L "I_host" 21L
            JobClass = "routine-documentation-delivery"
            StableOwnerId = "stable-route"
            PilotOwnerId = "pilot-route"
            Generation = Id.generation 3L
            AttemptLimit = 2L
            TokenLimit = 100L
            RuntimeSecondsLimit = 120L
            CostMicrosLimit = 1000L
            ExpiresAt = now.AddHours 1.
            Capacity = 2
            RecoveryCapacity = 1
            StartupPolicy = "manual"
            AutoResume = false
        }

    let readback purpose =
        {
            SubjectId = permit.SubjectId
            Generation = permit.Generation
            Purpose = purpose
            OperationId = None
            Provider = "github-readback-fixture"
            SourceRevision = "revision-1"
            EvidenceSha256 = String.replicate 64 "a"
            ObservedAt = now
        }
        |> fun input ->
            let capability =
                { new IPilotReadbackCapability with
                    member _.ReadCurrent(_, _) = Task.FromResult(Ok input)
                }

            PilotReadback.read
                ({ new TimeProvider() with
                     override _.GetUtcNow() = now
                 })
                capability
                {
                    Permit = permit
                    Purpose = purpose
                    OperationId = None
                    NotBefore = now.AddSeconds -1.
                }
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> Result.defaultWith failwith

    let pilotOwned =
        [
            PermitIssued permit
            TransferIntentPersisted
                {
                    SubjectId = permit.SubjectId
                    Generation = permit.Generation
                    EvidenceSha256 = String.replicate 64 "b"
                    Quiesced = true
                    Excluded = true
                    ObservedAt = now
                }
            TransferAcknowledged(readback TransferAcknowledgement)
        ]
        |> Pilot.replay

    type FixedClock() =
        inherit TimeProvider()
        override _.GetUtcNow() = now

    type MemoryJournal() =
        let mutable events: SerializedEvent list = []
        let accepted = Dictionary<CommandId, string * int64>()

        member _.State =
            events
            |> List.map (fun stored -> EventEnvelope.tryDecode stored.Payload |> Result.defaultWith failwith)
            |> replay

        interface IJournalStore with
            member _.CheckReadiness _ = Task.FromResult(Ok())

            member _.Recover(_, _) =
                Task.FromResult(
                    Ok
                        {
                            Events = events
                            Snapshot = None
                            UnsettledEffects = []
                            RequiresExternalReconciliation = false
                        }
                )

            member _.Append(request, _) =
                match accepted.TryGetValue request.Inbox.CommandId with
                | true, (digest, sequence) when digest = request.Inbox.BodySha256 ->
                    Task.FromResult(AppendOutcome.Duplicate sequence)
                | true, _ -> Task.FromResult AppendOutcome.Conflict
                | false, _ when request.ExpectedSequence <> int64 events.Length ->
                    Task.FromResult(WrongExpectedSequence(int64 events.Length))
                | false, _ ->
                    let sequence =
                        request.Events
                        |> List.tryLast
                        |> Option.map _.Sequence
                        |> Option.defaultValue request.ExpectedSequence

                    accepted.Add(request.Inbox.CommandId, (request.Inbox.BodySha256, sequence))
                    events <- events @ request.Events
                    Task.FromResult(AppendOutcome.Appended sequence)

            member _.SaveSnapshot(_, _) = Task.FromResult(Ok())
            member _.SaveProjectionCheckpoint(_, _) = Task.FromResult(Ok())

    type MemoryExecutor(state: unit -> State) =
        let inputs = Dictionary<string, byte array>()
        let workspaces = Dictionary<string, byte array>()
        let routes = Dictionary<Guid * Guid, byte array>()
        let attempts = Dictionary<Guid * Guid, StoredSession>()
        let subscriptions = Dictionary<Guid, byte array>()
        let mutable writes = 0
        let mutable admissionObserved = false
        member _.Writes = writes
        member _.AdmissionObserved = admissionObserved

        interface IExecutorCommandStore with
            member _.StageInput(manifestBytes, bytes, _) =
                writes <- writes + 1
                let current = state ()

                admissionObserved <-
                    current.WorkItemId.IsSome
                    && current.HostedRoute.IsSome
                    && (match current.Control with
                        | Paused _ -> true
                        | _ -> false)
                    && not current.ReadbackCurrent

                match ExecutorWire.parseInputManifest manifestBytes with
                | Error reason -> Task.FromResult(Error reason)
                | Ok manifest ->
                    match inputs.TryGetValue manifest.InputDigest with
                    | true, prior when not (ReadOnlySpan<byte>(prior).SequenceEqual(ReadOnlySpan<byte>(bytes))) ->
                        Task.FromResult(Error "input-conflict")
                    | _ ->
                        inputs[manifest.InputDigest] <- bytes
                        Task.FromResult(Ok())

            member _.ReadInput(digest, _) =
                match inputs.TryGetValue digest with
                | true, value -> Task.FromResult(Ok value)
                | _ -> Task.FromResult(Error "missing")

            member _.StageWorkspaceManifest(bytes, _) =
                writes <- writes + 1
                let digest = RunnerWire.sha256 bytes

                match workspaces.TryGetValue digest with
                | true, prior when not (ReadOnlySpan<byte>(prior).SequenceEqual(ReadOnlySpan<byte>(bytes))) ->
                    Task.FromResult(Error "workspace-conflict")
                | _ ->
                    workspaces[digest] <- bytes
                    Task.FromResult(Ok digest)

            member _.ReadWorkspaceManifest(digest, _) =
                match workspaces.TryGetValue digest with
                | true, value -> Task.FromResult(Ok value)
                | _ -> Task.FromResult(Error "missing")

            member _.BindRoute(bytes, _) =
                writes <- writes + 1

                match ExecutorWire.parseRouteBinding bytes with
                | Error reason -> Task.FromResult(Error reason)
                | Ok binding ->
                    let key = binding.AssignmentId, binding.AttemptId

                    match routes.TryGetValue key with
                    | true, prior when not (ReadOnlySpan<byte>(prior).SequenceEqual(ReadOnlySpan<byte>(bytes))) ->
                        Task.FromResult(Error "route-conflict")
                    | _ ->
                        routes[key] <- bytes
                        Task.FromResult(Ok binding.BindingSha256)

            member _.ReadRoute(assignment, attempt, _) =
                match routes.TryGetValue((assignment, attempt)) with
                | true, value -> Task.FromResult(Ok value)
                | _ -> Task.FromResult(Error "missing")

            member _.FindAttemptBySession(_, _) = Task.FromResult(Error "unused")
            member _.PersistCommand(_, _) = Task.FromResult CommandConflict
            member _.ReadPending(_, _) = Task.FromResult []
            member _.SettleCommand(_, _, _) = Task.FromResult(Ok())

            member _.ReserveSubscription(bytes, _, _, _) =
                writes <- writes + 1

                match SubscriptionAccountingCodec.decodeReservation bytes with
                | Error _ -> Task.FromResult SubscriptionAuthorityRefused
                | Ok reservation ->
                    match subscriptions.TryGetValue reservation.ReservationId with
                    | true, prior when ReadOnlySpan<byte>(prior).SequenceEqual(ReadOnlySpan<byte>(bytes)) ->
                        Task.FromResult SubscriptionDuplicate
                    | true, _ -> Task.FromResult SubscriptionConflict
                    | false, _ ->
                        subscriptions.Add(reservation.ReservationId, bytes)
                        Task.FromResult SubscriptionReserved

            member _.SettleSubscription(_, _, _) = Task.FromResult(Ok())

            member _.ReleaseSubscription(id, _, _, _) =
                if subscriptions.Remove id then
                    Task.FromResult SubscriptionReleased
                else
                    Task.FromResult SubscriptionReleaseDuplicate

            member _.ReadSubscription(id, _) =
                match subscriptions.TryGetValue id with
                | true, value -> Task.FromResult(Ok(value, None))
                | _ -> Task.FromResult(Error "missing")

        interface IExecutionSessionJournal with
            member _.ReadAttempt(assignment, attempt, _) =
                match attempts.TryGetValue((assignment, attempt)) with
                | true, value -> Task.FromResult(Some value)
                | _ -> Task.FromResult None

            member _.AppendAttempt(assignment, attempt, revision, eventValue, _) =
                writes <- writes + 1
                let key = assignment, attempt

                match attempts.TryGetValue key with
                | false, _ ->
                    attempts.Add(
                        key,
                        {
                            Revision = 1L
                            Events = [ eventValue ]
                        }
                    )

                    Task.FromResult Appended
                | true, stored when revision = stored.Revision ->
                    attempts[key] <-
                        {
                            Revision = revision + 1L
                            Events = stored.Events @ [ eventValue ]
                        }

                    Task.FromResult Appended
                | true, stored when revision = 0L && stored.Events = [ eventValue ] -> Task.FromResult DuplicateEvent
                | _ -> Task.FromResult AppendConflict

    let preparationRequest () =
        let ids =
            [|
                for index in 1..15 -> Guid.Parse(sprintf "71000000-0000-4000-8000-%012d" index)
            |]

        {
            Schema = MainAdmissionPreparer.schema
            PreparationId = ids[0]
            ProjectId = ids[1]
            WorkflowRevision = 7L
            CanonicalSha256 = String.replicate 64 "a"
            SelectedAt = now
            RouteId = ids[2]
            AttemptId = ids[3]
            CandidateId = ids[4]
            BranchRef = "refs/heads/fsgg/pilot/o2-i4c"
            ClaimResourceId = "claim-o2-i4c"
            ClaimOperationId = ids[5]
            ProcessOperationId = ids[6]
            CandidateOperationId = ids[7]
            BranchOperationId = ids[8]
            PullRequestOperationId = ids[9]
            MergeOperationId = ids[10]
            ReadbackOperationId = ids[11]
            RunnerId = ids[12]
            RunnerFingerprintSha256 = String.replicate 64 "b"
            SessionId = ids[13]
            ReservationId = ids[14]
            ExecutionReservationId = Guid.Parse("71000000-0000-4000-8000-000000000016")
            RouteProviderRevision = "selected-route"
            RouteEvidenceSha256 = String.replicate 64 "c"
            RepositoryBinding = "selected-repository"
            BaselineObjectId = String.replicate 40 "d"
            Workspace = "pilot"
            AllowedPaths = [| "docs/**" |]
            Validations = [| "git-diff-check" |]
            ExecutorBinding = "codex-main"
            RequestedModel = null
            RequestedEffort = null
            InputMediaType = "text/markdown; charset=utf-8"
        }

    let unusedWorkItems =
        { new IJournalStore with
            member _.CheckReadiness _ = Task.FromResult(Ok())

            member _.Recover(_, _) =
                Task.FromResult(Error [ StoreUnavailable "unused" ])

            member _.Append(_, _) = Task.FromResult(InvalidAppend "unused")
            member _.SaveSnapshot(_, _) = Task.FromResult(Ok())
            member _.SaveProjectionCheckpoint(_, _) = Task.FromResult(Ok())
        }

    let unusedCandidates =
        { new ICandidateStore with
            member _.Put(_, _) = Task.FromResult(Error CapacityRefused)
            member _.Read(_, _) = Task.FromResult(Error "unused")
            member _.Quarantine(_, _, _) = Task.FromResult(Error "unused")
            member _.CleanupUnreferenced(_, _, _) = Task.FromResult 0
        }

    let durableStore state =
        let accepted = Dictionary<Guid, string * int64>()
        let appended = ResizeArray<PilotAppendRequest>()

        let store =
            {
                CheckReadiness = fun _ -> Task.FromResult(Ok())
                WorkItems = unusedWorkItems
                Candidates = unusedCandidates
                Recover = fun _ _ -> Task.FromResult(Ok { Events = []; State = state })
                Append =
                    fun request _ ->
                        let digest = PilotCodec.commandSha256 request.Command

                        match accepted.TryGetValue request.Command.CommandId with
                        | true, (priorDigest, sequence) when priorDigest = digest ->
                            Task.FromResult(PilotDuplicate sequence)
                        | true, _ -> Task.FromResult PilotConflict
                        | false, _ when request.Events.IsEmpty ->
                            Task.FromResult(PilotInvalidAppend "new-command-requires-events")
                        | false, _ ->
                            let sequence = request.Command.ExpectedSequence + int64 request.Events.Length
                            accepted.Add(request.Command.CommandId, (digest, sequence))
                            appended.Add request
                            Task.FromResult(PilotAppended sequence)
            }

        store, appended

[<Fact>]
let ``Main route stage identity is stable inside and distinct across attempt scopes`` () =
    let work = WorkItemIdentity.create "R_route" 1L "I_route" 2L
    let route = Guid.Parse "10000000-0000-4000-8000-000000000001"
    let attempt = Id.attempt (Guid.Parse "20000000-0000-4000-8000-000000000001")

    let first =
        MainRouteWorkflowIdentity.commandId work route attempt (Id.generation 1L) "reserve"

    Assert.Equal(first, MainRouteWorkflowIdentity.commandId work route attempt (Id.generation 1L) "reserve")
    Assert.NotEqual(first, MainRouteWorkflowIdentity.commandId work route attempt (Id.generation 2L) "reserve")

    Assert.NotEqual(
        first,
        MainRouteWorkflowIdentity.commandId
            work
            route
            (Id.attempt (Guid.Parse "20000000-0000-4000-8000-000000000002"))
            (Id.generation 1L)
            "reserve"
    )

    Assert.NotEqual(
        first,
        MainRouteWorkflowIdentity.commandId
            work
            (Guid.Parse "10000000-0000-4000-8000-000000000002")
            attempt
            (Id.generation 1L)
            "reserve"
    )

    Assert.NotEqual(first, MainRouteWorkflowIdentity.commandId work route attempt (Id.generation 1L) "select-route")

[<Fact>]
let ``Main route readmits only absent or terminally ended subscriptions`` () =
    Assert.True(MainRouteWorkflowPolicy.needsSubscriptionAdmission initial)

    Assert.True(
        MainRouteWorkflowPolicy.needsSubscriptionAdmission
            { initial with
                WorkItemId = Some Fixture.permit.SubjectId
                Control = ControlState.Revoked "ended"
            }
    )

    Assert.True(
        MainRouteWorkflowPolicy.needsSubscriptionAdmission
            { initial with
                WorkItemId = Some Fixture.permit.SubjectId
                Control = ControlState.Cancelled "ended"
            }
    )

    Assert.False(
        MainRouteWorkflowPolicy.needsSubscriptionAdmission
            { initial with
                WorkItemId = Some Fixture.permit.SubjectId
                Control = ControlState.Running
            }
    )

    Assert.False(
        MainRouteWorkflowPolicy.needsSubscriptionAdmission
            { initial with
                WorkItemId = Some Fixture.permit.SubjectId
                Control = ControlState.Paused "waiting"
            }
    )

[<Fact>]
let ``main admission preparation is ordered retry stable and required by workflow`` () =
    task {
        let journal = Fixture.MemoryJournal()
        let workItems = journal :> IJournalStore
        let executor = Fixture.MemoryExecutor(fun () -> journal.State)
        let commands = executor :> IExecutorCommandStore
        let attempts = executor :> IExecutionSessionJournal
        let request = Fixture.preparationRequest ()

        let input =
            Encoding.UTF8.GetBytes "Deliver the bounded O2-I4c documentation change."

        let jsonOptions =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

        jsonOptions.UnmappedMemberHandling <- System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        let requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, jsonOptions)

        let decodedRequest =
            MainAdmissionPreparer.decodeRequest requestBytes |> Result.defaultWith failwith

        let oversizedJournal = Fixture.MemoryJournal()
        let oversizedExecutor = Fixture.MemoryExecutor(fun () -> oversizedJournal.State)

        let! empty =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                oversizedJournal
                oversizedExecutor
                oversizedExecutor
                Fixture.permit.SubjectId
                "pilot-route"
                decodedRequest
                Array.empty
                CancellationToken.None

        let! oversized =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                oversizedJournal
                oversizedExecutor
                oversizedExecutor
                Fixture.permit.SubjectId
                "pilot-route"
                decodedRequest
                (Array.zeroCreate<byte>(1024 * 1024 + 1))
                CancellationToken.None

        let! uppercaseBaseline =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                oversizedJournal
                oversizedExecutor
                oversizedExecutor
                Fixture.permit.SubjectId
                "pilot-route"
                { decodedRequest with
                    BaselineObjectId = String.replicate 40 "A"
                }
                input
                CancellationToken.None

        let! traversalPath =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                oversizedJournal
                oversizedExecutor
                oversizedExecutor
                Fixture.permit.SubjectId
                "pilot-route"
                { decodedRequest with
                    AllowedPaths = [| "../x" |]
                }
                input
                CancellationToken.None

        Assert.Equal(Error "main-admission-preparation-input-size-refused", empty)
        Assert.Equal(Error "main-admission-preparation-input-size-refused", oversized)
        Assert.Equal(Error "executor-workspace-manifest-refused", uppercaseBaseline)
        Assert.Equal(Error "executor-workspace-manifest-refused", traversalPath)
        Assert.Equal(0, oversizedExecutor.Writes)
        Assert.Equal(0L, Id.generationValue oversizedJournal.State.Generation)

        let! first =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                workItems
                commands
                attempts
                Fixture.permit.SubjectId
                "pilot-route"
                decodedRequest
                input
                CancellationToken.None

        let firstBytes = first |> Result.defaultWith failwith
        Assert.True(executor.AdmissionObserved)
        Assert.Equal(1L, Id.generationValue journal.State.Generation)
        Assert.True(journal.State.Reservation.IsNone)
        Assert.Empty(journal.State.Attempts)
        Assert.Empty(journal.State.Operations)
        Assert.False(journal.State.ReadbackCurrent)

        let! retry =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                workItems
                commands
                attempts
                Fixture.permit.SubjectId
                "pilot-route"
                decodedRequest
                input
                CancellationToken.None

        let retryBytes = retry |> Result.defaultWith failwith
        Assert.True(ReadOnlySpan<byte>(firstBytes).SequenceEqual(ReadOnlySpan<byte>(retryBytes)))

        let! changed =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                workItems
                commands
                attempts
                Fixture.permit.SubjectId
                "pilot-route"
                { decodedRequest with
                    BranchRef = "refs/heads/fsgg/pilot/o2-i4c-changed"
                }
                input
                CancellationToken.None

        Assert.Equal(Error "main-admission-preparation-route-conflict", changed)

        let appendCore command =
            task {
                let! current =
                    HostedWriterJournal.recover workItems Fixture.permit.SubjectId CancellationToken.None

                let state = current |> Result.defaultWith (sprintf "%A" >> failwith) |> _.State

                let envelope =
                    {
                        CommandId = Id.command (Guid.NewGuid())
                        ProtocolVersion = Id.protocolVersion 1 0
                        ExpectedRevision = state.Revision
                        ExpectedGeneration = state.Generation
                        PrincipalId = "pilot-route"
                        SessionId = None
                        IssuedAt = Fixture.now
                        ExpiresAt = Fixture.now.AddMinutes 1.
                        Command = command
                    }

                let! result =
                    HostedWriterJournal.decideAndAppend
                        (Fixture.FixedClock())
                        workItems
                        Fixture.permit.SubjectId
                        envelope
                        CancellationToken.None

                return result |> Result.defaultWith failwith |> fst |> _.Receipt.Disposition
            }

        let terminalJournal = Fixture.MemoryJournal()
        let terminalExecutor = Fixture.MemoryExecutor(fun () -> terminalJournal.State)

        let! terminalFirst =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                terminalJournal
                terminalExecutor
                terminalExecutor
                Fixture.permit.SubjectId
                "pilot-route"
                decodedRequest
                input
                CancellationToken.None

        Assert.True(Result.isOk terminalFirst)
        let terminalWorkItems = terminalJournal :> IJournalStore

        let appendTerminal command =
            task {
                let! current =
                    HostedWriterJournal.recover terminalWorkItems Fixture.permit.SubjectId CancellationToken.None

                let state = current |> Result.defaultWith (sprintf "%A" >> failwith) |> _.State

                let envelope =
                    {
                        CommandId = Id.command (Guid.NewGuid())
                        ProtocolVersion = Id.protocolVersion 1 0
                        ExpectedRevision = state.Revision
                        ExpectedGeneration = state.Generation
                        PrincipalId = "pilot-route"
                        SessionId = None
                        IssuedAt = Fixture.now
                        ExpiresAt = Fixture.now.AddMinutes 1.
                        Command = command
                    }

                let! result =
                    HostedWriterJournal.decideAndAppend
                        (Fixture.FixedClock())
                        terminalWorkItems
                        Fixture.permit.SubjectId
                        envelope
                        CancellationToken.None

                return result |> Result.defaultWith failwith |> fst |> _.Receipt.Disposition
            }

        let! _ = appendTerminal (RequestCancel "terminal-retry")
        let! _ = appendTerminal (ConfirmCancelled "terminal-retry")
        let writesBeforeTerminalRetry = terminalExecutor.Writes

        let! terminalRetry =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                terminalWorkItems
                terminalExecutor
                terminalExecutor
                Fixture.permit.SubjectId
                "pilot-route"
                decodedRequest
                input
                CancellationToken.None

        Assert.Equal(Error "command-identity-conflict", terminalRetry)
        Assert.Equal(writesBeforeTerminalRetry, terminalExecutor.Writes)

        Assert.True(
            terminalJournal.State.Control
            |> function
                | ControlState.Cancelled _ -> true
                | _ -> false
        )

        let distinctReadmission =
            { decodedRequest with
                PreparationId = Guid.NewGuid()
                AttemptId = Guid.NewGuid()
                RouteId = Guid.NewGuid() }
        let! missingParent =
            MainAdmissionPreparer.prepare
                (Fixture.FixedClock())
                terminalWorkItems
                terminalExecutor
                terminalExecutor
                Fixture.permit.SubjectId
                "pilot-route"
                distinctReadmission
                input
                CancellationToken.None
        Assert.Equal(Error "telemetry-parent-attempt-missing", missingParent)
        Assert.Equal(writesBeforeTerminalRetry, terminalExecutor.Writes)

        let preparation =
            MainRouteAdmission.decode Fixture.permit.SubjectId "pilot-route" firstBytes
            |> Result.defaultWith failwith

        let legacyBinding = ExecutorWire.encodeRouteBinding preparation.Binding
        use legacyBindingJson = JsonDocument.Parse legacyBinding
        let mutable ignoredParent = Unchecked.defaultof<JsonElement>
        Assert.False(legacyBindingJson.RootElement.TryGetProperty("parentAttemptId", &ignoredParent))
        Assert.Equal(Ok preparation.Binding, ExecutorWire.parseRouteBinding legacyBinding)

        let parentBinding0 =
            { preparation.Binding with
                Schema = ExecutorWire.routeBindingSchemaV2
                BindingSha256 = ""
                ParentAttemptId = Nullable(Guid.NewGuid())
                ParentGeneration = Nullable(0L)
                TelemetryRelation = "child" }
        let parentBinding =
            { parentBinding0 with BindingSha256 = ExecutorWire.routeBindingDigest parentBinding0 }
        Assert.Equal(Ok parentBinding, ExecutorWire.parseRouteBinding (ExecutorWire.encodeRouteBinding parentBinding))

        let guessedJournal = Fixture.MemoryJournal()
        let guessedExecutor = Fixture.MemoryExecutor(fun () -> guessedJournal.State)

        let guessedWorkflow =
            MainRouteWorkflow(
                Fixture.FixedClock(),
                guessedJournal,
                Fixture.unusedCandidates,
                guessedExecutor,
                guessedExecutor,
                Fixture.permit.SubjectId,
                "pilot-route"
            )

        let! guessed = guessedWorkflow.Prepare(preparation, CancellationToken.None)
        Assert.Equal(Error "main-route-pre-admission-required", guessed)
        Assert.Equal(0, guessedExecutor.Writes)

        let workflow =
            MainRouteWorkflow(
                Fixture.FixedClock(),
                workItems,
                Fixture.unusedCandidates,
                commands,
                attempts,
                Fixture.permit.SubjectId,
                "pilot-route"
            )

        let! advanced = workflow.Prepare(preparation, CancellationToken.None)
        Assert.Equal(Ok(), advanced)
        Assert.Equal(Some preparation.Reservation, journal.State.Reservation)
        Assert.Equal(ControlState.Running, journal.State.Control)
        Assert.True(journal.State.ReadbackCurrent)
        Assert.True(journal.State.Operations.ContainsKey preparation.Route.ClaimOperationId)
        Assert.Empty(journal.State.Attempts)
        let runningExecutor = Fixture.MemoryExecutor(fun () -> journal.State)

        let runningWorkflow =
            MainRouteWorkflow(
                Fixture.FixedClock(),
                workItems,
                Fixture.unusedCandidates,
                runningExecutor,
                runningExecutor,
                Fixture.permit.SubjectId,
                "pilot-route"
            )

        let! running = runningWorkflow.Prepare(preparation, CancellationToken.None)
        Assert.Equal(Error "main-route-pre-admission-required", running)
        Assert.Equal(0, runningExecutor.Writes)
        let! pauseDisposition = appendCore (Command.Pause "test-current-readback")
        Assert.Equal(ReceiptDisposition.Accepted, pauseDisposition)
        let currentReadbackExecutor = Fixture.MemoryExecutor(fun () -> journal.State)

        let currentReadbackWorkflow =
            MainRouteWorkflow(
                Fixture.FixedClock(),
                workItems,
                Fixture.unusedCandidates,
                currentReadbackExecutor,
                currentReadbackExecutor,
                Fixture.permit.SubjectId,
                "pilot-route"
            )

        let! currentReadback =
            currentReadbackWorkflow.Prepare(preparation, CancellationToken.None)

        Assert.Equal(Error "main-route-pre-admission-required", currentReadback)
        Assert.Equal(0, currentReadbackExecutor.Writes)
        let root = Directory.CreateTempSubdirectory("main-admission-output-")

        try
            let output = Path.Combine(root.FullName, "admission.json")
            Assert.Equal(Ok(), MainAdmissionPreparer.writeAtomicPrivate output firstBytes)
            Assert.Equal(Ok(), MainAdmissionPreparer.writeAtomicPrivate output retryBytes)

            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(
                    output,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead
                )

                Assert.Equal(Ok(), MainAdmissionPreparer.writeAtomicPrivate output retryBytes)
                Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, File.GetUnixFileMode output)

            Assert.Equal(
                Error "main-admission-output-conflict",
                MainAdmissionPreparer.writeAtomicPrivate output (Array.append firstBytes [| 0uy |])
            )

            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, File.GetUnixFileMode output)
        finally
            root.Delete true
    }

[<Fact>]
let ``pilot permit admits only the hosted writer job class`` () =
    Assert.True(Pilot.validatePermit Fixture.permit)

    Assert.False(
        Pilot.validatePermit
            { Fixture.permit with
                JobClass = "routine-implementation"
            }
    )

[<Fact>]
let ``status is ready but remains default paused with dispatch disabled`` () =
    task {
        let store =
            {
                CheckReadiness = fun _ -> Task.FromResult(Ok())
                WorkItems = Fixture.unusedWorkItems
                Candidates = Fixture.unusedCandidates
                Recover =
                    fun _ _ ->
                        Task.FromResult(
                            Ok
                                {
                                    Events = []
                                    State =
                                        { Fixture.pilotOwned with
                                            ReadbackCurrent = false
                                        }
                                }
                        )
                Append = fun _ _ -> Task.FromResult(PilotInvalidAppend "unused")
            }

        let! status = HostRuntime.status store Fixture.permitId CancellationToken.None
        Assert.True(status.Ready)
        Assert.False(status.DispatchEnabled)
        Assert.Equal("paused", status.Mode)
        Assert.Equal(Some false, status.ReadbackCurrent)
        Assert.Contains("fresh-reconnect-readback-required", status.Findings)
    }

[<Fact>]
let ``storage readiness failure prevents pilot recovery`` () =
    task {
        let mutable recovered = false

        let store =
            {
                CheckReadiness = fun _ -> Task.FromResult(Error [ ReadOnlyStore ])
                WorkItems = Fixture.unusedWorkItems
                Candidates = Fixture.unusedCandidates
                Recover =
                    fun _ _ ->
                        recovered <- true
                        Task.FromResult(Ok { Events = []; State = Pilot.initial })
                Append = fun _ _ -> Task.FromResult(PilotInvalidAppend "unused")
            }

        let! status = HostRuntime.status store Fixture.permitId CancellationToken.None
        Assert.False(status.Ready)
        Assert.False(recovered)
        Assert.Contains("ReadOnlyStore", status.Findings)
    }

[<Fact>]
let ``operator authentication is exact bearer token comparison`` () =
    let token = String.replicate 32 "x"
    Assert.True(HostRuntime.authorize token (Some("Bearer " + token)))
    Assert.False(HostRuntime.authorize token (Some("Bearer " + token + "x")))
    Assert.False(HostRuntime.authorize token None)

[<Fact>]
let ``pause control binds permit principal sequence and stable command identity`` () =
    task {
        let store, appended = Fixture.durableStore Fixture.pilotOwned
        let commandId = Guid.Parse "62000000-0000-0000-0000-000000000002"

        let control =
            {
                Schema = "fsgg.orchestration.host-control/1"
                PermitId = Fixture.permitId
                CommandId = commandId
                ExpectedSequence = Fixture.pilotOwned.Sequence
                ExpectedGeneration = Id.generationValue Fixture.permit.Generation
                PrincipalId = Fixture.permit.PilotOwnerId
                IssuedAt = Fixture.now.AddSeconds -1.
                ExpiresAt = Fixture.now.AddMinutes 1.
                Reason = "operator-pause"
            }

        let! result =
            HostRuntime.applyControl
                (Fixture.FixedClock())
                store
                Fixture.permitId
                "pilot-route"
                control
                false
                CancellationToken.None

        Assert.Equal(Ok(Fixture.pilotOwned.Sequence + 1L), result)
        let request = Assert.Single appended
        Assert.Equal(Fixture.permitId, request.PermitId)
        Assert.Equal(commandId, request.Command.CommandId)
        Assert.Equal(Fixture.pilotOwned.Sequence, request.Command.ExpectedSequence)

        Assert.True(
            request.Command.Command
            |> function
                | Pause "operator-pause" -> true
                | _ -> false
        )

        Assert.Equal("pilot-route", request.Command.PrincipalId)
    }

[<Fact>]
let ``stale generation and sequence controls refuse without durable mutation`` () =
    task {
        let store, appended = Fixture.durableStore Fixture.pilotOwned

        let template =
            {
                Schema = "fsgg.orchestration.host-control/1"
                PermitId = Fixture.permitId
                CommandId = Guid.NewGuid()
                ExpectedSequence = Fixture.pilotOwned.Sequence
                ExpectedGeneration = Id.generationValue Fixture.permit.Generation
                PrincipalId = "pilot-route"
                IssuedAt = Fixture.now.AddSeconds -1.
                ExpiresAt = Fixture.now.AddMinutes 1.
                Reason = "pause"
            }

        let! generation =
            HostRuntime.applyControl
                (Fixture.FixedClock())
                store
                Fixture.permitId
                "pilot-route"
                { template with
                    ExpectedGeneration = 99L
                }
                false
                CancellationToken.None

        let! sequence =
            HostRuntime.applyControl
                (Fixture.FixedClock())
                store
                Fixture.permitId
                "pilot-route"
                { template with ExpectedSequence = 0L }
                false
                CancellationToken.None

        Assert.Equal(Error "control-authority-mismatch", generation)
        Assert.Equal(Error "wrong-expected-sequence", sequence)
        Assert.Empty(appended)
    }

[<Fact>]
let ``exact retry returns durable receipt and changed payload conflicts`` () =
    task {
        let store, appended = Fixture.durableStore Fixture.pilotOwned

        let request =
            {
                Schema = "fsgg.orchestration.host-control/1"
                PermitId = Fixture.permitId
                CommandId = Guid.Parse "62000000-0000-0000-0000-000000000004"
                ExpectedSequence = Fixture.pilotOwned.Sequence
                ExpectedGeneration = Id.generationValue Fixture.permit.Generation
                PrincipalId = "pilot-route"
                IssuedAt = Fixture.now.AddSeconds -1.
                ExpiresAt = Fixture.now.AddMinutes 1.
                Reason = "stable-retry"
            }

        let! accepted =
            HostRuntime.applyControl
                (Fixture.FixedClock())
                store
                Fixture.permitId
                "pilot-route"
                request
                false
                CancellationToken.None

        let! replayed =
            HostRuntime.applyControl
                (Fixture.FixedClock())
                store
                Fixture.permitId
                "pilot-route"
                request
                false
                CancellationToken.None

        let! conflict =
            HostRuntime.applyControl
                (Fixture.FixedClock())
                store
                Fixture.permitId
                "pilot-route"
                { request with Reason = "changed" }
                false
                CancellationToken.None

        Assert.Equal(Ok(Fixture.pilotOwned.Sequence + 1L), accepted)
        Assert.Equal(accepted, replayed)
        Assert.Equal(Error "command-identity-conflict", conflict)
        Assert.Single(appended) |> ignore
    }

[<Fact>]
let ``serve configuration requires private files loopback and explicit identities`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fsgg-host-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore

    try
        let connection, token, runnerToken, githubToken =
            Path.Combine(root, "connection"),
            Path.Combine(root, "token"),
            Path.Combine(root, "runner-token"),
            Path.Combine(root, "github-token")

        File.WriteAllText(connection, "Host=127.0.0.1;Database=fixture")
        File.WriteAllText(token, String.replicate 32 "t")
        File.WriteAllText(runnerToken, String.replicate 32 "r")
        File.WriteAllText(githubToken, String.replicate 32 "g")

        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(connection, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            File.SetUnixFileMode(token, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            File.SetUnixFileMode(runnerToken, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            File.SetUnixFileMode(githubToken, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        let arguments =
            [|
                "--connection-file"
                connection
                "--token-file"
                token
                "--runner-token-file"
                runnerToken
                "--prefix"
                "http://127.0.0.1:5109/"
                "--store-id"
                "main-pilot"
                "--backup-identity"
                Guid.NewGuid().ToString()
                "--minimum-generation-fence"
                "3"
                "--permit-id"
                Fixture.permitId.ToString()
                "--pilot-principal"
                "pilot-route"
                "--repository-node-id"
                "R_host"
                "--repository-database-id"
                "8"
                "--issue-node-id"
                "I_host"
                "--issue-database-id"
                "21"
            |]

        Assert.True(HostConfiguration.parseServe arguments |> Result.isOk)

        Assert.Equal(
            Error "duplicate-option",
            HostConfiguration.parseServe (Array.append arguments [| "--permit-id"; Fixture.permitId.ToString() |])
        )

        let publicArguments = arguments |> Array.copy
        publicArguments[7] <- "http://0.0.0.0:5109/"
        Assert.Equal(Error "prefix-must-be-loopback-http-root", HostConfiguration.parseServe publicArguments)
        let queryArguments = arguments |> Array.copy
        queryArguments[7] <- "http://127.0.0.1:5109/?token=forbidden"
        Assert.Equal(Error "prefix-must-be-loopback-http-root", HostConfiguration.parseServe queryArguments)
        let tokenLink = Path.Combine(root, "token-link")
        File.CreateSymbolicLink(tokenLink, token) |> ignore
        let linkArguments = arguments |> Array.copy
        linkArguments[3] <- tokenLink
        Assert.Equal(Error "secret-file-must-be-regular", HostConfiguration.parseServe linkArguments)

        let localOptions =
            [|
                "--github-token-file"
                githubToken
                "--github-repository"
                "FS-GG/.github"
                "--github-issue-number"
                "3421"
                "--github-base-ref"
                "main"
                "--runner-executable"
                "/app/runner/fsgg-coord-orchestration-runner"
                "--runner-repository-root"
                "/srv/repository"
                "--runner-workspace-root"
                "/srv/workspaces"
                "--runner-input-root"
                "/srv/inputs"
                "--runner-state-root"
                "/srv/state"
                "--runner-artifact-root"
                "/srv/artifacts"
                "--codex-executable"
                "/usr/bin/codex"
                "--executor-binding"
                "codex-main"
            |]

        let localArguments =
            arguments
            |> Array.chunkBySize 2
            |> Array.filter (fun pair -> pair[0] <> "--runner-token-file")
            |> Array.concat
            |> fun values -> Array.append values localOptions

        let parsed = HostConfiguration.parseServe localArguments
        Assert.True(Result.isOk parsed)

        Assert.Equal(
            Some "/app/runner/fsgg-coord-orchestration-runner",
            parsed
            |> Result.toOption
            |> Option.bind (fun value -> value.LocalExecutor |> Option.map _.RunnerExecutable)
        )

        Assert.Equal(None, parsed |> Result.toOption |> Option.bind _.RunnerToken)

        Assert.Equal(
            Error "runner-token-not-allowed-with-local-executor",
            HostConfiguration.parseServe (Array.append arguments localOptions)
        )

        let containerListen = localArguments |> Array.copy
        let prefixIndex = Array.findIndex ((=) "--prefix") containerListen
        containerListen[prefixIndex + 1] <- "http://*:5109/"
        Assert.True(HostConfiguration.parseServe containerListen |> Result.isOk)

        for refused in
            [
                "http://0.0.0.0:5109/"
                "http://+:5109/"
                "http://*:5110/"
                "https://*:5109/"
            ] do
            containerListen[prefixIndex + 1] <- refused
            let invalidContainerPrefix = HostConfiguration.parseServe containerListen

            Assert.True(
                (invalidContainerPrefix = Error "local-executor-prefix-must-be-loopback-or-container-listen"),
                sprintf "%s: %A" refused invalidContainerPrefix
            )

        Assert.Equal(
            Error "incomplete-local-executor-configuration",
            HostConfiguration.parseServe (localArguments[.. localArguments.Length - 3])
        )

        let relative = localArguments |> Array.copy
        let workspaceIndex = Array.findIndex ((=) "--runner-workspace-root") relative
        relative[workspaceIndex + 1] <- "relative"
        Assert.Equal(Error "local-executor-path-must-be-absolute", HostConfiguration.parseServe relative)

        let telemetryOptions =
            [|
                "--telemetry-executable"; "/usr/local/bin/fsgg-coord-engine"
                "--telemetry-config"; "/etc/fs-gg/telemetry/workspace.json"
                "--telemetry-credential-file"; "/run/secrets/telemetry-orchestration-credential"
                "--telemetry-ca-file"; "/etc/fs-gg/telemetry/ca.crt"
                "--telemetry-outbox"; "/srv/runner-state/telemetry-outbox"
                "--telemetry-binding-digest"; String.replicate 64 "a"
                "--telemetry-repository"; "FS-GG/.github"
            |]

        let withTelemetry = HostConfiguration.parseServe (Array.append localArguments telemetryOptions)
        Assert.True((withTelemetry |> Result.toOption |> Option.bind _.LocalExecutor |> Option.bind _.Telemetry).IsSome)
        Assert.Equal(
            Error "incomplete-local-telemetry-configuration",
            HostConfiguration.parseServe (Array.append localArguments telemetryOptions[.. telemetryOptions.Length - 3])
        )

        let staleDigest = telemetryOptions |> Array.copy
        staleDigest[Array.findIndex ((=) "--telemetry-binding-digest") staleDigest + 1] <- "stale"
        Assert.Equal(
            Error "local-telemetry-binding-digest-refused",
            HostConfiguration.parseServe (Array.append localArguments staleDigest)
        )

        let wrongRepository = telemetryOptions |> Array.copy
        wrongRepository[Array.findIndex ((=) "--telemetry-repository") wrongRepository + 1] <- "FS-GG/another"
        Assert.Equal(
            Error "local-telemetry-repository-binding-mismatch",
            HostConfiguration.parseServe (Array.append localArguments wrongRepository)
        )
    finally
        Directory.Delete(root, true)

let private freePrefix () =
    use socket = new TcpListener(IPAddress.Loopback, 0)
    socket.Start()
    let port = (socket.LocalEndpoint :?> IPEndPoint).Port
    socket.Stop()
    $"http://127.0.0.1:{port}/"

[<Fact>]
let ``linux wildcard prefix starts accepts loopback request and stops`` () =
    task {
        if OperatingSystem.IsLinux() then
            let loopbackPrefix, token = freePrefix (), String.replicate 32 "z"
            let port = Uri(loopbackPrefix).Port

            let local =
                {
                    RunnerExecutable = "/app/runner"
                    RepositoryRoot = "/srv/repository"
                    WorkspaceRoot = "/srv/workspaces"
                    InputRoot = "/srv/inputs"
                    StateRoot = "/srv/state"
                    ArtifactRoot = "/srv/artifacts"
                    CodexExecutable = "/usr/bin/codex"
                    ExecutorBinding = "codex-main"
                    Telemetry = None
                }

            let configuration =
                {
                    ConnectionString = "unused"
                    Token = token
                    RunnerToken = None
                    Prefix = $"http://*:{port}/"
                    StoreId = "fixture"
                    BackupIdentity = Guid.NewGuid().ToString()
                    MinimumGenerationFence = 0L
                    PermitId = Fixture.permitId
                    PilotPrincipalId = "pilot-route"
                    WorkItemId = Fixture.permit.SubjectId
                    GitHub = None
                    LocalExecutor = Some local
                    RequestTimeout = TimeSpan.FromSeconds 1.
                    MaximumConcurrentRequests = 2
                }

            let store, _ = Fixture.durableStore Fixture.pilotOwned
            use shutdown = new CancellationTokenSource()

            let server =
                HostRuntime.serve (Fixture.FixedClock()) configuration store shutdown.Token

            do! Task.Delay 50
            use client = new HttpClient()
            client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
            let! response = client.GetAsync(loopbackPrefix + "v1/status")
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            shutdown.Cancel()
            do! server.WaitAsync(TimeSpan.FromSeconds 2.)
    }

[<Fact>]
let ``http host bounds malformed and slow control requests without stopping status`` () =
    task {
        let prefix, token = freePrefix (), String.replicate 32 "z"

        let configuration =
            {
                ConnectionString = "unused"
                Token = token
                RunnerToken = Some(String.replicate 32 "r")
                Prefix = prefix
                StoreId = "fixture"
                BackupIdentity = Guid.NewGuid().ToString()
                MinimumGenerationFence = 0L
                PermitId = Fixture.permitId
                PilotPrincipalId = "pilot-route"
                WorkItemId = Fixture.permit.SubjectId
                GitHub = None
                LocalExecutor = None
                RequestTimeout = TimeSpan.FromMilliseconds 150.
                MaximumConcurrentRequests = 2
            }

        let store, _ =
            Fixture.durableStore
                { Fixture.pilotOwned with
                    ReadbackCurrent = false
                }

        use shutdown = new CancellationTokenSource()

        let server =
            HostRuntime.serve (Fixture.FixedClock()) configuration store shutdown.Token

        do! Task.Delay 50
        use client = new HttpClient()
        let! denied = client.GetAsync(prefix + "v1/status")
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode)
        use runnerBody = new StringContent("{}", Encoding.UTF8, "application/json")
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)

        let! operatorDeniedOnRunner =
            client.PostAsync(prefix + "v1/runner/assignment", runnerBody)

        Assert.Equal(HttpStatusCode.Unauthorized, operatorDeniedOnRunner.StatusCode)
        use malformedRunnerBody = new StringContent("{}", Encoding.UTF8, "application/json")

        client.DefaultRequestHeaders.Authorization <-
            Headers.AuthenticationHeaderValue("Bearer", configuration.RunnerToken.Value)

        let! malformedRunner =
            client.PostAsync(prefix + "v1/runner/assignment", malformedRunnerBody)

        Assert.Equal(HttpStatusCode.BadRequest, malformedRunner.StatusCode)
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
        let issuedAt = Fixture.now.AddSeconds(-1.).ToString("O")
        let expiresAt = Fixture.now.AddMinutes(1.).ToString("O")

        let validJson =
            $"{{\"schema\":\"fsgg.orchestration.host-control/1\",\"permitId\":\"{Fixture.permitId}\",\"commandId\":\"62000000-0000-0000-0000-000000000003\",\"expectedSequence\":{Fixture.pilotOwned.Sequence},\"expectedGeneration\":3,\"principalId\":\"pilot-route\",\"issuedAt\":\"{issuedAt}\",\"expiresAt\":\"{expiresAt}\",\"reason\":\"http-pause\"}}"

        use valid = new StringContent(validJson, Encoding.UTF8, "application/json")
        let! accepted = client.PostAsync(prefix + "v1/pause", valid)
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode)
        let! acceptedBody = accepted.Content.ReadAsStringAsync()
        use replay = new StringContent(validJson, Encoding.UTF8, "application/json")
        let! replayed = client.PostAsync(prefix + "v1/pause", replay)
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode)
        let! replayedBody = replayed.Content.ReadAsStringAsync()
        Assert.Equal(acceptedBody, replayedBody)

        use changed =
            new StringContent(validJson.Replace("http-pause", "changed-payload"), Encoding.UTF8, "application/json")

        let! conflicted = client.PostAsync(prefix + "v1/pause", changed)
        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode)

        use malformed =
            new StringContent(validJson.TrimEnd('}') + ",\"unknown\":true}", Encoding.UTF8, "application/json")

        let! malformedResponse = client.PostAsync(prefix + "v1/pause", malformed)
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode)

        use missing =
            new StringContent(validJson.Replace(",\"expectedGeneration\":3", ""), Encoding.UTF8, "application/json")

        let! missingResponse = client.PostAsync(prefix + "v1/pause", missing)
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode)

        use duplicate =
            new StringContent(
                validJson.Replace("\"reason\":", "\"reason\":\"duplicate\",\"reason\":"),
                Encoding.UTF8,
                "application/json"
            )

        let! duplicateResponse = client.PostAsync(prefix + "v1/pause", duplicate)
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode)
        use wrongMedia = new StringContent(validJson, Encoding.UTF8, "application/jsonevil")
        let! wrongMediaResponse = client.PostAsync(prefix + "v1/pause", wrongMedia)
        Assert.Equal(HttpStatusCode.BadRequest, wrongMediaResponse.StatusCode)

        use slow = new TcpClient()
        do! slow.ConnectAsync(IPAddress.Loopback, Uri(prefix).Port)

        let bytes =
            Encoding.ASCII.GetBytes(
                $"POST /v1/pause HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{{"
            )

        do! slow.GetStream().WriteAsync bytes
        do! Task.Delay 250
        let! statusResponse = client.GetAsync(prefix + "v1/status")
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode)
        shutdown.Cancel()
        do! server.WaitAsync(TimeSpan.FromSeconds 2.)
    }

[<Fact>]
let ``local child mode exposes no legacy runner HTTP route`` () =
    task {
        let prefix, token = freePrefix (), String.replicate 32 "z"

        let local =
            {
                RunnerExecutable = "/app/runner"
                RepositoryRoot = "/srv/repository"
                WorkspaceRoot = "/srv/workspaces"
                InputRoot = "/srv/inputs"
                StateRoot = "/srv/state"
                ArtifactRoot = "/srv/artifacts"
                CodexExecutable = "/usr/bin/codex"
                ExecutorBinding = "codex-main"
                Telemetry = None
            }

        let configuration =
            {
                ConnectionString = "unused"
                Token = token
                RunnerToken = None
                Prefix = prefix
                StoreId = "fixture"
                BackupIdentity = Guid.NewGuid().ToString()
                MinimumGenerationFence = 0L
                PermitId = Fixture.permitId
                PilotPrincipalId = "pilot-route"
                WorkItemId = Fixture.permit.SubjectId
                GitHub = None
                LocalExecutor = Some local
                RequestTimeout = TimeSpan.FromSeconds 1.
                MaximumConcurrentRequests = 2
            }

        let store, _ = Fixture.durableStore Fixture.pilotOwned
        use shutdown = new CancellationTokenSource()

        let server =
            HostRuntime.serve (Fixture.FixedClock()) configuration store shutdown.Token

        do! Task.Delay 50
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
        use body = new StringContent("{}", Encoding.UTF8, "application/json")
        let! response = client.PostAsync(prefix + "v1/runner/assignment", body)
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        shutdown.Cancel()
        do! server.WaitAsync(TimeSpan.FromSeconds 2.)
    }
