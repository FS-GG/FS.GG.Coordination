namespace FS.GG.Coordination.Orchestration.PostgreSql

open System
open System.Data
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Observer

exception private ObserverGateException of ObserverRecoveryFailure list
exception private ObserverAppendException of string

module private ObserverStoreSupport =
    let hash (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let add (command: NpgsqlCommand) value = command.Parameters.AddWithValue(value) |> ignore

    let mapRootFailure = function
        | StoreUnavailable value -> ObserverStoreUnavailable value
        | ReadOnlyStore -> ObserverStoreReadOnly
        | CapacityUnavailable -> ObserverCapacityUnavailable
        | MigrationInterrupted value -> ObserverMigrationInterrupted value
        | IncompatibleDowngrade(databaseVersion, runtimeVersion) -> ObserverIncompatibleDowngrade(databaseVersion, runtimeVersion)
        | BackupRequiresReconciliation value -> ObserverBackupRequiresReconciliation value
        | failure -> ObserverStoreUnavailable(sprintf "root-store-%A" failure)

    let classify (exceptionValue: exn) =
        match exceptionValue with
        | :? PostgresException as pg when pg.SqlState = "25006" -> ObserverStoreReadOnly
        | :? PostgresException as pg when pg.SqlState.StartsWith("53", StringComparison.Ordinal) -> ObserverCapacityUnavailable
        | :? PostgresException as pg -> ObserverStoreUnavailable $"postgres-sqlstate-{pg.SqlState}"
        | :? NpgsqlException -> ObserverStoreUnavailable "postgres-unavailable"
        | _ -> ObserverStoreUnavailable "observer-persistence-failure"

[<RequireQualifiedAccess>]
module PostgreSqlObserverSchema =
    let migrate (dataSource: NpgsqlDataSource) cancellationToken =
        task {
            let assembly = Assembly.GetExecutingAssembly()
            let resource =
                assembly.GetManifestResourceNames()
                |> Array.tryFind (fun name -> name.EndsWith("ObserverMigration.sql", StringComparison.Ordinal))
                |> Option.defaultWith (fun () -> invalidOp "embedded observer migration missing")
            use stream = assembly.GetManifestResourceStream resource
            use reader = new StreamReader(stream)
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            use command = new NpgsqlCommand(reader.ReadToEnd(), connection, transaction)
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            do! transaction.CommitAsync cancellationToken
        }

type PostgreSqlObserverStore(options: StoreOptions) =
    let gate (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("""
SELECT r.schema_version,r.migration_state,r.backup_identity::text,r.generation_fence,
       o.schema_version,o.migration_state,o.backup_identity::text,current_setting('transaction_read_only')
FROM fsgg_orchestration.store_metadata r
JOIN fsgg_orchestration.observer_store_metadata o ON o.singleton=r.singleton
WHERE r.singleton FOR SHARE OF r,o
            """, connection, transaction)
            use! reader = command.ExecuteReaderAsync cancellationToken
            let! found = reader.ReadAsync cancellationToken
            if not found then return [ ObserverMigrationInterrupted "observer-or-root-metadata-missing" ]
            else
                let failures = ResizeArray<ObserverRecoveryFailure>()
                let rootVersion = reader.GetInt32 0
                if rootVersion > options.RuntimeSchemaVersion then failures.Add(ObserverIncompatibleDowngrade(rootVersion, options.RuntimeSchemaVersion))
                elif rootVersion < options.RuntimeSchemaVersion then failures.Add(ObserverMigrationInterrupted $"root-v{rootVersion}-runtime-v{options.RuntimeSchemaVersion}")
                if reader.GetString 1 <> "ready" then failures.Add(ObserverMigrationInterrupted(reader.GetString 1))
                let rootIdentity = reader.GetString 2
                if not (String.Equals(rootIdentity, options.BackupIdentity, StringComparison.OrdinalIgnoreCase)) then failures.Add(ObserverBackupRequiresReconciliation "backup-identity-mismatch")
                if reader.GetInt64 3 < options.MinimumGenerationFence then failures.Add(ObserverBackupRequiresReconciliation "generation-fence-regressed")
                let observerVersion = reader.GetInt32 4
                if observerVersion > 1 then failures.Add(ObserverIncompatibleDowngrade(observerVersion, 1))
                elif observerVersion < 1 then failures.Add(ObserverMigrationInterrupted $"observer-v{observerVersion}-runtime-v1")
                if reader.GetString 5 <> "ready" then failures.Add(ObserverMigrationInterrupted(reader.GetString 5))
                if not (String.Equals(reader.GetString 6, rootIdentity, StringComparison.OrdinalIgnoreCase)) then failures.Add(ObserverBackupRequiresReconciliation "observer-backup-identity-mismatch")
                if reader.GetString 7 = "on" then failures.Add ObserverStoreReadOnly
                return List.ofSeq failures
        }

    let loadEvents (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (observerId: string) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("SELECT sequence_number,event_id,schema_version,serializer_version,payload,payload_sha256,recorded_at FROM fsgg_orchestration.observer_event WHERE observer_id=$1 ORDER BY sequence_number", connection, transaction)
            ObserverStoreSupport.add command observerId
            use! reader = command.ExecuteReaderAsync cancellationToken
            let events = ResizeArray<ObserverStoredEvent>()
            let failures = ResizeArray<ObserverRecoveryFailure>()
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
                    if sequence <> expected || ObserverStoreSupport.hash payload <> reader.GetString 5 then failures.Add(ObserverCorruptRecord(observerId, sequence))
                    if schemaVersion <> ObserverEventCodec.schemaVersion then failures.Add(ObserverUnknownEventVersion(observerId, sequence, schemaVersion))
                    if serializerVersion <> ObserverEventCodec.serializerVersion then failures.Add(ObserverUnknownSerializerVersion serializerVersion)
                    match ObserverEventCodec.tryDecode payload with
                    | Error _ -> failures.Add(ObserverCorruptRecord(observerId, sequence))
                    | Ok eventValue ->
                        events.Add
                            { ObserverId = observerId; Sequence = sequence; EventId = reader.GetGuid 1
                              SchemaVersion = schemaVersion; SerializerVersion = serializerVersion
                              Event = eventValue; RecordedAt = reader.GetFieldValue<DateTimeOffset> 6 }
                    expected <- sequence + 1L
            do! reader.DisposeAsync().AsTask()
            use headCommand = new NpgsqlCommand("SELECT last_sequence FROM fsgg_orchestration.observer_stream WHERE observer_id=$1", connection, transaction)
            ObserverStoreSupport.add headCommand observerId
            let! headValue = headCommand.ExecuteScalarAsync cancellationToken
            let tail = expected - 1L
            if isNull headValue then
                if events.Count > 0 then failures.Add(ObserverCorruptRecord(observerId, tail))
            elif Convert.ToInt64 headValue <> tail then
                failures.Add(ObserverCorruptRecord(observerId, max tail (Convert.ToInt64 headValue)))
            if failures.Count > 0 then return Error(List.ofSeq failures)
            else
                try return Ok(List.ofSeq events)
                with _ -> return Error [ ObserverCorruptRecord(observerId, max 1L (expected - 1L)) ]
        }

    interface IObserverJournalStore with
        member _.AppendObserver(request, cancellationToken) =
            task {
                if String.IsNullOrWhiteSpace request.ObserverId || Id.commandValue request.Command.CommandId = Guid.Empty
                   || request.Command.ExpectedSequence < 0L then return ObserverInvalidAppend "invalid-observer-command"
                else
                    let bodyHash = Observer.commandSha256 request.Command
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use! transaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                        let! gateFailures = gate connection transaction cancellationToken
                        if not gateFailures.IsEmpty then raise (ObserverGateException gateFailures)
                        use ensure = new NpgsqlCommand("INSERT INTO fsgg_orchestration.observer_stream(observer_id,last_sequence) VALUES($1,0) ON CONFLICT DO NOTHING", connection, transaction)
                        ObserverStoreSupport.add ensure request.ObserverId
                        let! _ = ensure.ExecuteNonQueryAsync cancellationToken
                        use duplicate = new NpgsqlCommand("SELECT body_sha256,terminal_sequence FROM fsgg_orchestration.observer_inbox WHERE observer_id=$1 AND command_id=$2", connection, transaction)
                        ObserverStoreSupport.add duplicate request.ObserverId
                        ObserverStoreSupport.add duplicate (Id.commandValue request.Command.CommandId)
                        use! duplicateReader = duplicate.ExecuteReaderAsync cancellationToken
                        let! found = duplicateReader.ReadAsync cancellationToken
                        let duplicateResult =
                            if found then
                                if String.Equals(duplicateReader.GetString 0, bodyHash, StringComparison.OrdinalIgnoreCase) then Some(ObserverDuplicate(duplicateReader.GetInt64 1))
                                else Some ObserverConflict
                            else None
                        do! duplicateReader.DisposeAsync().AsTask()
                        match duplicateResult with
                        | Some result ->
                            let terminal = match result with ObserverDuplicate value -> value | _ -> 0L
                            let! history = loadEvents connection transaction request.ObserverId cancellationToken
                            match history with
                            | Error failures -> raise (ObserverGateException failures)
                            | Ok events when terminal > int64 events.Length -> raise (ObserverGateException [ ObserverCorruptRecord(request.ObserverId, terminal) ])
                            | Ok _ -> ()
                            do! transaction.RollbackAsync cancellationToken
                            return result
                        | None when request.Events.IsEmpty ->
                            do! transaction.RollbackAsync cancellationToken
                            return ObserverInvalidAppend "new-command-requires-events"
                        | None ->
                            use sequence = new NpgsqlCommand("SELECT last_sequence FROM fsgg_orchestration.observer_stream WHERE observer_id=$1 FOR UPDATE", connection, transaction)
                            ObserverStoreSupport.add sequence request.ObserverId
                            let! current = sequence.ExecuteScalarAsync cancellationToken
                            let actual = Convert.ToInt64 current
                            if actual <> request.Command.ExpectedSequence then
                                do! transaction.RollbackAsync cancellationToken
                                return ObserverWrongExpectedSequence actual
                            else
                                let! prior = loadEvents connection transaction request.ObserverId cancellationToken
                                let priorEvents = match prior with Ok value -> value | Error failures -> raise (ObserverGateException failures)
                                let state = priorEvents |> List.map _.Event |> Observer.replay
                                if state.Sequence <> actual then raise (ObserverAppendException "observer-state-head-mismatch")
                                let decision = Observer.decide request.ReceivedAt state request.Command
                                let requestedEvents = request.Events |> List.map _.Event
                                if decision.Receipt.Disposition <> ObserverAccepted || decision.Events <> requestedEvents then
                                    raise (ObserverAppendException "events-do-not-match-command-decision")
                                match requestedEvents with
                                | SessionOpened(sessionId, _, _) :: _ when ObserverJournal.observerId sessionId <> request.ObserverId ->
                                    raise (ObserverAppendException "observer-session-identity-mismatch")
                                | _ -> ()
                                request.Events
                                |> List.iteri (fun index value ->
                                    if value.ObserverId <> request.ObserverId
                                       || value.Sequence <> actual + int64 index + 1L
                                       || value.SchemaVersion <> ObserverEventCodec.schemaVersion
                                       || value.SerializerVersion <> ObserverEventCodec.serializerVersion
                                       || value.RecordedAt <> request.ReceivedAt then
                                        raise (ObserverAppendException "invalid-stored-event"))
                                for eventValue in request.Events do
                                    let payload = ObserverEventCodec.encode eventValue.Event
                                    use insert = new NpgsqlCommand("INSERT INTO fsgg_orchestration.observer_event(observer_id,sequence_number,event_id,schema_version,serializer_version,payload,payload_sha256,recorded_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", connection, transaction)
                                    for value in [ request.ObserverId :> obj; eventValue.Sequence :> obj; eventValue.EventId :> obj; eventValue.SchemaVersion :> obj
                                                   eventValue.SerializerVersion :> obj; payload :> obj; ObserverStoreSupport.hash payload :> obj; eventValue.RecordedAt.ToUniversalTime() :> obj ] do ObserverStoreSupport.add insert value
                                    let! _ = insert.ExecuteNonQueryAsync cancellationToken
                                    ()
                                let terminal = request.Events |> List.last |> _.Sequence
                                use inbox = new NpgsqlCommand("INSERT INTO fsgg_orchestration.observer_inbox(observer_id,command_id,body_sha256,terminal_sequence,received_at) VALUES($1,$2,$3,$4,$5)", connection, transaction)
                                for value in [ request.ObserverId :> obj; Id.commandValue request.Command.CommandId :> obj; bodyHash :> obj; terminal :> obj; request.ReceivedAt.ToUniversalTime() :> obj ] do ObserverStoreSupport.add inbox value
                                let! _ = inbox.ExecuteNonQueryAsync cancellationToken
                                use update = new NpgsqlCommand("UPDATE fsgg_orchestration.observer_stream SET last_sequence=$2 WHERE observer_id=$1", connection, transaction)
                                ObserverStoreSupport.add update request.ObserverId
                                ObserverStoreSupport.add update terminal
                                let! _ = update.ExecuteNonQueryAsync cancellationToken
                                do! transaction.CommitAsync cancellationToken
                                return ObserverAppended terminal
                    with
                    | ObserverGateException failures -> return ObserverAppendUnavailable(sprintf "%A" failures)
                    | ObserverAppendException reason -> return ObserverInvalidAppend reason
                    | :? PostgresException as pg when pg.SqlState = "40001" || pg.SqlState = "23505" -> return ObserverAppendUnavailable "concurrent-append-retry"
                    | exceptionValue -> return ObserverAppendUnavailable(match ObserverStoreSupport.classify exceptionValue with ObserverStoreUnavailable reason -> reason | failure -> sprintf "%A" failure)
            }

        member _.RecoverObserver(observerId, cancellationToken) =
            task {
                if String.IsNullOrWhiteSpace observerId then return Error [ ObserverStoreUnavailable "observer-id-required" ]
                else
                    try
                        use! connection = options.DataSource.OpenConnectionAsync cancellationToken
                        use! transaction = connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                        let! gateFailures = gate connection transaction cancellationToken
                        if not gateFailures.IsEmpty then raise (ObserverGateException gateFailures)
                        let! loaded = loadEvents connection transaction observerId cancellationToken
                        match loaded with
                        | Error failures -> return Error failures
                        | Ok events ->
                            try
                                let state = events |> List.map _.Event |> Observer.replay
                                do! transaction.CommitAsync cancellationToken
                                return Ok { Events = events; State = state }
                            with _ -> return Error [ ObserverCorruptRecord(observerId, max 1L (int64 events.Length)) ]
                    with
                    | ObserverGateException failures -> return Error failures
                    | exceptionValue -> return Error [ ObserverStoreSupport.classify exceptionValue ]
            }
