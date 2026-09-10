namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.PostgreSql

type WorkItemRecovery =
    { PersistenceId: string
      State: State
      UnsettledEffects: EffectIntent list
      RequiresExternalReconciliation: bool }

type HostedWriterProviderCalls =
    { AcquireExternalClaim: HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedEffectReadback, string>>
      DispatchRunner: HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedEffectReadback, string>>
      StoreCandidate: HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedEffectReadback, string>>
      PublishCandidateBranch: HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedEffectReadback, string>>
      CreatePullRequest: HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedEffectReadback, string>>
      MergePullRequest: HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<HostedEffectReadback, string>>
      ReadNativeDelivery: HostedRoutePlan -> EffectIntent -> CancellationToken -> Task<Result<NativeDeliveryReadback, string>> }

type HostedWriterProviderReadback =
    | HostedEffect of HostedEffectReadback
    | NativeDelivery of NativeDeliveryReadback

[<Sealed>]
type HostedWriterProviderAdapter private (calls: HostedWriterProviderCalls) =
    static member Create(calls: HostedWriterProviderCalls) = HostedWriterProviderAdapter(calls)

    member _.Dispatch(route: HostedRoutePlan, intent: EffectIntent, cancellationToken: CancellationToken) = task {
        let expected =
            match intent.Kind with
            | AcquireExternalClaim -> Some(route.ClaimOperationId, route.ClaimResourceId)
            | DispatchRunner -> Some(route.ProcessOperationId, Id.attemptValue route.AttemptId |> string)
            | StoreCandidate -> Some(route.CandidateOperationId, Id.candidateValue route.CandidateId |> string)
            | PublishCandidateBranch -> Some(route.BranchOperationId, route.BranchRef)
            | CreatePullRequest -> Some(route.PullRequestOperationId, route.BranchRef)
            | MergePullRequest -> Some(route.MergeOperationId, route.BranchRef)
            | ReadNativeDelivery -> Some(route.ReadbackOperationId, route.BranchRef)
            | ReleaseExternalClaim | CancelRunner | InspectExternalOperation -> None

        let validSha256 (value: string) =
            value.Length = 64 && value |> Seq.forall(fun character -> Char.IsAsciiHexDigit character && not(Char.IsUpper character))
        let hostedResult (result: Result<HostedEffectReadback,string>) =
            result
            |> Result.bind(fun readback ->
                if readback.OperationId=intent.OperationId && readback.RouteId=route.RouteId
                   && readback.AttemptId=route.AttemptId && readback.CandidateId=route.CandidateId
                   && readback.RepositoryNodeId=route.RepositoryNodeId
                   && readback.Generation=route.Generation && readback.WorkflowRevision=route.WorkflowRevision
                   && readback.ObservedAt>=route.SelectedAt then Ok(HostedEffect readback)
                else Error "provider-readback-is-not-bound-to-selected-hosted-route")
        let nativeResult (result: Result<NativeDeliveryReadback,string>) =
            result
            |> Result.bind(fun readback ->
                if readback.OperationId=intent.OperationId && readback.RouteId=route.RouteId
                   && readback.AttemptId=route.AttemptId && readback.CandidateId=route.CandidateId
                   && readback.RepositoryNodeId=route.RepositoryNodeId
                   && readback.Generation=route.Generation && readback.WorkflowRevision=route.WorkflowRevision
                   && readback.ObservedAt>=route.SelectedAt then Ok(NativeDelivery readback)
                else Error "provider-readback-is-not-bound-to-selected-hosted-route")

        if expected <> Some(intent.OperationId, intent.ResourceId)
           || intent.Generation <> route.Generation
           || intent.WorkflowRevision <> route.WorkflowRevision
           || not(validSha256 intent.PayloadSha256) then
            return Error "effect-is-not-bound-to-selected-hosted-route"
        else
            match intent.Kind with
            | AcquireExternalClaim ->
                let! result = calls.AcquireExternalClaim route intent cancellationToken
                return hostedResult result
            | DispatchRunner ->
                let! result = calls.DispatchRunner route intent cancellationToken
                return hostedResult result
            | StoreCandidate ->
                let! result = calls.StoreCandidate route intent cancellationToken
                return hostedResult result
            | PublishCandidateBranch ->
                let! result = calls.PublishCandidateBranch route intent cancellationToken
                return hostedResult result
            | CreatePullRequest ->
                let! result = calls.CreatePullRequest route intent cancellationToken
                return hostedResult result
            | MergePullRequest ->
                let! result = calls.MergePullRequest route intent cancellationToken
                return hostedResult result
            | ReadNativeDelivery ->
                let! result = calls.ReadNativeDelivery route intent cancellationToken
                return nativeResult result
            | ReleaseExternalClaim | CancelRunner | InspectExternalOperation ->
                return Error "effect-kind-is-outside-hosted-writer-capability" }

[<RequireQualifiedAccess>]
module HostedWriterJournal =
    let private eventChange = function
        | EffectIntentRecorded intent -> IntentAdded intent
        | EffectSettled(operationId, _) -> Settled operationId
        | _ -> NoEffect

    let private eventId (commandId: CommandId) sequence =
        let command = Id.commandValue commandId
        let bytes = Encoding.UTF8.GetBytes($"{command:D}:{sequence}") |> SHA256.HashData
        Guid(ReadOnlySpan(bytes, 0, 16))

    let recover (store: IJournalStore) workItemId cancellationToken = task {
        let persistenceId = WorkItemIdentity.persistenceId workItemId
        let! recovered = store.Recover(persistenceId, cancellationToken)
        match recovered with
        | Error failures -> return Error failures
        | Ok value when value.Snapshot.IsSome ->
            return Error [ CorruptRecord(persistenceId, value.Snapshot.Value.Sequence) ]
        | Ok value ->
            let decoded =
                value.Events
                |> List.map(fun stored ->
                    EventEnvelope.tryDecode stored.Payload
                    |> Result.mapError(fun _ -> CorruptRecord(persistenceId, stored.Sequence)))
            match decoded |> List.tryPick(function Error failure -> Some failure | Ok _ -> None) with
            | Some failure -> return Error [ failure ]
            | None ->
                let events = decoded |> List.choose(function Ok eventValue -> Some eventValue | Error _ -> None)
                return
                    Ok
                        { PersistenceId = persistenceId
                          State = replay events
                          UnsettledEffects = value.UnsettledEffects
                          RequiresExternalReconciliation = value.RequiresExternalReconciliation } }

    let appendRequest persistenceId receivedAt (state: State) (envelope: CommandEnvelope) (decision: Decision) =
        let bodySha256 = canonicalEnvelopeSha256 envelope
        let events =
            decision.Events
            |> List.mapi(fun index eventValue ->
                let sequence = Id.revisionValue state.Revision + int64 index + 1L
                let payload = EventEnvelope.encode eventValue
                { PersistenceId = persistenceId
                  Sequence = sequence
                  EventId = eventId envelope.CommandId sequence
                  SchemaVersion = 1
                  SerializerVersion = EventEnvelope.serializerVersion
                  Payload = payload
                  PayloadSha256 = payload |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
                  EffectChange = eventChange eventValue
                  RecordedAt = receivedAt })
        { Inbox =
            { PersistenceId = persistenceId
              CommandId = envelope.CommandId
              BodySha256 = bodySha256
              ReceivedAt = receivedAt }
          ExpectedSequence = Id.revisionValue state.Revision
          Events = events }

    let decideAndAppend (clock: TimeProvider) (store: IJournalStore) workItemId envelope cancellationToken = task {
        let! recovered = recover store workItemId cancellationToken
        match recovered with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok recovery ->
            let decision = decide (clock.GetUtcNow()) recovery.State envelope
            let request = appendRequest recovery.PersistenceId (clock.GetUtcNow()) recovery.State envelope decision
            let! appended = store.Append(request, cancellationToken)
            match appended with
            | Appended sequence | Duplicate sequence -> return Ok(decision, sequence)
            | Conflict -> return Error "command-identity-conflict"
            | WrongExpectedSequence sequence -> return Error($"wrong-expected-sequence:{sequence}")
            | InvalidAppend reason -> return Error reason }

    let persistStartupPause (clock: TimeProvider) (store: IJournalStore) workItemId principalId cancellationToken = task {
        let! recovered = recover store workItemId cancellationToken
        match recovered with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok recovery when recovery.State.WorkItemId <> Some workItemId -> return Error "configured-work-item-not-admitted"
        | Ok recovery ->
            let now = clock.GetUtcNow()
            let envelope =
                { CommandId = Id.command(Guid.NewGuid())
                  ProtocolVersion = Id.protocolVersion 1 0
                  ExpectedRevision = recovery.State.Revision
                  ExpectedGeneration = recovery.State.Generation
                  PrincipalId = principalId
                  SessionId = None
                  IssuedAt = now
                  ExpiresAt = now.AddMinutes 1.
                  Command = RecordStartupPause "process-startup" }
            let! appended = decideAndAppend clock store workItemId envelope cancellationToken
            match appended with
            | Ok(decision, sequence) when decision.Receipt.Disposition = Accepted -> return Ok sequence
            | Ok(decision, _) -> return Error decision.Receipt.Detail
            | Error reason -> return Error reason }
