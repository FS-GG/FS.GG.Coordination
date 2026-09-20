namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Pattern
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.PostgreSql

type MainHostComposition =
    {
        ExecutionProvider: IExecutionProvider
        ExecutionActor: Props
        HostedProvider: HostedWriterProviderAdapter
        EffectDriver: MainEffectDriver
    }

type RunningMainHost =
    {
        ExecutionActor: IActorRef
        EffectLoop: Task
    }

type RunningProductionMainHost =
    {
        ExecutionActor: IActorRef
        EffectLoop: Task
        Workflow: MainRouteWorkflow
        Callbacks: MainProductionCallbacks
        Transport: IAuthenticatedExecutorTransport
    }

[<RequireQualifiedAccess>]
module MainHostComposition =
    let private normalInterval = TimeSpan.FromMilliseconds 250.
    let private externalObservationBackoff = TimeSpan.FromSeconds 15.

    let private pacing results =
        if
            results
            |> Seq.exists (function
                | EffectNeedsExternalReconciliation _ -> true
                | EffectDriveRefused reason when reason.StartsWith("github-", StringComparison.Ordinal) -> true
                | _ -> false)
        then
            externalObservationBackoff
        else
            normalInterval

    let private pump
        (workItems: IJournalStore)
        (workItemId: WorkItemId)
        (driver: MainEffectDriver)
        (cancellationToken: CancellationToken)
        =
        task {
            while not cancellationToken.IsCancellationRequested do
                let! recovered = HostedWriterJournal.recover workItems workItemId cancellationToken

                match recovered with
                | Error _ -> do! Task.Delay(TimeSpan.FromSeconds 1., cancellationToken)
                | Ok current ->
                    let results = ResizeArray<MainEffectDriveResult>()

                    for intent in current.UnsettledEffects do
                        let! result = driver.Drive(intent.OperationId, cancellationToken)
                        results.Add result

                    do! Task.Delay(pacing results, cancellationToken)
        }

    /// Wires the production authority graph. Main owns both journals and the actor;
    /// the authenticated transport owns only framed executor I/O. GitHub callbacks
    /// remain a separately supplied delivery identity.
    let create
        clock
        workItems
        (executions: PostgreSqlExecutionStore)
        workItemId
        principal
        resolver
        transport
        calls
        reconcile
        =
        let provider =
            RemoteExecutorProvider(executions :> IExecutorCommandStore, resolver, transport) :> IExecutionProvider

        let coordinator =
            ExecutionSessionCoordinator(provider, executions :> IExecutionSessionJournal, clock)

        let hosted = HostedWriterProviderAdapter.Create calls

        {
            ExecutionProvider = provider
            ExecutionActor = ExecutionSessionActor.Props coordinator
            HostedProvider = hosted
            EffectDriver = MainEffectDriver(clock, workItems, workItemId, principal, hosted, reconcile)
        }

    /// Starts the actual supervised execution actor and bounded effect pump. Startup
    /// remains paused by Core state; the pump can only reconcile already-exposed
    /// effects until a separately authorized Resume command is durable.
    let start
        (system: ActorSystem)
        (workItems: IJournalStore)
        (workItemId: WorkItemId)
        (composition: MainHostComposition)
        (cancellationToken: CancellationToken)
        =
        let actor = system.ActorOf(composition.ExecutionActor, "main-execution-session")
        let loop = pump workItems workItemId composition.EffectDriver cancellationToken

        {
            ExecutionActor = actor
            EffectLoop = loop
        }

    /// Actual production graph used by Program after startup pause. Construction
    /// never calls Prepare: only the authenticated admission route may introduce
    /// a fresh route readback and resume the selected attempt.
    let startProduction
        (system: ActorSystem)
        (clock: TimeProvider)
        (workItems: IJournalStore)
        (candidates: ICandidateStore)
        (executions: PostgreSqlExecutionStore)
        workItemId
        principal
        (resolver: IExecutorBindingResolver)
        (github: GitHubRouteClient)
        (transport: IAuthenticatedExecutorTransport)
        (preparation: MainRoutePreparation)
        (cancellationToken: CancellationToken)
        (outcomeBridge: TelemetryOutcomeBridge option)
        =
        let remote =
            RemoteExecutorProvider(executions :> IExecutorCommandStore, resolver, transport)

        let coordinator =
            ExecutionSessionCoordinator(remote :> IExecutionProvider, executions :> IExecutionSessionJournal, clock)

        let actor =
            system.ActorOf(ExecutionSessionActor.Props coordinator, "main-execution-session")

        let callbacks =
            MainProductionCallbacks(clock, actor, remote, candidates, github, preparation)

        let workflow =
            MainRouteWorkflow(
                clock,
                workItems,
                candidates,
                executions :> IExecutorCommandStore,
                executions :> IExecutionSessionJournal,
                workItemId,
                principal
            )

        let advance (route: HostedRoutePlan) intent _ token =
            workflow.Advance(preparation, intent, callbacks.TryCandidateReceipt route.CandidateId, token)

        let reconcile route intent token =
            callbacks.Reconcile(route, intent, token)

        let preflight (route: HostedRoutePlan) (intent: EffectIntent) token =
            task {
                let! ownership =
                    if intent.Kind = AcquireExternalClaim then
                        Task.FromResult(Ok())
                    else
                        task {
                            let! claim =
                                github.ReadClaim(route.ClaimResourceId, Id.operationValue route.ClaimOperationId, token)

                            return claim |> Result.map ignore
                        }

                match ownership with
                | Error reason -> return Error reason
                | Ok() when intent.Kind <> MergePullRequest -> return Ok()
                | Ok() ->
                    let! candidate = candidates.Read(route.CandidateId, token)

                    match candidate with
                    | Error reason -> return Error reason
                    | Ok value ->
                        let! qualification =
                            github.CheckProtectedHead(route.BranchRef, value.Candidate.HeadSha, token)

                        return qualification |> Result.map ignore
            }

        let hosted = HostedWriterProviderAdapter.Create callbacks.Calls

        let driver =
            MainEffectDriver(clock, workItems, workItemId, principal, hosted, reconcile, advance, preflight)

        let loop =
            task {
                let mutable publishedOutcomes = Set.empty<OperationId>
                let mutable nextOutcomeCheck = DateTimeOffset.MinValue

                while not cancellationToken.IsCancellationRequested do
                    let! recovered = HostedWriterJournal.recover workItems workItemId cancellationToken

                    match recovered with
                    | Error _ -> do! Task.Delay(TimeSpan.FromSeconds 1., cancellationToken)
                    | Ok current ->
                        let results = ResizeArray<MainEffectDriveResult>()

                        for intent in current.UnsettledEffects do
                            let! result = driver.Drive(intent.OperationId, cancellationToken)
                            results.Add result

                        if clock.GetUtcNow() >= nextOutcomeCheck then
                            nextOutcomeCheck <- clock.GetUtcNow().AddSeconds 5.

                            match outcomeBridge, current.State.HostedRoute with
                            | Some bridge, Some route when not (Set.contains route.ReadbackOperationId publishedOutcomes) ->
                                match
                                    Map.tryFind route.ReadbackOperationId current.State.NativeDeliveryReadbacks,
                                    Map.tryFind route.ReadbackOperationId current.State.Operations,
                                    Map.tryFind route.AttemptId current.State.Attempts
                                with
                                | Some readback, Some(OperationState.Settled(intent, EffectOutcome.Applied revision)), Some attempt when
                                    intent.Kind = ReadNativeDelivery
                                    && revision = readback.ProviderRevision
                                    && attempt.Status = AttemptStatus.Completed
                                    ->
                                    let! result = bridge.Publish(route, readback, true, cancellationToken)

                                    if result = FS.GG.Coordination.Orchestration.Runner.Client.Applied then
                                        publishedOutcomes <- Set.add route.ReadbackOperationId publishedOutcomes
                                | _ -> ()
                            | _ -> ()

                        let! _ = workflow.RecoverContinuation(preparation, cancellationToken)
                        do! Task.Delay(pacing results, cancellationToken)
            }

        {
            ExecutionActor = actor
            EffectLoop = loop
            Workflow = workflow
            Callbacks = callbacks
            Transport = transport
        }

/// The production admission boundary shared by Program and executable tests.
/// It binds one immutable preparation to one running actor graph. Exact retries
/// recover the original workflow; conflicting bytes cannot replace authority.
[<Sealed>]
type MainProductionAdmission
    (
        system: ActorSystem,
        clock: TimeProvider,
        workItems: IJournalStore,
        candidates: ICandidateStore,
        executions: PostgreSqlExecutionStore,
        workItemId: WorkItemId,
        principal: string,
        github: GitHubRouteClient,
        transport: IAuthenticatedExecutorTransport,
        cancellationToken: CancellationToken,
        ?outcomeBridge: TelemetryOutcomeBridge,
        ?terminalEvidenceDirectory: string
    ) =
    let gate = obj ()
    let mutable running: RunningProductionMainHost option = None
    let mutable boundDigest: string option = None
    let mutable boundPreparation: MainRoutePreparation option = None
    let terminalEvidenceDirectory = defaultArg terminalEvidenceDirectory MainTerminalEvidence.defaultDirectory

    let decode bytes =
        match MainRouteAdmission.decode workItemId principal bytes with
        | Error reason -> Error reason
        | Ok preparation ->
            let admissionDigest =
                SHA256.HashData bytes
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()

            Ok(preparation, admissionDigest)

    let bind preparation admissionDigest =
        lock gate (fun () ->
            match running, boundDigest with
            | Some value, Some existing when existing = admissionDigest -> Ok(value, preparation)
            | Some _, _ -> Error "main-route-admission-binding-conflict"
            | None, _ ->
                let resolver =
                    PostgreSqlExecutorBindingResolver(
                        executions :> IExecutorCommandStore,
                        executions :> IExecutionSessionJournal,
                        preparation.LaunchIntent.Key.AssignmentId,
                        preparation.LaunchIntent.Key.AttemptId
                    )
                    :> IExecutorBindingResolver

                let value =
                    MainHostComposition.startProduction
                        system
                        clock
                        workItems
                        candidates
                        executions
                        workItemId
                        principal
                        resolver
                        github
                        transport
                        preparation
                        cancellationToken
                        outcomeBridge

                running <- Some value
                boundDigest <- Some admissionDigest
                boundPreparation <- Some preparation
                Ok(value, preparation))

    member _.Running = lock gate (fun () -> running)

    member private _.SettleAbsentCandidate (control: MainRouteControl) (token: CancellationToken) =
        task {
            let! initial = HostedWriterJournal.recover workItems workItemId token

            match initial, lock gate (fun () -> boundPreparation) with
            | Error failures, _ -> return Error(sprintf "%A" failures)
            | _, None -> return Error "absent-candidate-route-not-bound"
            | Ok current, Some preparation ->
                let route = preparation.Route
                let now = clock.GetUtcNow()

                let operation = Map.tryFind route.CandidateOperationId current.State.Operations

                let controlAuthorization =
                    MainAbsentCandidateControl.authorize control route current.State now

                let candidatePending =
                    match operation with
                    | Some(NeedsObservation(intent, _)) when intent.Kind = StoreCandidate -> true
                    | Some(OperationState.Settled(intent, ProvenAbsent)) when intent.Kind = StoreCandidate -> true
                    | _ -> false

                let attemptRecoverable =
                    match Map.tryFind route.AttemptId current.State.Attempts with
                    | Some attempt ->
                        match attempt.Status with
                        | AttemptStatus.Active
                        | AttemptStatus.OutcomeUnknown _
                        | AttemptStatus.ReconciledAbsent "candidate-deliverable-absent:candidate-touch-set-refused" -> true
                        | _ -> false
                    | None -> false

                let noLaterEffects =
                    [ route.BranchOperationId; route.PullRequestOperationId; route.MergeOperationId; route.ReadbackOperationId ]
                    |> List.forall (fun id -> not (current.State.Operations.ContainsKey id))

                let generationCurrent =
                    current.State.Generation = route.Generation
                    || (match current.State.Control with
                        | Revoked _ -> Id.generationValue current.State.Generation = Id.generationValue route.Generation + 1L
                        | _ -> false)

                if
                    Result.isError controlAuthorization
                    || not generationCurrent
                    || current.State.HostedRoute <> Some route
                    || now < preparation.Budget.DeliveryDeadline
                    || control.Reason <> "candidate-touch-set-refused"
                    || not candidatePending
                    || not attemptRecoverable
                    || not noLaterEffects
                    || (match current.State.Control with
                        | Paused _ | Revoked _ -> false
                        | _ -> true)
                then
                    return Error "absent-candidate-state-or-authority-refused"
                else
                    let! candidate = candidates.Read(route.CandidateId, token)
                    let! liveRoute = github.ReadHostedRoute(route, token)
                    let! branch = github.ReadBranchAbsent(route.BranchRef, token)
                    let! pull = github.ReadPullRequestAbsent(route.BranchRef, token)
                    let! claim = github.ReadClaimAbsent(route.ClaimResourceId, Id.operationValue route.ClaimOperationId, token)
                    let terminalEvidence =
                        MainTerminalEvidence.verify terminalEvidenceDirectory preparation.LaunchIntent (clock.GetUtcNow())
                    let! execution =
                        (executions :> IExecutionSessionJournal)
                            .ReadAttempt(
                                preparation.LaunchIntent.Key.AssignmentId,
                                preparation.LaunchIntent.Key.AttemptId,
                                token
                            )

                    let executionCandidateAbsent =
                        execution
                        |> Option.bind (fun stored -> SessionState.replay stored.Events)
                        |> Option.exists (fun state ->
                            state.CancelWasRequested
                            && (state.Observation
                                |> Option.exists (fun value ->
                                    value.Lifecycle = SessionLifecycle.OutcomeUnknown && value.Candidate.IsNone)))

                    match candidate, liveRoute, branch, pull, claim, executionCandidateAbsent, terminalEvidence with
                    | Error "candidate-not-found", Ok _, Ok(), Ok(), Ok(), true, Ok _ ->
                        let stageId stage =
                            let bytes =
                                SHA256.HashData(
                                    Text.Encoding.UTF8.GetBytes($"{control.CommandId:D}:absent-candidate:{stage}")
                                )

                            Id.command (Guid(ReadOnlySpan(bytes, 0, 16)))

                        let append stage command =
                            task {
                                let! latest = HostedWriterJournal.recover workItems workItemId token

                                match latest with
                                | Error failures -> return Error(sprintf "%A" failures)
                                | Ok value ->
                                    let envelope =
                                        {
                                            CommandId = stageId stage
                                            ProtocolVersion = Id.protocolVersion 1 0
                                            ExpectedRevision = value.State.Revision
                                            ExpectedGeneration = value.State.Generation
                                            PrincipalId = principal
                                            SessionId = None
                                            IssuedAt = clock.GetUtcNow()
                                            ExpiresAt = clock.GetUtcNow().AddMinutes 1.
                                            Command = command
                                        }

                                    let! result =
                                        HostedWriterJournal.decideAndAppend clock workItems workItemId envelope token

                                    return
                                        result
                                        |> Result.bind (fun (decision, _) ->
                                            match decision.Receipt.Disposition with
                                            | ReceiptDisposition.Accepted
                                            | ReceiptDisposition.Duplicate -> Ok()
                                            | _ -> Error decision.Receipt.Detail)
                            }

                        let! settled =
                            match operation with
                            | Some(OperationState.Settled(_, ProvenAbsent)) -> Task.FromResult(Ok())
                            | Some(NeedsObservation(intent, _)) ->
                                let observedAt = clock.GetUtcNow()
                                let readback =
                                    {
                                        OperationId = intent.OperationId
                                        RouteId = route.RouteId
                                        AttemptId = route.AttemptId
                                        CandidateId = route.CandidateId
                                        RepositoryNodeId = route.RepositoryNodeId
                                        ProviderResourceId = intent.ResourceId
                                        CandidateHeadSha = None
                                        ResultSha = None
                                        ProviderRevision = Result.defaultValue "" controlAuthorization
                                        Generation = route.Generation
                                        WorkflowRevision = route.WorkflowRevision
                                        ObservedAt = observedAt
                                        Exists = false
                                    }

                                if observedAt >= control.ExpiresAt then
                                    Task.FromResult(Error "absent-candidate-control-expired-before-first-write")
                                else
                                    append "candidate-absent" (RecordHostedEffectReadback(intent.OperationId, readback))
                            | _ -> Task.FromResult(Error "absent-candidate-operation-refused")

                        match settled with
                        | Error reason -> return Error reason
                        | Ok() ->
                            let! afterCandidate = HostedWriterJournal.recover workItems workItemId token

                            match afterCandidate with
                            | Error failures -> return Error(sprintf "%A" failures)
                            | Ok state ->
                                let attempt = Map.tryFind route.AttemptId state.State.Attempts

                                let! terminal =
                                    match attempt with
                                    | Some value when value.Status = ReconciledAbsent "candidate-deliverable-absent:candidate-touch-set-refused" ->
                                        Task.FromResult(Ok())
                                    | Some _ ->
                                        append
                                            "attempt-absent"
                                            (ObserveAttempt(
                                                route.AttemptId,
                                                ReconciledAbsent "candidate-deliverable-absent:candidate-touch-set-refused"
                                            ))
                                    | None -> Task.FromResult(Error "absent-candidate-attempt-missing")

                                match terminal with
                                | Error reason -> return Error reason
                                | Ok() ->
                                    let! released =
                                        (executions :> IExecutorCommandStore)
                                            .ReleaseSubscription(
                                                preparation.ExecutionReservation.ReservationId,
                                                preparation.ExecutionReservation.AttemptId,
                                                preparation.ExecutionReservation.Generation,
                                                token
                                            )

                                    match released with
                                    | SubscriptionReleased
                                    | SubscriptionReleaseDuplicate ->
                                        let! beforeRevoke = HostedWriterJournal.recover workItems workItemId token

                                        match beforeRevoke with
                                        | Error failures -> return Error(sprintf "%A" failures)
                                        | Ok state ->
                                            let! revoked =
                                                match state.State.Control with
                                                | Revoked _ -> Task.FromResult(Ok())
                                                | Paused _ -> append "revoke" (Revoke "candidate-deliverable-absent")
                                                | _ -> Task.FromResult(Error "absent-candidate-control-changed")

                                            match revoked with
                                            | Error reason -> return Error reason
                                            | Ok() ->
                                                let! beforeClaim = HostedWriterJournal.recover workItems workItemId token

                                                match beforeClaim with
                                                | Error failures -> return Error(sprintf "%A" failures)
                                                | Ok state ->
                                                    let! cleared =
                                                        if state.State.ExternalClaims.ContainsKey route.ClaimResourceId then
                                                            append "claim-released" (ObserveClaimReleased route.ClaimResourceId)
                                                        else
                                                            Task.FromResult(Ok())

                                                    match cleared with
                                                    | Error reason -> return Error reason
                                                    | Ok() ->
                                                        let! finalState = HostedWriterJournal.recover workItems workItemId token

                                                        match finalState with
                                                        | Error failures -> return Error(sprintf "%A" failures)
                                                        | Ok value when
                                                            value.State.Reservation.IsNone
                                                            && value.State.ExternalClaims.IsEmpty
                                                            && value.State.RecoveryObligations.IsEmpty
                                                            && value.State.CompensationFailures.IsEmpty
                                                            ->
                                                            return
                                                                Ok
                                                                    {
                                                                        Sequence = Id.revisionValue value.State.Revision
                                                                        Action = control.Action
                                                                        RequestPersisted = true
                                                                        ProcessTerminationObserved = None
                                                                        Detail = "candidate-deliverable-absent-settled"
                                                                    }
                                                        | Ok _ -> return Error "absent-candidate-compensation-incomplete"
                                    | _ -> return Error "absent-candidate-execution-reservation-release-refused"
                    | Ok _, _, _, _, _, _, _ -> return Error "absent-candidate-store-still-present"
                    | Error reason, _, _, _, _, _, _ when reason <> "candidate-not-found" -> return Error reason
                    | _, Error reason, _, _, _, _, _
                    | _, _, Error reason, _, _, _, _
                    | _, _, _, Error reason, _, _, _
                    | _, _, _, _, Error reason, _, _ -> return Error reason
                    | _, _, _, _, _, false, _ -> return Error "absent-candidate-execution-not-proven"
                    | _, _, _, _, _, _, Error reason -> return Error reason
                    | _ -> return Error "absent-candidate-readback-refused"
        }

    interface IMainRouteAdmissionHandler with
        member _.Admit(bytes, token) =
            task {
                match decode bytes with
                | Error reason -> return Error reason
                | Ok(preparation, digest) ->
                    match bind preparation digest with
                    | Error reason -> return Error reason
                    | Ok(value, _) -> return! value.Workflow.Prepare(preparation, token)
            }

        member _.RecoverPaused(bytes, token) =
            task {
                match decode bytes with
                | Error reason -> return Error reason
                | Ok(preparation, digest) ->
                    // Validate every immutable preparation field against the durable
                    // Core and execution journals before starting or caching a graph.
                    // A canonical but altered recovery document therefore cannot
                    // poison a later retry of the exact original bytes.
                    let validator =
                        MainRouteWorkflow(
                            clock,
                            workItems,
                            candidates,
                            executions :> IExecutorCommandStore,
                            executions :> IExecutionSessionJournal,
                            workItemId,
                            principal
                        )

                    let! validated = validator.ValidatePausedBinding(preparation, token)

                    match validated with
                    | Error reason -> return Error reason
                    | Ok() ->
                        let! readback = github.ReadHostedRoute(preparation.Route, token)

                        let! claim =
                            github.ReadClaim(
                                preparation.Route.ClaimResourceId,
                                Id.operationValue preparation.Route.ClaimOperationId,
                                token
                            )

                        match readback, claim with
                        | Error reason, _ -> return Error reason
                        | Ok fresh, claimReadback ->
                            match bind preparation digest with
                            | Error reason -> return Error reason
                            | Ok(value, _) ->
                                // A lost claim or expired delivery clock leaves the
                                // exact graph paused and observation-only. Its pump may
                                // reconcile an already exposed PR, but status cannot
                                // become dispatch-enabled and Resume remains refused.
                                match claimReadback with
                                | Error _ -> return Ok()
                                | Ok _ when clock.GetUtcNow() >= preparation.Budget.DeliveryDeadline -> return Ok()
                                | Ok _ -> return! value.Workflow.ReconnectPaused(preparation, fresh, token)
            }

        member _.Status(token) =
            task {
                let! recovered = HostedWriterJournal.recover workItems workItemId token

                match recovered with
                | Error failures -> return Error(sprintf "%A" failures)
                | Ok value ->
                    let admitted, preparation =
                        lock gate (fun () -> boundDigest.IsSome, boundPreparation)

                    let now = clock.GetUtcNow()

                    let mode =
                        match value.State.Control with
                        | ControlState.Running -> "running"
                        | ControlState.Paused _ -> "paused"
                        | ControlState.CancelPending _ -> "cancel-pending"
                        | ControlState.Cancelled _ -> "cancelled"
                        | ControlState.Revoked _ -> "revoked"

                    let unknown =
                        value.State.Operations
                        |> Map.values
                        |> Seq.filter (function
                            | NeedsObservation _ -> true
                            | _ -> false)
                        |> Seq.length

                    let executionRequired =
                        value.State.HostedRoute
                        |> Option.forall (fun route ->
                            value.State.HostedEffectReadbacks
                            |> Map.tryFind route.ProcessOperationId
                            |> Option.exists _.Exists
                            |> not)

                    let findings = ResizeArray<string>()

                    if not admitted then
                        findings.Add "main-route-admission-required"

                    if value.State.Control <> ControlState.Running then
                        findings.Add "main-route-control-not-running"

                    if not value.State.ReadbackCurrent then
                        findings.Add "main-route-readback-not-current"

                    match value.State.HostedRoute with
                    | None -> findings.Add "main-route-not-selected"
                    | Some route when route.Generation <> value.State.Generation ->
                        findings.Add "main-route-generation-stale"
                    | Some route ->
                        match Map.tryFind route.AttemptId value.State.Attempts with
                        | Some {
                                   Status = AttemptStatus.Completed | AttemptStatus.CancelledByRunner | AttemptStatus.ReconciledAbsent _
                               } -> findings.Add "main-route-attempt-terminal"
                        | Some {
                                   Status = AttemptStatus.OutcomeUnknown _
                               } -> findings.Add "main-route-attempt-observation-required"
                        | _ -> ()

                    match value.State.SubscriptionBudget with
                    | Some budget when
                        budget.Schema = FS.GG.Coordination.Orchestration.Pilot.SubscriptionPilot.budgetSchema
                        && budget.AttemptLimit = 1
                        && budget.MaximumRuntime > TimeSpan.Zero
                        && budget.MaximumRuntime <= TimeSpan.FromMinutes 30.
                        && budget.DeliveryDeadline > now
                        ->
                        ()
                    | _ -> findings.Add "main-route-budget-not-current"

                    match value.State.SubscriptionBudget with
                    | Some budget when not executionRequired || budget.ExecutionDeadline > now -> ()
                    | _ -> findings.Add "main-route-execution-budget-not-current"

                    match value.State.Reservation with
                    | Some reservation when
                        reservation.Generation = value.State.Generation
                        && (not executionRequired || reservation.ExpiresAt > now)
                        ->
                        ()
                    | _ -> findings.Add "main-route-reservation-not-current"

                    match preparation with
                    | Some prep when
                        prep.Runner.Generation = value.State.Generation
                        && prep.ExecutionReservation.Generation = Id.generationValue value.State.Generation
                        && (not executionRequired
                            || (prep.Runner.ExpiresAt > now && prep.ExecutionReservation.Deadline > now))
                        ->
                        ()
                    | _ -> findings.Add "main-route-executor-authority-not-current"

                    if unknown > 0 then
                        findings.Add "main-route-observation-required"

                    let dispatch = findings.Count = 0

                    return
                        Ok
                            {
                                Admitted = admitted
                                Ready = dispatch
                                DispatchEnabled = dispatch
                                Mode = mode
                                Sequence = Id.revisionValue value.State.Revision
                                Generation = Id.generationValue value.State.Generation
                                UnknownOperations = unknown
                                Findings = List.ofSeq findings
                            }
            }

        member this.Control(control, token) =
            task {
                if
                    control.CommandId = Guid.Empty
                    || control.ExpectedSequence < 0L
                    || control.ExpectedGeneration < 0L
                    || control.PrincipalId <> principal
                    || String.IsNullOrWhiteSpace control.Reason
                    || control.Reason <> control.Reason.Trim()
                    || control.IssuedAt = DateTimeOffset.MinValue
                    || control.ExpiresAt <= control.IssuedAt
                    || (control.Action = "settle-absent-candidate"
                        && control.ExpiresAt - control.IssuedAt > TimeSpan.FromMinutes 5.)
                then
                    return Error "invalid-main-control-request"
                elif control.Action = "settle-absent-candidate" then
                    return! this.SettleAbsentCandidate control token
                else
                    let command =
                        match control.Action with
                        | "pause" -> Some(Pause control.Reason)
                        | "resume" -> Some Resume
                        | "revoke" -> Some(Revoke control.Reason)
                        | "cancel" -> Some(RequestCancel control.Reason)
                        | _ -> None

                    match command with
                    | None -> return Error "invalid-main-control-action"
                    | Some command ->
                        let envelope =
                            {
                                CommandId = Id.command control.CommandId
                                ProtocolVersion = Id.protocolVersion 1 0
                                ExpectedRevision = Id.revision control.ExpectedSequence
                                ExpectedGeneration = Id.generation control.ExpectedGeneration
                                PrincipalId = control.PrincipalId
                                SessionId = None
                                IssuedAt = control.IssuedAt
                                ExpiresAt = control.ExpiresAt
                                Command = command
                            }

                        let! appended =
                            HostedWriterJournal.decideAndAppend clock workItems workItemId envelope token

                        match appended with
                        | Error reason -> return Error reason
                        | Ok(decision, sequence) ->
                            match decision.Receipt.Disposition with
                            | ReceiptDisposition.Accepted
                            | ReceiptDisposition.Duplicate when control.Action = "cancel" ->
                                let selected = lock gate (fun () -> running, boundPreparation)

                                match selected with
                                | Some host, Some preparation ->
                                    try
                                        let! result =
                                            host.ExecutionActor.Ask<CoordinationResult>(
                                                box (Cancel preparation.LaunchIntent.Key),
                                                TimeSpan.FromSeconds 30.,
                                                token
                                            )

                                        let detail =
                                            match result with
                                            | SessionAdvanced _
                                            | SessionDuplicate _ -> "execution-cancel-reconciled"
                                            | SessionNeedsReconciliation reason
                                            | SessionRefused reason -> reason

                                        let! durable =
                                            (executions :> IExecutionSessionJournal)
                                                .ReadAttempt(
                                                    preparation.LaunchIntent.Key.AssignmentId,
                                                    preparation.LaunchIntent.Key.AttemptId,
                                                    token
                                                )

                                        let observed =
                                            durable
                                            |> Option.bind (fun stored -> SessionState.replay stored.Events)
                                            |> Option.bind _.Observation
                                            |> Option.map (fun value ->
                                                match value.Lifecycle with
                                                | Cancelled
                                                | Succeeded
                                                | Failed
                                                | DeadlineExceeded -> true
                                                | _ -> false)
                                            |> Option.defaultValue false

                                        return
                                            Ok
                                                {
                                                    Sequence = sequence
                                                    Action = control.Action
                                                    RequestPersisted = true
                                                    ProcessTerminationObserved = Some observed
                                                    Detail = detail
                                                }
                                    with :? OperationCanceledException ->
                                        return
                                            Ok
                                                {
                                                    Sequence = sequence
                                                    Action = control.Action
                                                    RequestPersisted = true
                                                    ProcessTerminationObserved = Some false
                                                    Detail = "execution-cancel-observation-timeout"
                                                }
                                | _ ->
                                    return
                                        Ok
                                            {
                                                Sequence = sequence
                                                Action = control.Action
                                                RequestPersisted = true
                                                ProcessTerminationObserved = Some false
                                                Detail = "execution-cancel-binding-unavailable"
                                            }
                            | ReceiptDisposition.Accepted
                            | ReceiptDisposition.Duplicate when control.Action = "revoke" ->
                                match lock gate (fun () -> boundPreparation) with
                                | None ->
                                    return
                                        Ok
                                            {
                                                Sequence = sequence
                                                Action = control.Action
                                                RequestPersisted = true
                                                ProcessTerminationObserved = None
                                                Detail = decision.Receipt.Detail
                                            }
                                | Some preparation ->
                                    let reservation = preparation.ExecutionReservation

                                    let! released =
                                        (executions :> IExecutorCommandStore)
                                            .ReleaseSubscription(
                                                reservation.ReservationId,
                                                reservation.AttemptId,
                                                reservation.Generation,
                                                token
                                            )

                                    match released with
                                    | SubscriptionReleased
                                    | SubscriptionReleaseDuplicate ->
                                        return
                                            Ok
                                                {
                                                    Sequence = sequence
                                                    Action = control.Action
                                                    RequestPersisted = true
                                                    ProcessTerminationObserved = None
                                                    Detail = decision.Receipt.Detail
                                                }
                                    | SubscriptionReleaseConflict -> return Error "subscription-release-conflict"
                            | ReceiptDisposition.Accepted
                            | ReceiptDisposition.Duplicate ->
                                return
                                    Ok
                                        {
                                            Sequence = sequence
                                            Action = control.Action
                                            RequestPersisted = true
                                            ProcessTerminationObserved = None
                                            Detail = decision.Receipt.Detail
                                        }
                            | _ -> return Error decision.Receipt.Detail
            }
