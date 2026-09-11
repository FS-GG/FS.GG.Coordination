namespace FS.GG.Coordination.Orchestration.PostgreSql

open System
open System.Data
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Runner.Protocol
open FS.GG.Coordination.Orchestration.Pilot

type ExecutorCommandAppend = CommandPersisted of int64 | CommandDuplicate of int64 | CommandConflict | CommandRefused of string
type SubscriptionAppend = SubscriptionReserved | SubscriptionDuplicate | SubscriptionCapacityRefused | SubscriptionAuthorityRefused | SubscriptionConflict

type private StoredExecutorCommand =
    { CommandId:Guid; BodySha256:string; Kind:string; AssignmentId:Guid; AttemptId:Guid
      Generation:int64; ExpectedRevision:int64; RecordedAt:DateTimeOffset; Deadline:DateTimeOffset
      MaximumRuntimeSeconds:int64; MaximumAttempts:int; Workspace:string; RequestedModel:string
      RequestedEffort:string; InputDigest:string; ExecutorBinding:string; WorkspaceManifestSha256:string option }

[<RequireQualifiedAccess>]
module private StoredExecutorCommand =
    let parse bytes =
        match ExecutorWire.parseCommand bytes with
        | Ok value -> Ok { CommandId=value.CommandId;BodySha256=value.BodySha256;Kind=value.Kind
                           AssignmentId=value.AssignmentId;AttemptId=value.AttemptId;Generation=value.Generation
                           ExpectedRevision=value.ExpectedRevision;RecordedAt=value.RecordedAt;Deadline=value.Deadline
                           MaximumRuntimeSeconds=value.MaximumRuntimeSeconds;MaximumAttempts=value.MaximumAttempts
                           Workspace=value.Workspace;RequestedModel=value.RequestedModel;RequestedEffort=value.RequestedEffort
                           InputDigest=value.InputDigest;ExecutorBinding=value.ExecutorBinding;WorkspaceManifestSha256=None }
        | Error legacyError ->
            match ExecutorWire.parseCommandV2 bytes with
            | Ok value -> Ok { CommandId=value.CommandId;BodySha256=value.BodySha256;Kind=value.Kind
                               AssignmentId=value.AssignmentId;AttemptId=value.AttemptId;Generation=value.Generation
                               ExpectedRevision=value.ExpectedRevision;RecordedAt=value.RecordedAt;Deadline=value.Deadline
                               MaximumRuntimeSeconds=value.MaximumRuntimeSeconds;MaximumAttempts=value.MaximumAttempts
                               Workspace=value.Workspace;RequestedModel=value.RequestedModel;RequestedEffort=value.RequestedEffort
                               InputDigest=value.InputDigest;ExecutorBinding=value.ExecutorBinding;WorkspaceManifestSha256=Some value.WorkspaceManifestSha256 }
            | Error _ -> Error legacyError

type IExecutorCommandStore =
    abstract BindRoute: bindingBytes:byte array * CancellationToken -> Task<Result<string,string>>
    abstract ReadRoute: assignmentId:Guid * attemptId:Guid * CancellationToken -> Task<Result<byte array,string>>
    abstract FindAttemptBySession: providerSessionReference:string * CancellationToken -> Task<Result<Guid * Guid,string>>
    abstract StageInput: manifestBytes:byte array * bytes:byte array * CancellationToken -> Task<Result<unit,string>>
    abstract ReadInput: digest:string * CancellationToken -> Task<Result<byte array,string>>
    abstract StageWorkspaceManifest: manifestBytes:byte array * CancellationToken -> Task<Result<string,string>>
    abstract ReadWorkspaceManifest: digest:string * CancellationToken -> Task<Result<byte array,string>>
    abstract PersistCommand: commandBytes:byte array * CancellationToken -> Task<ExecutorCommandAppend>
    abstract ReadPending: maximum:int * CancellationToken -> Task<byte array list>
    abstract SettleCommand: commandId:Guid * receiptBytes:byte array * CancellationToken -> Task<Result<unit,string>>
    abstract ReserveSubscription: reservationBytes:byte array * ordinaryCapacity:int * recoveryCapacity:int * CancellationToken -> Task<SubscriptionAppend>
    abstract SettleSubscription: reservationId:Guid * settlementBytes:byte array * CancellationToken -> Task<Result<unit,string>>
    abstract ReadSubscription: reservationId:Guid * CancellationToken -> Task<Result<byte array * byte array option,string>>

[<RequireQualifiedAccess>]
module PostgreSqlExecutionSchema =
    let migrate (dataSource:NpgsqlDataSource) cancellationToken = task {
        let assembly=Assembly.GetExecutingAssembly()
        let name=assembly.GetManifestResourceNames() |> Array.find(fun value->value.EndsWith("ExecutionMigration.sql",StringComparison.Ordinal))
        use stream=assembly.GetManifestResourceStream name
        use reader=new StreamReader(stream)
        use! connection=dataSource.OpenConnectionAsync cancellationToken
        use! transaction=connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken)
        use command=new NpgsqlCommand(reader.ReadToEnd(),connection,transaction)
        let! _=command.ExecuteNonQueryAsync cancellationToken
        do! transaction.CommitAsync cancellationToken }

type PostgreSqlExecutionStore(options:StoreOptions) =
    let dataSource=options.DataSource
    let add (command:NpgsqlCommand) value = command.Parameters.AddWithValue(value) |> ignore
    let sha (bytes:byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let gate (connection:NpgsqlConnection) (transaction:NpgsqlTransaction) forWrite (cancellationToken:CancellationToken) = task {
        use command=new NpgsqlCommand("SELECT schema_version,migration_state,backup_identity::text,generation_fence,current_setting('transaction_read_only') FROM fsgg_orchestration.store_metadata WHERE singleton FOR SHARE",connection,transaction)
        use! row=command.ExecuteReaderAsync cancellationToken
        let! found=row.ReadAsync cancellationToken
        if not found then return raise (InvalidOperationException "execution-store-metadata-missing")
        else
            let valid=row.GetInt32(0)=options.RuntimeSchemaVersion && options.RuntimeSchemaVersion=2
                      && row.GetString(1)="ready" && row.GetString(2)=options.BackupIdentity
                      && row.GetInt64(3)>=options.MinimumGenerationFence && (not forWrite || row.GetString(4)="off")
            if not valid then return raise (InvalidOperationException "execution-store-fence-refused") }
    let read assignmentId attemptId cancellationToken = task {
        use! connection=dataSource.OpenConnectionAsync cancellationToken
        use! transaction=connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,cancellationToken)
        do! gate connection transaction false cancellationToken
        use streamCommand=new NpgsqlCommand("SELECT last_revision,generation FROM fsgg_orchestration.execution_stream WHERE assignment_id=$1 AND attempt_id=$2",connection,transaction)
        add streamCommand assignmentId;add streamCommand attemptId
        use! streamRow=streamCommand.ExecuteReaderAsync cancellationToken
        let! streamFound=streamRow.ReadAsync cancellationToken
        let tail=if streamFound then Some(streamRow.GetInt64 0) else None
        let streamGeneration=if streamFound && not(streamRow.IsDBNull 1) then Some(streamRow.GetInt64 1) else None
        do! streamRow.CloseAsync()
        match tail with
        | None -> return None
        | Some expectedTail ->
            use command=new NpgsqlCommand("SELECT revision,event_identity,schema,payload FROM fsgg_orchestration.execution_event WHERE assignment_id=$1 AND attempt_id=$2 ORDER BY revision",connection,transaction)
            add command assignmentId;add command attemptId
            use! reader=command.ExecuteReaderAsync cancellationToken
            let events=ResizeArray<SessionEvent>()
            let mutable revision=0L
            let mutable failure=None
            let mutable reading=true
            while reading do
                let! more=reader.ReadAsync cancellationToken
                reading<-more
                if more && failure.IsNone then
                    let next=reader.GetInt64 0
                    if next<>revision+1L then failure<-Some "execution-event-revision-gap-refused"
                    revision<-next
                    let identity=reader.GetString 1
                    let schema=reader.GetString 2
                    let payload=reader.GetFieldValue<byte array> 3
                    if schema<>SessionEventCodec.schema then failure<-Some "execution-event-schema-refused"
                    elif sha payload<>identity then failure<-Some "execution-event-digest-refused"
                    else match SessionEventCodec.decode payload with Ok value->events.Add value|Error reason->failure<-Some reason
            do! reader.CloseAsync()
            match failure with
            | Some reason -> return raise(InvalidDataException reason)
            | None when revision<>expectedTail -> return raise(InvalidDataException "execution-stream-tail-refused")
            | None ->
                match events |> Seq.tryHead with
                | Some(LaunchIntentRecorded intent) when streamGeneration=Some intent.Key.Generation ->
                    do! transaction.CommitAsync cancellationToken
                    return Some{Revision=revision;Events=List.ofSeq events}
                | _ -> return raise(InvalidDataException "execution-stream-generation-refused") }
    interface IExecutionSessionJournal with
        member _.ReadAttempt(assignmentId,attemptId,cancellationToken)=read assignmentId attemptId cancellationToken
        member _.AppendAttempt(assignmentId,attemptId,expectedRevision,eventValue,cancellationToken)=task {
            let payload=SessionEventCodec.encode eventValue
            let identity=SessionEventCodec.identity eventValue
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
            do! gate connection transaction true cancellationToken
            use ensure=new NpgsqlCommand("INSERT INTO fsgg_orchestration.execution_stream(assignment_id,attempt_id) VALUES($1,$2) ON CONFLICT DO NOTHING",connection,transaction)
            add ensure assignmentId;add ensure attemptId
            let! _=ensure.ExecuteNonQueryAsync cancellationToken
            use query=new NpgsqlCommand("SELECT last_revision,generation FROM fsgg_orchestration.execution_stream WHERE assignment_id=$1 AND attempt_id=$2 FOR UPDATE",connection,transaction)
            add query assignmentId;add query attemptId
            use! row=query.ExecuteReaderAsync cancellationToken
            let! _=row.ReadAsync cancellationToken
            let current=row.GetInt64 0
            let storedGeneration=if row.IsDBNull 1 then None else Some(row.GetInt64 1)
            do! row.CloseAsync()
            use duplicate=new NpgsqlCommand("SELECT 1 FROM fsgg_orchestration.execution_event WHERE assignment_id=$1 AND attempt_id=$2 AND event_identity=$3",connection,transaction)
            add duplicate assignmentId;add duplicate attemptId;add duplicate identity
            let! exists=duplicate.ExecuteScalarAsync cancellationToken
            if not(isNull exists) then
                do! transaction.CommitAsync cancellationToken
                return DuplicateEvent
            elif current<>expectedRevision then
                do! transaction.RollbackAsync cancellationToken
                return AppendConflict
            elif current > 0L && (match eventValue with LaunchIntentRecorded _ -> true | _ -> false) then
                do! transaction.RollbackAsync cancellationToken
                return AppendConflict
            else
                let generation = match eventValue with LaunchIntentRecorded intent when intent.Key.AssignmentId=assignmentId && intent.Key.AttemptId=attemptId -> Some intent.Key.Generation | _ -> storedGeneration
                if generation.IsNone || (storedGeneration.IsSome && storedGeneration<>generation) then
                    do! transaction.RollbackAsync cancellationToken
                    return AppendConflict
                else
                    let next=current+1L
                    use insert=new NpgsqlCommand("INSERT INTO fsgg_orchestration.execution_event(assignment_id,attempt_id,revision,event_identity,schema,payload,recorded_at) VALUES($1,$2,$3,$4,$5,$6,$7)",connection,transaction)
                    add insert assignmentId;add insert attemptId;add insert next;add insert identity;add insert SessionEventCodec.schema;add insert payload;add insert DateTimeOffset.UtcNow
                    let! _=insert.ExecuteNonQueryAsync cancellationToken
                    use update=new NpgsqlCommand("UPDATE fsgg_orchestration.execution_stream SET last_revision=$3,generation=$4 WHERE assignment_id=$1 AND attempt_id=$2",connection,transaction)
                    add update assignmentId;add update attemptId;add update next;add update generation.Value
                    let! _=update.ExecuteNonQueryAsync cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return Appended }
    interface IExecutorCommandStore with
        member _.BindRoute(bindingBytes,cancellationToken)=task {
            match ExecutorWire.parseRouteBinding bindingBytes with
            | Error reason -> return Error reason
            | Ok binding ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken)
                do! gate connection transaction true cancellationToken
                use command=new NpgsqlCommand("INSERT INTO fsgg_orchestration.execution_route_binding(assignment_id,attempt_id,generation,binding_sha256,payload,created_at) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(assignment_id,attempt_id) DO NOTHING",connection,transaction)
                add command binding.AssignmentId;add command binding.AttemptId;add command binding.Generation;add command binding.BindingSha256;add command bindingBytes;add command DateTimeOffset.UtcNow
                let! changed=command.ExecuteNonQueryAsync cancellationToken
                if changed=1 then
                    do! transaction.CommitAsync cancellationToken
                    return Ok binding.BindingSha256
                else
                    use existing=new NpgsqlCommand("SELECT generation,binding_sha256,payload FROM fsgg_orchestration.execution_route_binding WHERE assignment_id=$1 AND attempt_id=$2 FOR UPDATE",connection,transaction)
                    add existing binding.AssignmentId;add existing binding.AttemptId
                    use! row=existing.ExecuteReaderAsync cancellationToken
                    let! found=row.ReadAsync cancellationToken
                    let same=found && row.GetInt64(0)=binding.Generation && row.GetString(1)=binding.BindingSha256 && (row.GetFieldValue<byte array>(2)).AsSpan().SequenceEqual(bindingBytes.AsSpan())
                    do! row.CloseAsync()
                    do! transaction.CommitAsync cancellationToken
                    return if same then Ok binding.BindingSha256 else Error "execution-route-binding-conflict" }
        member _.ReadRoute(assignmentId,attemptId,cancellationToken)=task {
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use! transaction=connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,cancellationToken)
            do! gate connection transaction false cancellationToken
            use command=new NpgsqlCommand("SELECT generation,binding_sha256,payload FROM fsgg_orchestration.execution_route_binding WHERE assignment_id=$1 AND attempt_id=$2",connection,transaction)
            add command assignmentId;add command attemptId
            use! row=command.ExecuteReaderAsync cancellationToken
            let! found=row.ReadAsync cancellationToken
            if not found then return Error "execution-route-binding-not-found"
            else
                let generation=row.GetInt64 0
                let digest=row.GetString 1
                let payload=row.GetFieldValue<byte array> 2
                return match ExecutorWire.parseRouteBinding payload with Ok value when value.AssignmentId=assignmentId && value.AttemptId=attemptId && value.Generation=generation && value.BindingSha256=digest->Ok payload|_->Error "execution-route-binding-corrupt" }
        member _.FindAttemptBySession(providerSessionReference,cancellationToken)=task {
            match ProviderSessionReference.create providerSessionReference with
            | Error reason -> return Error reason
            | Ok expected ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,cancellationToken)
                do! gate connection transaction false cancellationToken
                use command=new NpgsqlCommand("SELECT assignment_id,attempt_id,payload FROM fsgg_orchestration.execution_event WHERE schema=$1 ORDER BY recorded_at DESC LIMIT 256",connection,transaction)
                add command SessionEventCodec.schema
                use! row=command.ExecuteReaderAsync cancellationToken
                let mutable found=None
                let mutable ambiguous=false
                let mutable reading=true
                while reading do
                    let! more=row.ReadAsync cancellationToken
                    reading<-more
                    if more then
                        match SessionEventCodec.decode(row.GetFieldValue<byte array> 2) with
                        | Ok(StartObserved observation)|Ok(ObservationRecorded observation) when observation.Session=expected ->
                            let key=row.GetGuid 0,row.GetGuid 1
                            match found with None->found<-Some key|Some prior when prior<>key->ambiguous<-true|_->()
                        | _ -> ()
                return
                    if ambiguous then Error "execution-session-binding-ambiguous"
                    else match found with Some value->Ok value|None->Error "execution-session-binding-not-found" }
        member _.StageInput(manifestBytes,bytes,cancellationToken)=task {
            match ExecutorWire.parseInputManifest manifestBytes with
            | Error reason -> return Error reason
            | Ok manifest when isNull bytes || bytes.LongLength<>manifest.SizeBytes || sha bytes<>manifest.InputDigest -> return Error "execution-input-refused"
            | Ok manifest ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
                do! gate connection transaction true cancellationToken
                use command=new NpgsqlCommand("INSERT INTO fsgg_orchestration.execution_input_object(input_sha256,manifest_payload,bytes,size_bytes,created_at) VALUES($1,$2,$3,$4,$5) ON CONFLICT(input_sha256) DO NOTHING",connection,transaction)
                add command manifest.InputDigest;add command manifestBytes;add command bytes;add command bytes.LongLength;add command DateTimeOffset.UtcNow
                let! _=command.ExecuteNonQueryAsync cancellationToken
                do! transaction.CommitAsync cancellationToken
                return Ok() }
        member _.ReadInput(digest,cancellationToken)=task {
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use! transaction=connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,cancellationToken)
            do! gate connection transaction false cancellationToken
            use command=new NpgsqlCommand("SELECT bytes FROM fsgg_orchestration.execution_input_object WHERE input_sha256=$1",connection,transaction)
            add command digest
            let! value=command.ExecuteScalarAsync cancellationToken
            if isNull value then return Error "execution-input-not-found"
            else let bytes=unbox<byte array> value in return if sha bytes=digest then Ok bytes else Error "execution-input-corrupt" }
        member _.StageWorkspaceManifest(manifestBytes,cancellationToken)=task {
            match ExecutorWire.parseWorkspaceManifest manifestBytes with
            | Error reason -> return Error reason
            | Ok _ ->
                let digest=sha manifestBytes
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
                do! gate connection transaction true cancellationToken
                use command=new NpgsqlCommand("INSERT INTO fsgg_orchestration.execution_workspace_manifest(manifest_sha256,payload,created_at) VALUES($1,$2,$3) ON CONFLICT(manifest_sha256) DO NOTHING",connection,transaction)
                add command digest;add command manifestBytes;add command DateTimeOffset.UtcNow
                let! _=command.ExecuteNonQueryAsync cancellationToken
                do! transaction.CommitAsync cancellationToken
                return Ok digest }
        member _.ReadWorkspaceManifest(digest,cancellationToken)=task {
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use! transaction=connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,cancellationToken)
            do! gate connection transaction false cancellationToken
            use command=new NpgsqlCommand("SELECT payload FROM fsgg_orchestration.execution_workspace_manifest WHERE manifest_sha256=$1",connection,transaction)
            add command digest
            let! value=command.ExecuteScalarAsync cancellationToken
            if isNull value then return Error "execution-workspace-manifest-not-found"
            else let bytes=unbox<byte array> value in return if sha bytes=digest && ExecutorWire.parseWorkspaceManifest bytes|>Result.isOk then Ok bytes else Error "execution-workspace-manifest-corrupt" }
        member _.PersistCommand(commandBytes,cancellationToken)=task {
            match StoredExecutorCommand.parse commandBytes with
            | Error reason->return CommandRefused reason
            | Ok commandValue ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken)
                do! gate connection transaction true cancellationToken
                use existing=new NpgsqlCommand("SELECT body_sha256,durable_revision FROM fsgg_orchestration.executor_command WHERE command_id=$1 FOR UPDATE",connection,transaction)
                add existing commandValue.CommandId
                use! row=existing.ExecuteReaderAsync cancellationToken
                let! found=row.ReadAsync cancellationToken
                let prior=if found then Some(row.GetString 0,row.GetInt64 1) else None
                do! row.CloseAsync()
                match prior with
                | Some(body,revision) when body=commandValue.BodySha256 ->
                    do! transaction.CommitAsync cancellationToken
                    return CommandDuplicate revision
                | Some _ ->
                    do! transaction.RollbackAsync cancellationToken
                    return CommandConflict
                | None ->
                    use authority=new NpgsqlCommand("SELECT last_revision,generation,executor_binding FROM fsgg_orchestration.execution_stream WHERE assignment_id=$1 AND attempt_id=$2 FOR UPDATE",connection,transaction)
                    add authority commandValue.AssignmentId;add authority commandValue.AttemptId
                    use! auth=authority.ExecuteReaderAsync cancellationToken
                    let! authorityFound=auth.ReadAsync cancellationToken
                    let storedBinding=if authorityFound && not(auth.IsDBNull 2) then Some(auth.GetString 2) else None
                    let valid=authorityFound && auth.GetInt64(0)=commandValue.ExpectedRevision && not(auth.IsDBNull 1) && auth.GetInt64(1)=commandValue.Generation
                              && (storedBinding.IsNone || storedBinding=Some commandValue.ExecutorBinding)
                    do! auth.CloseAsync()
                    if not valid then
                        do! transaction.RollbackAsync cancellationToken
                        return CommandRefused "executor-command-stale-authority"
                    else
                        use intentCommand=new NpgsqlCommand("SELECT payload FROM fsgg_orchestration.execution_event WHERE assignment_id=$1 AND attempt_id=$2 AND revision=1",connection,transaction)
                        add intentCommand commandValue.AssignmentId;add intentCommand commandValue.AttemptId
                        let! intentPayload=intentCommand.ExecuteScalarAsync cancellationToken
                        let intentMatches =
                            if isNull intentPayload then false
                            else
                                match SessionEventCodec.decode(unbox<byte array> intentPayload) with
                                | Ok(LaunchIntentRecorded intent) ->
                                    intent.Key.Generation=commandValue.Generation && intent.InputDigest=commandValue.InputDigest
                                    && intent.Workspace=commandValue.Workspace && intent.RecordedAt=commandValue.RecordedAt && intent.Limits.Deadline=commandValue.Deadline
                                    && int64 intent.Limits.MaximumRuntime.TotalSeconds=commandValue.MaximumRuntimeSeconds
                                    && intent.Limits.MaximumAttempts=commandValue.MaximumAttempts
                                    && Option.toObj intent.Requested.Model=commandValue.RequestedModel && Option.toObj intent.Requested.Effort=commandValue.RequestedEffort
                                | _ -> false
                        use input=new NpgsqlCommand("SELECT 1 FROM fsgg_orchestration.execution_input_object WHERE input_sha256=$1",connection,transaction)
                        add input commandValue.InputDigest
                        let! inputExists=input.ExecuteScalarAsync cancellationToken
                        let! workspaceExists = task {
                            match commandValue.WorkspaceManifestSha256 with
                            | None -> return box 1
                            | Some digest ->
                                use workspace=new NpgsqlCommand("SELECT 1 FROM fsgg_orchestration.execution_workspace_manifest WHERE manifest_sha256=$1",connection,transaction)
                                add workspace digest
                                return! workspace.ExecuteScalarAsync cancellationToken }
                        use reservationCommand=new NpgsqlCommand("SELECT reservation_payload FROM fsgg_orchestration.subscription_reservation WHERE assignment_id=$1 AND attempt_id=$2 AND generation=$3 AND (expected_revision=$4 OR ($6 AND expected_revision=$4-1)) AND active AND deadline >= $5",connection,transaction)
                        add reservationCommand commandValue.AssignmentId;add reservationCommand commandValue.AttemptId;add reservationCommand commandValue.Generation;add reservationCommand commandValue.ExpectedRevision;add reservationCommand commandValue.Deadline;add reservationCommand (commandValue.Kind="launch" && commandValue.ExpectedRevision=2L)
                        let! reservationPayload=reservationCommand.ExecuteScalarAsync cancellationToken
                        let reservationMatches =
                            if isNull reservationPayload then false
                            else
                                match SubscriptionAccountingCodec.decodeReservation(unbox<byte array> reservationPayload) with
                                | Ok value -> value.Deadline>=commandValue.Deadline && value.MaximumRuntimeSeconds>=commandValue.MaximumRuntimeSeconds && value.AttemptLimit>=commandValue.MaximumAttempts
                                | Error _ -> false
                        let effectiveExpiry=min commandValue.Deadline (commandValue.RecordedAt.AddSeconds(float commandValue.MaximumRuntimeSeconds))
                        let launchAuthorized=commandValue.Kind<>"launch" || (intentMatches && reservationMatches && effectiveExpiry>DateTimeOffset.UtcNow)
                        if isNull inputExists || isNull workspaceExists || not launchAuthorized then
                            do! transaction.RollbackAsync cancellationToken
                            let reasons =
                                ["intent",intentMatches
                                 "input",not(isNull inputExists)
                                 "workspace",not(isNull workspaceExists)
                                 "reservation",commandValue.Kind<>"launch" || reservationMatches
                                 "expiry",commandValue.Kind<>"launch" || effectiveExpiry>DateTimeOffset.UtcNow]
                                |>List.choose(fun(name,valid)->if valid then None else Some name)
                                |>String.concat ","
                            return CommandRefused($"executor-command-authority-refused:{reasons}")
                        else
                            if storedBinding.IsNone && commandValue.Kind="launch" then
                                use bind=new NpgsqlCommand("UPDATE fsgg_orchestration.execution_stream SET executor_binding=$3 WHERE assignment_id=$1 AND attempt_id=$2 AND executor_binding IS NULL",connection,transaction)
                                add bind commandValue.AssignmentId;add bind commandValue.AttemptId;add bind commandValue.ExecutorBinding
                                let! _=bind.ExecuteNonQueryAsync cancellationToken
                                ()
                            let durableRevision=commandValue.ExpectedRevision
                            use insert=new NpgsqlCommand("INSERT INTO fsgg_orchestration.executor_command(command_id,body_sha256,assignment_id,attempt_id,generation,expected_revision,deadline,payload,durable_revision,visible,created_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,false,$10)",connection,transaction)
                            add insert commandValue.CommandId;add insert commandValue.BodySha256;add insert commandValue.AssignmentId;add insert commandValue.AttemptId;add insert commandValue.Generation;add insert commandValue.ExpectedRevision;add insert commandValue.Deadline;add insert commandBytes;add insert durableRevision;add insert DateTimeOffset.UtcNow
                            let! _=insert.ExecuteNonQueryAsync cancellationToken
                            use expose=new NpgsqlCommand("UPDATE fsgg_orchestration.executor_command SET visible=true WHERE command_id=$1",connection,transaction)
                            add expose commandValue.CommandId
                            let! _=expose.ExecuteNonQueryAsync cancellationToken
                            do! transaction.CommitAsync cancellationToken
                            return CommandPersisted durableRevision }
        member _.ReadPending(maximum,cancellationToken)=task {
            if maximum<1 || maximum>128 then invalidArg (nameof maximum) "pending command bound"
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use! transaction=connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,cancellationToken)
            // Taking work is an authority-bearing read: require a writable, current store and
            // revalidate the stream and reservation at visibility time.
            do! gate connection transaction true cancellationToken
            use command=new NpgsqlCommand("""
SELECT c.payload,c.body_sha256,s.last_revision,s.generation,s.executor_binding,
       (SELECT r.reservation_payload FROM fsgg_orchestration.subscription_reservation r
              WHERE r.assignment_id=c.assignment_id AND r.attempt_id=c.attempt_id
                AND r.generation=c.generation
                AND (r.expected_revision=c.expected_revision OR (c.expected_revision=2 AND r.expected_revision=1))
                AND r.active AND r.deadline >= c.deadline AND r.deadline > now())
FROM fsgg_orchestration.executor_command c
JOIN fsgg_orchestration.execution_stream s USING(assignment_id,attempt_id)
WHERE c.visible AND NOT c.settled
ORDER BY c.created_at,c.command_id LIMIT $1
""",connection,transaction)
            add command maximum
            use! reader=command.ExecuteReaderAsync cancellationToken
            let values=ResizeArray<byte array>()
            let mutable failure=None
            let mutable reading=true
            while reading do
                let! more=reader.ReadAsync cancellationToken
                reading<-more
                if more then
                    let payload=reader.GetFieldValue<byte array> 0
                    match StoredExecutorCommand.parse payload with
                    | Error reason -> failure <- Some reason
                    | Ok queued ->
                        let indexValid =
                            queued.BodySha256 = reader.GetString 1
                            && queued.ExpectedRevision = reader.GetInt64 2
                            && queued.Generation = reader.GetInt64 3
                            && not(reader.IsDBNull 4)
                            && queued.ExecutorBinding = reader.GetString 4
                        let effectiveExpiry=min queued.Deadline (queued.RecordedAt.AddSeconds(float queued.MaximumRuntimeSeconds))
                        let reservationMatches =
                            if reader.IsDBNull 5 then false
                            else
                                match SubscriptionAccountingCodec.decodeReservation(reader.GetFieldValue<byte array> 5) with
                                | Ok value -> value.Deadline>=queued.Deadline && value.MaximumRuntimeSeconds>=queued.MaximumRuntimeSeconds && value.AttemptLimit>=queued.MaximumAttempts
                                | Error _ -> false
                        let dispatchAuthorized=queued.Kind<>"launch" || (reservationMatches && effectiveExpiry>DateTimeOffset.UtcNow)
                        if not indexValid then failure <- Some "executor-command-index-refused"
                        elif dispatchAuthorized then values.Add payload
            match failure with
            | Some reason -> return raise(InvalidDataException reason)
            | None -> return List.ofSeq values }
        member _.SettleCommand(commandId,receiptBytes,cancellationToken)=task {
            let binding =
                match ExecutorWire.parseReceipt receiptBytes with
                | Ok receipt -> Ok(receipt.CommandId,receipt.BodySha256)
                | Error _ ->
                    match ExecutorWire.parseOperationOutcome receiptBytes with
                    | Ok outcome -> Ok(outcome.CommandId,outcome.BodySha256)
                    | Error _ -> ExecutorWire.parseResponse receiptBytes |> Result.map(fun response->response.CommandId,response.BodySha256)
            match binding with
            | Error reason->return Error reason
            | Ok(receiptCommandId,_) when receiptCommandId<>commandId->return Error "executor-receipt-identity-refused"
            | Ok(_,bodySha256)->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
                do! gate connection transaction true cancellationToken
                use command=new NpgsqlCommand("UPDATE fsgg_orchestration.executor_command SET settled=true,receipt=$2 WHERE command_id=$1 AND body_sha256=$3 AND NOT settled",connection,transaction)
                add command commandId;add command receiptBytes;add command bodySha256
                let! changed=command.ExecuteNonQueryAsync cancellationToken
                do! transaction.CommitAsync cancellationToken
                return if changed=1 then Ok() else Error "executor-command-settlement-refused" }
        member _.ReserveSubscription(reservationBytes,ordinaryCapacity,recoveryCapacity,cancellationToken)=task {
            match SubscriptionAccountingCodec.decodeReservation reservationBytes with
            | Error _ -> return SubscriptionConflict
            | Ok reservation when ordinaryCapacity<1 || recoveryCapacity<1 || reservation.Deadline<=DateTimeOffset.UtcNow -> return SubscriptionCapacityRefused
            | Ok reservation ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
                do! gate connection transaction true cancellationToken
                use capacityLock=new NpgsqlCommand("LOCK TABLE fsgg_orchestration.subscription_reservation IN SHARE ROW EXCLUSIVE MODE",connection,transaction)
                let! _=capacityLock.ExecuteNonQueryAsync cancellationToken
                use existing=new NpgsqlCommand("SELECT reservation_payload FROM fsgg_orchestration.subscription_reservation WHERE reservation_id=$1 OR assignment_id=$2 OR attempt_id=$3 FOR UPDATE",connection,transaction)
                add existing reservation.ReservationId;add existing reservation.AssignmentId;add existing reservation.AttemptId
                let! prior=existing.ExecuteScalarAsync cancellationToken
                if not(isNull prior) then
                    let same=unbox<byte array> prior |> fun bytes -> bytes.AsSpan().SequenceEqual(reservationBytes.AsSpan())
                    do! transaction.CommitAsync cancellationToken
                    return if same then SubscriptionDuplicate else SubscriptionConflict
                else
                    use authority=new NpgsqlCommand("SELECT last_revision,generation FROM fsgg_orchestration.execution_stream WHERE assignment_id=$1 AND attempt_id=$2 FOR UPDATE",connection,transaction)
                    add authority reservation.AssignmentId;add authority reservation.AttemptId
                    use! row=authority.ExecuteReaderAsync cancellationToken
                    let! found=row.ReadAsync cancellationToken
                    let current=found && row.GetInt64(0)=reservation.ExpectedRevision && not(row.IsDBNull 1) && row.GetInt64(1)=reservation.Generation
                    do! row.CloseAsync()
                    if not current then
                        do! transaction.RollbackAsync cancellationToken
                        return SubscriptionAuthorityRefused
                    else
                        use capacity=new NpgsqlCommand("SELECT count(*) FROM fsgg_orchestration.subscription_reservation WHERE active",connection,transaction)
                        let! count=capacity.ExecuteScalarAsync cancellationToken
                        if Convert.ToInt32 count>=ordinaryCapacity then
                            do! transaction.RollbackAsync cancellationToken
                            return SubscriptionCapacityRefused
                        else
                            use insert=new NpgsqlCommand("INSERT INTO fsgg_orchestration.subscription_reservation(reservation_id,assignment_id,attempt_id,generation,expected_revision,reservation_payload,reserved_at,deadline) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",connection,transaction)
                            add insert reservation.ReservationId;add insert reservation.AssignmentId;add insert reservation.AttemptId;add insert reservation.Generation;add insert reservation.ExpectedRevision;add insert reservationBytes;add insert reservation.ReservedAt;add insert reservation.Deadline
                            let! _=insert.ExecuteNonQueryAsync cancellationToken
                            do! transaction.CommitAsync cancellationToken
                            return SubscriptionReserved }
        member _.SettleSubscription(reservationId,settlementBytes,cancellationToken)=task {
            match SubscriptionAccountingCodec.decodeSettlement settlementBytes with
            | Error reason -> return Error reason
            | Ok settlement when settlement.ReservationId<>reservationId -> return Error "subscription-settlement-identity-refused"
            | Ok _ ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
                do! gate connection transaction true cancellationToken
                // Accounting arrival is not terminal/reconciliation authority. Keep capacity reserved.
                use command=new NpgsqlCommand("UPDATE fsgg_orchestration.subscription_reservation SET settlement_payload=$2 WHERE reservation_id=$1 AND settlement_payload IS NULL",connection,transaction)
                add command reservationId;add command settlementBytes
                let! changed=command.ExecuteNonQueryAsync cancellationToken
                do! transaction.CommitAsync cancellationToken
                return if changed=1 then Ok() else Error "subscription-settlement-conflict" }
        member _.ReadSubscription(reservationId,cancellationToken)=task {
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use! transaction=connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,cancellationToken)
            do! gate connection transaction false cancellationToken
            use command=new NpgsqlCommand("SELECT reservation_payload,settlement_payload FROM fsgg_orchestration.subscription_reservation WHERE reservation_id=$1",connection,transaction)
            add command reservationId
            use! reader=command.ExecuteReaderAsync cancellationToken
            let! found=reader.ReadAsync cancellationToken
            if not found then return Error "subscription-reservation-not-found"
            else
                let reservation=reader.GetFieldValue<byte array> 0
                let settlement=if reader.IsDBNull 1 then None else Some(reader.GetFieldValue<byte array> 1)
                return Ok(reservation,settlement) }
