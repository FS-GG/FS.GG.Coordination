namespace FS.GG.Coordination.Orchestration.PostgreSql.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Persistence
open Npgsql
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.PostgreSql

module private Fixture =
    let private environment name fallback =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value -> value
    let mode = environment "FSGG_PG_MODE" "binary"
    let root =
        match Environment.GetEnvironmentVariable "FSGG_PG_ROOT" with
        | null | "" when mode = "binary" -> File.ReadAllText("/tmp/o0-postgresql-current-path").Trim()
        | null | "" -> "/tmp"
        | value -> value
    let socket = Path.Combine(root, "socket")
    let host = environment "FSGG_PG_HOST" socket
    let port = environment "FSGG_PG_PORT" "55439"
    let username = environment "FSGG_PG_USERNAME" "developer"
    let container = environment "FSGG_PG_CONTAINER" ""
    let connectionString database = $"Host={host};Port={port};Database={database};Username={username};Pooling=false"
    let dataSource database = NpgsqlDataSource.Create(connectionString database)
    let sha (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let run (executable: string) (arguments: string) =
        let startInfo = ProcessStartInfo(executable, arguments)
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        use child = Process.Start startInfo
        child.WaitForExit(30_000) |> ignore
        if not child.HasExited || child.ExitCode <> 0 then
            failwith $"{executable} failed: {child.StandardError.ReadToEnd()}"

    let private databaseTool tool arguments =
        if mode = "docker" then run "docker" $"exec {container} {tool} -U {username} {arguments}"
        else run $"/usr/bin/{tool}" $"-h {host} -p {port} -U {username} {arguments}"

    let stopImmediate () =
        if mode = "docker" then run "docker" $"kill {container}"
        else run "/usr/bin/pg_ctl" $"-D {root}/data stop -m immediate"

    let start () =
        if mode = "docker" then run "docker" $"start {container}"
        else run "/usr/bin/pg_ctl" $"-D {root}/data -l {root}/postgres.log -o \"-k {socket} -h '' -p {port} -c fsync=on -c synchronous_commit=on -c full_page_writes=on\" start"

    let waitReady () =
        task {
            let mutable ready = false
            let mutable attempt = 0
            while not ready && attempt < 100 do
                attempt <- attempt + 1
                try
                    use probe = dataSource "postgres"
                    use! connection = probe.OpenConnectionAsync()
                    ready <- true
                with _ -> do! Task.Delay 100
            if not ready then failwith "PostgreSQL did not become ready"
        }

    let dump () =
        if mode = "docker" then
            databaseTool "pg_dump" "-Fc -f /tmp/orchestration_o0.dump orchestration_o0"
            "/tmp/orchestration_o0.dump"
        else
            let path = Path.Combine(root, "orchestration_o0.dump")
            databaseTool "pg_dump" $"-Fc -f {path} orchestration_o0"
            path

    let restore dumpPath =
        databaseTool "dropdb" "--if-exists orchestration_o0_restore"
        databaseTool "createdb" "orchestration_o0_restore"
        databaseTool "pg_restore" $"-d orchestration_o0_restore {dumpPath}"

    let sql database statement =
        task {
            use dataSource = dataSource database
            use! connection = dataSource.OpenConnectionAsync()
            use command = new NpgsqlCommand(statement, connection)
            return! command.ExecuteNonQueryAsync()
        }

    let reset () =
        task {
            let dataSource = dataSource "orchestration_o0"
            use! connection = dataSource.OpenConnectionAsync()
            use drop = new NpgsqlCommand("DROP SCHEMA IF EXISTS fsgg_orchestration CASCADE", connection)
            let! _ = drop.ExecuteNonQueryAsync()
            do! connection.CloseAsync()
            let! identity = PostgreSqlSchema.migrate dataSource CancellationToken.None
            return dataSource, identity
        }

    let options dataSource identity minimumFence =
        { DataSource = dataSource
          StoreId = "private-pg18-lab"
          BackupIdentity = identity
          MinimumGenerationFence = minimumFence
          RuntimeSchemaVersion = 1
          SupportedEventSchemaVersions = Set [ 1; 2 ]
          SupportedSerializerVersions = Set [ EventEnvelope.serializerVersion ]
          MaximumCandidateBytes = 1_048_576L }

    let event persistenceId sequence effect payload =
        let coreEvent =
            match effect with
            | NoEffect -> PausedEvent payload
            | IntentAdded intent -> EffectIntentRecorded intent
            | Settled operationId -> EffectSettled(operationId, Applied payload)
        let envelope = EventEnvelope.encode coreEvent
        { PersistenceId = persistenceId
          Sequence = sequence
          EventId = Guid.NewGuid()
          SchemaVersion = 1
          SerializerVersion = EventEnvelope.serializerVersion
          Payload = envelope
          PayloadSha256 = sha envelope
          EffectChange = effect
          RecordedAt = DateTimeOffset.UtcNow }

    let coreEvent persistenceId sequence (eventValue: Event) =
        let payload = EventEnvelope.encode eventValue
        { PersistenceId = persistenceId; Sequence = sequence; EventId = Guid.NewGuid(); SchemaVersion = 1
          SerializerVersion = EventEnvelope.serializerVersion; Payload = payload; PayloadSha256 = sha payload
          EffectChange = NoEffect; RecordedAt = DateTimeOffset.UtcNow }

    let append persistenceId expected commandId bodyHash events =
        { Inbox =
            { PersistenceId = persistenceId
              CommandId = commandId
              BodySha256 = bodyHash
              ReceivedAt = DateTimeOffset.UtcNow }
          ExpectedSequence = expected
          Events = events }

    let candidate (label: string) =
        let bytes = Encoding.UTF8.GetBytes label
        let digest = sha bytes
        let value =
            { CandidateId = Id.candidate(Guid.NewGuid())
              BaselineSha = String.replicate 40 "a"; HeadSha = String.replicate 40 "b"; TreeSha = String.replicate 40 "c"
              ManifestSha256 = sha(Encoding.UTF8.GetBytes $"manifest:{label}"); ContentSha256 = digest
              MediaType = "application/vnd.fsgg.runner-candidate+zip"; SizeBytes = int64 bytes.LongLength; RetainUntil = DateTimeOffset.UtcNow.AddDays 1.0
              Location = ContentAddressedObject($"sha256/{digest}") }
        { Candidate = value; Bytes = bytes }

type private MarkerActor(persistenceId: string) as this =
    inherit UntypedPersistentActor()
    let mutable count = 0
    override _.PersistenceId = persistenceId
    override _.OnRecover message =
        match message with :? string -> count <- count + 1 | _ -> ()
    override _.OnCommand message =
        match message with
        | :? string as text when text = "count" -> this.Sender.Tell count
        | :? string as text ->
            let replyTo = this.Sender
            this.Persist(text, fun persisted -> count <- count + 1; replyTo.Tell persisted)
        | _ -> this.Unhandled message

type PostgreSqlStoreTests() =
    let cancellationToken = CancellationToken.None

    [<Fact>]
    member _.``native delivery readback and appended effect kinds survive recovery``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let! _ = Fixture.sql "orchestration_o0" "ALTER TABLE fsgg_orchestration.event DROP CONSTRAINT ck_effect_shape; ALTER TABLE fsgg_orchestration.event ADD CONSTRAINT ck_effect_shape CHECK (effect_kind BETWEEN 0 AND 4)"
        let! migratedIdentity = PostgreSqlSchema.migrate dataSource cancellationToken
        Assert.Equal(identity,migratedIdentity)
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-hosted-delivery"
        let operationId = Id.operation(Guid.NewGuid())
        let routeId = Guid.NewGuid()
        let attemptId = Id.attempt(Guid.NewGuid())
        let candidateId = Id.candidate(Guid.NewGuid())
        let intent =
            { OperationId=operationId; Kind=ReadNativeDelivery; Generation=Id.generation 1L
              WorkflowRevision=Id.revision 8L; ResourceId="github-pr-node"; PayloadSha256=String.replicate 64 "a" }
        let readback =
            { OperationId=operationId; RouteId=routeId; AttemptId=attemptId; CandidateId=candidateId
              RepositoryNodeId="R_repo"; PullRequestNodeId="PR_node"
              CandidateHeadSha=String.replicate 40 "b"; ObservedPullRequestHeadSha=String.replicate 40 "b"
              MergeCommitSha=String.replicate 40 "c"; ProviderRevision="github-pr-revision-1"
              Generation=Id.generation 1L; WorkflowRevision=Id.revision 8L
              ObservedAt=DateTimeOffset.UtcNow; Merged=true }
        let intentEvent = Fixture.event persistenceId 1L (IntentAdded intent) "intent"
        let readbackEvent = Fixture.coreEvent persistenceId 2L (NativeDeliveryReadbackAccepted readback)
        let settledBase = Fixture.coreEvent persistenceId 3L (EffectSettled(operationId,Applied readback.ProviderRevision))
        let settledEvent = { settledBase with EffectChange=Settled operationId }
        let body = Fixture.sha(Encoding.UTF8.GetBytes "hosted-delivery-command")
        let! appended = store.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [intentEvent;readbackEvent;settledEvent], cancellationToken)
        Assert.Equal<AppendOutcome>(Appended 3L, appended)
        let! recovered = store.Recover(persistenceId, cancellationToken)
        match recovered with
        | Error failures -> failwithf "hosted delivery did not recover: %A" failures
        | Ok result ->
            let state = result.Events |> List.map(fun value -> EventEnvelope.tryDecode value.Payload |> Result.defaultWith failwith) |> replay
            Assert.Equal(readback, state.NativeDeliveryReadbacks[operationId])
            match state.Operations[operationId] with
            | OperationState.Settled(stored,Applied revision) -> Assert.Equal(ReadNativeDelivery,stored.Kind); Assert.Equal(readback.ProviderRevision,revision)
            | other -> failwithf "unexpected recovered operation: %A" other
    }

    [<Fact>]
    member _.``startup pause and hosted route readback survive PostgreSQL recovery``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-startup-readback"
        let workItem = WorkItemIdentity.create "R_repo_node" 17L "I_issue_node" 42L
        let readback =
            { RouteId=Guid.NewGuid();WorkItemId=workItem;RepositoryNodeId="R_repo_node"
              ProviderRevision="github-route-revision-1";EvidenceSha256=String.replicate 64 "f"
              Generation=Id.generation 1L;WorkflowRevision=Id.revision 8L;ObservedAt=DateTimeOffset.UtcNow }
        let pauseEvent = Fixture.coreEvent persistenceId 1L (StartupPausedEvent "process-startup")
        let readbackEvent = Fixture.coreEvent persistenceId 2L (HostedRouteReadbackAccepted readback)
        let body = Fixture.sha(Encoding.UTF8.GetBytes "startup-readback")
        let! appended = store.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [pauseEvent;readbackEvent], cancellationToken)
        Assert.Equal<AppendOutcome>(Appended 2L,appended)
        let! recovered = store.Recover(persistenceId,cancellationToken)
        match recovered with
        | Error failures -> failwithf "startup readback did not recover: %A" failures
        | Ok result ->
            let events = result.Events |> List.map(fun value -> EventEnvelope.tryDecode value.Payload |> Result.defaultWith failwith)
            Assert.Equal<Event list>([StartupPausedEvent "process-startup";HostedRouteReadbackAccepted readback],events)
            let afterPause = evolve initial events[0]
            Assert.False(afterPause.ReadbackCurrent)
            Assert.Equal(Paused "process-startup",afterPause.Control)
            Assert.True((evolve afterPause events[1]).ReadbackCurrent)
    }

    [<Fact>]
    member _.``migration and exact readiness succeed``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let! readiness = store.CheckReadiness cancellationToken
        Assert.Equal<Result<unit, ReadinessFailure list>>(Ok(), readiness)
    }

    [<Fact>]
    member _.``append atomically deduplicates and enforces sequence``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-atomic"
        let commandId = Id.command(Guid.NewGuid())
        let body = Fixture.sha(Encoding.UTF8.GetBytes "command")
        let first = Fixture.event persistenceId 1L NoEffect "accepted"
        let! appended = store.Append(Fixture.append persistenceId 0L commandId body [ first ], cancellationToken)
        Assert.Equal<AppendOutcome>(Appended 1L, appended)
        let! duplicate = store.Append(Fixture.append persistenceId 99L commandId body [], cancellationToken)
        Assert.Equal<AppendOutcome>(Duplicate 1L, duplicate)
        let changed = Fixture.sha(Encoding.UTF8.GetBytes "changed")
        let! conflict = store.Append(Fixture.append persistenceId 1L commandId changed [ Fixture.event persistenceId 2L NoEffect "changed" ], cancellationToken)
        Assert.Equal<AppendOutcome>(Conflict, conflict)
        let! wrong = store.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [ Fixture.event persistenceId 1L NoEffect "again" ], cancellationToken)
        Assert.Equal<AppendOutcome>(WrongExpectedSequence 1L, wrong)
        let! empty = store.Append(Fixture.append persistenceId 1L (Id.command(Guid.NewGuid())) body [], cancellationToken)
        Assert.Equal<AppendOutcome>(InvalidAppend "new-command-requires-events", empty)
    }

    [<Fact>]
    member _.``lost append response is resolved by identical inbox retry``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-lost-response"
        let commandId = Id.command(Guid.NewGuid())
        let body = Fixture.sha(Encoding.UTF8.GetBytes "same-command")
        let request = Fixture.append persistenceId 0L commandId body [ Fixture.event persistenceId 1L NoEffect "committed-before-client-lost-response" ]
        let! _lostResponse = store.Append(request, cancellationToken)
        let! observed = store.Append(Fixture.append persistenceId 99L commandId body [], cancellationToken)
        Assert.Equal<AppendOutcome>(Duplicate 1L, observed)
    }

    [<Fact>]
    member _.``concurrent owners cannot both append one sequence``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-concurrent"
        let request (suffix: string) =
            let body = Fixture.sha(Encoding.UTF8.GetBytes suffix)
            Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [ Fixture.event persistenceId 1L NoEffect suffix ]
        let! outcomes = Task.WhenAll [| store.Append(request "left", cancellationToken); store.Append(request "right", cancellationToken) |]
        Assert.Equal(1, outcomes |> Array.filter (function Appended 1L -> true | _ -> false) |> Array.length)
        let! recovered = store.Recover(persistenceId, cancellationToken)
        match recovered with
        | Ok result -> Assert.Single result.Events |> ignore
        | Error failures -> failwithf "recovery failed: %A" failures
    }

    [<Fact>]
    member _.``pending effects are derived from checked envelopes``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-effects"
        let operationId = Id.operation(Guid.NewGuid())
        let intent =
            { OperationId = operationId
              Kind = DispatchRunner
              Generation = Id.generation 3L
              WorkflowRevision = Id.revision 7L
              ResourceId = "runner:alpha"
              PayloadSha256 = Fixture.sha(Encoding.UTF8.GetBytes "effect") }
        let body = Fixture.sha(Encoding.UTF8.GetBytes "command")
        let intentEvent = Fixture.event persistenceId 1L (IntentAdded intent) "intent"
        let! _ = store.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [ intentEvent ], cancellationToken)
        let! pending = store.Recover(persistenceId, cancellationToken)
        match pending with
        | Ok result -> Assert.Equal<EffectIntent list>([ intent ], result.UnsettledEffects); Assert.True result.RequiresExternalReconciliation
        | Error failures -> failwithf "recovery failed: %A" failures
        let mismatched = { Fixture.event persistenceId 2L NoEffect "mismatch" with EffectChange = Settled operationId }
        let! refused = store.Append(Fixture.append persistenceId 1L (Id.command(Guid.NewGuid())) body [ mismatched ], cancellationToken)
        Assert.Equal<AppendOutcome>(InvalidAppend "effect-metadata-mismatch", refused)
        let settled = Fixture.event persistenceId 2L (Settled operationId) "settled"
        let! _ = store.Append(Fixture.append persistenceId 1L (Id.command(Guid.NewGuid())) body [ settled ], cancellationToken)
        let! recovered = store.Recover(persistenceId, cancellationToken)
        match recovered with
        | Ok result -> Assert.Empty result.UnsettledEffects
        | Error failures -> failwithf "recovery failed: %A" failures
    }

    [<Fact>]
    member _.``snapshot cannot advance beyond journal``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let payload = Encoding.UTF8.GetBytes "snapshot"
        let snapshot = { PersistenceId = "work-item-v1-snapshot"; Sequence = 1L; SchemaVersion = 1; Payload = payload; PayloadSha256 = Fixture.sha payload }
        let! ahead = store.SaveSnapshot(snapshot, cancellationToken)
        Assert.Equal<Result<unit, ReadinessFailure>>(Error(SnapshotAheadOfJournal snapshot.PersistenceId), ahead)
        let body = Fixture.sha(Encoding.UTF8.GetBytes "command")
        let! _ = store.Append(Fixture.append snapshot.PersistenceId 0L (Id.command(Guid.NewGuid())) body [ Fixture.event snapshot.PersistenceId 1L NoEffect "event" ], cancellationToken)
        let! saved = store.SaveSnapshot(snapshot, cancellationToken)
        Assert.Equal<Result<unit, ReadinessFailure>>(Ok(), saved)
        let! identical = store.SaveSnapshot(snapshot, cancellationToken)
        Assert.Equal<Result<unit, ReadinessFailure>>(Ok(), identical)
        let changedPayload = Encoding.UTF8.GetBytes "changed-snapshot"
        let changed = { snapshot with Payload = changedPayload; PayloadSha256 = Fixture.sha changedPayload }
        let! conflict = store.SaveSnapshot(changed, cancellationToken)
        Assert.Equal<Result<unit, ReadinessFailure>>(Error(CorruptRecord(snapshot.PersistenceId, 1L)), conflict)
        let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.domain_snapshot SET schema_version=99 WHERE persistence_id='work-item-v1-snapshot'"
        let! unknown = store.Recover(snapshot.PersistenceId, cancellationToken)
        match unknown with Error failures -> Assert.Contains(failures, function UnknownEventVersion(_, 1L, 99) -> true | _ -> false) | _ -> failwith "future snapshot recovered"
    }

    [<Fact>]
    member _.``unknown serializer schema and corrupt bytes fail recovery``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-corrupt"
        let body = Fixture.sha(Encoding.UTF8.GetBytes "command")
        let! _ = store.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [ Fixture.event persistenceId 1L NoEffect "event" ], cancellationToken)
        let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.event SET schema_version=99,serializer_version='future',payload=decode('00','hex') WHERE persistence_id='work-item-v1-corrupt'"
        let! recovered = store.Recover(persistenceId, cancellationToken)
        match recovered with
        | Ok _ -> failwith "corrupt record recovered"
        | Error failures ->
            Assert.Contains(failures, function UnknownEventVersion(_, 1L, 99) -> true | _ -> false)
            Assert.Contains(failures, function UnknownSerializerVersion "future" -> true | _ -> false)
            Assert.Contains(failures, function CorruptRecord(_, 1L) -> true | _ -> false)
    }

    [<Fact>]
    member _.``supported row schema versions replay the same Core event codec``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-schema-upgrade"
        let first = Fixture.event persistenceId 1L NoEffect "v1"
        let v2Payload = EventEnvelope.encode ResumedEvent
        let second =
            { Fixture.event persistenceId 2L NoEffect "placeholder" with
                SchemaVersion = 2; Payload = v2Payload; PayloadSha256 = Fixture.sha v2Payload }
        let body = Fixture.sha(Encoding.UTF8.GetBytes "upgrade")
        let! outcome = store.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [ first; second ], cancellationToken)
        Assert.Equal<AppendOutcome>(Appended 2L, outcome)
        let! recovered = store.Recover(persistenceId, cancellationToken)
        match recovered with
        | Ok result ->
            Assert.Equal<int list>([ 1; 2 ], result.Events |> List.map _.SchemaVersion)
            Assert.Equal<Result<Event,string>>(Ok ResumedEvent, EventEnvelope.tryDecode result.Events[1].Payload)
        | Error failures -> failwithf "upgrade recovery failed: %A" failures
    }

    [<Fact>]
    member _.``Core decided lifecycle roundtrips private identities through PostgreSQL``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let journal = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let candidateStore = PostgreSqlStore(Fixture.options dataSource identity 0L) :> ICandidateStore
        let workItem = WorkItemIdentity.create "R_repo_node" 17L "I_issue_node" 42L
        let persistenceId = WorkItemIdentity.persistenceId workItem
        let projectId = Id.project(Guid.NewGuid())
        let workflowRevision = Id.revision 7L
        let planning =
            { ProjectId = projectId; WorkItemId = workItem; WorkflowRevision = workflowRevision
              CanonicalSha256 = String.replicate 64 "a"; BoardMembershipIds = [ "PVTI_board" ]
              CapturedAt = DateTimeOffset.UtcNow }
        let budget =
            { TokenLimit = 1000L; RuntimeSecondsLimit = 600L; CostMicrosLimit = 100000L
              Deadline = DateTimeOffset.UtcNow.AddHours 1.0 }
        let mutable state = FS.GG.Coordination.Core.Orchestration.initial
        let mutable terminalSequence = 0L
        let appendCommand command = task {
            let now = DateTimeOffset.UtcNow
            let envelope =
                { CommandId = Id.command(Guid.NewGuid()); ProtocolVersion = Id.protocolVersion 1 0
                  ExpectedRevision = state.Revision; ExpectedGeneration = state.Generation
                  PrincipalId = "test:operator"; SessionId = None; IssuedAt = now; ExpiresAt = now.AddMinutes 5.0
                  Command = command }
            let decision = WorkItem.decide now state envelope
            Assert.Equal<ReceiptDisposition>(Accepted, decision.Receipt.Disposition)
            let firstSequence = terminalSequence + 1L
            let serialized = decision.Events |> List.mapi (fun index value -> Fixture.coreEvent persistenceId (firstSequence + int64 index) value)
            let request = Fixture.append persistenceId terminalSequence envelope.CommandId decision.Receipt.BodySha256 serialized
            let! outcome = journal.Append(request, cancellationToken)
            state <- decision.Events |> List.fold FS.GG.Coordination.Core.Orchestration.evolve state
            match outcome with Appended sequence -> terminalSequence <- sequence | _ -> failwithf "append failed: %A" outcome
        }
        do! appendCommand (Admit(planning, budget))
        let reservationId = Id.reservation(Guid.NewGuid())
        do! appendCommand (Reserve(reservationId, DateTimeOffset.UtcNow.AddMinutes 30.0, Set [ "claim:repo" ]))
        do! appendCommand (ObserveClaim { ClaimId = "claim:repo"; Generation = state.Generation; WorkflowRevision = workflowRevision; ObservedAt = DateTimeOffset.UtcNow })
        let attemptId, sessionId = Id.attempt(Guid.NewGuid()), Id.session(Guid.NewGuid())
        let runner =
            { RunnerId = Id.runner(Guid.NewGuid()); PrincipalId = "runner:one"; FingerprintSha256 = String.replicate 64 "b"
              Generation = state.Generation; ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 30.0 }
        do! appendCommand (StartAttempt(attemptId, sessionId, runner))
        let candidate = Fixture.candidate "lifecycle-candidate"
        let! stored = candidateStore.Put(candidate, cancellationToken)
        let receipt = match stored with Ok value -> value | Error error -> failwithf "candidate put failed: %A" error
        do! appendCommand (RecordCandidate(candidate.Candidate, receipt))
        do! appendCommand (ObserveAttempt(attemptId, OutcomeUnknown "provider-response-lost"))
        do! appendCommand (Pause "operator")
        do! appendCommand (RequestCancel "cancel")
        let! recovered = journal.Recover(persistenceId, cancellationToken)
        match recovered with
        | Error failures -> failwithf "lifecycle recovery failed: %A" failures
        | Ok result ->
            let decoded = result.Events |> List.map (fun value -> EventEnvelope.tryDecode value.Payload)
            Assert.DoesNotContain(decoded, function Error _ -> true | _ -> false)
            let replayed = decoded |> List.choose (function Ok value -> Some value | _ -> None) |> FS.GG.Coordination.Core.Orchestration.replay
            Assert.Equal<State>(state, replayed)
            Assert.True(Map.containsKey candidate.Candidate.CandidateId replayed.Candidates)
            Assert.True(Map.containsKey attemptId replayed.Attempts)
            Assert.Equal<ControlState>(CancelPending "cancel", replayed.Control)
            Assert.Equal<Budget option>(Some budget, replayed.Budget)
    }

    [<Fact>]
    member _.``readiness refuses migration downgrade backup and read only``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let readyStore = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.store_metadata SET migration_state='applying'"
        let! interrupted = readyStore.CheckReadiness cancellationToken
        match interrupted with Error failures -> Assert.Contains(failures, function MigrationInterrupted "applying" -> true | _ -> false) | _ -> failwith "interrupted migration ready"
        let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.store_metadata SET migration_state='ready',schema_version=2"
        let! downgrade = readyStore.CheckReadiness cancellationToken
        match downgrade with Error failures -> Assert.Contains(failures, function IncompatibleDowngrade(2,1) -> true | _ -> false) | _ -> failwith "downgrade ready"
        let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.store_metadata SET schema_version=1"
        let wrongBackup = PostgreSqlStore(Fixture.options dataSource (Guid.NewGuid().ToString()) 0L) :> IJournalStore
        let! backup = wrongBackup.CheckReadiness cancellationToken
        match backup with Error failures -> Assert.Contains(failures, function BackupRequiresReconciliation _ -> true | _ -> false) | _ -> failwith "old backup ready"
        let! _ = Fixture.sql "orchestration_o0" "ALTER DATABASE orchestration_o0 SET default_transaction_read_only=on"
        NpgsqlConnection.ClearAllPools()
        let! readOnly = readyStore.CheckReadiness cancellationToken
        match readOnly with Error failures -> Assert.Contains(ReadOnlyStore, failures) | _ -> failwith "read-only store ready"
        let! _ = Fixture.sql "postgres" "ALTER DATABASE orchestration_o0 SET default_transaction_read_only=off"
        NpgsqlConnection.ClearAllPools()
    }

    [<Fact>]
    member _.``candidate acknowledgement requires recoverable matching bytes``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> ICandidateStore
        let request = Fixture.candidate "candidate archive"
        let candidate, bytes, digest = request.Candidate, request.Bytes, request.Candidate.ContentSha256
        let! stored = store.Put(request, cancellationToken)
        let receipt = match stored with Ok value -> value | Error error -> failwithf "put failed: %A" error
        Assert.Equal(digest, receipt.ContentSha256)
        let! readBack = store.Read(candidate.CandidateId, cancellationToken)
        match readBack with Ok value -> Assert.Equal<byte array>(bytes, value.Bytes) | Error reason -> failwith reason
        let! existing = store.Put({ Candidate = candidate; Bytes = bytes }, cancellationToken)
        match existing with Error(Existing _) -> () | _ -> failwithf "expected existing, got %A" existing
        let! changedHead = store.Put({ request with Candidate = { candidate with HeadSha = String.replicate 40 "d" } }, cancellationToken)
        Assert.Equal<Result<CandidateStorageReceipt, CandidatePutOutcome>>(Error IdentityConflict, changedHead)
        let! changedRetention = store.Put({ request with Candidate = { candidate with RetainUntil = candidate.RetainUntil.AddDays 1.0 } }, cancellationToken)
        Assert.Equal<Result<CandidateStorageReceipt, CandidatePutOutcome>>(Error IdentityConflict, changedRetention)
        let! digestConflict = store.Put({ Candidate = { candidate with CandidateId = Id.candidate(Guid.NewGuid()); ContentSha256 = String.replicate 64 "0" }; Bytes = bytes }, cancellationToken)
        Assert.Equal<Result<CandidateStorageReceipt, CandidatePutOutcome>>(Error DigestConflict, digestConflict)
        let! fakeLocation = store.Put({ Candidate = { candidate with CandidateId = Id.candidate(Guid.NewGuid()); Location = ContentAddressedObject "host/path" }; Bytes = bytes }, cancellationToken)
        Assert.Equal<Result<CandidateStorageReceipt, CandidatePutOutcome>>(Error IdentityConflict, fakeLocation)
        let! remote = store.Put({ Candidate = { candidate with CandidateId = Id.candidate(Guid.NewGuid()); Location = ImmutableRemoteGitRef("repo", String.replicate 40 "d", "refs/fsgg/candidate") }; Bytes = bytes }, cancellationToken)
        Assert.Equal<Result<CandidateStorageReceipt, CandidatePutOutcome>>(Error InvalidArchive, remote)
        let! _ = Fixture.sql "orchestration_o0" $"UPDATE fsgg_orchestration.candidate_object SET bytes=decode(repeat('00',size_bytes::integer),'hex') WHERE content_sha256='{digest}'"
        let! corruptDuplicate = store.Put(request, cancellationToken)
        Assert.Equal<Result<CandidateStorageReceipt, CandidatePutOutcome>>(Error IdentityConflict, corruptDuplicate)
        let! quarantined = store.Quarantine(candidate.CandidateId, "restore-check", cancellationToken)
        Assert.Equal<Result<unit,string>>(Ok(), quarantined)
        let! quarantinedDuplicate = store.Put(request, cancellationToken)
        Assert.Equal<Result<CandidateStorageReceipt, CandidatePutOutcome>>(Error IdentityConflict, quarantinedDuplicate)
        let! refusedRead = store.Read(candidate.CandidateId, cancellationToken)
        Assert.Equal<Result<CandidatePut,string>>(Error "candidate-quarantined", refusedRead)
    }

    [<Fact>]
    member _.``bounded cleanup deletes staging but retains acknowledged object``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> ICandidateStore
        let digest = String.replicate 64 "a"
        let! _ = Fixture.sql "orchestration_o0" $"INSERT INTO fsgg_orchestration.candidate_upload_staging VALUES(gen_random_uuid(),gen_random_uuid(),'{digest}',decode('00','hex'),now()-interval '2 day',now()-interval '1 day')"
        let! count = store.CleanupUnreferenced(DateTimeOffset.UtcNow.AddDays(-1.0), 1, cancellationToken)
        Assert.Equal(1, count)
    }

    [<Fact>]
    member _.``PostgreSQL process kill reports unavailable then preserves committed event``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let store = PostgreSqlStore(Fixture.options dataSource identity 0L) :> IJournalStore
        let persistenceId = "work-item-v1-process-kill"
        let body = Fixture.sha(Encoding.UTF8.GetBytes "command")
        let! _ = store.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [ Fixture.event persistenceId 1L NoEffect "event" ], cancellationToken)
        let candidateStore = PostgreSqlStore(Fixture.options dataSource identity 0L) :> ICandidateStore
        let candidate = Fixture.candidate "restart-candidate"
        let! acknowledged = candidateStore.Put(candidate, cancellationToken)
        match acknowledged with Ok _ -> () | Error error -> failwithf "candidate not acknowledged: %A" error
        Fixture.stopImmediate ()
        NpgsqlConnection.ClearAllPools()
        let! unavailable = store.Recover(persistenceId, cancellationToken)
        match unavailable with Error failures -> Assert.Contains(failures, function StoreUnavailable _ -> true | _ -> false) | _ -> failwith "stopped database recovered"
        Fixture.start ()
        do! Fixture.waitReady ()
        let! recovered = store.Recover(persistenceId, cancellationToken)
        match recovered with Ok result -> Assert.Single result.Events |> ignore | Error failures -> failwithf "restart recovery failed: %A" failures
        let! recoveredCandidate = candidateStore.Read(candidate.Candidate.CandidateId, cancellationToken)
        match recoveredCandidate with Ok stored -> Assert.Equal<byte array>(candidate.Bytes, stored.Bytes) | Error error -> failwith error
    }

    [<Fact>]
    member _.``logical backup restore is fenced and exposes missing post-backup acknowledgement``() = task {
        let! source, identity = Fixture.reset()
        use source = source
        let journal = PostgreSqlStore(Fixture.options source identity 0L) :> IJournalStore
        let candidates = PostgreSqlStore(Fixture.options source identity 0L) :> ICandidateStore
        let persistenceId = "work-item-v1-backup"
        let operationId = Id.operation(Guid.NewGuid())
        let intent =
            { OperationId = operationId; Kind = InspectExternalOperation; Generation = Id.generation 0L
              WorkflowRevision = Id.revision 0L; ResourceId = "provider:backup"; PayloadSha256 = String.replicate 64 "a" }
        let body = Fixture.sha(Encoding.UTF8.GetBytes "backup-command")
        let! _ = journal.Append(Fixture.append persistenceId 0L (Id.command(Guid.NewGuid())) body [ Fixture.event persistenceId 1L (IntentAdded intent) "pending" ], cancellationToken)
        let before = Fixture.candidate "before-backup"
        let! _ = candidates.Put(before, cancellationToken)
        let dump = Fixture.dump ()
        let after = Fixture.candidate "acknowledged-after-backup"
        let! afterReceipt = candidates.Put(after, cancellationToken)
        match afterReceipt with Ok _ -> () | Error error -> failwithf "post-backup put failed: %A" error
        let! _ = Fixture.sql "orchestration_o0" "UPDATE fsgg_orchestration.store_metadata SET generation_fence=2"
        Fixture.restore dump
        use restored = Fixture.dataSource "orchestration_o0_restore"
        let restoredOptions = Fixture.options restored identity 2L
        let restoredJournal = PostgreSqlStore(restoredOptions) :> IJournalStore
        let! directRecovery = restoredJournal.Recover(persistenceId, cancellationToken)
        match directRecovery with Error failures -> Assert.Contains(failures, function BackupRequiresReconciliation "generation-fence-regressed" -> true | _ -> false) | _ -> failwith "old backup recovered without gate"
        let directEvent = Fixture.event persistenceId 2L NoEffect "must-not-write-old-backup"
        let! directWrite = restoredJournal.Append(Fixture.append persistenceId 1L (Id.command(Guid.NewGuid())) body [ directEvent ], cancellationToken)
        Assert.Equal<AppendOutcome>(InvalidAppend "store-not-ready", directWrite)
        let! fenced = restoredJournal.CheckReadiness cancellationToken
        match fenced with Error failures -> Assert.Contains(failures, function BackupRequiresReconciliation "generation-fence-regressed" -> true | _ -> false) | _ -> failwith "old backup was not fenced"
        let restoredCandidates = PostgreSqlStore(restoredOptions) :> ICandidateStore
        let! beforeRead = restoredCandidates.Read(before.Candidate.CandidateId, cancellationToken)
        match beforeRead with Ok stored -> Assert.Equal<byte array>(before.Bytes, stored.Bytes) | Error error -> failwith error
        let! afterRead = restoredCandidates.Read(after.Candidate.CandidateId, cancellationToken)
        Assert.Equal<Result<CandidatePut,string>>(Error "candidate-not-found", afterRead)
        let! _ = Fixture.sql "orchestration_o0_restore" "UPDATE fsgg_orchestration.store_metadata SET generation_fence=2"
        let reconciler = PostgreSqlStore(restoredOptions) :> IBackupReconciler
        let! reconciliation = reconciler.ReconcileGenerationsRevocationsAndEffects cancellationToken
        match reconciliation with Error failures -> Assert.Contains(failures, function BackupRequiresReconciliation "unsettled-external-effects" -> true | _ -> false) | _ -> failwith "pending restored effect was not gated"
    }

    [<Fact>]
    member _.``Akka plugin marker survives ActorSystem replacement``() = task {
        let! dataSource, _ = Fixture.reset()
        dataSource.Dispose()
        let persistenceId = $"runtime-marker-{Guid.NewGuid():N}"
        let config = AkkaPersistence.configuration(Fixture.connectionString "orchestration_o0")
        use first = ActorSystem.Create("o0-marker-first", config)
        let actor = first.ActorOf(Props.Create(fun () -> MarkerActor persistenceId))
        let! persisted = actor.Ask<string>("sequence-1", TimeSpan.FromSeconds 10.0)
        Assert.Equal("sequence-1", persisted)
        do! first.Terminate()
        use second = ActorSystem.Create("o0-marker-second", config)
        let recovered = second.ActorOf(Props.Create(fun () -> MarkerActor persistenceId))
        let! count = recovered.Ask<int>("count", TimeSpan.FromSeconds 10.0)
        Assert.Equal(1, count)
        do! second.Terminate()
    }

    [<Fact>]
    member _.``two live ActorSystems cannot store two sequence-one markers``() = task {
        let! dataSource, identity = Fixture.reset()
        use dataSource = dataSource
        let persistenceId = $"duplicate-runtime-marker-{Guid.NewGuid():N}"
        let config = AkkaPersistence.configuration(Fixture.connectionString "orchestration_o0")
        use leftSystem = ActorSystem.Create("o0-duplicate-left", config)
        use rightSystem = ActorSystem.Create("o0-duplicate-right", config)
        let left = leftSystem.ActorOf(Props.Create(fun () -> MarkerActor persistenceId))
        let right = rightSystem.ActorOf(Props.Create(fun () -> MarkerActor persistenceId))
        left.Tell "left"
        right.Tell "right"
        do! Task.Delay 1500
        use! connection = dataSource.OpenConnectionAsync()
        use command = new NpgsqlCommand("SELECT count(*) FROM fsgg_orchestration.journal WHERE persistence_id=$1 AND sequence_number=1", connection)
        command.Parameters.AddWithValue(persistenceId) |> ignore
        let! stored = command.ExecuteScalarAsync()
        Assert.Equal(1L, Convert.ToInt64 stored)
        let stale = PostgreSqlStore(Fixture.options dataSource identity 1L) :> IJournalStore
        let! readiness = stale.CheckReadiness cancellationToken
        match readiness with Error failures -> Assert.Contains(failures, function BackupRequiresReconciliation "generation-fence-regressed" -> true | _ -> false) | _ -> failwith "stale runtime became ready"
        do! leftSystem.Terminate()
        do! rightSystem.Terminate()
    }
