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
    | EffectAlreadySettled of OperationId
    | EffectDriveRefused of string

/// Durable Main-side effect pump. Reducer state is the only authority; providers are
/// observations keyed by the already-recorded operation identity.
[<Sealed>]
type MainEffectDriver
    (clock:TimeProvider, store:IJournalStore, workItemId:WorkItemId,
     principalId:string, providers:HostedWriterProviderAdapter,
     reconcile:HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedWriterProviderReadback,string>>) =

    let derivedCommandId (operationId:OperationId) stage =
        let seed = Id.operationValue operationId
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed:D}:{stage}"))
        Id.command(Guid(ReadOnlySpan(bytes,0,16)))

    let append state operationId stage command cancellationToken = task {
        let now = clock.GetUtcNow()
        let envelope =
            { CommandId=derivedCommandId operationId stage;ProtocolVersion=Id.protocolVersion 1 0
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
                let! marked=append current.State operationId "dispatch" (MarkEffectDispatching operationId) cancellationToken
                match marked with
                | Error reason -> return EffectDriveRefused reason
                | Ok _ ->
                    let! refreshed=HostedWriterJournal.recover store workItemId cancellationToken
                    match refreshed with
                    | Error failures -> return EffectDriveRefused(sprintf "%A" failures)
                    | Ok next -> return! this.RecordObservation(route,intent,next.State,providers.Dispatch(route,intent,cancellationToken),cancellationToken)
            | Some route,Some(OperationState.Dispatching intent)
            | Some route,Some(OperationState.NeedsObservation(intent,_)) ->
                // Restart and ambiguity paths are observation-only. They must never replay
                // an authority-bearing claim, launch, publication, PR, or merge call.
                return! this.RecordObservation(route,intent,current.State,reconcile route intent cancellationToken,cancellationToken) }

    member private _.RecordObservation(route:HostedRoutePlan,intent:EffectIntent,state:State,pending:Task<Result<HostedWriterProviderReadback,string>>,cancellationToken) = task {
        let! observed=pending
        match observed with
        | Ok(HostedEffect readback) ->
            let! appended=append state intent.OperationId "hosted-readback" (RecordHostedEffectReadback(intent.OperationId,readback)) cancellationToken
            return match appended with Ok sequence -> EffectCompleted(intent.OperationId,sequence) | Error reason -> EffectDriveRefused reason
        | Ok(NativeDelivery readback) ->
            let! appended=append state intent.OperationId "native-readback" (RecordNativeDeliveryReadback(intent.OperationId,readback)) cancellationToken
            return match appended with Ok sequence -> EffectCompleted(intent.OperationId,sequence) | Error reason -> EffectDriveRefused reason
        | Error reason ->
            match Map.tryFind intent.OperationId state.Operations with
            | Some(OperationState.NeedsObservation _) -> return EffectNeedsReconciliation(intent.OperationId,Id.revisionValue state.Revision)
            | _ ->
                let! appended=append state intent.OperationId "unknown" (ObserveEffect(intent.OperationId,Unknown reason)) cancellationToken
                return match appended with Ok sequence -> EffectNeedsReconciliation(intent.OperationId,sequence) | Error appendReason -> EffectDriveRefused appendReason }
