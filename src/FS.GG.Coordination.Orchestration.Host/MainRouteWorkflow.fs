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

/// Advances a selected route only through existing Core commands. Before every
/// append it rereads durable state, so response-loss retry observes the original
/// transition and cannot mint a replacement authority window.
[<Sealed>]
type MainRouteWorkflow(clock:TimeProvider,workItems:IJournalStore,candidates:ICandidateStore,executions:IExecutorCommandStore,executionJournal:IExecutionSessionJournal,workItemId:WorkItemId,principalId:string) =
    let commandId stage =
        let bytes=SHA256.HashData(Encoding.UTF8.GetBytes($"{WorkItemIdentity.persistenceId workItemId}:{stage}"))
        Id.command(Guid(ReadOnlySpan(bytes,0,16)))
    let append stage command token = task {
        let! recovered=HostedWriterJournal.recover workItems workItemId token
        match recovered with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok current ->
            let now=clock.GetUtcNow()
            let protocol=match command with AdmitSubscription _|RecordSubscriptionAccounting _->Id.protocolVersion 2 0|_->Id.protocolVersion 1 0
            let envelope={CommandId=commandId stage;ProtocolVersion=protocol;ExpectedRevision=current.State.Revision;ExpectedGeneration=current.State.Generation;PrincipalId=principalId;SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=command}
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
    let runIf condition stage command token = task { if condition then return! append stage command token else return Ok() }

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
             "reservation-deadline",value.ExecutionReservation.Deadline=value.Budget.ExecutionDeadline]
            |>List.choose(fun(name,valid)->if valid then None else Some name)
        let bindingFailureDetail=String.concat "," bindingFailures
        if not bindingFailures.IsEmpty then return Error($"main-route-preparation-binding-refused:{bindingFailureDetail}")
        else
            let! input=executions.StageInput(ExecutorWire.encodeInputManifest value.InputManifest,value.InputBytes,token)
            let! workspace=executions.StageWorkspaceManifest(ExecutorWire.encodeWorkspaceManifest value.WorkspaceManifest,token)
            match input,workspace with
            | Error reason,_|_,Error reason -> return Error reason
            | Ok(),Ok workspaceDigest when workspaceDigest<>value.Binding.WorkspaceManifestSha256 -> return Error "main-route-workspace-digest-refused"
            | Ok(),Ok _ ->
                let! bound=executions.BindRoute(ExecutorWire.encodeRouteBinding value.Binding,token)
                let mutable failure=match bound with Error reason->Some reason|Ok _->None
                if failure.IsNone then
                    let! intentStored=executionJournal.AppendAttempt(value.LaunchIntent.Key.AssignmentId,value.LaunchIntent.Key.AttemptId,0L,LaunchIntentRecorded value.LaunchIntent,token)
                    match intentStored with AppendConflict->failure<-Some "main-route-launch-intent-conflict"|Appended|DuplicateEvent->()
                if failure.IsNone then
                    let! reserved=executions.ReserveSubscription(SubscriptionAccountingCodec.encodeReservation value.ExecutionReservation,1,1,token)
                    match reserved with SubscriptionReserved|SubscriptionDuplicate->()|other->failure<-Some($"main-route-subscription-reservation-refused:{other}")
                if failure.IsNone then
                    let! before=state token
                    match before with
                    | Error reason -> failure<-Some reason
                    | Ok current ->
                        let! result=runIf current.WorkItemId.IsNone "admit-subscription" (AdmitSubscription(value.Snapshot,value.Budget)) token
                        match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf (current|>Result.exists _.Reservation.IsNone) "reserve" (Reserve(value.Reservation.ReservationId,value.Reservation.ExpiresAt,value.Reservation.RequiredClaimIds)) token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf (current|>Result.exists _.HostedRoute.IsNone) "select-route" (SelectHostedRoute value.Route) token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf (current|>Result.exists(fun value->value.Control=ControlState.Running && not value.ReadbackCurrent)) "prepare-pause" (Command.Pause "route-readback") token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf (current|>Result.exists(fun value->not value.ReadbackCurrent)) "route-readback" (RecordHostedRouteReadback value.Readback) token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let! current=state token
                    let! result=runIf (current|>Result.exists(fun value->value.Control<>ControlState.Running)) "resume" Command.Resume token
                    match result with Error reason->failure<-Some reason|_->()
                if failure.IsNone then
                    let claim=effect value.Route value.Binding.BindingSha256 AcquireExternalClaim value.Route.ClaimOperationId value.Route.ClaimResourceId
                    let! current=state token
                    let shouldRecord=current|>Result.exists(fun state->not(state.Operations.ContainsKey value.Route.ClaimOperationId))
                    let! result=runIf shouldRecord "intent-claim" (RecordEffectIntent claim) token
                    match result with Error reason->failure<-Some reason|_->()
                return
                    match failure with
                    | Some reason -> Error reason
                    | None -> Ok() }

    member _.Advance(value:MainRoutePreparation,intent:EffectIntent,candidateReceipt:CandidateStorageReceipt option,token:CancellationToken)=task {
        let payload=value.Binding.BindingSha256
        match intent.Kind with
        | AcquireExternalClaim ->
            let claim={ClaimId=value.Route.ClaimResourceId;Generation=value.Route.Generation;WorkflowRevision=value.Route.WorkflowRevision;ObservedAt=clock.GetUtcNow()}
            let! observed=append "observe-claim" (ObserveClaim claim) token
            match observed with
            | Error reason -> return Error reason
            | Ok() ->
                let! started=append "start-attempt" (StartAttempt(value.Route.AttemptId,value.SessionId,value.Runner)) token
                match started,nextIntent value.Route payload intent.Kind with Error reason,_->return Error reason|Ok(),Some next->return! append "intent-dispatch" (RecordEffectIntent next) token|_->return Error "main-route-transition-refused"
        | StoreCandidate ->
            let! stored=candidates.Read(value.Route.CandidateId,token)
            match stored,candidateReceipt with
            | Ok candidate,Some receipt when receipt.CandidateId=candidate.Candidate.CandidateId ->
                let! recorded=append "record-candidate" (RecordCandidate(candidate.Candidate,receipt)) token
                match recorded,nextIntent value.Route payload intent.Kind with Error reason,_->return Error reason|Ok(),Some next->return! append "intent-publish" (RecordEffectIntent next) token|_->return Error "main-route-transition-refused"
            | _ -> return Error "main-route-candidate-receipt-missing"
        | ReadNativeDelivery -> return! append "attempt-complete" (ObserveAttempt(value.Route.AttemptId,Completed)) token
        | kind ->
            match nextIntent value.Route payload kind with Some next->return! append ($"intent-{kind}") (RecordEffectIntent next) token|None->return Error "main-route-transition-refused" }

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
