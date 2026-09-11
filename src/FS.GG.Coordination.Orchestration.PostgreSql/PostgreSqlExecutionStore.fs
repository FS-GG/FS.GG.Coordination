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

type IExecutorCommandStore =
    abstract StageInput: digest:string * bytes:byte array * CancellationToken -> Task<Result<unit,string>>
    abstract ReadInput: digest:string * CancellationToken -> Task<Result<byte array,string>>
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

type PostgreSqlExecutionStore(dataSource:NpgsqlDataSource) =
    let add (command:NpgsqlCommand) value = command.Parameters.AddWithValue(value) |> ignore
    let sha (bytes:byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let read assignmentId attemptId cancellationToken = task {
        use! connection=dataSource.OpenConnectionAsync cancellationToken
        use command=new NpgsqlCommand("SELECT revision,event_identity,schema,payload FROM fsgg_orchestration.execution_event WHERE assignment_id=$1 AND attempt_id=$2 ORDER BY revision",connection)
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
                revision<-reader.GetInt64 0
                let identity=reader.GetString 1
                let schema=reader.GetString 2
                let payload=reader.GetFieldValue<byte array> 3
                if schema<>SessionEventCodec.schema then failure<-Some "execution-event-schema-refused"
                elif sha payload<>identity then failure<-Some "execution-event-digest-refused"
                else match SessionEventCodec.decode payload with Ok value->events.Add value|Error reason->failure<-Some reason
        match failure with
        | Some reason -> return raise(InvalidDataException reason)
        | None when revision=0L -> return None
        | None -> return Some{Revision=revision;Events=List.ofSeq events} }
    interface IExecutionSessionJournal with
        member _.ReadAttempt(assignmentId,attemptId,cancellationToken)=read assignmentId attemptId cancellationToken
        member _.AppendAttempt(assignmentId,attemptId,expectedRevision,eventValue,cancellationToken)=task {
            let payload=SessionEventCodec.encode eventValue
            let identity=SessionEventCodec.identity eventValue
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
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
        member _.StageInput(digest,bytes,cancellationToken)=task {
            if isNull bytes || bytes.LongLength>16L*1024L*1024L || not(RunnerWire.validSha256 digest) || sha bytes<>digest then return Error "execution-input-refused"
            else
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use command=new NpgsqlCommand("INSERT INTO fsgg_orchestration.execution_input_object(input_sha256,bytes,size_bytes,created_at) VALUES($1,$2,$3,$4) ON CONFLICT(input_sha256) DO NOTHING",connection)
                add command digest;add command bytes;add command bytes.LongLength;add command DateTimeOffset.UtcNow
                let! _=command.ExecuteNonQueryAsync cancellationToken
                return Ok() }
        member _.ReadInput(digest,cancellationToken)=task {
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use command=new NpgsqlCommand("SELECT bytes FROM fsgg_orchestration.execution_input_object WHERE input_sha256=$1",connection)
            add command digest
            let! value=command.ExecuteScalarAsync cancellationToken
            if isNull value then return Error "execution-input-not-found"
            else let bytes=unbox<byte array> value in return if sha bytes=digest then Ok bytes else Error "execution-input-corrupt" }
        member _.PersistCommand(commandBytes,cancellationToken)=task {
            match ExecutorWire.parseCommand commandBytes with
            | Error reason->return CommandRefused reason
            | Ok commandValue ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken)
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
                    use authority=new NpgsqlCommand("SELECT last_revision,generation FROM fsgg_orchestration.execution_stream WHERE assignment_id=$1 AND attempt_id=$2",connection,transaction)
                    add authority commandValue.AssignmentId;add authority commandValue.AttemptId
                    use! auth=authority.ExecuteReaderAsync cancellationToken
                    let! authorityFound=auth.ReadAsync cancellationToken
                    let valid=authorityFound && auth.GetInt64(0)=commandValue.ExpectedRevision && not(auth.IsDBNull 1) && auth.GetInt64(1)=commandValue.Generation
                    do! auth.CloseAsync()
                    if not valid then
                        do! transaction.RollbackAsync cancellationToken
                        return CommandRefused "executor-command-stale-authority"
                    else
                        use input=new NpgsqlCommand("SELECT 1 FROM fsgg_orchestration.execution_input_object WHERE input_sha256=$1",connection,transaction)
                        add input commandValue.InputDigest
                        let! inputExists=input.ExecuteScalarAsync cancellationToken
                        if isNull inputExists then
                            do! transaction.RollbackAsync cancellationToken
                            return CommandRefused "executor-command-input-not-found"
                        else
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
            use command=new NpgsqlCommand("SELECT payload FROM fsgg_orchestration.executor_command WHERE visible AND NOT settled ORDER BY created_at,command_id LIMIT $1",connection)
            add command maximum
            use! reader=command.ExecuteReaderAsync cancellationToken
            let values=ResizeArray<byte array>()
            let mutable reading=true
            while reading do
                let! more=reader.ReadAsync cancellationToken
                reading<-more
                if more then values.Add(reader.GetFieldValue<byte array> 0)
            return List.ofSeq values }
        member _.SettleCommand(commandId,receiptBytes,cancellationToken)=task {
            match ExecutorWire.parseReceipt receiptBytes with
            | Error reason->return Error reason
            | Ok receipt when receipt.CommandId<>commandId->return Error "executor-receipt-identity-refused"
            | Ok receipt->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use command=new NpgsqlCommand("UPDATE fsgg_orchestration.executor_command SET settled=true,receipt=$2 WHERE command_id=$1 AND body_sha256=$3 AND NOT settled",connection)
                add command commandId;add command receiptBytes;add command receipt.BodySha256
                let! changed=command.ExecuteNonQueryAsync cancellationToken
                return if changed=1 then Ok() else Error "executor-command-settlement-refused" }
        member _.ReserveSubscription(reservationBytes,ordinaryCapacity,recoveryCapacity,cancellationToken)=task {
            match SubscriptionAccountingCodec.decodeReservation reservationBytes with
            | Error _ -> return SubscriptionConflict
            | Ok reservation when ordinaryCapacity<=recoveryCapacity || recoveryCapacity<1 || reservation.Deadline<=DateTimeOffset.UtcNow -> return SubscriptionCapacityRefused
            | Ok reservation ->
                use! connection=dataSource.OpenConnectionAsync cancellationToken
                use! transaction=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken)
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
                        if Convert.ToInt32 count>=ordinaryCapacity-recoveryCapacity then
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
                // Accounting arrival is not terminal/reconciliation authority. Keep capacity reserved.
                use command=new NpgsqlCommand("UPDATE fsgg_orchestration.subscription_reservation SET settlement_payload=$2 WHERE reservation_id=$1 AND settlement_payload IS NULL",connection)
                add command reservationId;add command settlementBytes
                let! changed=command.ExecuteNonQueryAsync cancellationToken
                return if changed=1 then Ok() else Error "subscription-settlement-conflict" }
        member _.ReadSubscription(reservationId,cancellationToken)=task {
            use! connection=dataSource.OpenConnectionAsync cancellationToken
            use command=new NpgsqlCommand("SELECT reservation_payload,settlement_payload FROM fsgg_orchestration.subscription_reservation WHERE reservation_id=$1",connection)
            add command reservationId
            use! reader=command.ExecuteReaderAsync cancellationToken
            let! found=reader.ReadAsync cancellationToken
            if not found then return Error "subscription-reservation-not-found"
            else
                let reservation=reader.GetFieldValue<byte array> 0
                let settlement=if reader.IsDBNull 1 then None else Some(reader.GetFieldValue<byte array> 1)
                return Ok(reservation,settlement) }
