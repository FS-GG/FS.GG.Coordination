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
    { ExecutionProvider:IExecutionProvider
      ExecutionActor:Props
      HostedProvider:HostedWriterProviderAdapter
      EffectDriver:MainEffectDriver }

type RunningMainHost = { ExecutionActor:IActorRef; EffectLoop:Task }
type RunningProductionMainHost =
    { ExecutionActor:IActorRef; EffectLoop:Task; Workflow:MainRouteWorkflow
      Callbacks:MainProductionCallbacks; Relay:HostExecutorRelay }

[<RequireQualifiedAccess>]
module MainHostComposition =
    let private normalInterval=TimeSpan.FromMilliseconds 250.
    let private externalObservationBackoff=TimeSpan.FromSeconds 15.

    let private pacing results =
        if results|>Seq.exists(function
            | EffectNeedsExternalReconciliation _ -> true
            | EffectDriveRefused reason when reason.StartsWith("github-",StringComparison.Ordinal) -> true
            | _ -> false)
        then externalObservationBackoff
        else normalInterval

    let private pump (workItems:IJournalStore) (workItemId:WorkItemId) (driver:MainEffectDriver) (cancellationToken:CancellationToken) = task {
        while not cancellationToken.IsCancellationRequested do
            let! recovered=HostedWriterJournal.recover workItems workItemId cancellationToken
            match recovered with
            | Error _ -> do! Task.Delay(TimeSpan.FromSeconds 1.,cancellationToken)
            | Ok current ->
                let results=ResizeArray<MainEffectDriveResult>()
                for intent in current.UnsettledEffects do
                    let! result=driver.Drive(intent.OperationId,cancellationToken)
                    results.Add result
                do! Task.Delay(pacing results,cancellationToken) }
    /// Wires the production authority graph. Main owns both journals and the actor;
    /// the authenticated transport owns only framed executor I/O. GitHub callbacks
    /// remain a separately supplied delivery identity.
    let create clock workItems (executions:PostgreSqlExecutionStore) workItemId principal resolver transport calls reconcile =
        let provider=RemoteExecutorProvider(executions :> IExecutorCommandStore,resolver,transport) :> IExecutionProvider
        let coordinator=ExecutionSessionCoordinator(provider,executions :> IExecutionSessionJournal,clock)
        let hosted=HostedWriterProviderAdapter.Create calls
        { ExecutionProvider=provider
          ExecutionActor=ExecutionSessionActor.Props coordinator
          HostedProvider=hosted
          EffectDriver=MainEffectDriver(clock,workItems,workItemId,principal,hosted,reconcile) }

    /// Starts the actual supervised execution actor and bounded effect pump. Startup
    /// remains paused by Core state; the pump can only reconcile already-exposed
    /// effects until a separately authorized Resume command is durable.
    let start (system:ActorSystem) (workItems:IJournalStore) (workItemId:WorkItemId) (composition:MainHostComposition) (cancellationToken:CancellationToken) =
        let actor=system.ActorOf(composition.ExecutionActor,"main-execution-session")
        let loop=pump workItems workItemId composition.EffectDriver cancellationToken
        {ExecutionActor=actor;EffectLoop=loop}

    /// Actual production graph used by Program after startup pause. Construction
    /// never calls Prepare: only the authenticated admission route may introduce
    /// a fresh route readback and resume the selected attempt.
    let startProduction (system:ActorSystem) (clock:TimeProvider) (workItems:IJournalStore) (candidates:ICandidateStore) (executions:PostgreSqlExecutionStore)
                        workItemId principal (resolver:IExecutorBindingResolver) (github:GitHubRouteClient)
                        (relay:HostExecutorRelay) (preparation:MainRoutePreparation) (cancellationToken:CancellationToken) =
        let remote=RemoteExecutorProvider(executions :> IExecutorCommandStore,resolver,relay :> IAuthenticatedExecutorTransport)
        let coordinator=ExecutionSessionCoordinator(remote :> IExecutionProvider,executions :> IExecutionSessionJournal,clock)
        let actor=system.ActorOf(ExecutionSessionActor.Props coordinator,"main-execution-session")
        let callbacks=MainProductionCallbacks(clock,actor,remote,candidates,github,preparation)
        let workflow=MainRouteWorkflow(clock,workItems,candidates,executions :> IExecutorCommandStore,executions :> IExecutionSessionJournal,workItemId,principal)
        let advance (route:HostedRoutePlan) intent _ token=workflow.Advance(preparation,intent,callbacks.TryCandidateReceipt route.CandidateId,token)
        let reconcile route intent token=callbacks.Reconcile(route,intent,token)
        let preflight (route:HostedRoutePlan) (intent:EffectIntent) token=task {
            if intent.Kind<>MergePullRequest then return Ok() else
            let! candidate=candidates.Read(route.CandidateId,token)
            match candidate with
            | Error reason->return Error reason
            | Ok value->
                let! qualification=github.CheckProtectedHead(route.BranchRef,value.Candidate.HeadSha,token)
                return qualification|>Result.map ignore }
        let hosted=HostedWriterProviderAdapter.Create callbacks.Calls
        let driver=MainEffectDriver(clock,workItems,workItemId,principal,hosted,reconcile,advance,preflight)
        let loop=task {
            while not cancellationToken.IsCancellationRequested do
                let! recovered=HostedWriterJournal.recover workItems workItemId cancellationToken
                match recovered with
                | Error _->do! Task.Delay(TimeSpan.FromSeconds 1.,cancellationToken)
                | Ok current->
                    let results=ResizeArray<MainEffectDriveResult>()
                    for intent in current.UnsettledEffects do
                        let! result=driver.Drive(intent.OperationId,cancellationToken)
                        results.Add result
                    let! _=workflow.RecoverContinuation(preparation,cancellationToken)
                    do! Task.Delay(pacing results,cancellationToken) }
        {ExecutionActor=actor;EffectLoop=loop;Workflow=workflow;Callbacks=callbacks;Relay=relay}

/// The production admission boundary shared by Program and executable tests.
/// It binds one immutable preparation to one running actor graph. Exact retries
/// recover the original workflow; conflicting bytes cannot replace authority.
[<Sealed>]
type MainProductionAdmission
    (system:ActorSystem,clock:TimeProvider,workItems:IJournalStore,candidates:ICandidateStore,
     executions:PostgreSqlExecutionStore,workItemId:WorkItemId,principal:string,
     github:GitHubRouteClient,relay:HostExecutorRelay,cancellationToken:CancellationToken) =
    let gate=obj()
    let mutable running:RunningProductionMainHost option=None
    let mutable boundDigest:string option=None
    let mutable boundPreparation:MainRoutePreparation option=None
    member _.Running = lock gate (fun ()->running)
    interface IMainRouteAdmissionHandler with
        member _.Admit(bytes,token)=task {
            match MainRouteAdmission.decode workItemId principal bytes with
            | Error reason->return Error reason
            | Ok preparation->
                let admissionDigest=SHA256.HashData bytes|>Convert.ToHexString|>fun value->value.ToLowerInvariant()
                let selected =
                    lock gate (fun ()->
                        match running,boundDigest with
                        | Some value,Some existing when existing=admissionDigest->Ok value
                        | Some _,_->Error "main-route-admission-binding-conflict"
                        | None,_->
                            let resolver=
                                PostgreSqlExecutorBindingResolver(
                                    executions :> IExecutorCommandStore,
                                    executions :> IExecutionSessionJournal,
                                    preparation.LaunchIntent.Key.AssignmentId,
                                    preparation.LaunchIntent.Key.AttemptId) :> IExecutorBindingResolver
                            let value=
                                MainHostComposition.startProduction system clock workItems candidates executions
                                    workItemId principal resolver github relay preparation cancellationToken
                            running<-Some value
                            boundDigest<-Some admissionDigest
                            boundPreparation<-Some preparation
                            Ok value)
                match selected with
                | Error reason->return Error reason
                | Ok value->return! value.Workflow.Prepare(preparation,token) }
        member _.Status(token)=task {
            let! recovered=HostedWriterJournal.recover workItems workItemId token
            match recovered with
            | Error failures->return Error(sprintf "%A" failures)
            | Ok value->
                let admitted,preparation=lock gate (fun()->boundDigest.IsSome,boundPreparation)
                let now=clock.GetUtcNow()
                let mode=
                    match value.State.Control with
                    | ControlState.Running->"running"|ControlState.Paused _->"paused"|ControlState.CancelPending _->"cancel-pending"
                    | ControlState.Cancelled _->"cancelled"|ControlState.Revoked _->"revoked"
                let unknown=value.State.Operations|>Map.values|>Seq.filter(function NeedsObservation _->true|_->false)|>Seq.length
                let findings=ResizeArray<string>()
                if not admitted then findings.Add "main-route-admission-required"
                if value.State.Control<>ControlState.Running then findings.Add "main-route-control-not-running"
                if not value.State.ReadbackCurrent then findings.Add "main-route-readback-not-current"
                match value.State.HostedRoute with
                | None->findings.Add "main-route-not-selected"
                | Some route when route.Generation<>value.State.Generation->findings.Add "main-route-generation-stale"
                | Some route->
                    match Map.tryFind route.AttemptId value.State.Attempts with
                    | Some {Status=AttemptStatus.Completed|AttemptStatus.CancelledByRunner|AttemptStatus.ReconciledAbsent _}->findings.Add "main-route-attempt-terminal"
                    | Some {Status=AttemptStatus.OutcomeUnknown _}->findings.Add "main-route-attempt-observation-required"
                    | _->()
                match value.State.SubscriptionBudget with
                | Some budget when budget.AttemptLimit=1 && budget.MaximumRuntime>TimeSpan.Zero && budget.MaximumRuntime<=TimeSpan.FromMinutes 30. && budget.ExecutionDeadline>now->()
                | _->findings.Add "main-route-budget-not-current"
                match value.State.Reservation with
                | Some reservation when reservation.Generation=value.State.Generation && reservation.ExpiresAt>now->()
                | _->findings.Add "main-route-reservation-not-current"
                match preparation with
                | Some prep when prep.Runner.Generation=value.State.Generation && prep.Runner.ExpiresAt>now && prep.ExecutionReservation.Generation=Id.generationValue value.State.Generation && prep.ExecutionReservation.Deadline>now->()
                | _->findings.Add "main-route-executor-authority-not-current"
                if unknown>0 then findings.Add "main-route-observation-required"
                let dispatch=findings.Count=0
                return Ok{Admitted=admitted;Ready=dispatch;DispatchEnabled=dispatch;Mode=mode
                          Sequence=Id.revisionValue value.State.Revision;Generation=Id.generationValue value.State.Generation
                          UnknownOperations=unknown;Findings=List.ofSeq findings} }
        member _.Control(control,token)=task {
            if control.CommandId=Guid.Empty||control.ExpectedSequence<0L||control.ExpectedGeneration<0L
               ||control.PrincipalId<>principal||String.IsNullOrWhiteSpace control.Reason||control.Reason<>control.Reason.Trim()
               ||control.IssuedAt=DateTimeOffset.MinValue||control.ExpiresAt<=control.IssuedAt then
                return Error "invalid-main-control-request"
            else
                let command=
                    match control.Action with
                    | "pause"->Some(Pause control.Reason)
                    | "resume"->Some Resume
                    | "revoke"->Some(Revoke control.Reason)
                    | "cancel"->Some(RequestCancel control.Reason)
                    | _->None
                match command with
                | None->return Error "invalid-main-control-action"
                | Some command->
                    let envelope=
                        {CommandId=Id.command control.CommandId;ProtocolVersion=Id.protocolVersion 1 0
                         ExpectedRevision=Id.revision control.ExpectedSequence;ExpectedGeneration=Id.generation control.ExpectedGeneration
                         PrincipalId=control.PrincipalId;SessionId=None;IssuedAt=control.IssuedAt;ExpiresAt=control.ExpiresAt;Command=command}
                    let! appended=HostedWriterJournal.decideAndAppend clock workItems workItemId envelope token
                    match appended with
                    | Error reason->return Error reason
                    | Ok(decision,sequence)->
                        match decision.Receipt.Disposition with
                        | ReceiptDisposition.Accepted|ReceiptDisposition.Duplicate when control.Action="cancel"->
                            let selected=lock gate (fun()->running,boundPreparation)
                            match selected with
                            | Some host,Some preparation->
                                try
                                    let! result=host.ExecutionActor.Ask<CoordinationResult>(box(Cancel preparation.LaunchIntent.Key),TimeSpan.FromSeconds 30.,token)
                                    let detail=
                                        match result with
                                        | SessionAdvanced _|SessionDuplicate _->"execution-cancel-reconciled"
                                        | SessionNeedsReconciliation reason|SessionRefused reason->reason
                                    let! durable=(executions :> IExecutionSessionJournal).ReadAttempt(preparation.LaunchIntent.Key.AssignmentId,preparation.LaunchIntent.Key.AttemptId,token)
                                    let observed=
                                        durable
                                        |>Option.bind(fun stored->SessionState.replay stored.Events)
                                        |>Option.bind _.Observation
                                        |>Option.map(fun value->match value.Lifecycle with Cancelled|Succeeded|Failed|DeadlineExceeded->true|_->false)
                                        |>Option.defaultValue false
                                    return Ok{Sequence=sequence;Action=control.Action;RequestPersisted=true;ProcessTerminationObserved=Some observed;Detail=detail}
                                with :? OperationCanceledException->
                                    return Ok{Sequence=sequence;Action=control.Action;RequestPersisted=true;ProcessTerminationObserved=Some false;Detail="execution-cancel-observation-timeout"}
                            | _->return Ok{Sequence=sequence;Action=control.Action;RequestPersisted=true;ProcessTerminationObserved=Some false;Detail="execution-cancel-binding-unavailable"}
                        | ReceiptDisposition.Accepted|ReceiptDisposition.Duplicate->
                            return Ok{Sequence=sequence;Action=control.Action;RequestPersisted=true;ProcessTerminationObserved=None;Detail=decision.Receipt.Detail}
                        | _->return Error decision.Receipt.Detail }
