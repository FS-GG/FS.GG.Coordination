namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open AGUI.Abstractions
open AGUI.Formatting
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.PostgreSql

type AguiProjectionRequest =
    {
        WorkItemId: WorkItemId
        ExpectedGeneration: Generation
        AfterSequence: int64 option
    }

type AguiProjectionFailure =
    | ObserverUnavailable of ReadinessFailure list
    | SnapshotAuthorityUnsupported of sequence: int64
    | ForeignWorkItemRecord of sequence: int64
    | ConflictingDuplicate of sequence: int64
    | CorruptEvent of sequence: int64
    | WorkItemIdentityMissing
    | StaleGeneration of expected: int64 * observed: int64
    | InvalidReconnectCursor of requested: int64 * lastSequence: int64

type AguiProjectionResult =
    {
        MediaType: string
        SseBytes: byte array
        LastSequence: int64
        DuplicateCount: int
        Partial: bool
        Gap: (int64 * int64) option
    }

type private EventSequence(events: BaseEvent list) =
    interface IAsyncEnumerable<BaseEvent> with
        member _.GetAsyncEnumerator(cancellationToken) =
            let values = List.toArray events
            let mutable index = -1

            { new IAsyncEnumerator<BaseEvent> with
                member _.Current = values[index]

                member _.MoveNextAsync() =
                    if cancellationToken.IsCancellationRequested then
                        ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken))
                    else
                        index <- index + 1
                        ValueTask<bool>(index < values.Length)

                member _.DisposeAsync() = ValueTask()
            }

[<RequireQualifiedAccess>]
module AguiReadOnlyProjection =
    let schema = "fsgg.coordination.agui-work-item-projection/1"
    let testedProtocolVersion = "1.0"
    let testedSdkVersion = "1.0.0"
    let testedSdkCommit = "f08ccf853497914ef70a5f0197af45bb74915bc2"

    let private exactDuplicate (left: SerializedEvent) (right: SerializedEvent) =
        left.PersistenceId = right.PersistenceId
        && left.Sequence = right.Sequence
        && left.EventId = right.EventId
        && left.SchemaVersion = right.SchemaVersion
        && left.SerializerVersion = right.SerializerVersion
        && left.PayloadSha256 = right.PayloadSha256
        && left.Payload.AsSpan().SequenceEqual(right.Payload.AsSpan())
        && left.EffectChange = right.EffectChange
        && left.RecordedAt = right.RecordedAt

    let private collapse (persistenceId: string) (events: SerializedEvent list) =
        events
        |> List.groupBy _.Sequence
        |> List.sortBy fst
        |> List.fold
            (fun (state: Result<SerializedEvent list * int, AguiProjectionFailure>) (sequence, copies) ->
                state
                |> Result.bind (fun (found, duplicateCount) ->
                    if
                        copies
                        |> List.exists (fun eventValue -> eventValue.PersistenceId <> persistenceId)
                    then
                        Error(ForeignWorkItemRecord sequence)
                    elif copies |> List.forall (exactDuplicate copies.Head) then
                        Ok(copies.Head :: found, duplicateCount + copies.Length - 1)
                    else
                        Error(ConflictingDuplicate sequence)))
            (Ok([], 0))
        |> Result.map (fun (events, duplicateCount) -> List.rev events, duplicateCount)

    let private firstGap (events: SerializedEvent list) =
        events
        |> List.fold
            (fun state (eventValue: SerializedEvent) ->
                match state with
                | Choice2Of2 gap -> Choice2Of2 gap
                | Choice1Of2 expected when eventValue.Sequence = expected -> Choice1Of2(expected + 1L)
                | Choice1Of2 expected -> Choice2Of2(expected, eventValue.Sequence - 1L))
            (Choice1Of2 1L)
        |> function
            | Choice1Of2 _ -> None
            | Choice2Of2 gap -> Some gap

    let private controlName =
        function
        | Running -> "running"
        | Paused _ -> "paused"
        | CancelPending _ -> "cancel-pending"
        | Cancelled _ -> "cancelled"
        | Revoked _ -> "revoked"

    let private jsonElement (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        write writer
        writer.Flush()
        use document = JsonDocument.Parse(stream.ToArray())
        document.RootElement.Clone()

    let private snapshot
        (persistenceId: string)
        (lastSequence: int64)
        (duplicateCount: int)
        (afterSequence: int64 option)
        (gap: (int64 * int64) option)
        (state: State option)
        =
        jsonElement (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", schema)
            writer.WriteString("authority", "durable-work-item-replay")
            writer.WriteString("workItemPersistenceId", persistenceId)
            writer.WriteString("journalSequence", string lastSequence)
            writer.WriteNumber("duplicateCount", duplicateCount)

            afterSequence
            |> Option.iter (fun value -> writer.WriteString("reconnectAfterSequence", string value))

            match gap, state with
            | None, Some current ->
                writer.WriteBoolean("partial", false)
                writer.WriteString("workflowRevision", string (Id.revisionValue current.Revision))
                writer.WriteString("generation", string (Id.generationValue current.Generation))
                writer.WriteString("control", controlName current.Control)
                writer.WriteBoolean("readbackCurrent", current.ReadbackCurrent)
                writer.WriteNumber("attemptCount", current.Attempts.Count)

                writer.WriteNumber(
                    "unsettledOperationCount",
                    current.Operations
                    |> Seq.filter (fun (KeyValue(_, value)) ->
                        match value with
                        | OperationState.Settled _ -> false
                        | _ -> true)
                    |> Seq.length
                )

                writer.WriteBoolean("recoveryRequired", not (Set.isEmpty current.RecoveryObligations))
                writer.WriteBoolean("nativeDeliveryObserved", not (Map.isEmpty current.NativeDeliveryReadbacks))
            | Some(firstMissing, lastMissing), _ ->
                writer.WriteBoolean("partial", true)
                writer.WriteString("partialReason", "journal-sequence-gap")
                writer.WriteString("firstMissingSequence", string firstMissing)
                writer.WriteString("lastMissingSequence", string lastMissing)
            | None, None ->
                writer.WriteBoolean("partial", true)
                writer.WriteString("partialReason", "authoritative-state-unavailable")

            writer.WriteBoolean("deliveryVerified", false)
            writer.WriteString("deliveryMeaning", "projection-complete-is-not-work-item-delivery")
            writer.WriteEndObject())

    let private events
        (persistenceId: string)
        (lastSequence: int64)
        (duplicateCount: int)
        (afterSequence: int64 option)
        (gap: (int64 * int64) option)
        (state: State option)
        (timestamp: int64)
        =
        let runId = $"agui-read:{persistenceId}:{lastSequence}"

        let snapshotValue =
            snapshot persistenceId lastSequence duplicateCount afterSequence gap state

        let started = RunStartedEvent()
        started.ThreadId <- persistenceId
        started.RunId <- runId
        started.ProtocolVersion <- AGUIProtocol.Version
        started.Timestamp <- Nullable timestamp

        let projected = StateSnapshotEvent()
        projected.Snapshot <- snapshotValue
        projected.Timestamp <- Nullable timestamp

        let finished = RunFinishedEvent()
        finished.ThreadId <- persistenceId
        finished.RunId <- runId
        finished.Result <- Nullable snapshotValue
        finished.Timestamp <- Nullable timestamp

        [ started :> BaseEvent; projected :> BaseEvent; finished :> BaseEvent ]

    let private formatSse (events: BaseEvent list) (cancellationToken: CancellationToken) =
        task {
            use output = new MemoryStream()
            let formatter = SseEventStreamFormatter()
            do! formatter.WriteAsync(EventSequence(events), output, cancellationToken)
            return formatter.MediaType, output.ToArray()
        }

    let project (store: IJournalStore) (request: AguiProjectionRequest) (cancellationToken: CancellationToken) =
        task {
            let persistenceId = WorkItemIdentity.persistenceId request.WorkItemId
            let! recovered = store.Recover(persistenceId, cancellationToken)

            match recovered with
            | Error failures -> return Error(ObserverUnavailable failures)
            | Ok recovery when recovery.Snapshot.IsSome ->
                return Error(SnapshotAuthorityUnsupported recovery.Snapshot.Value.Sequence)
            | Ok recovery ->
                match collapse persistenceId recovery.Events with
                | Error failure -> return Error failure
                | Ok(collapsed, duplicateCount) ->
                    let lastSequence =
                        collapsed |> List.tryLast |> Option.map _.Sequence |> Option.defaultValue 0L

                    match request.AfterSequence with
                    | Some value when value < 0L || value > lastSequence ->
                        return Error(InvalidReconnectCursor(value, lastSequence))
                    | _ ->
                        let gap = firstGap collapsed

                        let decoded =
                            if gap.IsSome then
                                Ok None
                            else
                                collapsed
                                |> List.fold
                                    (fun state stored ->
                                        state
                                        |> Result.bind (fun found ->
                                            EventEnvelope.tryDecode stored.Payload
                                            |> Result.mapError (fun _ -> CorruptEvent stored.Sequence)
                                            |> Result.map (fun eventValue -> eventValue :: found)))
                                    (Ok [])
                                |> Result.map (List.rev >> replay >> Some)

                        match decoded with
                        | Error failure -> return Error failure
                        | Ok(Some state) when state.WorkItemId <> Some request.WorkItemId ->
                            return Error WorkItemIdentityMissing
                        | Ok(Some state) when state.Generation <> request.ExpectedGeneration ->
                            return
                                Error(
                                    StaleGeneration(
                                        Id.generationValue request.ExpectedGeneration,
                                        Id.generationValue state.Generation
                                    )
                                )
                        | Ok state ->
                            let timestamp =
                                collapsed
                                |> List.tryLast
                                |> Option.map (fun value -> value.RecordedAt.ToUnixTimeMilliseconds())
                                |> Option.defaultValue 0L

                            let! mediaType, bytes =
                                formatSse
                                    (events
                                        persistenceId
                                        lastSequence
                                        duplicateCount
                                        request.AfterSequence
                                        gap
                                        state
                                        timestamp)
                                    cancellationToken

                            return
                                Ok
                                    {
                                        MediaType = mediaType
                                        SseBytes = bytes
                                        LastSequence = lastSequence
                                        DuplicateCount = duplicateCount
                                        Partial = gap.IsSome || state.IsNone
                                        Gap = gap
                                    }
        }
