namespace FS.GG.Coordination.Orchestration.PostgreSql

open System
open System.Data
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Configuration
open System.Text.Json.Serialization
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open Npgsql

type StoreOptions =
    { DataSource: NpgsqlDataSource
      StoreId: string
      BackupIdentity: string
      MinimumGenerationFence: int64
      RuntimeSchemaVersion: int
      SupportedEventSchemaVersions: Set<int>
      SupportedSerializerVersions: Set<string>
      MaximumCandidateBytes: int64 }

module private Hash =
    let bytes (value: byte array) =
        SHA256.HashData value |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let valid (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 64
        && value |> Seq.forall Uri.IsHexDigit

module private TaskEx =
    let discard (pending: Task<'T>) =
        task {
            let! _ = pending
            return ()
        }

module private Effect =
    let kindToByte = function
        | AcquireExternalClaim -> 0uy
        | ReleaseExternalClaim -> 1uy
        | DispatchRunner -> 2uy
        | CancelRunner -> 3uy
        | InspectExternalOperation -> 4uy

    let byteToKind = function
        | 0uy -> Some AcquireExternalClaim
        | 1uy -> Some ReleaseExternalClaim
        | 2uy -> Some DispatchRunner
        | 3uy -> Some CancelRunner
        | 4uy -> Some InspectExternalOperation
        | _ -> None

    let equal left right =
        match left, right with
        | NoEffect, NoEffect -> true
        | Settled leftId, Settled rightId -> Id.operationValue leftId = Id.operationValue rightId
        | IntentAdded leftIntent, IntentAdded rightIntent ->
            Id.operationValue leftIntent.OperationId = Id.operationValue rightIntent.OperationId
            && leftIntent.Kind = rightIntent.Kind
            && Id.generationValue leftIntent.Generation = Id.generationValue rightIntent.Generation
            && Id.revisionValue leftIntent.WorkflowRevision = Id.revisionValue rightIntent.WorkflowRevision
            && leftIntent.ResourceId = rightIntent.ResourceId
            && String.Equals(leftIntent.PayloadSha256, rightIntent.PayloadSha256, StringComparison.OrdinalIgnoreCase)
        | _ -> false

    let derive (state: State) = function
        | EffectIntentRecorded intent -> IntentAdded intent
        | EffectSettled(operationId, _) -> Settled operationId
        | EffectRetryAuthorized operationId ->
            match Map.tryFind operationId state.Operations with
            | Some(OperationState.Settled(intent, ProvenAbsent)) -> IntentAdded intent
            | _ -> NoEffect
        | _ -> NoEffect

[<RequireQualifiedAccess>]
module EventEnvelope =
    let serializerVersion = "fsgg.orchestration.core-event-json/1"

    let private options =
        let value = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        value.PropertyNameCaseInsensitive <- false
        value.Converters.Add(JsonFSharpConverter())
        value

    let encode (eventValue: Event) = JsonSerializer.SerializeToUtf8Bytes(eventValue, options)

    let tryDecode (bytes: byte array) =
        try
            let value = JsonSerializer.Deserialize<Event>(ReadOnlySpan<byte>(bytes), options)
            if isNull (box value) then Error "event-json-null" else Ok value
        with
        | :? JsonException -> Error "event-json-invalid"
        | :? NotSupportedException -> Error "event-json-unsupported"

[<RequireQualifiedAccess>]
module PostgreSqlSchema =
    let migrate (dataSource: NpgsqlDataSource) cancellationToken =
        task {
            let assembly = Assembly.GetExecutingAssembly()
            let resource =
                assembly.GetManifestResourceNames()
                |> Array.tryFind (fun name -> name.EndsWith("Migration.sql", StringComparison.Ordinal))
                |> Option.defaultWith (fun () -> invalidOp "embedded migration missing")
            use stream = assembly.GetManifestResourceStream resource
            use reader = new StreamReader(stream)
            let sql = reader.ReadToEnd()
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            use command = new NpgsqlCommand(sql, connection, transaction)
            do! command.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
            do! transaction.CommitAsync cancellationToken
            use identityCommand = new NpgsqlCommand("SELECT backup_identity::text FROM fsgg_orchestration.store_metadata WHERE singleton", connection)
            let! identity = identityCommand.ExecuteScalarAsync cancellationToken
            return string identity
        }

module private Sql =
    let add (command: NpgsqlCommand) value = command.Parameters.AddWithValue(value) |> ignore

    let classifyReadiness (exceptionValue: exn) =
        match exceptionValue with
        | :? PostgresException as pg when pg.SqlState = "25006" -> ReadOnlyStore
        | :? PostgresException as pg when pg.SqlState.StartsWith("53", StringComparison.Ordinal) -> CapacityUnavailable
        | :? PostgresException as pg -> StoreUnavailable $"postgres-sqlstate-{pg.SqlState}"
        | :? NpgsqlException -> StoreUnavailable "postgres-unavailable"
        | _ -> StoreUnavailable "persistence-failure"

    let effectColumns = function
        | NoEffect -> 0s, DBNull.Value :> obj, DBNull.Value :> obj, DBNull.Value :> obj, DBNull.Value :> obj, DBNull.Value :> obj, DBNull.Value :> obj
        | IntentAdded intent ->
            1s,
            Id.operationValue intent.OperationId :> obj,
            int16 (Effect.kindToByte intent.Kind) :> obj,
            Id.generationValue intent.Generation :> obj,
            Id.revisionValue intent.WorkflowRevision :> obj,
            intent.ResourceId :> obj,
            intent.PayloadSha256.ToLowerInvariant() :> obj
        | Settled operationId -> 2s, Id.operationValue operationId :> obj, DBNull.Value :> obj, DBNull.Value :> obj, DBNull.Value :> obj, DBNull.Value :> obj, DBNull.Value :> obj

type PostgreSqlStore(options: StoreOptions) =
    let validateEvent (eventValue: SerializedEvent) =
        if String.IsNullOrWhiteSpace eventValue.PersistenceId then Error "persistence-id"
        elif eventValue.Sequence <= 0L then Error "sequence"
        elif not (options.SupportedEventSchemaVersions.Contains eventValue.SchemaVersion) then Error "schema-version"
        elif not (options.SupportedSerializerVersions.Contains eventValue.SerializerVersion) then Error "serializer-version"
        elif not (Hash.valid eventValue.PayloadSha256) || Hash.bytes eventValue.Payload <> eventValue.PayloadSha256.ToLowerInvariant() then Error "payload-digest"
        else
            EventEnvelope.tryDecode eventValue.Payload

    let receiptHash (candidate: CandidateArtifact) (objectKey: string) (verifiedAt: DateTimeOffset) =
        String.concat "\n"
            [ string (Id.candidateValue candidate.CandidateId)
              candidate.ContentSha256.ToLowerInvariant()
              candidate.ManifestSha256.ToLowerInvariant()
              string candidate.SizeBytes
              objectKey
              options.StoreId
              string options.RuntimeSchemaVersion
              verifiedAt.ToUniversalTime().ToString("O") ]
        |> Encoding.UTF8.GetBytes
        |> Hash.bytes

    let locationKey = function
        | ContentAddressedObject key -> Some key
        | ImmutableRemoteGitRef _ -> None

    let readCandidate candidateId cancellationToken =
        task {
            try
                use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                use command = new NpgsqlCommand("""
SELECT c.content_sha256,c.manifest_sha256,c.baseline_sha,c.head_sha,c.tree_sha,c.media_type,c.size_bytes,
       c.object_key,c.retain_until,c.receipt_sha256,c.verified_at,c.quarantined_reason,o.bytes
FROM fsgg_orchestration.candidate c
JOIN fsgg_orchestration.candidate_object o ON o.content_sha256=c.content_sha256
WHERE c.candidate_id=$1
                """, connection)
                Sql.add command (Id.candidateValue candidateId)
                use! reader = command.ExecuteReaderAsync cancellationToken
                let! found = reader.ReadAsync cancellationToken
                if not found then return Error "candidate-not-found"
                elif not (reader.IsDBNull 11) then return Error "candidate-quarantined"
                else
                    let contentSha = reader.GetString 0
                    let bytes = reader.GetFieldValue<byte array> 12
                    let size = reader.GetInt64 6
                    if Hash.bytes bytes <> contentSha || int64 bytes.LongLength <> size then return Error "candidate-corrupt"
                    else
                        let candidate =
                            { CandidateId = candidateId
                              ContentSha256 = contentSha
                              ManifestSha256 = reader.GetString 1
                              BaselineSha = reader.GetString 2
                              HeadSha = reader.GetString 3
                              TreeSha = reader.GetString 4
                              MediaType = reader.GetString 5
                              SizeBytes = size
                              RetainUntil = reader.GetFieldValue<DateTimeOffset> 8
                              Location = ContentAddressedObject(reader.GetString 7) }
                        return Ok { Candidate = candidate; Bytes = bytes }
            with exceptionValue -> return Error (match Sql.classifyReadiness exceptionValue with | StoreUnavailable reason -> reason | _ -> "candidate-store-refused")
        }

    let loadState (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (persistenceId: string) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("SELECT schema_version,serializer_version,payload,payload_sha256 FROM fsgg_orchestration.event WHERE persistence_id=$1 ORDER BY sequence_number", connection, transaction)
            Sql.add command persistenceId
            use! reader = command.ExecuteReaderAsync cancellationToken
            let mutable state = FS.GG.Coordination.Core.Orchestration.initial
            let mutable failure = None
            let mutable reading = true
            while reading do
                let! more = reader.ReadAsync cancellationToken
                reading <- more
                if more && failure.IsNone then
                    let schemaVersion = reader.GetInt32 0
                    let serializerVersion = reader.GetString 1
                    let payload = reader.GetFieldValue<byte array> 2
                    let payloadHash = reader.GetString 3
                    if not (options.SupportedEventSchemaVersions.Contains schemaVersion) then failure <- Some "schema-version"
                    elif not (options.SupportedSerializerVersions.Contains serializerVersion) then failure <- Some "serializer-version"
                    elif Hash.bytes payload <> payloadHash then failure <- Some "payload-digest"
                    else
                        match EventEnvelope.tryDecode payload with
                        | Error reason -> failure <- Some reason
                        | Ok eventValue ->
                            try state <- FS.GG.Coordination.Core.Orchestration.evolve state eventValue
                            with _ -> failure <- Some "event-evolve"
            return match failure with Some reason -> Error reason | None -> Ok state
        }

    interface IJournalStore with
        member _.CheckReadiness cancellationToken =
            task {
                try
                    use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                    use command = new NpgsqlCommand("""
SELECT schema_version,migration_state,backup_identity::text,generation_fence,
       current_setting('transaction_read_only')
FROM fsgg_orchestration.store_metadata WHERE singleton
                    """, connection)
                    use! reader = command.ExecuteReaderAsync cancellationToken
                    let! found = reader.ReadAsync cancellationToken
                    if not found then return Error [ MigrationInterrupted "metadata-missing" ]
                    else
                        let failures = ResizeArray<ReadinessFailure>()
                        let schemaVersion = reader.GetInt32 0
                        let migrationState = reader.GetString 1
                        let backupIdentity = reader.GetString 2
                        let generationFence = reader.GetInt64 3
                        if schemaVersion > options.RuntimeSchemaVersion then failures.Add(IncompatibleDowngrade(schemaVersion, options.RuntimeSchemaVersion))
                        elif schemaVersion < options.RuntimeSchemaVersion then failures.Add(MigrationInterrupted $"database-v{schemaVersion}-runtime-v{options.RuntimeSchemaVersion}")
                        if migrationState <> "ready" then failures.Add(MigrationInterrupted migrationState)
                        if not (String.Equals(backupIdentity, options.BackupIdentity, StringComparison.OrdinalIgnoreCase)) then failures.Add(BackupRequiresReconciliation "backup-identity-mismatch")
                        if generationFence < options.MinimumGenerationFence then failures.Add(BackupRequiresReconciliation "generation-fence-regressed")
                        if reader.GetString 4 = "on" then failures.Add ReadOnlyStore
                        return if failures.Count = 0 then Ok() else Error(List.ofSeq failures)
                with exceptionValue -> return Error [ Sql.classifyReadiness exceptionValue ]
            }

        member _.Append(request, cancellationToken) =
            task {
                let persistenceId = request.Inbox.PersistenceId
                let decoded = request.Events |> List.map validateEvent
                let invalid =
                    if String.IsNullOrWhiteSpace persistenceId || not (Hash.valid request.Inbox.BodySha256) then Some "invalid-inbox"
                    elif request.Events |> List.exists (fun eventValue -> eventValue.PersistenceId <> persistenceId) then Some "mixed-persistence-id"
                    elif request.Events |> List.mapi (fun index eventValue -> eventValue.Sequence = request.ExpectedSequence + int64 index + 1L) |> List.exists not then Some "non-contiguous-sequence"
                    else
                        decoded |> List.tryPick (function Ok _ -> None | Error reason -> Some reason)
                match invalid with
                | Some reason -> return InvalidAppend reason
                | None ->
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                        use ensureStream = new NpgsqlCommand("INSERT INTO fsgg_orchestration.stream(persistence_id,last_sequence) VALUES($1,0) ON CONFLICT DO NOTHING", connection, transaction)
                        Sql.add ensureStream persistenceId
                        do! ensureStream.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
                        use duplicateCommand = new NpgsqlCommand("SELECT body_sha256,terminal_sequence FROM fsgg_orchestration.inbox WHERE persistence_id=$1 AND command_id=$2", connection, transaction)
                        Sql.add duplicateCommand persistenceId
                        Sql.add duplicateCommand (Id.commandValue request.Inbox.CommandId)
                        use! duplicateReader = duplicateCommand.ExecuteReaderAsync cancellationToken
                        let! duplicate = duplicateReader.ReadAsync cancellationToken
                        let duplicateResult =
                            if duplicate then
                                if String.Equals(duplicateReader.GetString 0, request.Inbox.BodySha256, StringComparison.OrdinalIgnoreCase)
                                then Some(Duplicate(duplicateReader.GetInt64 1)) else Some Conflict
                            else None
                        do! duplicateReader.DisposeAsync().AsTask()
                        match duplicateResult with
                        | Some outcome ->
                            do! transaction.RollbackAsync cancellationToken
                            return outcome
                        | None ->
                            if request.Events.IsEmpty then
                                do! transaction.RollbackAsync cancellationToken
                                return InvalidAppend "new-command-requires-events"
                            else
                                use sequenceCommand = new NpgsqlCommand("SELECT last_sequence FROM fsgg_orchestration.stream WHERE persistence_id=$1 FOR UPDATE", connection, transaction)
                                Sql.add sequenceCommand persistenceId
                                let! actualObject = sequenceCommand.ExecuteScalarAsync cancellationToken
                                let actual = Convert.ToInt64 actualObject
                                if actual <> request.ExpectedSequence then
                                    do! transaction.RollbackAsync cancellationToken
                                    return WrongExpectedSequence actual
                                else
                                    let! priorState = loadState connection transaction persistenceId cancellationToken
                                    let mutable state = match priorState with Ok value -> value | Error _ -> FS.GG.Coordination.Core.Orchestration.initial
                                    let mutable semanticFailure = match priorState with Error reason -> Some reason | Ok _ -> None
                                    let eventPairs = List.zip request.Events (decoded |> List.choose (function Ok value -> Some value | Error _ -> None))
                                    for eventValue, coreEvent in eventPairs do
                                        let derived = Effect.derive state coreEvent
                                        if not (Effect.equal derived eventValue.EffectChange) then semanticFailure <- Some "effect-metadata-mismatch"
                                        if semanticFailure.IsNone then
                                            try state <- FS.GG.Coordination.Core.Orchestration.evolve state coreEvent
                                            with _ -> semanticFailure <- Some "event-evolve"
                                    match semanticFailure with
                                    | Some reason -> invalidOp $"semantic:{reason}"
                                    | None -> ()
                                    for eventValue in request.Events do
                                        let change, operationId, kind, generation, revision, resourceId, effectHash = Sql.effectColumns eventValue.EffectChange
                                        use insertEvent = new NpgsqlCommand("""
INSERT INTO fsgg_orchestration.event
(persistence_id,sequence_number,event_id,schema_version,serializer_version,payload,payload_sha256,
 effect_change,effect_operation_id,effect_kind,effect_generation,effect_workflow_revision,effect_resource_id,effect_payload_sha256,recorded_at)
VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
                                        """, connection, transaction)
                                        for value in [ persistenceId :> obj; eventValue.Sequence :> obj; eventValue.EventId :> obj; eventValue.SchemaVersion :> obj
                                                       eventValue.SerializerVersion :> obj; eventValue.Payload :> obj; eventValue.PayloadSha256.ToLowerInvariant() :> obj
                                                       change :> obj; operationId; kind; generation; revision; resourceId; effectHash; eventValue.RecordedAt.ToUniversalTime() :> obj ] do Sql.add insertEvent value
                                        do! insertEvent.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
                                    let terminal = request.Events |> List.last |> fun value -> value.Sequence
                                    use insertInbox = new NpgsqlCommand("INSERT INTO fsgg_orchestration.inbox(persistence_id,command_id,body_sha256,terminal_sequence,received_at) VALUES($1,$2,$3,$4,$5)", connection, transaction)
                                    for value in [ persistenceId :> obj; Id.commandValue request.Inbox.CommandId :> obj; request.Inbox.BodySha256.ToLowerInvariant() :> obj; terminal :> obj; request.Inbox.ReceivedAt.ToUniversalTime() :> obj ] do Sql.add insertInbox value
                                    do! insertInbox.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
                                    use updateStream = new NpgsqlCommand("UPDATE fsgg_orchestration.stream SET last_sequence=$2 WHERE persistence_id=$1", connection, transaction)
                                    Sql.add updateStream persistenceId
                                    Sql.add updateStream terminal
                                    do! updateStream.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
                                    do! transaction.CommitAsync cancellationToken
                                    return Appended terminal
                    with
                    | :? PostgresException as pg when pg.SqlState = "40001" || pg.SqlState = "23505" ->
                        return WrongExpectedSequence request.ExpectedSequence
                    | :? InvalidOperationException as invalid when invalid.Message.StartsWith("semantic:", StringComparison.Ordinal) ->
                        return InvalidAppend(invalid.Message.Substring("semantic:".Length))
            }

        member _.Recover(persistenceId, cancellationToken) =
            task {
                try
                    use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                    use! transaction = connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                    use command = new NpgsqlCommand("""
SELECT sequence_number,event_id,schema_version,serializer_version,payload,payload_sha256,effect_change,
       effect_operation_id,effect_kind,effect_generation,effect_workflow_revision,effect_resource_id,effect_payload_sha256,recorded_at
FROM fsgg_orchestration.event WHERE persistence_id=$1 ORDER BY sequence_number
                    """, connection, transaction)
                    Sql.add command persistenceId
                    use! reader = command.ExecuteReaderAsync cancellationToken
                    let events = ResizeArray<SerializedEvent>()
                    let failures = ResizeArray<ReadinessFailure>()
                    let intents = Collections.Generic.Dictionary<Guid, EffectIntent>()
                    let mutable state = FS.GG.Coordination.Core.Orchestration.initial
                    let mutable expected = 1L
                    let mutable keepReading = true
                    while keepReading do
                        let! more = reader.ReadAsync cancellationToken
                        keepReading <- more
                        if more then
                            let sequence = reader.GetInt64 0
                            let schemaVersion = reader.GetInt32 2
                            let serializerVersion = reader.GetString 3
                            let payload = reader.GetFieldValue<byte array> 4
                            let payloadHash = reader.GetString 5
                            if sequence <> expected || Hash.bytes payload <> payloadHash then failures.Add(CorruptRecord(persistenceId, sequence))
                            if not (options.SupportedEventSchemaVersions.Contains schemaVersion) then failures.Add(UnknownEventVersion(persistenceId, sequence, schemaVersion))
                            if not (options.SupportedSerializerVersions.Contains serializerVersion) then failures.Add(UnknownSerializerVersion serializerVersion)
                            let effectChange =
                                match reader.GetInt16 6 with
                                | 0s -> NoEffect
                                | 1s ->
                                    IntentAdded
                                        { OperationId = Id.operation(reader.GetGuid 7)
                                          Kind = Effect.byteToKind(byte(reader.GetInt16 8)) |> Option.defaultValue InspectExternalOperation
                                          Generation = Id.generation(reader.GetInt64 9)
                                          WorkflowRevision = Id.revision(reader.GetInt64 10)
                                          ResourceId = reader.GetString 11
                                          PayloadSha256 = reader.GetString 12 }
                                | 2s -> Settled(Id.operation(reader.GetGuid 7))
                                | _ -> NoEffect
                            match EventEnvelope.tryDecode payload with
                            | Ok coreEvent ->
                                let derived = Effect.derive state coreEvent
                                if not (Effect.equal derived effectChange) then failures.Add(CorruptRecord(persistenceId, sequence))
                                try state <- FS.GG.Coordination.Core.Orchestration.evolve state coreEvent
                                with _ -> failures.Add(CorruptRecord(persistenceId, sequence))
                            | Error _ -> failures.Add(CorruptRecord(persistenceId, sequence))
                            match effectChange with
                            | IntentAdded intent -> intents[Id.operationValue intent.OperationId] <- intent
                            | Settled operationId -> intents.Remove(Id.operationValue operationId) |> ignore
                            | NoEffect -> ()
                            events.Add
                                { PersistenceId = persistenceId; Sequence = sequence; EventId = reader.GetGuid 1
                                  SchemaVersion = schemaVersion; SerializerVersion = serializerVersion; Payload = payload
                                  PayloadSha256 = payloadHash; EffectChange = effectChange; RecordedAt = reader.GetFieldValue<DateTimeOffset> 13 }
                            expected <- sequence + 1L
                    do! reader.DisposeAsync().AsTask()
                    use snapshotCommand = new NpgsqlCommand("SELECT sequence_number,schema_version,payload,payload_sha256 FROM fsgg_orchestration.domain_snapshot WHERE persistence_id=$1 ORDER BY sequence_number DESC LIMIT 1", connection, transaction)
                    Sql.add snapshotCommand persistenceId
                    use! snapshotReader = snapshotCommand.ExecuteReaderAsync cancellationToken
                    let! hasSnapshot = snapshotReader.ReadAsync cancellationToken
                    let snapshot =
                        if not hasSnapshot then None
                        else
                            let sequence = snapshotReader.GetInt64 0
                            let payload = snapshotReader.GetFieldValue<byte array> 2
                            if sequence >= expected || Hash.bytes payload <> snapshotReader.GetString 3 then
                                failures.Add(SnapshotAheadOfJournal persistenceId)
                            Some { PersistenceId = persistenceId; Sequence = sequence; SchemaVersion = snapshotReader.GetInt32 1; Payload = payload; PayloadSha256 = snapshotReader.GetString 3 }
                    do! snapshotReader.DisposeAsync().AsTask()
                    do! transaction.CommitAsync cancellationToken
                    if failures.Count > 0 then return Error(List.ofSeq failures)
                    else
                        let pending = intents.Values |> Seq.toList
                        return Ok { Events = List.ofSeq events; Snapshot = snapshot; UnsettledEffects = pending; RequiresExternalReconciliation = not pending.IsEmpty }
                with exceptionValue -> return Error [ Sql.classifyReadiness exceptionValue ]
            }

        member _.SaveSnapshot(snapshot, cancellationToken) =
            task {
                if snapshot.Sequence <= 0L || not (Hash.valid snapshot.PayloadSha256) || Hash.bytes snapshot.Payload <> snapshot.PayloadSha256.ToLowerInvariant() then
                    return Error(CorruptRecord(snapshot.PersistenceId, snapshot.Sequence))
                elif not (options.SupportedEventSchemaVersions.Contains snapshot.SchemaVersion) then
                    return Error(UnknownEventVersion(snapshot.PersistenceId, snapshot.Sequence, snapshot.SchemaVersion))
                else
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                        use sequenceCommand = new NpgsqlCommand("SELECT last_sequence FROM fsgg_orchestration.stream WHERE persistence_id=$1 FOR SHARE", connection, transaction)
                        Sql.add sequenceCommand snapshot.PersistenceId
                        let! lastObject = sequenceCommand.ExecuteScalarAsync cancellationToken
                        if isNull lastObject || Convert.ToInt64 lastObject < snapshot.Sequence then
                            do! transaction.RollbackAsync cancellationToken
                            return Error(SnapshotAheadOfJournal snapshot.PersistenceId)
                        else
                            use command = new NpgsqlCommand("""
INSERT INTO fsgg_orchestration.domain_snapshot(persistence_id,sequence_number,schema_version,payload,payload_sha256)
VALUES($1,$2,$3,$4,$5) ON CONFLICT(persistence_id,sequence_number) DO NOTHING
                            """, connection, transaction)
                            for value in [ snapshot.PersistenceId :> obj; snapshot.Sequence :> obj; snapshot.SchemaVersion :> obj; snapshot.Payload :> obj; snapshot.PayloadSha256.ToLowerInvariant() :> obj ] do Sql.add command value
                            do! command.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
                            do! transaction.CommitAsync cancellationToken
                            return Ok()
                    with exceptionValue -> return Error(Sql.classifyReadiness exceptionValue)
            }

        member _.SaveProjectionCheckpoint(checkpoint, cancellationToken) =
            task {
                try
                    use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                    use command = new NpgsqlCommand("""
INSERT INTO fsgg_orchestration.projection_checkpoint(projection_id,persistence_id,sequence_number,projection_version)
SELECT $1,$2,$3,$4 WHERE $3 <= COALESCE((SELECT last_sequence FROM fsgg_orchestration.stream WHERE persistence_id=$2),0)
ON CONFLICT(projection_id,persistence_id) DO UPDATE SET sequence_number=excluded.sequence_number,projection_version=excluded.projection_version
WHERE fsgg_orchestration.projection_checkpoint.sequence_number <= excluded.sequence_number
                    """, connection)
                    for value in [ checkpoint.ProjectionId :> obj; checkpoint.PersistenceId :> obj; checkpoint.Sequence :> obj; checkpoint.ProjectionVersion :> obj ] do Sql.add command value
                    let! changed = command.ExecuteNonQueryAsync cancellationToken
                    return if changed = 1 then Ok() else Error(CorruptRecord(checkpoint.PersistenceId, checkpoint.Sequence))
                with exceptionValue -> return Error(Sql.classifyReadiness exceptionValue)
            }

    interface ICandidateStore with
        member _.Put(request, cancellationToken) =
            task {
                let candidate = request.Candidate
                let allowedMediaTypes = set [ "application/vnd.git.bundle"; "application/zip"; "application/zstd" ]
                match locationKey candidate.Location with
                | _ when not (allowedMediaTypes.Contains candidate.MediaType) -> return Error InvalidArchive
                | None -> return Error InvalidArchive
                | Some objectKey when
                    request.Bytes.LongLength > options.MaximumCandidateBytes
                    || request.Bytes.LongLength <> candidate.SizeBytes -> return Error CapacityRefused
                | Some objectKey when
                    not (Hash.valid candidate.ContentSha256)
                    || Hash.bytes request.Bytes <> candidate.ContentSha256.ToLowerInvariant() -> return Error DigestConflict
                | Some objectKey when objectKey <> $"sha256/{candidate.ContentSha256.ToLowerInvariant()}" -> return Error IdentityConflict
                | Some objectKey ->
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use existing = new NpgsqlCommand("SELECT content_sha256,manifest_sha256,receipt_sha256,verified_at,size_bytes FROM fsgg_orchestration.candidate WHERE candidate_id=$1", connection)
                        Sql.add existing (Id.candidateValue candidate.CandidateId)
                        use! existingReader = existing.ExecuteReaderAsync cancellationToken
                        let! found = existingReader.ReadAsync cancellationToken
                        if found then
                            let same = existingReader.GetString 0 = candidate.ContentSha256.ToLowerInvariant() && existingReader.GetString 1 = candidate.ManifestSha256.ToLowerInvariant() && existingReader.GetInt64 4 = candidate.SizeBytes
                            let receipt: CandidateStorageReceipt =
                                { CandidateId = candidate.CandidateId; ContentSha256 = existingReader.GetString 0; ManifestSha256 = existingReader.GetString 1
                                  SizeBytes = existingReader.GetInt64 4; Location = candidate.Location; StoreId = options.StoreId
                                  StoreSchemaVersion = options.RuntimeSchemaVersion; StorageReceiptSha256 = existingReader.GetString 2
                                  VerifiedAt = existingReader.GetFieldValue<DateTimeOffset> 3 }
                            do! existingReader.DisposeAsync().AsTask()
                            return if same then Error(Existing receipt) else Error IdentityConflict
                        else
                            do! existingReader.DisposeAsync().AsTask()
                            use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                            let verifiedAt = DateTimeOffset.UtcNow
                            let receiptSha = receiptHash candidate objectKey verifiedAt
                            use objectCommand = new NpgsqlCommand("INSERT INTO fsgg_orchestration.candidate_object(content_sha256,bytes,size_bytes,created_at,retain_until) VALUES($1,$2,$3,$4,$5) ON CONFLICT(content_sha256) DO NOTHING", connection, transaction)
                            for value in [ candidate.ContentSha256.ToLowerInvariant() :> obj; request.Bytes :> obj; candidate.SizeBytes :> obj; verifiedAt :> obj; candidate.RetainUntil.ToUniversalTime() :> obj ] do Sql.add objectCommand value
                            do! objectCommand.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
                            use candidateCommand = new NpgsqlCommand("""
INSERT INTO fsgg_orchestration.candidate
(candidate_id,content_sha256,manifest_sha256,baseline_sha,head_sha,tree_sha,media_type,size_bytes,object_key,retain_until,receipt_sha256,verified_at)
VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                            """, connection, transaction)
                            for value in [ Id.candidateValue candidate.CandidateId :> obj; candidate.ContentSha256.ToLowerInvariant() :> obj; candidate.ManifestSha256.ToLowerInvariant() :> obj
                                           candidate.BaselineSha :> obj; candidate.HeadSha :> obj; candidate.TreeSha :> obj; candidate.MediaType :> obj; candidate.SizeBytes :> obj
                                           objectKey :> obj; candidate.RetainUntil.ToUniversalTime() :> obj; receiptSha :> obj; verifiedAt :> obj ] do Sql.add candidateCommand value
                            do! candidateCommand.ExecuteNonQueryAsync cancellationToken |> TaskEx.discard
                            do! transaction.CommitAsync cancellationToken
                            let! readBack = readCandidate candidate.CandidateId cancellationToken
                            match readBack with
                            | Ok stored when
                                stored.Bytes = request.Bytes
                                && stored.Candidate.CandidateId = candidate.CandidateId
                                && stored.Candidate.ContentSha256 = candidate.ContentSha256.ToLowerInvariant()
                                && stored.Candidate.ManifestSha256 = candidate.ManifestSha256.ToLowerInvariant()
                                && stored.Candidate.BaselineSha = candidate.BaselineSha
                                && stored.Candidate.HeadSha = candidate.HeadSha
                                && stored.Candidate.TreeSha = candidate.TreeSha
                                && stored.Candidate.MediaType = candidate.MediaType
                                && stored.Candidate.SizeBytes = candidate.SizeBytes
                                && stored.Candidate.Location = candidate.Location ->
                                return Ok
                                    { CandidateId = candidate.CandidateId; ContentSha256 = candidate.ContentSha256.ToLowerInvariant(); ManifestSha256 = candidate.ManifestSha256.ToLowerInvariant()
                                      SizeBytes = candidate.SizeBytes; Location = candidate.Location; StoreId = options.StoreId; StoreSchemaVersion = options.RuntimeSchemaVersion
                                      StorageReceiptSha256 = receiptSha; VerifiedAt = verifiedAt }
                            | _ -> return Error DigestConflict
                    with
                    | :? PostgresException as pg when pg.SqlState.StartsWith("53", StringComparison.Ordinal) -> return Error CapacityRefused
                    | _ -> return Error CapacityRefused
            }

        member _.Read(candidateId, cancellationToken) = readCandidate candidateId cancellationToken

        member _.Quarantine(candidateId, reason, cancellationToken) =
            task {
                if String.IsNullOrWhiteSpace reason then return Error "quarantine-reason-required"
                else
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use command = new NpgsqlCommand("UPDATE fsgg_orchestration.candidate SET quarantined_reason=$2 WHERE candidate_id=$1", connection)
                        Sql.add command (Id.candidateValue candidateId)
                        Sql.add command reason
                        let! changed = command.ExecuteNonQueryAsync cancellationToken
                        return if changed = 1 then Ok() else Error "candidate-not-found"
                    with _ -> return Error "candidate-store-unavailable"
            }

        member _.CleanupUnreferenced(olderThan, maximum, cancellationToken) =
            task {
                if maximum <= 0 then return 0
                else
                    use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                    use! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                    use command = new NpgsqlCommand("""
WITH expired_staging AS (
  SELECT upload_id FROM fsgg_orchestration.candidate_upload_staging
  WHERE created_at < $1 ORDER BY created_at,upload_id FOR UPDATE SKIP LOCKED LIMIT $2
), deleted_staging AS (
  DELETE FROM fsgg_orchestration.candidate_upload_staging s USING expired_staging e WHERE s.upload_id=e.upload_id RETURNING 1
), expired_objects AS (
  SELECT o.content_sha256 FROM fsgg_orchestration.candidate_object o
  WHERE o.created_at < $1 AND NOT EXISTS(SELECT 1 FROM fsgg_orchestration.candidate c WHERE c.content_sha256=o.content_sha256)
  ORDER BY o.created_at,o.content_sha256 FOR UPDATE SKIP LOCKED LIMIT $2
), deleted_objects AS (
  DELETE FROM fsgg_orchestration.candidate_object o USING expired_objects e WHERE o.content_sha256=e.content_sha256 RETURNING 1
)
SELECT (SELECT count(*) FROM deleted_staging)+(SELECT count(*) FROM deleted_objects)
                    """, connection, transaction)
                    Sql.add command (olderThan.ToUniversalTime())
                    Sql.add command maximum
                    let! count = command.ExecuteScalarAsync cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return Convert.ToInt32 count
            }

    interface IBackupReconciler with
        member _.BackupIdentity = options.BackupIdentity
        member _.ReconcileGenerationsRevocationsAndEffects cancellationToken =
            task {
                let journal = (PostgreSqlStore(options) :> IJournalStore)
                let! readiness = journal.CheckReadiness cancellationToken
                match readiness with
                | Error failures -> return Error failures
                | Ok() ->
                    use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                    use command = new NpgsqlCommand("""
SELECT count(*) FROM (
  SELECT DISTINCT ON (persistence_id,effect_operation_id)
         persistence_id,effect_operation_id,effect_change
  FROM fsgg_orchestration.event
  WHERE effect_operation_id IS NOT NULL
  ORDER BY persistence_id,effect_operation_id,sequence_number DESC
) latest WHERE effect_change=1
                    """, connection)
                    let! count = command.ExecuteScalarAsync cancellationToken
                    return
                        if Convert.ToInt64 count = 0L then Ok()
                        else Error [ BackupRequiresReconciliation "unsettled-external-effects" ]
            }

type AkkaStoredEvent = { Envelope: byte array }

[<RequireQualifiedAccess>]
module AkkaPersistence =
    let configuration (connectionString: string) =
        let pluginConnection = $"{connectionString};Search Path=fsgg_orchestration"
        ConfigurationFactory.ParseString($$"""
akka.actor.allow-java-serialization = off
akka.remote.artery.enabled = off
akka.persistence.journal.plugin = "akka.persistence.journal.sql"
akka.persistence.journal.sql {
  class = "Akka.Persistence.Sql.Journal.SqlWriteJournal, Akka.Persistence.Sql"
  connection-string = "{{pluginConnection}}"
  provider-name = "PostgreSQL"
  auto-initialize = off
}
akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.sql"
akka.persistence.snapshot-store.sql {
  class = "Akka.Persistence.Sql.Snapshot.SqlSnapshotStore, Akka.Persistence.Sql"
  connection-string = "{{pluginConnection}}"
  provider-name = "PostgreSQL"
  auto-initialize = off
}
        """)
