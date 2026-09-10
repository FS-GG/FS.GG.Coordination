namespace FS.GG.Coordination.Orchestration.Observer.PostgreSql.Tests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Observer
open FS.GG.Coordination.Orchestration.PostgreSql

module private Fixture =
    let environment name fallback =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value -> value

    let mode = environment "FSGG_PG_MODE" "binary"
    let root =
        match Environment.GetEnvironmentVariable "FSGG_PG_ROOT" with
        | null | "" when mode = "binary" -> File.ReadAllText("/tmp/o0-postgresql-current-path").Trim()
        | null | "" -> "/tmp"
        | value -> value
    let host = environment "FSGG_PG_HOST" (Path.Combine(root, "socket"))
    let port = environment "FSGG_PG_PORT" "55439"
    let username = environment "FSGG_PG_USERNAME" "developer"
    let container = environment "FSGG_PG_CONTAINER" ""
    let connectionString database = $"Host={host};Port={port};Database={database};Username={username};Pooling=false"
    let dataSource database = NpgsqlDataSource.Create(connectionString database)

    let run (executable: string) (arguments: string) =
        let info = ProcessStartInfo(executable, arguments)
        info.RedirectStandardError <- true
        info.RedirectStandardOutput <- true
        use child = Process.Start info
        if not (child.WaitForExit 60_000) || child.ExitCode <> 0 then
            failwith $"{executable} failed: {child.StandardError.ReadToEnd()}"

    let databaseTool name arguments =
        if mode = "docker" then run "docker" $"exec {container} {name} -U {username} {arguments}"
        else run $"/usr/bin/{name}" $"-h {host} -p {port} -U {username} {arguments}"

    let sql database statement =
        task {
            use source = dataSource database
            use! connection = source.OpenConnectionAsync()
            use command = new NpgsqlCommand(statement, connection)
            return! command.ExecuteNonQueryAsync()
        }

    let reset () =
        task {
            let source = dataSource "orchestration_o0"
            use! connection = source.OpenConnectionAsync()
            use drop = new NpgsqlCommand("DROP SCHEMA IF EXISTS fsgg_orchestration CASCADE", connection)
            let! _ = drop.ExecuteNonQueryAsync()
            do! connection.CloseAsync()
            let! identity = PostgreSqlSchema.migrate source CancellationToken.None
            do! PostgreSqlObserverSchema.migrate source CancellationToken.None
            return source, identity
        }

    let options source identity fence =
        { DataSource = source
          StoreId = "observer-pg18-lab"
          BackupIdentity = identity
          MinimumGenerationFence = fence
          RuntimeSchemaVersion = 1
          SupportedEventSchemaVersions = Set [ 1 ]
          SupportedSerializerVersions = Set [ EventEnvelope.serializerVersion ]
          MaximumCandidateBytes = 1_048_576L }

    let stop () =
        if mode = "docker" then run "docker" $"kill {container}"
        else run "/usr/bin/pg_ctl" $"-D {root}/data stop -m immediate"

    let start () =
        if mode = "docker" then run "docker" $"start {container}"
        else run "/usr/bin/pg_ctl" $"-D {root}/data -l {root}/postgres.log -o \"-k {host} -h '' -p {port} -c fsync=on -c synchronous_commit=on -c full_page_writes=on\" start"

    let waitReady () =
        task {
            let mutable ready = false
            for _ in 1..100 do
                if not ready then
                    try
                        use probe = dataSource "postgres"
                        use! connection = probe.OpenConnectionAsync()
                        ready <- true
                    with _ -> do! Task.Delay 100
            if not ready then failwith "PostgreSQL unavailable"
        }

    let dump () =
        if mode = "docker" then
            databaseTool "pg_dump" "-Fc -f /tmp/observer.dump orchestration_o0"
            "/tmp/observer.dump"
        else
            let path = Path.Combine(root, "observer.dump")
            databaseTool "pg_dump" $"-Fc -f {path} orchestration_o0"
            path

    let restore path =
        databaseTool "dropdb" "--if-exists orchestration_observer_restore"
        databaseTool "createdb" "orchestration_observer_restore"
        databaseTool "pg_restore" $"-d orchestration_observer_restore {path}"

    let sha character = String.replicate 64 character
    let now = DateTimeOffset.Parse("2026-09-10T10:00:00Z")
    let projectId = Id.project(Guid.Parse "10000000-0000-0000-0000-000000000001")
    let sessionId = Id.session(Guid.Parse "20000000-0000-0000-0000-000000000002")
    let attemptId = Id.attempt(Guid.Parse "30000000-0000-0000-0000-000000000003")
    let proposalId = ProposalId.create(Guid.Parse "40000000-0000-0000-0000-000000000004")
    let workItem = WorkItemIdentity.create "R_kgDOExample" 123L "I_kwDOExample" 42L
    let budget = { TokenLimit = 100L; RuntimeSecondsLimit = 60L; CostMicrosLimit = 1_000L; Deadline = now.AddHours 1. }
    let usage tokens seconds cost = { Tokens = tokens; RuntimeSeconds = seconds; CostMicros = cost }
    let envelope (state: ObserverState) command =
        { CommandId = Id.command(Guid.NewGuid())
          ExpectedSequence = state.Sequence
          PrincipalId = "planner-owner"
          IssuedAt = now.AddSeconds -1.
          ExpiresAt = now.AddMinutes 5.
          Command = command }
    let observation () =
        let provenance = { Provider = "github-graphql"; QuerySha256 = sha "a"; EvidenceSha256 = sha "b"; CapturedAt = now.AddMinutes -2. }
        let draft =
            { ProjectId = projectId; SourceRevision = "project-rev-1"; WorkflowRevision = Id.revision 7L
              Generation = Id.generation 3L; ObservationSha256 = sha "0"; Provenance = provenance
              WorkItems = [ { Identity = workItem; MembershipItemId = "PVTI_item"; Archived = false } ]; NonWorkItemCount = 0 }
        { draft with ObservationSha256 = Observer.observationSha256 draft }


type PostgreSqlObserverStoreTests() =
    let cancellationToken = CancellationToken.None

    [<Fact>]
    member _.``typed budget attempt proposal lifecycle roundtrips through PostgreSQL``() =
        task {
            let! source, identity = Fixture.reset()
            use source = source
            let store = PostgreSqlObserverStore(Fixture.options source identity 0L) :> IObserverJournalStore
            let observerId = ObserverJournal.observerId Fixture.sessionId
            let mutable state = Observer.initial
            let append command =
                task {
                    let envelope = Fixture.envelope state command
                    let decision = Observer.decide Fixture.now state envelope
                    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
                    let request = ObserverJournal.appendRequest observerId Fixture.now envelope decision
                    let! outcome = store.AppendObserver(request, cancellationToken)
                    match outcome with
                    | ObserverAppended terminal -> Assert.Equal(decision.Receipt.Sequence, terminal)
                    | other -> failwithf "append failed: %A" other
                    state <- decision.Events |> List.fold Observer.evolve state
                }
            do! append (OpenSession(Fixture.sessionId, Fixture.projectId, Fixture.budget))
            let observation = Fixture.observation()
            do! append (RecordProjectObservation observation)
            do! append (StartPlanningAttempt(Fixture.attemptId, Fixture.usage 40L 20L 400L, Fixture.now.AddSeconds -30.))
            let proposal =
                { ProposalId = Fixture.proposalId; AttemptId = Fixture.attemptId; ObservationSha256 = observation.ObservationSha256
                  WorkflowRevision = Id.revision 7L; Generation = Id.generation 3L; Scope = "repo:123/issues"
                  NarrativeSha256 = Fixture.sha "d"; Actions = [ { Kind = InspectWorkItem; WorkItem = Fixture.workItem; ParametersSha256 = Fixture.sha "e" } ]
                  ProposedAt = Fixture.now }
            do! append (CompletePlanningAttempt(Fixture.attemptId, Fixture.usage 25L 10L 250L, proposal))
            let! recovered = store.RecoverObserver(observerId, cancellationToken)
            match recovered with
            | Error failures -> failwithf "recovery failed: %A" failures
            | Ok value ->
                Assert.Equal<ObserverState>(state, value.State)
                Assert.Equal(Fixture.budget, value.State.Budget.Value)
                Assert.True(value.State.Proposals.ContainsKey Fixture.proposalId)
        }

    [<Fact>]
    member _.``observer inbox deduplicates conflicts and sequence CAS``() =
        task {
            let! source, identity = Fixture.reset()
            use source = source
            let store = PostgreSqlObserverStore(Fixture.options source identity 0L) :> IObserverJournalStore
            let observerId = ObserverJournal.observerId Fixture.sessionId
            let envelope = Fixture.envelope Observer.initial (OpenSession(Fixture.sessionId, Fixture.projectId, Fixture.budget))
            let decision = Observer.decide Fixture.now Observer.initial envelope
            let request = ObserverJournal.appendRequest observerId Fixture.now envelope decision
            let mismatched = ObserverJournal.appendRequest "observer-session-v1-wrong" Fixture.now envelope decision
            let! mismatchedResult = store.AppendObserver(mismatched, cancellationToken)
            Assert.Equal<ObserverAppendOutcome>(ObserverInvalidAppend "observer-session-identity-mismatch", mismatchedResult)
            let! first = store.AppendObserver(request, cancellationToken)
            Assert.Equal<ObserverAppendOutcome>(ObserverAppended 1L, first)
            let! duplicate = store.AppendObserver({ request with Events = [] }, cancellationToken)
            Assert.Equal<ObserverAppendOutcome>(ObserverDuplicate 1L, duplicate)
            let! conflict = store.AppendObserver({ request with Command = { envelope with PrincipalId = "other" }; Events = [] }, cancellationToken)
            Assert.Equal<ObserverAppendOutcome>(ObserverConflict, conflict)
            let state = Observer.replay decision.Events
            let validEnvelope = Fixture.envelope state (RecordConversation { EntryId = Guid.NewGuid(); Role = Operator; BodySha256 = Fixture.sha "c"; RecordedAt = Fixture.now })
            let validDecision = Observer.decide Fixture.now state validEnvelope
            let staleEnvelope = { validEnvelope with ExpectedSequence = 0L }
            let stale = { ObserverJournal.appendRequest observerId Fixture.now staleEnvelope validDecision with Events = validDecision.Events |> List.mapi (fun index eventValue -> { ObserverId = observerId; Sequence = int64 index + 1L; EventId = Guid.NewGuid(); SchemaVersion = ObserverEventCodec.schemaVersion; SerializerVersion = ObserverEventCodec.serializerVersion; Event = eventValue; RecordedAt = Fixture.now }) }
            let! wrong = store.AppendObserver(stale, cancellationToken)
            Assert.Equal<ObserverAppendOutcome>(ObserverWrongExpectedSequence 1L, wrong)
            let second = ObserverJournal.appendRequest observerId Fixture.now validEnvelope validDecision
            let! appendedSecond = store.AppendObserver(second, cancellationToken)
            Assert.Equal<ObserverAppendOutcome>(ObserverAppended 2L, appendedSecond)
            let! _ = Fixture.sql "orchestration_o0" $"DELETE FROM fsgg_orchestration.observer_inbox WHERE observer_id='{observerId}' AND command_id='{Id.commandValue validEnvelope.CommandId}'"
            let! _ = Fixture.sql "orchestration_o0" $"DELETE FROM fsgg_orchestration.observer_event WHERE observer_id='{observerId}' AND sequence_number=2"
            let! truncatedRecovery = store.RecoverObserver(observerId, cancellationToken)
            match truncatedRecovery with
            | Error failures -> Assert.Contains(failures, function ObserverCorruptRecord(_, 2L) -> true | _ -> false)
            | _ -> failwith "truncated tail recovered"
            let! falseDuplicate = store.AppendObserver({ request with Events = [] }, cancellationToken)
            match falseDuplicate with
            | ObserverAppendUnavailable reason -> Assert.Contains("ObserverCorruptRecord", reason)
            | other -> failwithf "corrupt duplicate acknowledged: %A" other
        }

    [<Fact>]
    member _.``observer recovery refuses unknown versions and corrupt payload``() =
        task {
            let! source, identity = Fixture.reset()
            use source = source
            let store = PostgreSqlObserverStore(Fixture.options source identity 0L) :> IObserverJournalStore
            let observerId = ObserverJournal.observerId Fixture.sessionId
            let envelope = Fixture.envelope Observer.initial (OpenSession(Fixture.sessionId, Fixture.projectId, Fixture.budget))
            let decision = Observer.decide Fixture.now Observer.initial envelope
            let! _ = store.AppendObserver(ObserverJournal.appendRequest observerId Fixture.now envelope decision, cancellationToken)
            let! _ = Fixture.sql "orchestration_o0" $"UPDATE fsgg_orchestration.observer_event SET schema_version=99,serializer_version='future',payload=decode('00','hex') WHERE observer_id='{observerId}'"
            let! result = store.RecoverObserver(observerId, cancellationToken)
            match result with
            | Ok _ -> failwith "corrupt event recovered"
            | Error failures ->
                Assert.Contains(failures, function ObserverUnknownEventVersion(_, 1L, 99) -> true | _ -> false)
                Assert.Contains(failures, function ObserverUnknownSerializerVersion "future" -> true | _ -> false)
                Assert.Contains(failures, function ObserverCorruptRecord(_, 1L) -> true | _ -> false)
        }

    [<Fact>]
    member _.``observer metadata fence is mandatory for recovery and append``() =
        task {
            let! source, identity = Fixture.reset()
            use source = source
            let store = PostgreSqlObserverStore(Fixture.options source identity 0L) :> IObserverJournalStore
            let observerId = ObserverJournal.observerId Fixture.sessionId
            let envelope = Fixture.envelope Observer.initial (OpenSession(Fixture.sessionId, Fixture.projectId, Fixture.budget))
            let decision = Observer.decide Fixture.now Observer.initial envelope
            let request = ObserverJournal.appendRequest observerId Fixture.now envelope decision
            let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.observer_store_metadata SET schema_version=99"
            let! recovery = store.RecoverObserver(observerId, cancellationToken)
            match recovery with
            | Error failures -> Assert.Contains(failures, function ObserverIncompatibleDowngrade(99, 1) -> true | _ -> false)
            | _ -> failwith "future observer metadata recovered"
            let! write = store.AppendObserver(request, cancellationToken)
            match write with
            | ObserverAppendUnavailable reason -> Assert.Contains("ObserverIncompatibleDowngrade", reason)
            | other -> failwithf "future observer metadata wrote: %A" other
        }

    [<Fact>]
    member _.``observer reconnect recovers after PostgreSQL process replacement``() =
        task {
            let! source, identity = Fixture.reset()
            use source = source
            let store = PostgreSqlObserverStore(Fixture.options source identity 0L) :> IObserverJournalStore
            let observerId = ObserverJournal.observerId Fixture.sessionId
            let envelope = Fixture.envelope Observer.initial (OpenSession(Fixture.sessionId, Fixture.projectId, Fixture.budget))
            let decision = Observer.decide Fixture.now Observer.initial envelope
            let! _ = store.AppendObserver(ObserverJournal.appendRequest observerId Fixture.now envelope decision, cancellationToken)
            Fixture.stop()
            NpgsqlConnection.ClearAllPools()
            let! unavailable = store.RecoverObserver(observerId, cancellationToken)
            match unavailable with
            | Error failures -> Assert.Contains(failures, function ObserverStoreUnavailable _ -> true | _ -> false)
            | _ -> failwith "stopped database recovered"
            Fixture.start()
            do! Fixture.waitReady()
            let! recovered = store.RecoverObserver(observerId, cancellationToken)
            match recovered with
            | Ok value -> Assert.Equal(1L, value.State.Sequence)
            | Error failures -> failwithf "restart recovery failed: %A" failures
        }

    [<Fact>]
    member _.``restored old observer backup refuses recovery and append transactionally``() =
        task {
            let! source, identity = Fixture.reset()
            use source = source
            let store = PostgreSqlObserverStore(Fixture.options source identity 0L) :> IObserverJournalStore
            let observerId = ObserverJournal.observerId Fixture.sessionId
            let envelope = Fixture.envelope Observer.initial (OpenSession(Fixture.sessionId, Fixture.projectId, Fixture.budget))
            let decision = Observer.decide Fixture.now Observer.initial envelope
            let! _ = store.AppendObserver(ObserverJournal.appendRequest observerId Fixture.now envelope decision, cancellationToken)
            let dump = Fixture.dump()
            let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.store_metadata SET generation_fence=2"
            Fixture.restore dump
            use restored = Fixture.dataSource "orchestration_observer_restore"
            let restoredStore = PostgreSqlObserverStore(Fixture.options restored identity 2L) :> IObserverJournalStore
            let! recovery = restoredStore.RecoverObserver(observerId, cancellationToken)
            match recovery with
            | Error failures -> Assert.Contains(failures, function ObserverBackupRequiresReconciliation "generation-fence-regressed" -> true | _ -> false)
            | _ -> failwith "old restore recovered"
            let state = Observer.replay decision.Events
            let next = Fixture.envelope state (RecordConversation { EntryId = Guid.NewGuid(); Role = Operator; BodySha256 = Fixture.sha "d"; RecordedAt = Fixture.now })
            let nextDecision = Observer.decide Fixture.now state next
            let! write = restoredStore.AppendObserver(ObserverJournal.appendRequest observerId Fixture.now next nextDecision, cancellationToken)
            match write with
            | ObserverAppendUnavailable reason -> Assert.Contains("generation-fence-regressed", reason)
            | other -> failwithf "old restore wrote: %A" other
        }
