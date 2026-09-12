namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence

type MainEffectDriveResult =
    | EffectCompleted of OperationId * int64
    | EffectNeedsReconciliation of OperationId * int64
    | EffectNeedsExternalReconciliation of OperationId * int64
    | EffectAlreadySettled of OperationId
    | EffectDriveRefused of string

/// Durable Main-side effect pump. Reducer state is the only authority; providers are
/// observations keyed by the already-recorded operation identity.
[<Sealed>]
type MainEffectDriver
    (clock:TimeProvider, store:IJournalStore, workItemId:WorkItemId,
     principalId:string, providers:HostedWriterProviderAdapter,
     reconcile:HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedWriterProviderReadback,string>>,
     ?advance:HostedRoutePlan -> EffectIntent -> HostedWriterProviderReadback -> CancellationToken -> Task<Result<unit,string>>,
     ?preflight:HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<unit,string>>) =

    let advance=defaultArg advance (fun _ _ _ _->Task.FromResult(Ok()))
    let preflight=defaultArg preflight (fun _ _ _->Task.FromResult(Ok()))

    let authorityCurrent now (state:State) =
        state.Control=ControlState.Running
        && ((state.Budget|>Option.exists(fun budget->
                now<=budget.Deadline
                && state.Used.Tokens<=budget.TokenLimit
                && state.Used.RuntimeSeconds<=budget.RuntimeSecondsLimit
                && state.Used.CostMicros<=budget.CostMicrosLimit))
            || (state.SubscriptionBudget|>Option.exists(fun budget->now<budget.ExecutionDeadline)))

    let derivedCommandId (operationId:OperationId) stage (revision:WorkflowRevision) =
        let seed = Id.operationValue operationId
        // A pre-effect authority attempt at a new journal revision is a distinct
        // observation. Once accepted, the operation state itself prevents a
        // second mutation; rejected stale/paused attempts cannot poison the
        // identity used by a later freshly-authorized attempt.
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed:D}:{stage}:{Id.revisionValue revision}"))
        Id.command(Guid(ReadOnlySpan(bytes,0,16)))

    let append state operationId stage command cancellationToken = task {
        let now = clock.GetUtcNow()
        let envelope =
            { CommandId=derivedCommandId operationId stage state.Revision;ProtocolVersion=Id.protocolVersion 1 0
              ExpectedRevision=state.Revision;ExpectedGeneration=state.Generation
              PrincipalId=principalId;SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.
              Command=command }
        let! result=HostedWriterJournal.decideAndAppend clock store workItemId envelope cancellationToken
        return
            result
            |> Result.bind(fun (decision,sequence) ->
                match decision.Receipt.Disposition with
                | ReceiptDisposition.Accepted | ReceiptDisposition.Duplicate -> Ok sequence
                | _ -> Error decision.Receipt.Detail) }

    member this.Drive(operationId:OperationId,cancellationToken:CancellationToken) = task {
        let! recovered=HostedWriterJournal.recover store workItemId cancellationToken
        match recovered with
        | Error failures -> return EffectDriveRefused(sprintf "%A" failures)
        | Ok current ->
            match current.State.HostedRoute,Map.tryFind operationId current.State.Operations with
            | _,Some(OperationState.Settled _) -> return EffectAlreadySettled operationId
            | None,_ -> return EffectDriveRefused "hosted-route-not-selected"
            | _,None -> return EffectDriveRefused "effect-intent-not-recorded"
            | Some route,Some(OperationState.IntentRecorded intent) ->
                // Definitive preconditions are checked while the durable state
                // is still IntentRecorded. A pending check therefore remains
                // safely retryable. Once Dispatching is appended, the mutation
                // may have happened and every restart is reconcile-only.
                if not(authorityCurrent (clock.GetUtcNow()) current.State) then
                    return EffectDriveRefused "effect-authority-not-current"
                else
                    let! allowed=preflight route intent cancellationToken
                    match allowed with
                    | Error reason->return EffectDriveRefused reason
                    | Ok()->
                        let! authorized=HostedWriterJournal.recover store workItemId cancellationToken
                        match authorized with
                        | Error failures->return EffectDriveRefused(sprintf "%A" failures)
                        | Ok latest when not(authorityCurrent (clock.GetUtcNow()) latest.State)->return EffectDriveRefused "effect-authority-not-current"
                        | Ok latest->
                            let! marked=append latest.State operationId "dispatch" (MarkEffectDispatching operationId) cancellationToken
                            match marked with
                            | Error reason -> return EffectDriveRefused reason
                            | Ok _ ->
                                let! refreshed=HostedWriterJournal.recover store workItemId cancellationToken
                                match refreshed with
                                | Error failures -> return EffectDriveRefused(sprintf "%A" failures)
                                | Ok next -> return! this.RecordObservation(route,intent,next.State,providers.Dispatch(route,intent,cancellationToken),cancellationToken)
            | Some route,Some(OperationState.Dispatching intent)
                ->
                // Restart and ambiguity paths are observation-only. They must never replay
                // an authority-bearing claim, launch, publication, PR, or merge call.
                return! this.RecordObservation(route,intent,current.State,reconcile route intent cancellationToken,cancellationToken)
            | Some route,Some(OperationState.NeedsObservation(intent,_)) ->
                return! this.RecordObservation(route,intent,current.State,reconcile route intent cancellationToken,cancellationToken) }

    member private _.RecordObservation(route:HostedRoutePlan,intent:EffectIntent,state:State,pending:Task<Result<HostedWriterProviderReadback,string>>,cancellationToken) = task {
        let! observed=pending
        match observed with
        | Ok(HostedEffect readback) ->
            let! appended=append state intent.OperationId "hosted-readback" (RecordHostedEffectReadback(intent.OperationId,readback)) cancellationToken
            match appended with
            | Error reason -> return EffectDriveRefused reason
            | Ok sequence ->
                let! advanced=advance route intent (HostedEffect readback) cancellationToken
                return match advanced with Ok()->EffectCompleted(intent.OperationId,sequence)|Error reason->EffectDriveRefused reason
        | Ok(NativeDelivery readback) ->
            let! appended=append state intent.OperationId "native-readback" (RecordNativeDeliveryReadback(intent.OperationId,readback)) cancellationToken
            match appended with
            | Error reason -> return EffectDriveRefused reason
            | Ok sequence ->
                let! advanced=advance route intent (NativeDelivery readback) cancellationToken
                return match advanced with Ok()->EffectCompleted(intent.OperationId,sequence)|Error reason->EffectDriveRefused reason
        | Error reason ->
            let needsReconciliation sequence =
                if reason.StartsWith("github-",StringComparison.Ordinal) then EffectNeedsExternalReconciliation(intent.OperationId,sequence)
                else EffectNeedsReconciliation(intent.OperationId,sequence)
            match Map.tryFind intent.OperationId state.Operations with
            | Some(OperationState.NeedsObservation _) -> return needsReconciliation (Id.revisionValue state.Revision)
            | _ ->
                let! appended=append state intent.OperationId "unknown" (ObserveEffect(intent.OperationId,Unknown reason)) cancellationToken
                return match appended with Ok sequence -> needsReconciliation sequence | Error appendReason -> EffectDriveRefused appendReason }
