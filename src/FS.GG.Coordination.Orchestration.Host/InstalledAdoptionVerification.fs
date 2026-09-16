namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Npgsql
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Pilot
open FS.GG.Coordination.Orchestration.PostgreSql

[<CLIMutable>]
type InstalledAdoptionSubject =
    {
        ProjectId: Guid
        RepositoryNodeId: string
        RepositoryDatabaseId: int64
        IssueNodeId: string
        IssueDatabaseId: int64
    }

[<CLIMutable>]
type InstalledAdoptionRequest =
    {
        Schema: string
        OperationId: Guid
        ExpectedSourceRevision: string
        ExpectedExecutableSha256: string
        StoreId: string
        ExpectedBackupIdentity: Guid
        ExpectedSchemaVersion: int
        ExpectedGenerationFence: int64
        IssuedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        OrdinaryCapacity: int
        RecoveryCapacityPerProject: int
        ProjectA: InstalledAdoptionSubject
        ProjectB: InstalledAdoptionSubject
    }

[<CLIMutable>]
type InstalledAdoptionAssertion =
    {
        Name: string
        Status: string
        Detail: string
    }

[<CLIMutable>]
type InstalledAdoptionIds =
    {
        ProjectId: Guid
        SubjectId: string
        AssignmentId: Guid
        AttemptId: Guid
        ReservationId: Guid
    }

[<CLIMutable>]
type InstalledAdoptionDatabase =
    {
        State: string
        StoreId: string
        BackupIdentity: string
        SchemaVersion: Nullable<int>
        GenerationFence: Nullable<int64>
    }

[<CLIMutable>]
type InstalledAdoptionCounts =
    {
        State: string
        ActiveReservations: Nullable<int64>
        Commands: Nullable<int64>
        Candidates: Nullable<int64>
        ExternalEffects: Nullable<int64>
    }

[<CLIMutable>]
type InstalledAdoptionResult =
    {
        Schema: string
        Scope: string
        OperationId: Guid
        RequestSha256: string
        ActualSourceRevision: string
        ActualExecutableSha256: string
        Database: InstalledAdoptionDatabase
        ProjectA: InstalledAdoptionIds
        ProjectB: InstalledAdoptionIds
        Assertions: InstalledAdoptionAssertion array
        Outcomes: InstalledAdoptionAssertion array
        StartedAt: DateTimeOffset
        CompletedAt: DateTimeOffset
        FinalCounts: InstalledAdoptionCounts
        Passed: bool
        Failure: string
    }

type InstalledAdoptionRuntime =
    {
        SourceRevision: string
        ExecutablePath: string
        Clock: TimeProvider
        AfterAReserved: (unit -> unit) option
    }

[<RequireQualifiedAccess>]
module InstalledAdoptionVerification =
    let requestSchema = "fsgg.orchestration.installed-adoption-request/1"
    let resultSchema = "fsgg.orchestration.installed-adoption-result/1"
    let scope = "offline-installed-store-qualification"

    let private jsonOptions =
        let options =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 5)

        options.PropertyNameCaseInsensitive <- false
        options.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        options

    let serializeResult value =
        JsonSerializer.Serialize(value, jsonOptions)

    let private shaBytes (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let executableSha256 path = File.ReadAllBytes path |> shaBytes

    let embeddedSourceRevision () =
        Assembly.GetEntryAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        |> Seq.tryFind (fun value -> value.Key = "FsggSourceRevision")
        |> Option.map _.Value
        |> Option.defaultValue "unbound"

    let private exactNames expected (element: JsonElement) =
        element.ValueKind = JsonValueKind.Object
        && (element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = expected
        && (element.EnumerateObject() |> Seq.length) = expected.Count

    let private requestNames =
        set
            [
                "schema"
                "operationId"
                "expectedSourceRevision"
                "expectedExecutableSha256"
                "storeId"
                "expectedBackupIdentity"
                "expectedSchemaVersion"
                "expectedGenerationFence"
                "issuedAt"
                "expiresAt"
                "ordinaryCapacity"
                "recoveryCapacityPerProject"
                "projectA"
                "projectB"
            ]

    let private subjectNames =
        set
            [
                "projectId"
                "repositoryNodeId"
                "repositoryDatabaseId"
                "issueNodeId"
                "issueDatabaseId"
            ]

    let private lowerHex length (value: string) =
        not (isNull value)
        && value.Length = length
        && value
           |> Seq.forall (fun character -> Char.IsAsciiHexDigit character && not (Char.IsUpper character))

    let private validText maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && value.Length <= maximum

    let private boundedFailure (value: string) =
        let normalized =
            if String.IsNullOrWhiteSpace value then
                "installed-adoption-refused"
            else
                value.Replace('\r', ' ').Replace('\n', ' ').Trim()

        if normalized.Length <= 256 then
            normalized
        else
            normalized.Substring(0, 256)

    let decodeRequest now (bytes: byte array) =
        if isNull bytes || bytes.Length = 0 || bytes.Length > 65536 then
            Error "request-size-refused"
        else
            try
                use document =
                    JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 5))

                let root = document.RootElement

                if
                    not (exactNames requestNames root)
                    || not (exactNames subjectNames (root.GetProperty "projectA"))
                    || not (exactNames subjectNames (root.GetProperty "projectB"))
                then
                    Error "request-shape-refused"
                else
                    let request =
                        JsonSerializer.Deserialize<InstalledAdoptionRequest>(ReadOnlySpan bytes, jsonOptions)

                    if
                        isNull (box request)
                        || isNull (box request.ProjectA)
                        || isNull (box request.ProjectB)
                    then
                        Error "request-null-refused"
                    elif request.Schema <> requestSchema then
                        Error "request-schema-refused"
                    elif request.OperationId = Guid.Empty then
                        Error "operation-identity-refused"
                    elif
                        not (lowerHex 40 request.ExpectedSourceRevision)
                        || not (lowerHex 64 request.ExpectedExecutableSha256)
                    then
                        Error "runtime-binding-refused"
                    elif
                        not (validText 128 request.StoreId)
                        || request.ExpectedBackupIdentity = Guid.Empty
                        || request.ExpectedSchemaVersion <> 2
                        || request.ExpectedGenerationFence < 0L
                    then
                        Error "store-binding-refused"
                    elif
                        request.IssuedAt > now
                        || request.ExpiresAt <= now
                        || request.ExpiresAt <= request.IssuedAt
                        || request.ExpiresAt - request.IssuedAt > TimeSpan.FromMinutes 10.
                    then
                        Error "request-time-window-refused"
                    elif request.OrdinaryCapacity <> 1 || request.RecoveryCapacityPerProject < 1 then
                        Error "capacity-configuration-refused"
                    elif
                        request.ProjectA.ProjectId = Guid.Empty
                        || request.ProjectB.ProjectId = Guid.Empty
                        || request.ProjectA.ProjectId = request.ProjectB.ProjectId
                    then
                        Error "project-identity-refused"
                    else
                        try
                            let subjectA =
                                WorkItemIdentity.create
                                    request.ProjectA.RepositoryNodeId
                                    request.ProjectA.RepositoryDatabaseId
                                    request.ProjectA.IssueNodeId
                                    request.ProjectA.IssueDatabaseId

                            let subjectB =
                                WorkItemIdentity.create
                                    request.ProjectB.RepositoryNodeId
                                    request.ProjectB.RepositoryDatabaseId
                                    request.ProjectB.IssueNodeId
                                    request.ProjectB.IssueDatabaseId

                            if WorkItemIdentity.persistenceId subjectA = WorkItemIdentity.persistenceId subjectB then
                                Error "canonical-subjects-must-be-distinct"
                            else
                                Ok(request, subjectA, subjectB)
                        with _ ->
                            Error "canonical-subject-identity-refused"
            with
            | :? JsonException -> Error "request-json-refused"
            | :? InvalidOperationException -> Error "request-shape-refused"

    let private fixtureId operationId label projectId subjectId =
        let material =
            $"{operationId:D}\n{label}\n{projectId:D}\n{subjectId}\n"
            |> Encoding.UTF8.GetBytes

        let digest = SHA256.HashData material
        Guid(ReadOnlySpan(digest, 0, 16))

    let private ids operationId label (project: InstalledAdoptionSubject) subject =
        let subjectId = WorkItemIdentity.persistenceId subject

        {
            ProjectId = project.ProjectId
            SubjectId = subjectId
            AssignmentId = fixtureId operationId ($"{label}:assignment") project.ProjectId subjectId
            AttemptId = fixtureId operationId ($"{label}:attempt") project.ProjectId subjectId
            ReservationId = fixtureId operationId ($"{label}:reservation") project.ProjectId subjectId
        }

    let private unknownIds =
        {
            ProjectId = Guid.Empty
            SubjectId = "unknown"
            AssignmentId = Guid.Empty
            AttemptId = Guid.Empty
            ReservationId = Guid.Empty
        }

    let private unknownDatabase storeId =
        {
            State = "unknown"
            StoreId = storeId
            BackupIdentity = "unknown"
            SchemaVersion = Nullable()
            GenerationFence = Nullable()
        }

    let private unknownCounts =
        {
            State = "unknown"
            ActiveReservations = Nullable()
            Commands = Nullable()
            Candidates = Nullable()
            ExternalEffects = Nullable()
        }

    let failureResult runtime started requestDigest operationId database idsA idsB assertions outcomes counts reason =
        {
            Schema = resultSchema
            Scope = scope
            OperationId = operationId
            RequestSha256 = requestDigest
            ActualSourceRevision = runtime.SourceRevision
            ActualExecutableSha256 = executableSha256 runtime.ExecutablePath
            Database = database
            ProjectA = idsA
            ProjectB = idsB
            Assertions = assertions |> List.toArray
            Outcomes = outcomes |> List.toArray
            StartedAt = started
            CompletedAt = runtime.Clock.GetUtcNow()
            FinalCounts = counts
            Passed = false
            Failure = boundedFailure reason
        }

    let configurationFailure runtime reason =
        let started = runtime.Clock.GetUtcNow()

        failureResult
            runtime
            started
            "unknown"
            Guid.Empty
            (unknownDatabase "unknown")
            unknownIds
            unknownIds
            []
            []
            unknownCounts
            reason

    let private assertion name status detail =
        {
            Name = name
            Status = status
            Detail = detail
        }

    let private readDatabaseIdentity (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT schema_version,migration_state,backup_identity::text,generation_fence FROM fsgg_orchestration.store_metadata WHERE singleton",
                    connection
                )

            use! row = command.ExecuteReaderAsync cancellationToken

            if not (row.Read()) then
                return Error "store-metadata-missing"
            else
                return Ok(row.GetInt32 0, row.GetString 1, row.GetString 2, row.GetInt64 3)
        }

    let private readCounts (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT "
                    + "(SELECT count(*) FROM fsgg_orchestration.subscription_reservation WHERE active),"
                    + "(SELECT count(*) FROM fsgg_orchestration.executor_command),"
                    + "(SELECT count(*) FROM fsgg_orchestration.candidate),"
                    + "(SELECT count(*) FROM fsgg_orchestration.event WHERE effect_change <> 0),"
                    + "(SELECT count(*) FROM fsgg_orchestration.execution_stream),"
                    + "(SELECT count(*) FROM fsgg_orchestration.execution_event),"
                    + "(SELECT count(*) FROM fsgg_orchestration.subscription_reservation),"
                    + "(SELECT count(*) FROM fsgg_orchestration.stream)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.event)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.inbox)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.domain_snapshot)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.projection_checkpoint)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.candidate_object)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.candidate_upload_staging)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.journal)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.journal_metadata)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.tags)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.snapshot)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.pilot_stream)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.pilot_event)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.pilot_inbox)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.execution_input_object)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.execution_workspace_manifest)"
                    + "+(SELECT count(*) FROM fsgg_orchestration.execution_route_binding)",
                    connection
                )

            use! row = command.ExecuteReaderAsync cancellationToken

            if not (row.Read()) then
                return Error "store-count-readback-missing"
            else
                return
                    Ok(
                        row.GetInt64 0,
                        row.GetInt64 1,
                        row.GetInt64 2,
                        row.GetInt64 3,
                        row.GetInt64 4,
                        row.GetInt64 5,
                        row.GetInt64 6,
                        row.GetInt64 7
                    )
        }

    let private observedCounts (active, commands, candidates, effects, _, _, _, _) =
        {
            State = "observed"
            ActiveReservations = Nullable active
            Commands = Nullable commands
            Candidates = Nullable candidates
            ExternalEffects = Nullable effects
        }

    let private appendIntent
        (journal: IExecutionSessionJournal)
        now
        expires
        (ids: InstalledAdoptionIds)
        cancellationToken
        =
        task {
            let budget =
                { SubscriptionPilot.createBudget now with
                    ExecutionDeadline = expires
                    DeliveryDeadline = expires
                }

            let intent: LaunchIntent =
                {
                    Schema = ExecutionProtocol.launchSchema
                    Key =
                        {
                            AssignmentId = ids.AssignmentId
                            AttemptId = ids.AttemptId
                            Generation = 1L
                        }
                    InputDigest = shaBytes (Encoding.UTF8.GetBytes ids.SubjectId)
                    Workspace = $"installed-adoption/{ids.ProjectId:D}/{ids.SubjectId}"
                    Requested = { Model = None; Effort = None }
                    Limits =
                        {
                            Deadline = expires
                            MaximumRuntime = budget.MaximumRuntime
                            MaximumAttempts = 1
                        }
                    RecordedAt = now
                }

            let! appended =
                journal.AppendAttempt(
                    ids.AssignmentId,
                    ids.AttemptId,
                    0L,
                    SessionEvent.LaunchIntentRecorded intent,
                    cancellationToken
                )

            if appended <> AppendResult.Appended then
                return Error $"launch-intent-{appended}"
            else
                return SubscriptionPilot.reserve now ids.ReservationId ids.AssignmentId ids.AttemptId 1L 1L budget
        }

    let private require condition reason =
        if condition then Ok() else Error reason

    let run
        (connectionString: string)
        (requestBytes: byte array)
        (runtime: InstalledAdoptionRuntime)
        (cancellationToken: CancellationToken)
        =
        task {
            let started = runtime.Clock.GetUtcNow()
            let requestDigest = shaBytes requestBytes
            let mutable database = unknownDatabase "unknown"
            let mutable idsA = unknownIds
            let mutable idsB = unknownIds
            let assertions = ResizeArray<InstalledAdoptionAssertion>()
            let outcomes = ResizeArray<InstalledAdoptionAssertion>()
            let mutable finalCounts = unknownCounts

            let fail reason =
                failureResult
                    runtime
                    started
                    requestDigest
                    (if idsA = unknownIds then
                         Guid.Empty
                     else
                         match decodeRequest started requestBytes with
                         | Ok(request, _, _) -> request.OperationId
                         | _ -> Guid.Empty)
                    database
                    idsA
                    idsB
                    (List.ofSeq assertions)
                    (List.ofSeq outcomes)
                    finalCounts
                    reason

            try
                match decodeRequest started requestBytes with
                | Error reason -> return fail reason
                | Ok(request, subjectA, subjectB) ->
                    idsA <- ids request.OperationId "A" request.ProjectA subjectA
                    idsB <- ids request.OperationId "B" request.ProjectB subjectB

                    match
                        require (runtime.SourceRevision = request.ExpectedSourceRevision) "source-revision-mismatch",
                        require
                            (executableSha256 runtime.ExecutablePath = request.ExpectedExecutableSha256)
                            "executable-digest-mismatch"
                    with
                    | Error reason, _
                    | _, Error reason -> return fail reason
                    | Ok(), Ok() ->
                        assertions.Add(assertion "runtime-binding" "passed" "source-and-executable-exact")

                        let builder = NpgsqlConnectionStringBuilder connectionString

                        if
                            String.IsNullOrWhiteSpace builder.Username
                            || String.IsNullOrWhiteSpace builder.Password
                        then
                            return fail "postgresql-authentication-identity-required"
                        else
                            use source = NpgsqlDataSource.Create connectionString
                            use! lockConnection = source.OpenConnectionAsync cancellationToken

                            use lockCommand =
                                new NpgsqlCommand(
                                    "SELECT pg_try_advisory_lock(hashtextextended('fsgg-installed-adoption-verifier',0))",
                                    lockConnection
                                )

                            let! locked = lockCommand.ExecuteScalarAsync cancellationToken

                            if isNull locked || not (Convert.ToBoolean locked) then
                                return fail "concurrent-verifier-refused"
                            else
                                assertions.Add(
                                    assertion "exclusive-verifier-lock" "passed" "session-advisory-lock-held"
                                )

                                let! identityRead = readDatabaseIdentity lockConnection cancellationToken

                                match identityRead with
                                | Error reason -> return fail reason
                                | Ok(schemaVersion, migrationState, backupIdentity, generationFence) ->
                                    database <-
                                        {
                                            State = "observed"
                                            StoreId = request.StoreId
                                            BackupIdentity = backupIdentity
                                            SchemaVersion = Nullable schemaVersion
                                            GenerationFence = Nullable generationFence
                                        }

                                    if
                                        schemaVersion <> 2
                                        || migrationState <> "ready"
                                        || backupIdentity <> request.ExpectedBackupIdentity.ToString()
                                        || generationFence <> request.ExpectedGenerationFence
                                    then
                                        return fail "store-identity-or-fence-mismatch"
                                    else
                                        let! initialRead = readCounts lockConnection cancellationToken

                                        match initialRead with
                                        | Error reason -> return fail reason
                                        | Ok initialCounts ->
                                            let (active,
                                                 commands,
                                                 candidates,
                                                 effects,
                                                 streams,
                                                 events,
                                                 reservations,
                                                 otherRows) =
                                                initialCounts

                                            if
                                                active <> 0L
                                                || commands <> 0L
                                                || candidates <> 0L
                                                || effects <> 0L
                                                || streams <> 0L
                                                || events <> 0L
                                                || reservations <> 0L
                                                || otherRows <> 0L
                                            then
                                                finalCounts <- observedCounts initialCounts
                                                return fail "dedicated-store-not-empty"
                                            else
                                                assertions.Add(
                                                    assertion
                                                        "dedicated-empty-schema2-store"
                                                        "passed"
                                                        "metadata-and-zero-state-observed-before-write"
                                                )

                                                let options: StoreOptions =
                                                    {
                                                        DataSource = source
                                                        StoreId = request.StoreId
                                                        BackupIdentity = backupIdentity
                                                        MinimumGenerationFence = generationFence
                                                        RuntimeSchemaVersion = 2
                                                        SupportedEventSchemaVersions = set [ 1 ]
                                                        SupportedSerializerVersions =
                                                            set
                                                                [
                                                                    EventEnvelope.legacySerializerVersion
                                                                    EventEnvelope.serializerVersion
                                                                ]
                                                        MaximumCandidateBytes = 104857600L
                                                    }

                                                let store = PostgreSqlExecutionStore options
                                                let journal = store :> IExecutionSessionJournal
                                                let subscriptions = store :> IExecutorCommandStore

                                                let! reservationAResult =
                                                    appendIntent
                                                        journal
                                                        started
                                                        request.ExpiresAt
                                                        idsA
                                                        cancellationToken

                                                let! reservationBResult =
                                                    appendIntent
                                                        journal
                                                        started
                                                        request.ExpiresAt
                                                        idsB
                                                        cancellationToken

                                                match reservationAResult, reservationBResult with
                                                | Ok reservationA, Ok reservationB ->
                                                    let bytesA =
                                                        SubscriptionAccountingCodec.encodeReservation reservationA

                                                    let bytesB =
                                                        SubscriptionAccountingCodec.encodeReservation reservationB

                                                    let! reservedA =
                                                        subscriptions.ReserveSubscription(
                                                            bytesA,
                                                            request.OrdinaryCapacity,
                                                            request.RecoveryCapacityPerProject,
                                                            cancellationToken
                                                        )

                                                    outcomes.Add(
                                                        assertion
                                                            "project-a-reservation"
                                                            (string reservedA)
                                                            "ordinary-capacity-1"
                                                    )

                                                    if reservedA <> SubscriptionReserved then
                                                        return fail "project-a-reservation-refused"
                                                    else
                                                        runtime.AfterAReserved
                                                        |> Option.iter (fun callback -> callback ())

                                                        let! refusedB =
                                                            subscriptions.ReserveSubscription(
                                                                bytesB,
                                                                request.OrdinaryCapacity,
                                                                request.RecoveryCapacityPerProject,
                                                                cancellationToken
                                                            )

                                                        outcomes.Add(
                                                            assertion
                                                                "project-b-first-reservation"
                                                                (string refusedB)
                                                                "capacity-remains-owned-by-a"
                                                        )

                                                        if refusedB <> SubscriptionCapacityRefused then
                                                            return fail "project-b-capacity-refusal-missing"
                                                        else
                                                            let! readA =
                                                                subscriptions.ReadSubscription(
                                                                    idsA.ReservationId,
                                                                    cancellationToken
                                                                )

                                                            let! readB =
                                                                journal.ReadAttempt(
                                                                    idsB.AssignmentId,
                                                                    idsB.AttemptId,
                                                                    cancellationToken
                                                                )

                                                            if
                                                                (match readA with
                                                                 | Ok(bytes, None) ->
                                                                     bytes.AsSpan().SequenceEqual(bytesA.AsSpan())
                                                                 | _ -> false)
                                                                |> not
                                                                || readB.IsNone
                                                                || readB.Value.Revision <> 1L
                                                            then
                                                                return fail "initial-readback-refused"
                                                            else
                                                                assertions.Add(
                                                                    assertion
                                                                        "initial-exact-readback"
                                                                        "passed"
                                                                        "a-reservation-and-b-immutable-intent"
                                                                )

                                                                // Recreate only store objects. The source and advisory-lock session stay open.
                                                                let reopened = PostgreSqlExecutionStore options

                                                                let reopenedJournal =
                                                                    reopened :> IExecutionSessionJournal

                                                                let reopenedSubscriptions =
                                                                    reopened :> IExecutorCommandStore

                                                                let! durableA =
                                                                    reopenedSubscriptions.ReadSubscription(
                                                                        idsA.ReservationId,
                                                                        cancellationToken
                                                                    )

                                                                let! durableB =
                                                                    reopenedJournal.ReadAttempt(
                                                                        idsB.AssignmentId,
                                                                        idsB.AttemptId,
                                                                        cancellationToken
                                                                    )

                                                                if
                                                                    (match durableA with
                                                                     | Ok(bytes, None) ->
                                                                         bytes.AsSpan().SequenceEqual(bytesA.AsSpan())
                                                                     | _ -> false)
                                                                    |> not
                                                                    || durableB <> readB
                                                                then
                                                                    return fail "durable-reopen-readback-refused"
                                                                else
                                                                    assertions.Add(
                                                                        assertion
                                                                            "durable-reopen"
                                                                            "passed"
                                                                            "a-and-b-exact-after-store-recreation"
                                                                    )

                                                                    let unknown =
                                                                        SubscriptionPilot.settle
                                                                            (runtime.Clock.GetUtcNow())
                                                                            0L
                                                                            None
                                                                            "installed-adoption-no-provider-invocation"
                                                                            reservationA
                                                                        |> Result.defaultWith failwith
                                                                        |> SubscriptionAccountingCodec.encodeSettlement

                                                                    let! settled =
                                                                        reopenedSubscriptions.SettleSubscription(
                                                                            idsA.ReservationId,
                                                                            unknown,
                                                                            cancellationToken
                                                                        )

                                                                    let! stillRefusedB =
                                                                        reopenedSubscriptions.ReserveSubscription(
                                                                            bytesB,
                                                                            request.OrdinaryCapacity,
                                                                            request.RecoveryCapacityPerProject,
                                                                            cancellationToken
                                                                        )

                                                                    outcomes.Add(
                                                                        assertion
                                                                            "project-a-unknown-settlement"
                                                                            (if Result.isOk settled then
                                                                                 "recorded"
                                                                             else
                                                                                 "refused")
                                                                            "unknown-does-not-release"
                                                                    )

                                                                    if
                                                                        Result.isError settled
                                                                        || stillRefusedB <> SubscriptionCapacityRefused
                                                                    then
                                                                        return
                                                                            fail "unknown-settlement-released-capacity"
                                                                    else
                                                                        let! mismatch =
                                                                            reopenedSubscriptions.ReleaseSubscription(
                                                                                idsA.ReservationId,
                                                                                idsB.AttemptId,
                                                                                1L,
                                                                                cancellationToken
                                                                            )

                                                                        if mismatch <> SubscriptionReleaseConflict then
                                                                            return fail "mismatched-release-not-refused"
                                                                        else
                                                                            let! releaseA =
                                                                                reopenedSubscriptions
                                                                                    .ReleaseSubscription(
                                                                                        idsA.ReservationId,
                                                                                        idsA.AttemptId,
                                                                                        1L,
                                                                                        cancellationToken
                                                                                    )

                                                                            let! reserveB =
                                                                                reopenedSubscriptions
                                                                                    .ReserveSubscription(
                                                                                        bytesB,
                                                                                        request.OrdinaryCapacity,
                                                                                        request
                                                                                            .RecoveryCapacityPerProject,
                                                                                        cancellationToken
                                                                                    )

                                                                            let! releaseB =
                                                                                reopenedSubscriptions
                                                                                    .ReleaseSubscription(
                                                                                        idsB.ReservationId,
                                                                                        idsB.AttemptId,
                                                                                        1L,
                                                                                        cancellationToken
                                                                                    )

                                                                            outcomes.Add(
                                                                                assertion
                                                                                    "exact-release-and-handoff"
                                                                                    $"{releaseA};{reserveB};{releaseB}"
                                                                                    "original-unexpired-b-request"
                                                                            )

                                                                            if
                                                                                releaseA <> SubscriptionReleased
                                                                                || reserveB <> SubscriptionReserved
                                                                                || releaseB <> SubscriptionReleased
                                                                            then
                                                                                return
                                                                                    fail "exact-release-handoff-refused"
                                                                            else
                                                                                let! pending =
                                                                                    reopenedSubscriptions.ReadPending(
                                                                                        1,
                                                                                        cancellationToken
                                                                                    )

                                                                                let! terminalRead =
                                                                                    readCounts
                                                                                        lockConnection
                                                                                        cancellationToken

                                                                                match terminalRead with
                                                                                | Error reason -> return fail reason
                                                                                | Ok terminalCounts ->
                                                                                    finalCounts <-
                                                                                        observedCounts terminalCounts

                                                                                    let (active,
                                                                                         commands,
                                                                                         candidates,
                                                                                         effects,
                                                                                         streams,
                                                                                         events,
                                                                                         reservations,
                                                                                         otherRows) =
                                                                                        terminalCounts

                                                                                    if
                                                                                        active <> 0L
                                                                                        || commands <> 0L
                                                                                        || candidates <> 0L
                                                                                        || effects <> 0L
                                                                                        || not pending.IsEmpty
                                                                                        || streams <> 2L
                                                                                        || events <> 2L
                                                                                        || reservations <> 2L
                                                                                        || otherRows <> 0L
                                                                                    then
                                                                                        return
                                                                                            fail
                                                                                                "terminal-readback-refused"
                                                                                    else
                                                                                        assertions.Add(
                                                                                            assertion
                                                                                                "terminal-zero-effects"
                                                                                                "passed"
                                                                                                "intents-and-inactive-reservations-retained"
                                                                                        )

                                                                                        return
                                                                                            {
                                                                                                Schema = resultSchema
                                                                                                Scope = scope
                                                                                                OperationId =
                                                                                                    request.OperationId
                                                                                                RequestSha256 =
                                                                                                    requestDigest
                                                                                                ActualSourceRevision =
                                                                                                    runtime
                                                                                                        .SourceRevision
                                                                                                ActualExecutableSha256 =
                                                                                                    executableSha256
                                                                                                        runtime
                                                                                                            .ExecutablePath
                                                                                                Database = database
                                                                                                ProjectA = idsA
                                                                                                ProjectB = idsB
                                                                                                Assertions =
                                                                                                    assertions.ToArray()
                                                                                                Outcomes =
                                                                                                    outcomes.ToArray()
                                                                                                StartedAt = started
                                                                                                CompletedAt =
                                                                                                    runtime.Clock
                                                                                                        .GetUtcNow()
                                                                                                FinalCounts =
                                                                                                    finalCounts
                                                                                                Passed = true
                                                                                                Failure = ""
                                                                                            }
                                                | _ -> return fail "launch-intent-or-reservation-construction-refused"
            with error ->
                try
                    use source = NpgsqlDataSource.Create connectionString
                    use! connection = source.OpenConnectionAsync cancellationToken
                    let! counts = readCounts connection cancellationToken
                    counts |> Result.iter (fun value -> finalCounts <- observedCounts value)
                with _ ->
                    ()

                return fail error.Message
        }
