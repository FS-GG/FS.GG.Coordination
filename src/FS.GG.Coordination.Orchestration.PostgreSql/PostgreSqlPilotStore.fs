namespace FS.GG.Coordination.Orchestration.PostgreSql

open System
open System.Data
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open Npgsql
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Pilot

exception private PilotGateException of PilotRecoveryFailure list
exception private PilotAppendException of string

module private PilotStoreSupport =
    let hash (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let add (command: NpgsqlCommand) value = command.Parameters.AddWithValue(value) |> ignore

    let mapRootFailure = function
        | StoreUnavailable value -> PilotStoreUnavailable value
        | ReadOnlyStore -> PilotStoreReadOnly
        | CapacityUnavailable -> PilotStoreUnavailable "capacity-unavailable"
        | MigrationInterrupted value -> PilotMigrationInterrupted value
        | IncompatibleDowngrade(databaseVersion, runtimeVersion) -> PilotIncompatibleDowngrade(databaseVersion, runtimeVersion)
        | BackupRequiresReconciliation value -> PilotBackupRequiresReconciliation value
        | failure -> PilotStoreUnavailable(sprintf "root-store-%A" failure)

    let classify (exceptionValue: exn) =
        match exceptionValue with
        | :? PostgresException as pg when pg.SqlState = "25006" -> PilotStoreReadOnly
        | :? PostgresException as pg when pg.SqlState.StartsWith("53", StringComparison.Ordinal) -> PilotStoreUnavailable $"postgres-sqlstate-{pg.SqlState}"
        | :? PostgresException as pg -> PilotStoreUnavailable $"postgres-sqlstate-{pg.SqlState}"
        | :? NpgsqlException -> PilotStoreUnavailable "postgres-unavailable"
        | _ -> PilotStoreUnavailable "pilot-persistence-failure"

[<RequireQualifiedAccess>]
module PostgreSqlPilotSchema =
    let migrate (dataSource: NpgsqlDataSource) cancellationToken =
        task {
            let assembly = Assembly.GetExecutingAssembly()
            let resource =
                assembly.GetManifestResourceNames()
                |> Array.tryFind (fun name -> name.EndsWith("PilotMigration.sql", StringComparison.Ordinal))
                |> Option.defaultWith (fun () -> invalidOp "embedded pilot migration missing")
            use stream = assembly.GetManifestResourceStream resource
            use reader = new StreamReader(stream)
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            use command = new NpgsqlCommand(reader.ReadToEnd(), connection, transaction)
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            do! transaction.CommitAsync cancellationToken
        }

type PostgreSqlPilotStore(options: StoreOptions) =
    let knownPermits = ConcurrentDictionary<Guid, byte>()
    let reconnectRequired = ConcurrentDictionary<Guid, byte>()
    let gate (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("""
SELECT r.schema_version,r.migration_state,r.backup_identity::text,r.generation_fence,
       o.schema_version,o.migration_state,o.backup_identity::text,current_setting('transaction_read_only')
FROM fsgg_orchestration.store_metadata r
JOIN fsgg_orchestration.pilot_store_metadata o ON o.singleton=r.singleton
WHERE r.singleton FOR SHARE OF r,o
            """, connection, transaction)
            use! reader = command.ExecuteReaderAsync cancellationToken
            let! found = reader.ReadAsync cancellationToken
            if not found then return [ PilotMigrationInterrupted "pilot-or-root-metadata-missing" ]
            else
                let failures = ResizeArray<PilotRecoveryFailure>()
                let rootVersion = reader.GetInt32 0
                if rootVersion > options.RuntimeSchemaVersion then failures.Add(PilotIncompatibleDowngrade(rootVersion, options.RuntimeSchemaVersion))
                elif rootVersion < options.RuntimeSchemaVersion then failures.Add(PilotMigrationInterrupted $"root-v{rootVersion}-runtime-v{options.RuntimeSchemaVersion}")
                if reader.GetString 1 <> "ready" then failures.Add(PilotMigrationInterrupted(reader.GetString 1))
                let rootIdentity = reader.GetString 2
                if not (String.Equals(rootIdentity, options.BackupIdentity, StringComparison.OrdinalIgnoreCase)) then failures.Add(PilotBackupRequiresReconciliation "backup-identity-mismatch")
                if reader.GetInt64 3 < options.MinimumGenerationFence then failures.Add(PilotBackupRequiresReconciliation "generation-fence-regressed")
                let observerVersion = reader.GetInt32 4
                if observerVersion > 1 then failures.Add(PilotIncompatibleDowngrade(observerVersion, 1))
                elif observerVersion < 1 then failures.Add(PilotMigrationInterrupted $"pilot-v{observerVersion}-runtime-v1")
                if reader.GetString 5 <> "ready" then failures.Add(PilotMigrationInterrupted(reader.GetString 5))
                if not (String.Equals(reader.GetString 6, rootIdentity, StringComparison.OrdinalIgnoreCase)) then failures.Add(PilotBackupRequiresReconciliation "pilot-backup-identity-mismatch")
                if reader.GetString 7 = "on" then failures.Add PilotStoreReadOnly
                return List.ofSeq failures
        }

    let loadEvents (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (permitId: Guid) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("SELECT sequence_number,event_id,schema_version,serializer_version,payload,payload_sha256,recorded_at FROM fsgg_orchestration.pilot_event WHERE permit_id=$1 ORDER BY sequence_number", connection, transaction)
            PilotStoreSupport.add command permitId
            use! reader = command.ExecuteReaderAsync cancellationToken
            let events = ResizeArray<PilotStoredEvent>()
            let failures = ResizeArray<PilotRecoveryFailure>()
            let mutable expected = 1L
            let mutable reading = true
            while reading do
                let! more = reader.ReadAsync cancellationToken
                reading <- more
                if more then
                    let sequence = reader.GetInt64 0
                    let schemaVersion = reader.GetInt32 2
                    let serializerVersion = reader.GetString 3
                    let payload = reader.GetFieldValue<byte array> 4
                    if sequence <> expected || PilotStoreSupport.hash payload <> reader.GetString 5 then failures.Add(PilotCorruptRecord(permitId, sequence))
                    if schemaVersion <> PilotCodec.schemaVersion then failures.Add(PilotUnknownEventVersion(permitId, sequence, schemaVersion))
                    if serializerVersion <> PilotCodec.serializerVersion then failures.Add(PilotUnknownSerializerVersion serializerVersion)
                    match PilotCodec.tryDecode payload with
                    | Error _ -> failures.Add(PilotCorruptRecord(permitId, sequence))
                    | Ok eventValue ->
                        events.Add
                            { PermitId = permitId; Sequence = sequence; EventId = reader.GetGuid 1
                              SchemaVersion = schemaVersion; SerializerVersion = serializerVersion
                              Event = eventValue; RecordedAt = reader.GetFieldValue<DateTimeOffset> 6 }
                    expected <- sequence + 1L
            do! reader.DisposeAsync().AsTask()
            use headCommand = new NpgsqlCommand("SELECT last_sequence FROM fsgg_orchestration.pilot_stream WHERE permit_id=$1", connection, transaction)
            PilotStoreSupport.add headCommand permitId
            let! headValue = headCommand.ExecuteScalarAsync cancellationToken
            let tail = expected - 1L
            if isNull headValue then
                if events.Count > 0 then failures.Add(PilotCorruptRecord(permitId, tail))
            elif Convert.ToInt64 headValue <> tail then
                failures.Add(PilotCorruptRecord(permitId, max tail (Convert.ToInt64 headValue)))
            if failures.Count > 0 then return Error(List.ofSeq failures)
            else
                try return Ok(List.ofSeq events)
                with _ -> return Error [ PilotCorruptRecord(permitId, max 1L (expected - 1L)) ]
        }

    interface IPilotJournalStore with
        member _.AppendPilot(request, cancellationToken) =
            task {
                if request.PermitId = Guid.Empty || request.Command.CommandId = Guid.Empty
                   || request.Command.ExpectedSequence < 0L then return PilotInvalidAppend "invalid-pilot-command"
                else
                    let bodyHash = PilotCodec.commandSha256 request.Command
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                        let! gateFailures = gate connection transaction cancellationToken
                        if not gateFailures.IsEmpty then raise (PilotGateException gateFailures)
                        use ensure = new NpgsqlCommand("INSERT INTO fsgg_orchestration.pilot_stream(permit_id,last_sequence) VALUES($1,0) ON CONFLICT DO NOTHING", connection, transaction)
                        PilotStoreSupport.add ensure request.PermitId
                        let! _ = ensure.ExecuteNonQueryAsync cancellationToken
                        use duplicate = new NpgsqlCommand("SELECT body_sha256,terminal_sequence FROM fsgg_orchestration.pilot_inbox WHERE permit_id=$1 AND command_id=$2", connection, transaction)
                        PilotStoreSupport.add duplicate request.PermitId
                        PilotStoreSupport.add duplicate request.Command.CommandId
                        use! duplicateReader = duplicate.ExecuteReaderAsync cancellationToken
                        let! found = duplicateReader.ReadAsync cancellationToken
                        let duplicateResult =
                            if found then
                                if String.Equals(duplicateReader.GetString 0, bodyHash, StringComparison.OrdinalIgnoreCase) then Some(PilotDuplicate(duplicateReader.GetInt64 1))
                                else Some PilotConflict
                            else None
                        do! duplicateReader.DisposeAsync().AsTask()
                        match duplicateResult with
                        | Some result ->
                            let terminal = match result with PilotDuplicate value -> value | _ -> 0L
                            let! history = loadEvents connection transaction request.PermitId cancellationToken
                            match history with
                            | Error failures -> raise (PilotGateException failures)
                            | Ok events when terminal > int64 events.Length -> raise (PilotGateException [ PilotCorruptRecord(request.PermitId, terminal) ])
                            | Ok _ -> ()
                            do! transaction.RollbackAsync cancellationToken
                            return result
                        | None when request.Events.IsEmpty ->
                            do! transaction.RollbackAsync cancellationToken
                            return PilotInvalidAppend "new-command-requires-events"
                        | None ->
                            use sequence = new NpgsqlCommand("SELECT last_sequence FROM fsgg_orchestration.pilot_stream WHERE permit_id=$1 FOR UPDATE", connection, transaction)
                            PilotStoreSupport.add sequence request.PermitId
                            let! current = sequence.ExecuteScalarAsync cancellationToken
                            let actual = Convert.ToInt64 current
                            if actual <> request.Command.ExpectedSequence then
                                do! transaction.RollbackAsync cancellationToken
                                return PilotWrongExpectedSequence actual
                            else
                                if actual > 0L && not (knownPermits.ContainsKey request.PermitId) then
                                    raise (PilotAppendException "recover-before-new-command")
                                let! prior = loadEvents connection transaction request.PermitId cancellationToken
                                let priorEvents = match prior with Ok value -> value | Error failures -> raise (PilotGateException failures)
                                let state =
                                    priorEvents |> List.map _.Event |> Pilot.validateReplay request.PermitId
                                    |> Result.defaultWith (fun _ -> raise (PilotGateException [ PilotCorruptRecord(request.PermitId, max 1L actual) ]))
                                if state.Sequence <> actual then raise (PilotAppendException "pilot-state-head-mismatch")
                                let decisionState =
                                    if reconnectRequired.ContainsKey request.PermitId then { state with ReadbackCurrent = false }
                                    else state
                                let decision = Pilot.decide request.ReceivedAt decisionState request.Command
                                let requestedEvents = request.Events |> List.map _.Event
                                match decision with
                                | Error reason -> raise (PilotAppendException reason)
                                | Ok events when events <> requestedEvents -> raise (PilotAppendException "events-do-not-match-command-decision")
                                | Ok _ -> ()
                                match requestedEvents with
                                | PermitIssued permit :: _ when permit.PermitId <> request.PermitId ->
                                    raise (PilotAppendException "permit-stream-identity-mismatch")
                                | _ -> ()
                                request.Events
                                |> List.iteri (fun index value ->
                                    if value.PermitId <> request.PermitId
                                       || value.Sequence <> actual + int64 index + 1L
                                       || value.SchemaVersion <> PilotCodec.schemaVersion
                                       || value.SerializerVersion <> PilotCodec.serializerVersion
                                       || value.RecordedAt <> request.ReceivedAt then
                                        raise (PilotAppendException "invalid-stored-event"))
                                for eventValue in request.Events do
                                    let payload = PilotCodec.encode eventValue.Event
                                    use insert = new NpgsqlCommand("INSERT INTO fsgg_orchestration.pilot_event(permit_id,sequence_number,event_id,schema_version,serializer_version,payload,payload_sha256,recorded_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", connection, transaction)
                                    for value in [ request.PermitId :> obj; eventValue.Sequence :> obj; eventValue.EventId :> obj; eventValue.SchemaVersion :> obj
                                                   eventValue.SerializerVersion :> obj; payload :> obj; PilotStoreSupport.hash payload :> obj; eventValue.RecordedAt.ToUniversalTime() :> obj ] do PilotStoreSupport.add insert value
                                    let! _ = insert.ExecuteNonQueryAsync cancellationToken
                                    ()
                                let terminal = request.Events |> List.last |> _.Sequence
                                use inbox = new NpgsqlCommand("INSERT INTO fsgg_orchestration.pilot_inbox(permit_id,command_id,body_sha256,terminal_sequence,received_at) VALUES($1,$2,$3,$4,$5)", connection, transaction)
                                for value in [ request.PermitId :> obj; request.Command.CommandId :> obj; bodyHash :> obj; terminal :> obj; request.ReceivedAt.ToUniversalTime() :> obj ] do PilotStoreSupport.add inbox value
                                let! _ = inbox.ExecuteNonQueryAsync cancellationToken
                                use update = new NpgsqlCommand("UPDATE fsgg_orchestration.pilot_stream SET last_sequence=$2 WHERE permit_id=$1", connection, transaction)
                                PilotStoreSupport.add update request.PermitId
                                PilotStoreSupport.add update terminal
                                let! _ = update.ExecuteNonQueryAsync cancellationToken
                                do! transaction.CommitAsync cancellationToken
                                knownPermits[request.PermitId] <- 0uy
                                if requestedEvents |> List.exists (function ReadbackReconnected _ -> true | _ -> false) then
                                    reconnectRequired.TryRemove request.PermitId |> ignore
                                return PilotAppended terminal
                    with
                    | PilotGateException failures -> return PilotAppendUnavailable(sprintf "%A" failures)
                    | PilotAppendException reason -> return PilotInvalidAppend reason
                    | :? PostgresException as pg when pg.SqlState = "40001" || pg.SqlState = "23505" -> return PilotAppendUnavailable "concurrent-append-retry"
                    | exceptionValue -> return PilotAppendUnavailable(match PilotStoreSupport.classify exceptionValue with PilotStoreUnavailable reason -> reason | failure -> sprintf "%A" failure)
            }

        member _.RecoverPilot(permitId, cancellationToken) =
            task {
                if permitId = Guid.Empty then return Error [ PilotStoreUnavailable "permit-id-required" ]
                else
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use! transaction = connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                        let! gateFailures = gate connection transaction cancellationToken
                        if not gateFailures.IsEmpty then raise (PilotGateException gateFailures)
                        let! loaded = loadEvents connection transaction permitId cancellationToken
                        match loaded with
                        | Error failures -> return Error failures
                        | Ok events ->
                            try
                                let state =
                                    events |> List.map _.Event |> Pilot.validateReplay permitId
                                    |> Result.defaultWith (fun _ -> raise (PilotGateException [ PilotCorruptRecord(permitId, max 1L (int64 events.Length)) ]))
                                knownPermits[permitId] <- 0uy
                                let recoveredState =
                                    match state.Permit with
                                    | Some permit when state.AssignedOwnerId = permit.PilotOwnerId ->
                                        reconnectRequired[permitId] <- 0uy
                                        { state with ReadbackCurrent = false }
                                    | _ -> state
                                do! transaction.CommitAsync cancellationToken
                                return Ok { Events = events; State = recoveredState }
                            with _ -> return Error [ PilotCorruptRecord(permitId, max 1L (int64 events.Length)) ]
                    with
                    | PilotGateException failures -> return Error failures
                    | exceptionValue -> return Error [ PilotStoreSupport.classify exceptionValue ]
            }
