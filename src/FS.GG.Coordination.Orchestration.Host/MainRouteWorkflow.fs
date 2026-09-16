namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Pilot
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Runner.Protocol

[<RequireQualifiedAccess>]
module MainRouteWorkflowIdentity =
    let commandId (workItemId:WorkItemId) (routeId:Guid) (attemptId:AttemptId) (generation:Generation) (stage:string) =
        let route=routeId.ToString("D")
        let attempt=(Id.attemptValue attemptId).ToString("D")
        let generationValue=Id.generationValue generation
        let bytes=SHA256.HashData(Encoding.UTF8.GetBytes($"{WorkItemIdentity.persistenceId workItemId}:{route}:{attempt}:{generationValue}:{stage}"))
        Id.command(Guid(ReadOnlySpan(bytes,0,16)))

[<RequireQualifiedAccess>]
module MainRouteWorkflowPolicy =
    let needsSubscriptionAdmission (state:State) =
        state.WorkItemId.IsNone
        || match state.Control with
           | ControlState.Revoked _ | ControlState.Cancelled _ -> true
           | _ -> false

/// Advances a selected route only through existing Core commands. Before every
/// append it rereads durable state, so response-loss retry observes the original
/// transition and cannot mint a replacement authority window.
[<Sealed>]
type MainRouteWorkflow(clock:TimeProvider,workItems:IJournalStore,candidates:ICandidateStore,executions:IExecutorCommandStore,executionJournal:IExecutionSessionJournal,workItemId:WorkItemId,principalId:string) =
    let legacyCommandId stage =
        let bytes=SHA256.HashData(Encoding.UTF8.GetBytes($"{WorkItemIdentity.persistenceId workItemId}:{stage}"))
        Id.command(Guid(ReadOnlySpan(bytes,0,16)))
    let commandId (value:MainRoutePreparation) stage =
        MainRouteWorkflowIdentity.commandId workItemId value.Route.RouteId value.Route.AttemptId value.Route.Generation stage
    let append value stage command token = task {
        let! recovered=HostedWriterJournal.recover workItems workItemId token
        match recovered with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok current ->
            let now=clock.GetUtcNow()
            let protocol=match command with AdmitSubscription _|RecordSubscriptionAccounting _->Id.protocolVersion 2 0|_->Id.protocolVersion 1 0
            let envelope={CommandId=commandId value stage;ProtocolVersion=protocol;ExpectedRevision=current.State.Revision;ExpectedGeneration=current.State.Generation;PrincipalId=principalId;SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=command}
            let! result=HostedWriterJournal.decideAndAppend clock workItems workItemId envelope token
            return result|>Result.bind(fun(decision,_)->match decision.Receipt.Disposition with ReceiptDisposition.Accepted|ReceiptDisposition.Duplicate->Ok()|_->Error decision.Receipt.Detail) }
    let state token = task {
        let! recovered=HostedWriterJournal.recover workItems workItemId token
        return recovered|>Result.map _.State|>Result.mapError(sprintf "%A") }
    let effect (route:HostedRoutePlan) payload kind operation resource : EffectIntent =
        {OperationId=operation;Kind=kind;Generation=route.Generation;WorkflowRevision=route.WorkflowRevision;ResourceId=resource;PayloadSha256=payload}
    let nextIntent (route:HostedRoutePlan) payload kind =
        match kind with
        | AcquireExternalClaim -> Some(effect route payload DispatchRunner route.ProcessOperationId (string(Id.attemptValue route.AttemptId)))
        | DispatchRunner -> Some(effect route payload StoreCandidate route.CandidateOperationId (string(Id.candidateValue route.CandidateId)))
        | StoreCandidate -> Some(effect route payload PublishCandidateBranch route.BranchOperationId route.BranchRef)
        | PublishCandidateBranch -> Some(effect route payload CreatePullRequest route.PullRequestOperationId route.BranchRef)
        | CreatePullRequest -> Some(effect route payload MergePullRequest route.MergeOperationId route.BranchRef)
        | MergePullRequest -> Some(effect route payload ReadNativeDelivery route.ReadbackOperationId route.BranchRef)
        | _ -> None
    let runIf value condition stage command token = task { if condition then return! append value stage command token else return Ok() }

    let validatePausedBinding (value:MainRoutePreparation) token = task {
        let! durableRoute=executions.ReadRoute(value.LaunchIntent.Key.AssignmentId,value.LaunchIntent.Key.AttemptId,token)
        let! durableAttempt=executionJournal.ReadAttempt(value.LaunchIntent.Key.AssignmentId,value.LaunchIntent.Key.AttemptId,token)
        let! durableSubscription=executions.ReadSubscription(value.ExecutionReservation.ReservationId,token)
        let! durableInput=executions.ReadInput(value.InputManifest.InputDigest,token)
        let workspaceBytes=ExecutorWire.encodeWorkspaceManifest value.WorkspaceManifest
        let workspaceDigest=SHA256.HashData workspaceBytes|>Convert.ToHexString|>fun digest->digest.ToLowerInvariant()
        let! durableWorkspace=executions.ReadWorkspaceManifest(workspaceDigest,token)
        let! durableJournal=workItems.Recover(WorkItemIdentity.persistenceId workItemId,token)
        let! recovered=HostedWriterJournal.recover workItems workItemId token
        let routeBound =
            durableRoute
            |>Result.toOption
            |>Option.bind(fun bytes->ExecutorWire.parseRouteBinding bytes|>Result.toOption)
            |>Option.exists((=) value.Binding)
        let launchBound=durableAttempt|>Option.bind(fun stored->SessionState.replay stored.Events)|>Option.exists(fun state->state.Intent=value.LaunchIntent)
        let subscriptionBound=durableSubscription|>Result.toOption|>Option.bind(fun(bytes,_)->SubscriptionAccountingCodec.decodeReservation bytes|>Result.toOption)|>Option.exists((=) value.ExecutionReservation)
        let inputBound=durableInput|>Result.toOption|>Option.exists(fun bytes->ReadOnlySpan<byte>(bytes).SequenceEqual(ReadOnlySpan<byte>(value.InputBytes)))
        let workspaceBound=durableWorkspace|>Result.toOption|>Option.exists(fun bytes->ReadOnlySpan<byte>(bytes).SequenceEqual(ReadOnlySpan<byte>(workspaceBytes)))
        match recovered with
        | Error failures->return Error(sprintf "%A" failures)
        | Ok recovery->
            let current=recovery.State
            let originalReadback =
                durableJournal
                |>Result.toOption
                |>Option.bind(fun (journal:RecoveryResult)->
                    journal.Events
                    |>List.choose(fun stored->EventEnvelope.tryDecode stored.Payload|>Result.toOption)
                    |>List.tryPick(function HostedRouteReadbackAccepted readback->Some readback|_->None))
            let attemptBound =
                current.Attempts
                |>Map.tryFind value.Route.AttemptId
                |>Option.exists(fun attempt->attempt.SessionId=value.SessionId && attempt.Runner=value.Runner)
            let failures =
                ["execution-route",routeBound;"launch-intent",launchBound;"subscription",subscriptionBound;"input",inputBound;"workspace",workspaceBound
                 "attempt",attemptBound;"paused",(current.Control|>function Paused _->true|_->false);"readback-stale",not current.ReadbackCurrent
                 "work-item",current.WorkItemId=Some workItemId;"snapshot",current.Snapshot=Some value.Snapshot;"hosted-route",current.HostedRoute=Some value.Route
                 "budget",current.SubscriptionBudget=Some value.Budget;"reservation",current.Reservation=Some value.Reservation
                 "original-readback",originalReadback=Some value.Readback;"budget-schema",value.Budget.Schema=SubscriptionPilot.budgetSchema]
                |>List.choose(fun(name,valid)->if valid then None else Some name)
            let failureDetail=String.concat "," failures
            return if failures.IsEmpty then Ok current else Error($"main-route-paused-recovery-binding-refused:{failureDetail}") }

    member _.Prepare(value:MainRoutePreparation,token:CancellationToken)=task {
        let bindingFailures =
            ["work-item",value.Route.WorkItemId=workItemId
             "route-assignment",Id.operationValue value.Route.ProcessOperationId=value.LaunchIntent.Key.AssignmentId
             "route-attempt",Id.attemptValue value.Route.AttemptId=value.LaunchIntent.Key.AttemptId
             "binding-assignment",value.Binding.AssignmentId=value.LaunchIntent.Key.AssignmentId
             "binding-attempt",value.Binding.AttemptId=value.LaunchIntent.Key.AttemptId
             "binding-route",value.Binding.RouteId=value.Route.RouteId
             "binding-candidate",value.Binding.CandidateId=Id.candidateValue value.Route.CandidateId
             "binding-generation",value.Binding.Generation=Id.generationValue value.Route.Generation
             "binding-prompt",value.Binding.PromptDigest=value.LaunchIntent.InputDigest
             "reservation-assignment",value.ExecutionReservation.AssignmentId=value.LaunchIntent.Key.AssignmentId
             "reservation-attempt",value.ExecutionReservation.AttemptId=value.LaunchIntent.Key.AttemptId
             "reservation-generation",value.ExecutionReservation.Generation=value.LaunchIntent.Key.Generation
             // The reservation is admitted against the immutable intent at
             // revision one. The actor then records the sole launch attempt at
             // revision two before any executor command becomes visible.
             "reservation-revision",value.ExecutionReservation.ExpectedRevision=1L
             "reservation-deadline",value.ExecutionReservation.Deadline=value.Budget.ExecutionDeadline
             "launch-deadline",value.LaunchIntent.Limits.Deadline<=value.Budget.ExecutionDeadline
             "launch-runtime",value.LaunchIntent.Limits.MaximumRuntime<=value.Budget.MaximumRuntime]
            |>List.choose(fun(name,valid)->if valid then None else Some name)
        let bindingFailureDetail=String.concat "," bindingFailures
        if not bindingFailures.IsEmpty then return Error($"main-route-preparation-binding-refused:{bindingFailureDetail}")
        else
            // A caller must first establish the admission boundary. The only
            // exception is the narrowly recoverable pre-PR389 branch spelling.
            let! beforePreparation=state token
            let rejectedCommandId=legacyCommandId "select-route"
            let recoverable=
                beforePreparation|>Result.exists(fun current->
                    current.HostedRoute.IsNone && current.Snapshot=Some value.Snapshot && current.SubscriptionBudget=Some value.Budget
                    && current.Generation=value.Route.Generation && (match current.Control with ControlState.Paused _->true|_->false)
                    && (current.CommandReceipts|>Map.tryFind rejectedCommandId|>Option.exists(fun receipt->receipt.Disposition=Rejected&&receipt.Detail="invalid-hosted-route")))
            let mutable preflightFailure=match beforePreparation with Ok _->None|Error reason->Some reason
            if recoverable then
                let priorRoute={value.Route with BranchRef=value.Route.BranchRef.Replace("refs/heads/fsgg/pilot/","refs/heads/fsgg/")}
                let! recoveredRoute=append value "recover-select-route" (RecoverHostedRoute(rejectedCommandId,priorRoute,value.Route)) token
                match recoveredRoute with Error reason->preflightFailure<-Some reason|Ok()->()
            let! prepared=state token
            let preAdmitted=
                preflightFailure.IsNone
                && (prepared |> Result.exists(fun (current:State) ->
                    current.Snapshot = Some value.Snapshot
                    && current.SubscriptionBudget = Some value.Budget
                    && current.Generation = value.Route.Generation
                    && current.HostedRoute = Some value.Route
                    && (match current.Control with ControlState.Paused _->true|_->false)
                    && not current.ReadbackCurrent))
            let refused=preflightFailure|>Option.defaultValue "main-route-pre-admission-required"
            let! input,workspace=
                if preAdmitted then task {
                    let! input=executions.StageInput(ExecutorWire.encodeInputManifest value.InputManifest,value.InputBytes,token)
                    let! workspace=executions.StageWorkspaceManifest(ExecutorWire.encodeWorkspaceManifest value.WorkspaceManifest,token)
                    return input,workspace }
                else Task.FromResult((Error refused,Error refused))
            match input,workspace with
            | Error reason,_|_,Error reason -> return Error reason
            | Ok(),Ok workspaceDigest when workspaceDigest<>value.Binding.WorkspaceManifestSha256 -> return Error "main-route-workspace-digest-refused"
            | Ok(),Ok _ ->
                let! bound=executions.BindRoute(ExecutorWire.encodeRouteBinding value.Binding,token)
                let mutable failure=match bound with Error reason->Some reason|Ok _->None
                if failure.IsNone then
                    let! intentStored=executionJournal.AppendAttempt(value.LaunchIntent.Key.AssignmentId,value.LaunchIntent.Key.AttemptId,0L,LaunchIntentRecorded value.LaunchIntent,token)
                    match intentStored with AppendConflict->failure<-Some "main-route-launch-intent-conflict"|Appended|DuplicateEvent->()
                let mutable subscriptionReservationAcquired=false
                if failure.IsNone then
                    let! reserved=executions.ReserveSubscription(SubscriptionAccountingCodec.encodeReservation value.ExecutionReservation,1,1,token)
                    match reserved with
                    | SubscriptionReserved->subscriptionReservationAcquired<-true
                    | SubscriptionDuplicate->()
                    | other->failure<-Some($"main-route-subscription-reservation-refused:{other}")
                if failure.IsSome && subscriptionReservationAcquired then
                    let originalFailure=failure.Value
                    try
                        use cleanup=new CancellationTokenSource(TimeSpan.FromSeconds 10.)
                        let! released=executions.ReleaseSubscription(value.ExecutionReservation.ReservationId,value.ExecutionReservation.AttemptId,value.ExecutionReservation.Generation,cleanup.Token)
                        match released with
                        | SubscriptionReleased|SubscriptionReleaseDuplicate->()
                        | SubscriptionReleaseConflict->failure<-Some($"%s{originalFailure};main-route-subscription-release-conflict")
                    with error ->
                        failure<-Some($"%s{originalFailure};main-route-subscription-release-failed:%s{error.GetType().Name}")
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf value (current|>Result.exists(fun state->not state.ReadbackCurrent)) "route-readback" (RecordHostedRouteReadback value.Readback) token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf value (current|>Result.exists(fun state->state.Control<>ControlState.Running)) "resume" Command.Resume token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf value (current|>Result.exists _.Reservation.IsNone) "reserve" (Reserve(value.Reservation.ReservationId,value.Reservation.ExpiresAt,value.Reservation.RequiredClaimIds)) token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let claim=effect value.Route value.Binding.BindingSha256 AcquireExternalClaim value.Route.ClaimOperationId value.Route.ClaimResourceId
                    let! current=state token
                    let shouldRecord=current|>Result.exists(fun state->not(state.Operations.ContainsKey value.Route.ClaimOperationId))
                    let! result=runIf value shouldRecord "intent-claim" (RecordEffectIntent claim) token
                    match result with Error reason->failure<-Some reason|_->()
                return
                    match failure with
                    | Some reason -> Error reason
                    | None -> Ok() }

    /// Reconnects an already admitted route after startup without replaying
    /// admission, reserving execution, recording an intent, or resuming effects.
    member _.ValidatePausedBinding(value:MainRoutePreparation,token:CancellationToken)=task {
        let! result=validatePausedBinding value token
        return result|>Result.map ignore }

    member _.ReconnectPaused(value:MainRoutePreparation,readback:HostedRouteReadback,token:CancellationToken)=task {
        let! validated=validatePausedBinding value token
        match validated with
        | Error reason->return Error reason
        | Ok current->
            let now=clock.GetUtcNow()
            let bound =
                now<value.Budget.DeliveryDeadline
                && readback.RouteId=value.Route.RouteId
                && readback.WorkItemId=value.Route.WorkItemId
                && readback.RepositoryNodeId=value.Route.RepositoryNodeId
                && readback.Generation=value.Route.Generation
                && readback.WorkflowRevision=value.Route.WorkflowRevision
                && readback.ObservedAt>value.Readback.ObservedAt
                && readback.ObservedAt<=now
            if not bound then return Error "main-route-paused-recovery-binding-refused"
            else return! append value $"reconnect-route-readback-{Id.revisionValue current.Revision}" (RecordHostedRouteReadback readback) token }

    member _.Advance(value:MainRoutePreparation,intent:EffectIntent,candidateReceipt:CandidateStorageReceipt option,token:CancellationToken)=task {
        let payload=value.Binding.BindingSha256
        match intent.Kind with
        | AcquireExternalClaim ->
            let claim={ClaimId=value.Route.ClaimResourceId;Generation=value.Route.Generation;WorkflowRevision=value.Route.WorkflowRevision;ObservedAt=clock.GetUtcNow()}
            let! observed=append value "observe-claim" (ObserveClaim claim) token
            match observed with
            | Error reason -> return Error reason
            | Ok() ->
                let! started=append value "start-attempt" (StartAttempt(value.Route.AttemptId,value.SessionId,value.Runner)) token
                match started,nextIntent value.Route payload intent.Kind with Error reason,_->return Error reason|Ok(),Some next->return! append value "intent-dispatch" (RecordEffectIntent next) token|_->return Error "main-route-transition-refused"
        | StoreCandidate ->
            let! stored=candidates.Read(value.Route.CandidateId,token)
            match stored,candidateReceipt with
            | Ok candidate,Some receipt when receipt.CandidateId=candidate.Candidate.CandidateId ->
                let! recorded=append value "record-candidate" (RecordCandidate(candidate.Candidate,receipt)) token
                match recorded,nextIntent value.Route payload intent.Kind with Error reason,_->return Error reason|Ok(),Some next->return! append value "intent-publish" (RecordEffectIntent next) token|_->return Error "main-route-transition-refused"
            | _ -> return Error "main-route-candidate-receipt-missing"
        | ReadNativeDelivery -> return! append value "attempt-complete" (ObserveAttempt(value.Route.AttemptId,Completed)) token
        | kind ->
            match nextIntent value.Route payload kind with Some next->return! append value ($"intent-{kind}") (RecordEffectIntent next) token|None->return Error "main-route-transition-refused" }

    /// Repairs the narrow crash window after a provider readback was durably
    /// settled but before its deterministic continuation command was appended.
    /// Candidate receipt reconstruction replays Put against Main-owned bytes;
    /// it never trusts an in-memory callback cache.
    member this.RecoverContinuation(value:MainRoutePreparation,token:CancellationToken)=task {
        let! current=state token
        match current with
        | Error reason->return Error reason
        | Ok state->
            let ordered=
                [value.Route.ClaimOperationId;value.Route.ProcessOperationId;value.Route.CandidateOperationId
                 value.Route.BranchOperationId;value.Route.PullRequestOperationId;value.Route.MergeOperationId;value.Route.ReadbackOperationId]
            let pendingNext index = index=ordered.Length-1 || not(state.Operations.ContainsKey ordered[index+1])
            let settled : EffectIntent option =
                ordered|>List.indexed|>List.tryPick(fun(index,id)->
                    match Map.tryFind id state.Operations with Some(OperationState.Settled(intent,Applied _)) when pendingNext index->Some intent|_->None)
            match settled with
            | None->return Ok()
            | Some intent when intent.Kind=StoreCandidate->
                let! candidate=candidates.Read(value.Route.CandidateId,token)
                match candidate with
                | Error reason->return Error reason
                | Ok bytes->
                    let! replay=candidates.Put(bytes,token)
                    match replay with Ok receipt|Error(Existing receipt)->return! this.Advance(value,intent,Some receipt,token)|other->return Error($"main-route-candidate-receipt-recovery-refused:{other}")
            | Some intent->return! this.Advance(value,intent,None,token) }
