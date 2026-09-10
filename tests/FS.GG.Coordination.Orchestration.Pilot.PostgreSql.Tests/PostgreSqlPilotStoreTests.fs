namespace FS.GG.Coordination.Orchestration.Pilot.PostgreSql.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Pilot
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Host

module private Fixture =
    type FixedClock(value: DateTimeOffset) =
        inherit TimeProvider()
        override _.GetUtcNow() = value
    type Capability(input: ReadbackEvidenceInput) =
        interface IPilotReadbackCapability with
            member _.ReadCurrent(_, _) = Task.FromResult(Ok input)
    let environment name fallback = Environment.GetEnvironmentVariable(name) |> function null | "" -> fallback | value -> value
    let mode = environment "FSGG_PG_MODE" "binary"
    let root =
        match Environment.GetEnvironmentVariable("FSGG_PG_ROOT") with
        | null | "" when mode = "binary" -> File.ReadAllText("/tmp/o0-postgresql-current-path").Trim()
        | null | "" -> "/tmp"
        | value -> value
    let host = environment "FSGG_PG_HOST" (Path.Combine(root, "socket"))
    let port = environment "FSGG_PG_PORT" "55439"
    let username = environment "FSGG_PG_USERNAME" "developer"
    let container = environment "FSGG_PG_CONTAINER" ""
    let connectionString = $"Host={host};Port={port};Database=orchestration_o0;Username={username};Pooling=false"
    let source () = NpgsqlDataSource.Create connectionString
    let now = DateTimeOffset.Parse "2026-09-10T12:00:00Z"
    let sha value = String.replicate 64 value
    let permitId = Guid.Parse "51000000-0000-0000-0000-000000000001"
    let subject = WorkItemIdentity.create "R_kgDOPilot" 5L "I_kwDOPilot" 17L
    let generation = Id.generation 4L
    let permit () =
        { SchemaVersion = 1; PermitId = permitId; SubjectId = subject; JobClass = "routine-implementation"
          StableOwnerId = "stable-route"; PilotOwnerId = "pilot-route"; Generation = generation
          AttemptLimit = 2L; TokenLimit = 100L; RuntimeSecondsLimit = 60L; CostMicrosLimit = 1000L
          ExpiresAt = now.AddMinutes 30.; Capacity = 2; RecoveryCapacity = 1
          StartupPolicy = "manual"; AutoResume = false }
    let stableEvidence () =
        { SubjectId = subject; Generation = generation; EvidenceSha256 = sha "a"; Quiesced = true
          Excluded = true; ObservedAt = now }
    let input (purpose: ReadbackPurpose) (operationId: OperationId option) (observedAt: DateTimeOffset) : ReadbackEvidenceInput =
        { SubjectId = subject; Generation = generation; Purpose = purpose; OperationId = operationId
          Provider = "github-readback-fixture"; SourceRevision = "github-revision-1"
          EvidenceSha256 = sha "b"; ObservedAt = observedAt }
    let readbackAt (permitValue: PilotPermit) (purpose: ReadbackPurpose) (operationId: OperationId option) (observedAt: DateTimeOffset) =
        let input = { input purpose operationId observedAt with SubjectId = permitValue.SubjectId; Generation = permitValue.Generation }
        let request = { Permit = permitValue; Purpose = purpose; OperationId = operationId; NotBefore = observedAt.AddSeconds -1. }
        PilotReadback.read (FixedClock observedAt) (Capability input) request CancellationToken.None
        |> _.GetAwaiter().GetResult()
        |> Result.defaultWith failwith
    let readback purpose operationId = readbackAt (permit()) purpose operationId now
    let allocation () =
        { O0ReservationId = Id.reservation(Guid.NewGuid()); O0Deadline = now.AddMinutes 10.
          PlanningBudgetSha256 = Some(sha "c"); Tokens = 25L; RuntimeSeconds = 10L; CostMicros = 100L }
    let envelope (state: PilotState) (command: PilotCommand) : PilotCommandEnvelope =
        let principal =
            match command with
            | IssuePermit _ | RecordTransferIntent _ | AcknowledgeReturn _ -> "stable-route"
            | _ -> "pilot-route"
        { CommandId = Guid.NewGuid(); ExpectedSequence = state.Sequence; PrincipalId = principal
          IssuedAt = now.AddSeconds -1.; ExpiresAt = now.AddMinutes 5.; Command = command }
    let advance state command =
        let envelope = envelope state command
        Pilot.decide now state envelope |> Result.defaultWith failwith |> List.fold Pilot.evolve state
    let reset () = task {
        let source = source()
        use! connection = source.OpenConnectionAsync()
        use drop = new NpgsqlCommand("DROP SCHEMA IF EXISTS fsgg_orchestration CASCADE", connection)
        let! _ = drop.ExecuteNonQueryAsync()
        do! connection.CloseAsync()
        let! identity = PostgreSqlSchema.migrate source CancellationToken.None
        do! PostgreSqlPilotSchema.migrate source CancellationToken.None
        return source, identity }
    let options source identity fence =
        { DataSource = source; StoreId = "pilot-pg18-lab"; BackupIdentity = identity
          MinimumGenerationFence = fence; RuntimeSchemaVersion = 1
          SupportedEventSchemaVersions = Set [ 1 ]; SupportedSerializerVersions = Set [ EventEnvelope.serializerVersion ]
          MaximumCandidateBytes = 1_048_576L }
    let sql statement = task {
        use source = source()
        use! connection = source.OpenConnectionAsync()
        use command = new NpgsqlCommand(statement, connection)
        return! command.ExecuteNonQueryAsync() }
    let run (executable: string) (arguments: string) =
        let info = Diagnostics.ProcessStartInfo(executable, arguments)
        info.RedirectStandardError <- true
        use child = Diagnostics.Process.Start(info)
        if not (child.WaitForExit 60_000) || child.ExitCode <> 0 then failwith (child.StandardError.ReadToEnd())
    let stop () =
        if mode = "docker" then run "docker" $"kill {container}"
        else run "/usr/bin/pg_ctl" $"-D {root}/data stop -m immediate"
    let start () =
        if mode = "docker" then run "docker" $"start {container}"
        else run "/usr/bin/pg_ctl" $"-D {root}/data -l {root}/postgres.log -o \"-k {host} -h '' -p {port} -c fsync=on -c synchronous_commit=on -c full_page_writes=on\" start"
    let waitReady () = task {
        let mutable ready = false
        for _ in 1..100 do
            if not ready then
                try
                    use probe = source()
                    use! connection = probe.OpenConnectionAsync()
                    ready <- true
                with _ -> do! Task.Delay 100
        if not ready then failwith "PostgreSQL unavailable" }

type PostgreSqlPilotStoreTests() =
    let token = CancellationToken.None

    [<Fact>]
    member _.``pilot reducer preserves controls unknown authority and cumulative budgets``() =
        let permit = { Fixture.permit() with Capacity = 3; AttemptLimit = 4L }
        let mutable state = Fixture.advance Pilot.initial (IssuePermit permit)
        state <- Fixture.advance state (RecordTransferIntent(Fixture.stableEvidence()))
        state <- Fixture.advance state (AcknowledgeTransfer(Fixture.readback TransferAcknowledgement None))
        let op1, op2 = Id.operation(Guid.NewGuid()), Id.operation(Guid.NewGuid())
        let attempt1, attempt2 = Id.attempt(Guid.NewGuid()), Id.attempt(Guid.NewGuid())
        state <- Fixture.advance state (StartAssignment(op1, attempt1, Fixture.allocation(), Fixture.readback AssignmentAdmission (Some op1)))
        state <- Fixture.advance state (StartAssignment(op2, attempt2, Fixture.allocation(), Fixture.readback AssignmentAdmission (Some op2)))
        state <- Fixture.advance state (MarkOutcomeUnknown(op1, "lost"))
        state <- Fixture.advance state (MarkOutcomeUnknown(op2, "lost"))
        let operationBorrow = Fixture.envelope state (ReconcileOutcome(op2, Fixture.readback AssignmentReconciliation (Some op1)))
        Assert.True(Pilot.decide Fixture.now state operationBorrow |> Result.isError)
        state <- Fixture.advance state (ReconcileOutcome(op1, Fixture.readback AssignmentReconciliation (Some op1)))
        Assert.Equal(PilotPhase.OutcomeUnknown, state.Phase)
        Assert.True(state.UnknownOperations.Contains op2)
        state <- Fixture.advance state (Pause "pause")
        state <- Fixture.advance state (MarkOutcomeUnknown(op2, "still-lost"))
        Assert.Equal(PilotPhase.Paused, state.Phase)
        state <- Fixture.advance state (Revoke "revoke")
        state <- Fixture.advance state (MarkOutcomeUnknown(op2, "still-lost"))
        Assert.Equal(PilotPhase.Revoked, state.Phase)
        state <- Fixture.advance state (ReconcileOutcome(op2, Fixture.readback AssignmentReconciliation (Some op2)))
        Assert.Equal(PilotPhase.Revoked, state.Phase)
        let oldRequest = { Permit = permit; Purpose = Reconnect; OperationId = None; NotBefore = Fixture.now.AddSeconds -1. }
        let oldInput = Fixture.input Reconnect None (Fixture.now.AddMinutes -10.)
        let oldResult = PilotReadback.read (Fixture.FixedClock Fixture.now) (Fixture.Capability oldInput) oldRequest CancellationToken.None |> _.GetAwaiter().GetResult()
        Assert.Equal(Error "readback-evidence-untrusted-stale-or-unbound", oldResult)
        let staleGenerationInput = { Fixture.input Reconnect None Fixture.now with Generation = Id.generation 5L }
        let staleGenerationResult = PilotReadback.read (Fixture.FixedClock Fixture.now) (Fixture.Capability staleGenerationInput) oldRequest CancellationToken.None |> _.GetAwaiter().GetResult()
        Assert.Equal(Error "readback-evidence-untrusted-stale-or-unbound", staleGenerationResult)
        let reused = Fixture.envelope state (StartAssignment(op1, Id.attempt(Guid.NewGuid()), Fixture.allocation(), Fixture.readback AssignmentAdmission (Some op1)))
        Assert.True(Pilot.decide Fixture.now state reused |> Result.isError)
        let forgedPrincipal = { Fixture.envelope state (RecordReturnIntent(Fixture.readback ReturnIntent None)) with PrincipalId = "borrower" }
        Assert.True(Pilot.decide Fixture.now state forgedPrincipal |> Result.isError)

        let mutable budgetState = Fixture.advance Pilot.initial (IssuePermit permit)
        budgetState <- Fixture.advance budgetState (RecordTransferIntent(Fixture.stableEvidence()))
        budgetState <- Fixture.advance budgetState (AcknowledgeTransfer(Fixture.readback TransferAcknowledgement None))
        let allocation60 = { Fixture.allocation() with Tokens = 60L }
        let budgetOp = Id.operation(Guid.NewGuid())
        budgetState <- Fixture.advance budgetState (StartAssignment(budgetOp, Id.attempt(Guid.NewGuid()), allocation60, Fixture.readback AssignmentAdmission (Some budgetOp)))
        budgetState <- Fixture.advance budgetState (SettleAssignment(budgetOp, Fixture.readback AssignmentSettlement (Some budgetOp)))
        let reuseSettled = Fixture.envelope budgetState (StartAssignment(budgetOp, Id.attempt(Guid.NewGuid()), Fixture.allocation(), Fixture.readback AssignmentAdmission (Some budgetOp)))
        Assert.True(Pilot.decide Fixture.now budgetState reuseSettled |> Result.isError)
        let excessOp = Id.operation(Guid.NewGuid())
        let exceeds = Fixture.envelope budgetState (StartAssignment(excessOp, Id.attempt(Guid.NewGuid()), allocation60, Fixture.readback AssignmentAdmission (Some excessOp)))
        Assert.True(Pilot.decide Fixture.now budgetState exceeds |> Result.isError)

    [<Fact>]
    member _.``closed pilot lifecycle survives store replacement and preserves paused reconciliation``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let mutable state = Pilot.initial
        let store = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let append command = task {
            let envelope = Fixture.envelope state command
            let events = Pilot.decide Fixture.now state envelope |> Result.defaultWith failwith
            let! result = store.AppendPilot(PilotJournal.appendRequest Fixture.permitId Fixture.now envelope events, token)
            match result with PilotAppended sequence -> Assert.Equal(state.Sequence + int64 events.Length, sequence) | other -> failwithf "%A" other
            state <- List.fold Pilot.evolve state events }
        do! append (IssuePermit(Fixture.permit()))
        do! append (RecordTransferIntent(Fixture.stableEvidence()))
        do! append (AcknowledgeTransfer(Fixture.readback TransferAcknowledgement None))
        let operationId, attemptId = Id.operation(Guid.NewGuid()), Id.attempt(Guid.NewGuid())
        do! append (StartAssignment(operationId, attemptId, Fixture.allocation(), Fixture.readback AssignmentAdmission (Some operationId)))
        do! append (MarkOutcomeUnknown(operationId, "heartbeat-lost"))
        do! append (Pause "operator-pause")
        do! append (ReconcileOutcome(operationId, Fixture.readback AssignmentReconciliation (Some operationId)))
        Assert.Equal(PilotPhase.Paused, state.Phase)
        do! append (Revoke "operator-revoke")
        do! append (RecordReturnIntent(Fixture.readback ReturnIntent None))
        do! append (AcknowledgeReturn(Fixture.readback ReturnAcknowledgement None))
        let replacement = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let! recovered = replacement.RecoverPilot(Fixture.permitId, token)
        match recovered with Ok value -> Assert.Equal<PilotState>(state, value.State) | Error failure -> failwithf "%A" failure }

    [<Fact>]
    member _.``pilot inbox binds complete envelope and sequence CAS``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let store = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let envelope = Fixture.envelope Pilot.initial (IssuePermit(Fixture.permit()))
        let events = Pilot.decide Fixture.now Pilot.initial envelope |> Result.defaultWith failwith
        let request = PilotJournal.appendRequest Fixture.permitId Fixture.now envelope events
        let! first = store.AppendPilot(request, token)
        Assert.Equal<PilotAppendOutcome>(PilotAppended 1L, first)
        let! duplicate = store.AppendPilot({ request with Events = [] }, token)
        Assert.Equal<PilotAppendOutcome>(PilotDuplicate 1L, duplicate)
        let! conflict = store.AppendPilot({ request with Command = { envelope with PrincipalId = "borrower" }; Events = [] }, token)
        Assert.Equal<PilotAppendOutcome>(PilotConflict, conflict)
        let nextState = Pilot.replay events
        let nextEnvelope = { Fixture.envelope nextState (RecordTransferIntent(Fixture.stableEvidence())) with ExpectedSequence = 0L }
        let nextEvents = [ TransferIntentPersisted(Fixture.stableEvidence()) ]
        let! wrong = store.AppendPilot(PilotJournal.appendRequest Fixture.permitId Fixture.now nextEnvelope nextEvents, token)
        Assert.Equal<PilotAppendOutcome>(PilotWrongExpectedSequence 1L, wrong) }

    [<Fact>]
    member _.``process replacement requires fresh trusted reconnect before further assignment``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let first = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let mutable state = Pilot.initial
        let append (store: IPilotJournalStore) command = task {
            let envelope = Fixture.envelope state command
            let events = Pilot.decide Fixture.now state envelope |> Result.defaultWith failwith
            let! result = store.AppendPilot(PilotJournal.appendRequest Fixture.permitId Fixture.now envelope events, token)
            match result with PilotAppended _ -> state <- List.fold Pilot.evolve state events | other -> failwithf "%A" other }
        do! append first (IssuePermit(Fixture.permit()))
        do! append first (RecordTransferIntent(Fixture.stableEvidence()))
        do! append first (AcknowledgeTransfer(Fixture.readback TransferAcknowledgement None))
        Fixture.stop()
        NpgsqlConnection.ClearAllPools()
        Fixture.start()
        do! Fixture.waitReady()
        let replacement = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let! recovered = replacement.RecoverPilot(Fixture.permitId, token)
        let recoveredState = recovered |> Result.map _.State |> Result.defaultWith (sprintf "%A" >> failwith)
        Assert.False(recoveredState.ReadbackCurrent)
        let maliciousState = { recoveredState with ReadbackCurrent = true }
        let operationId = Id.operation(Guid.NewGuid())
        let maliciousEnvelope = Fixture.envelope maliciousState (StartAssignment(operationId, Id.attempt(Guid.NewGuid()), Fixture.allocation(), Fixture.readback AssignmentAdmission (Some operationId)))
        let maliciousEvents = Pilot.decide Fixture.now maliciousState maliciousEnvelope |> Result.defaultWith failwith
        let! refused = replacement.AppendPilot(PilotJournal.appendRequest Fixture.permitId Fixture.now maliciousEnvelope maliciousEvents, token)
        Assert.Equal<PilotAppendOutcome>(PilotInvalidAppend "pilot-command-refused", refused)
        state <- recoveredState
        do! append replacement (ReconnectReadback(Fixture.readback Reconnect None))
        do! append replacement (StartAssignment(operationId, Id.attempt(Guid.NewGuid()), Fixture.allocation(), Fixture.readback AssignmentAdmission (Some operationId)))
        Assert.True(state.ActiveAssignments.ContainsKey operationId) }

    [<Fact>]
    member _.``pilot recovery rejects acknowledged tail loss and future codec``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let store = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let envelope = Fixture.envelope Pilot.initial (IssuePermit(Fixture.permit()))
        let events = Pilot.decide Fixture.now Pilot.initial envelope |> Result.defaultWith failwith
        let! _ = store.AppendPilot(PilotJournal.appendRequest Fixture.permitId Fixture.now envelope events, token)
        let! _ = Fixture.sql $"UPDATE fsgg_orchestration.pilot_event SET schema_version=99,serializer_version='future' WHERE permit_id='{Fixture.permitId}'"
        let! corrupt = store.RecoverPilot(Fixture.permitId, token)
        match corrupt with
        | Error failures ->
            Assert.Contains(failures, function PilotUnknownEventVersion(_, 1L, 99) -> true | _ -> false)
            Assert.Contains(failures, function PilotUnknownSerializerVersion "future" -> true | _ -> false)
        | Ok _ -> failwith "future codec recovered"
        let! _ = Fixture.sql $"DELETE FROM fsgg_orchestration.pilot_inbox WHERE permit_id='{Fixture.permitId}'; DELETE FROM fsgg_orchestration.pilot_event WHERE permit_id='{Fixture.permitId}'"
        let! truncated = store.RecoverPilot(Fixture.permitId, token)
        match truncated with Error failures -> Assert.Contains(failures, function PilotCorruptRecord(_, 1L) -> true | _ -> false) | Ok _ -> failwith "tail loss recovered" }

    [<Fact>]
    member _.``pilot recovery rejects a valid payload whose permit identity mismatches its stream``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let store = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let envelope = Fixture.envelope Pilot.initial (IssuePermit(Fixture.permit()))
        let events = Pilot.decide Fixture.now Pilot.initial envelope |> Result.defaultWith failwith
        let! _ = store.AppendPilot(PilotJournal.appendRequest Fixture.permitId Fixture.now envelope events, token)
        let otherPermit = { Fixture.permit() with PermitId = Guid.NewGuid() }
        let payload = PilotCodec.encode (PermitIssued otherPermit)
        let hash = Security.Cryptography.SHA256.HashData payload |> Convert.ToHexString |> _.ToLowerInvariant()
        use! connection = source.OpenConnectionAsync()
        use command = new NpgsqlCommand("UPDATE fsgg_orchestration.pilot_event SET payload=$1,payload_sha256=$2 WHERE permit_id=$3", connection)
        command.Parameters.AddWithValue(payload) |> ignore
        command.Parameters.AddWithValue(hash) |> ignore
        command.Parameters.AddWithValue(Fixture.permitId) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        let! recovered = store.RecoverPilot(Fixture.permitId, token)
        match recovered with Error failures -> Assert.Contains(failures, function PilotCorruptRecord(_, 1L) -> true | _ -> false) | Ok _ -> failwith "mismatched permit recovered" }

    [<Fact>]
    member _.``pilot metadata generation fence is mandatory``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let store = PostgreSqlPilotStore(Fixture.options source identity 2L) :> IPilotJournalStore
        let! recovery = store.RecoverPilot(Fixture.permitId, token)
        match recovery with
        | Error failures -> Assert.Contains(failures, function PilotBackupRequiresReconciliation "generation-fence-regressed" -> true | _ -> false)
        | Ok _ -> failwith "stale generation recovered"
        let envelope = Fixture.envelope Pilot.initial (IssuePermit(Fixture.permit()))
        let events = Pilot.decide Fixture.now Pilot.initial envelope |> Result.defaultWith failwith
        let! append = store.AppendPilot(PilotJournal.appendRequest Fixture.permitId Fixture.now envelope events, token)
        match append with PilotAppendUnavailable reason -> Assert.Contains("generation-fence-regressed", reason) | other -> failwithf "%A" other }

    [<Fact>]
    member _.``expiry stops new assignment but late reconciliation and return remain recoverable``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let store = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let permit = { Fixture.permit() with ExpiresAt = Fixture.now.AddMinutes 1. }
        let mutable state = Pilot.initial
        let append (at: DateTimeOffset) (command: PilotCommand) = task {
            let template = Fixture.envelope state command
            let expiresAt =
                match command with
                | IssuePermit value -> value.ExpiresAt
                | _ -> at.AddMinutes 5.
            let envelope = { template with IssuedAt = at.AddSeconds -1.; ExpiresAt = expiresAt }
            let events = Pilot.decide at state envelope |> Result.defaultWith failwith
            let! result = store.AppendPilot(PilotJournal.appendRequest Fixture.permitId at envelope events, token)
            match result with PilotAppended _ -> state <- List.fold Pilot.evolve state events | other -> failwithf "%A" other }
        do! append Fixture.now (IssuePermit permit)
        do! append Fixture.now (RecordTransferIntent(Fixture.stableEvidence()))
        do! append Fixture.now (AcknowledgeTransfer(Fixture.readbackAt permit TransferAcknowledgement None Fixture.now))
        let operationId = Id.operation(Guid.NewGuid())
        do! append Fixture.now (StartAssignment(operationId, Id.attempt(Guid.NewGuid()), { Fixture.allocation() with O0Deadline = permit.ExpiresAt }, Fixture.readbackAt permit AssignmentAdmission (Some operationId) Fixture.now))
        do! append Fixture.now (MarkOutcomeUnknown(operationId, "lost"))
        let late = permit.ExpiresAt.AddMinutes 1.
        let newOperation = Id.operation(Guid.NewGuid())
        let newAttempt = Fixture.envelope state (StartAssignment(newOperation, Id.attempt(Guid.NewGuid()), Fixture.allocation(), Fixture.readbackAt permit AssignmentAdmission (Some newOperation) Fixture.now))
        Assert.True(Pilot.decide late state { newAttempt with IssuedAt = late.AddSeconds -1.; ExpiresAt = late.AddMinutes 1. } |> Result.isError)
        do! append late (ReconcileOutcome(operationId, Fixture.readbackAt permit AssignmentReconciliation (Some operationId) late))
        do! append late (RecordReturnIntent(Fixture.readbackAt permit ReturnIntent None late))
        do! append late (AcknowledgeReturn(Fixture.readbackAt permit ReturnAcknowledgement None late))
        let replacement = PostgreSqlPilotStore(Fixture.options source identity 0L) :> IPilotJournalStore
        let! recovered = replacement.RecoverPilot(Fixture.permitId, token)
        match recovered with Ok value -> Assert.Equal(PilotPhase.StableOwned, value.State.Phase) | Error failures -> failwithf "%A" failures }

    [<Fact>]
    member _.``ordinary host startup refuses interrupted migration without repairing it``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let! _ = Fixture.sql "UPDATE fsgg_orchestration.pilot_store_metadata SET migration_state='applying'"
        let configuration =
            { ConnectionString = Fixture.connectionString; Token = String.replicate 32 "x"; Prefix = "http://127.0.0.1:5110/"
              StoreId = "pilot-pg18-lab"; BackupIdentity = identity; MinimumGenerationFence = 0L
              PermitId = Fixture.permitId; PilotPrincipalId = "pilot-route"; RequestTimeout = TimeSpan.FromSeconds 1.
              MaximumConcurrentRequests = 1 }
        let hostSource, hostStore = HostRuntime.createStore configuration
        use hostSource = hostSource
        let! status = HostRuntime.status hostStore Fixture.permitId token
        Assert.False(status.Ready)
        Assert.Contains(status.Findings, fun value -> value.Contains("PilotMigrationInterrupted"))
        use! connection = source.OpenConnectionAsync()
        use command = new NpgsqlCommand("SELECT migration_state FROM fsgg_orchestration.pilot_store_metadata WHERE singleton", connection)
        let! migrationState = command.ExecuteScalarAsync()
        Assert.Equal("applying", string migrationState) }
