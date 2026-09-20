namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Pilot
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

[<CLIMutable>]
type MainAdmissionPreparationRequest =
    {
        Schema: string
        PreparationId: Guid
        ProjectId: Guid
        WorkflowRevision: int64
        CanonicalSha256: string
        SelectedAt: DateTimeOffset
        RouteId: Guid
        AttemptId: Guid
        CandidateId: Guid
        BranchRef: string
        ClaimResourceId: string
        ClaimOperationId: Guid
        ProcessOperationId: Guid
        CandidateOperationId: Guid
        BranchOperationId: Guid
        PullRequestOperationId: Guid
        MergeOperationId: Guid
        ReadbackOperationId: Guid
        RunnerId: Guid
        RunnerFingerprintSha256: string
        SessionId: Guid
        ReservationId: Guid
        ExecutionReservationId: Guid
        RouteProviderRevision: string
        RouteEvidenceSha256: string
        RepositoryBinding: string
        BaselineObjectId: string
        Workspace: string
        AllowedPaths: string array
        Validations: string array
        ExecutorBinding: string
        RequestedModel: string
        RequestedEffort: string
        InputMediaType: string
    }

[<RequireQualifiedAccess>]
module MainAdmissionPreparer =
    let schema = "fsgg.orchestration.main-route-preparation-request/1"

    exception private TelemetryParentRefused of string

    type TelemetryParent =
        { AttemptId: Guid
          Generation: int64
          Relation: string }

    /// Decide from the durable state immediately before the latest admission.
    let selectTelemetryParentCandidate (before: State) attemptId generation =
        if before.WorkItemId.IsNone then
            Ok None
        else
            let candidates = before.Attempts |> Map.toList |> List.map snd

            match candidates with
            | [] -> Error "telemetry-parent-attempt-missing"
            | _ ->
                let latestGeneration =
                    candidates |> List.map (fun attempt -> Id.generationValue attempt.Generation) |> List.max

                match candidates |> List.filter (fun attempt -> Id.generationValue attempt.Generation = latestGeneration) with
                | [ attempt ] when
                    latestGeneration < Id.generationValue generation
                    && Id.attemptValue attempt.AttemptId <> attemptId
                    ->
                    match before.HostedRoute, attempt.Status with
                    | Some route, status when route.AttemptId = attempt.AttemptId && route.Generation = attempt.Generation ->
                        let relation =
                            match status with
                            | Completed -> Some "follow-up"
                            | CancelledByRunner
                            | ReconciledAbsent _ -> Some "child"
                            | _ -> None

                        match relation with
                        | Some relation ->
                            Ok(Some { AttemptId = Id.attemptValue attempt.AttemptId
                                      Generation = latestGeneration
                                      Relation = relation })
                        | None -> Error "telemetry-parent-attempt-not-terminal"
                    | _ -> Error "telemetry-parent-route-ambiguous"
                | _ -> Error "telemetry-parent-attempt-ambiguous"

    // Reconstruct the state immediately before the latest subscription admission.
    // A readmission clears active Attempts, so the current state cannot choose its
    // parent. The journal is authoritative across retries and process restarts.
    let private selectTelemetryParent (store: IJournalStore) workItemId attemptId generation token =
        task {
            let persistenceId = WorkItemIdentity.persistenceId workItemId
            let! recovered = store.Recover(persistenceId, token)

            match recovered with
            | Error failures -> return Error(sprintf "telemetry-parent-journal-unavailable:%A" failures)
            | Ok value when value.Snapshot.IsSome -> return Error "telemetry-parent-snapshot-unavailable"
            | Ok value ->
                let mutable state = initial
                let mutable prior = None
                let mutable corrupt = false

                for stored in value.Events do
                    match EventEnvelope.tryDecode stored.Payload with
                    | Error _ -> corrupt <- true
                    | Ok eventValue when not corrupt ->
                        match eventValue with
                        | SubscriptionWorkAdmitted _ -> prior <- Some state
                        | _ -> ()

                        state <- evolve state eventValue
                    | Ok _ -> ()

                if corrupt then
                    return Error "telemetry-parent-history-corrupt"
                elif state.Generation <> generation || state.WorkItemId <> Some workItemId then
                    return Error "telemetry-parent-history-stale"
                else
                    match prior with
                    | None -> return Error "telemetry-parent-admission-missing"
                    | Some before -> return selectTelemetryParentCandidate before attemptId generation
        }

    let private options =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 8,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        )

    let private properties =
        set["schema"
            "preparationId"
            "projectId"
            "workflowRevision"
            "canonicalSha256"
            "selectedAt"
            "routeId"
            "attemptId"
            "candidateId"
            "branchRef"
            "claimResourceId"
            "claimOperationId"
            "processOperationId"
            "candidateOperationId"
            "branchOperationId"
            "pullRequestOperationId"
            "mergeOperationId"
            "readbackOperationId"
            "runnerId"
            "runnerFingerprintSha256"
            "sessionId"
            "reservationId"
            "executionReservationId"
            "routeProviderRevision"
            "routeEvidenceSha256"
            "repositoryBinding"
            "baselineObjectId"
            "workspace"
            "allowedPaths"
            "validations"
            "executorBinding"
            "requestedModel"
            "requestedEffort"
            "inputMediaType"]

    let private sha value =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 64
        && (value |> Seq.forall (fun c -> Char.IsAsciiHexDigit c && not (Char.IsUpper c)))

    let private text maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && value.Length <= maximum

    let private optionalText maximum (value: string) =
        isNull value
        || (value = value.Trim() && value.Length > 0 && value.Length <= maximum)

    let private guid value = value <> Guid.Empty

    let private duplicateFree (element: JsonElement) =
        let rec loop (item: JsonElement) =
            match item.ValueKind with
            | JsonValueKind.Object ->
                let names = item.EnumerateObject() |> Seq.map _.Name |> Seq.toList

                names.Length = (Set.ofList names).Count
                && item.EnumerateObject() |> Seq.forall (fun property -> loop property.Value)
            | JsonValueKind.Array -> item.EnumerateArray() |> Seq.forall loop
            | _ -> true

        loop element

    let decodeRequest (bytes: byte array) =
        try
            if isNull bytes || bytes.Length = 0 || bytes.Length > 65536 then
                Error "main-admission-preparation-size-refused"
            else
                use document =
                    JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 8))

                let root = document.RootElement

                let names =
                    if root.ValueKind = JsonValueKind.Object then
                        root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    else
                        []

                if
                    not (duplicateFree root)
                    || names.Length <> properties.Count
                    || Set.ofList names <> properties
                then
                    Error "main-admission-preparation-shape-refused"
                else
                    let value = root.Deserialize<MainAdmissionPreparationRequest>(options)

                    let operations =
                        [
                            value.ClaimOperationId
                            value.ProcessOperationId
                            value.CandidateOperationId
                            value.BranchOperationId
                            value.PullRequestOperationId
                            value.MergeOperationId
                            value.ReadbackOperationId
                        ]

                    let arraysValid (items: string array) maximum count =
                        not (isNull items)
                        && items.Length > 0
                        && items.Length <= count
                        && items |> Array.forall (text maximum)
                        && items.Length = (Set.ofArray items).Count

                    if
                        isNull (box value)
                        || value.Schema <> schema
                        || not (
                            guid value.PreparationId
                            && guid value.ProjectId
                            && guid value.RouteId
                            && guid value.AttemptId
                            && guid value.CandidateId
                            && guid value.RunnerId
                            && guid value.SessionId
                            && guid value.ReservationId
                            && guid value.ExecutionReservationId
                        )
                        || value.WorkflowRevision < 1L
                        || value.SelectedAt = DateTimeOffset.MinValue
                        || not (
                            sha value.CanonicalSha256
                            && sha value.RouteEvidenceSha256
                            && sha value.RunnerFingerprintSha256
                        )
                        || not (text 255 value.BranchRef)
                        || not (value.BranchRef.StartsWith("refs/heads/fsgg/pilot/", StringComparison.Ordinal))
                        || not (
                            text 128 value.ClaimResourceId
                            && text 256 value.RouteProviderRevision
                            && text 256 value.RepositoryBinding
                            && text 128 value.Workspace
                            && text 256 value.ExecutorBinding
                            && text 128 value.InputMediaType
                        )
                        || not (
                            (value.BaselineObjectId.Length = 40 || value.BaselineObjectId.Length = 64)
                            && value.BaselineObjectId |> Seq.forall Uri.IsHexDigit
                        )
                        || not (arraysValid value.AllowedPaths 512 128 && arraysValid value.Validations 256 16)
                        || operations |> List.exists (Guid.Empty.Equals)
                        || Set.count (Set.ofList operations) <> operations.Length
                        || not (optionalText 128 value.RequestedModel && optionalText 128 value.RequestedEffort)
                    then
                        Error "main-admission-preparation-authority-refused"
                    else
                        Ok value
        with
        | :? JsonException -> Error "main-admission-preparation-json-refused"
        | :? NullReferenceException -> Error "main-admission-preparation-value-refused"

    let private commandId preparationId stage =
        let bytes =
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{preparationId:D}:{stage}"))

        Id.command (Guid(ReadOnlySpan(bytes, 0, 16)))

    let private append
        (clock: TimeProvider)
        (workItems: IJournalStore)
        (workItemId: WorkItemId)
        (principal: string)
        (preparationId: Guid)
        (stage: string)
        (protocol: ProtocolVersion)
        (command: Command)
        (token: CancellationToken)
        =
        task {
            let! recovered = HostedWriterJournal.recover workItems workItemId token

            match recovered with
            | Error failures -> return Error(sprintf "%A" failures)
            | Ok current ->
                let now = clock.GetUtcNow()

                let envelope =
                    {
                        CommandId = commandId preparationId stage
                        ProtocolVersion = protocol
                        ExpectedRevision = current.State.Revision
                        ExpectedGeneration = current.State.Generation
                        PrincipalId = principal
                        SessionId = None
                        IssuedAt = now
                        ExpiresAt = now.AddMinutes 1.
                        Command = command
                    }

                let! result =
                    HostedWriterJournal.decideAndAppend clock workItems workItemId envelope token

                return
                    result
                    |> Result.bind (fun (decision, _) ->
                        match decision.Receipt.Disposition with
                        | ReceiptDisposition.Accepted
                        | ReceiptDisposition.Duplicate -> Ok()
                        | _ -> Error decision.Receipt.Detail)
        }

    let private prepareBounded
        (clock: TimeProvider)
        (workItems: IJournalStore)
        (executions: IExecutorCommandStore)
        (executionJournal: IExecutionSessionJournal)
        workItemId
        principal
        (request: MainAdmissionPreparationRequest)
        (inputBytes: byte array)
        token
        =
        task {
            let budget = SubscriptionPilot.createBudget request.SelectedAt

            let snapshot =
                {
                    ProjectId = Id.project request.ProjectId
                    WorkItemId = workItemId
                    WorkflowRevision = Id.revision request.WorkflowRevision
                    CanonicalSha256 = request.CanonicalSha256
                    BoardMembershipIds = []
                    CapturedAt = request.SelectedAt
                }

            let! beforeAdmission = HostedWriterJournal.recover workItems workItemId token

            let! admitted =
                match beforeAdmission with
                | Error failures -> Task.FromResult(Error(sprintf "%A" failures))
                | Ok current when
                    current.State.WorkItemId.IsNone
                    || (match current.State.Control with
                        | ControlState.Revoked _
                        | ControlState.Cancelled _ -> true
                        | _ -> false)
                    ->
                    append
                        clock
                        workItems
                        workItemId
                        principal
                        request.PreparationId
                        "admit-subscription"
                        (Id.protocolVersion 2 0)
                        (AdmitSubscription(snapshot, budget))
                        token
                | Ok current when
                    current.State.Snapshot = Some snapshot
                    && current.State.SubscriptionBudget = Some budget
                    ->
                    Task.FromResult(Ok())
                | Ok _ -> Task.FromResult(Error "main-admission-preparation-admission-conflict")

            match admitted with
            | Error reason -> return Error reason
            | Ok() ->
                let! admission = HostedWriterJournal.recover workItems workItemId token

                match admission with
                | Error failures -> return Error(sprintf "%A" failures)
                | Ok admittedState when
                    admittedState.State.Snapshot <> Some snapshot
                    || admittedState.State.SubscriptionBudget <> Some budget
                    ->
                    return Error "main-admission-preparation-admission-conflict"
                | Ok admittedState ->
                    let generation = admittedState.State.Generation
                    let! parentResult = selectTelemetryParent workItems workItemId request.AttemptId generation token
                    let parent = parentResult |> Result.defaultWith (fun reason -> raise (TelemetryParentRefused reason))
                    let repositoryNodeId = WorkItemIdentity.repositoryNodeId workItemId

                    let route =
                        {
                            RouteId = request.RouteId
                            WorkItemId = workItemId
                            JobClass = "routine-documentation-delivery"
                            AttemptId = Id.attempt request.AttemptId
                            CandidateId = Id.candidate request.CandidateId
                            RepositoryNodeId = repositoryNodeId
                            BranchRef = request.BranchRef
                            ClaimResourceId = request.ClaimResourceId
                            ClaimOperationId = Id.operation request.ClaimOperationId
                            ProcessOperationId = Id.operation request.ProcessOperationId
                            CandidateOperationId = Id.operation request.CandidateOperationId
                            BranchOperationId = Id.operation request.BranchOperationId
                            PullRequestOperationId = Id.operation request.PullRequestOperationId
                            MergeOperationId = Id.operation request.MergeOperationId
                            ReadbackOperationId = Id.operation request.ReadbackOperationId
                            Generation = generation
                            WorkflowRevision = snapshot.WorkflowRevision
                            SelectedAt = request.SelectedAt
                        }

                    let! selected =
                        match admittedState.State.HostedRoute with
                        | None ->
                            append
                                clock
                                workItems
                                workItemId
                                principal
                                request.PreparationId
                                "select-route"
                                (Id.protocolVersion 1 0)
                                (SelectHostedRoute route)
                                token
                        | Some existing when existing = route -> Task.FromResult(Ok())
                        | Some _ -> Task.FromResult(Error "main-admission-preparation-route-conflict")

                    match selected with
                    | Error reason -> return Error reason
                    | Ok() ->
                        let! beforePause = HostedWriterJournal.recover workItems workItemId token

                        let! paused =
                            match beforePause with
                            | Error failures -> Task.FromResult(Error(sprintf "%A" failures))
                            | Ok current when
                                (match current.State.Control with
                                 | Paused _ -> true
                                 | _ -> false)
                                && not current.State.ReadbackCurrent
                                ->
                                Task.FromResult(Ok())
                            | Ok _ ->
                                append
                                    clock
                                    workItems
                                    workItemId
                                    principal
                                    request.PreparationId
                                    "startup-pause"
                                    (Id.protocolVersion 1 0)
                                    (RecordStartupPause "main-admission-preparation")
                                    token

                        match paused with
                        | Error reason -> return Error reason
                        | Ok() ->
                            let inputDigest = RunnerWire.sha256 inputBytes

                            let workspace =
                                {
                                    Schema = ExecutorWire.workspaceManifestSchema
                                    Workspace = request.Workspace
                                    RepositoryBinding = request.RepositoryBinding
                                    BaselineObjectId = request.BaselineObjectId
                                    AllowedPaths = request.AllowedPaths
                                    Validations = request.Validations
                                    InputDigest = inputDigest
                                }

                            let workspaceBytes = ExecutorWire.encodeWorkspaceManifest workspace
                            let workspaceDigest = RunnerWire.sha256 workspaceBytes

                            let launch =
                                {
                                    Schema = ExecutionProtocol.launchSchema
                                    Key =
                                        {
                                            AssignmentId = request.ProcessOperationId
                                            AttemptId = request.AttemptId
                                            Generation = Id.generationValue generation
                                        }
                                    InputDigest = inputDigest
                                    Workspace = request.Workspace
                                    Requested =
                                        {
                                            Model = Option.ofObj request.RequestedModel
                                            Effort = Option.ofObj request.RequestedEffort
                                        }
                                    Limits =
                                        {
                                            Deadline = budget.ExecutionDeadline
                                            MaximumRuntime = budget.MaximumRuntime
                                            MaximumAttempts = 1
                                        }
                                    RecordedAt = request.SelectedAt
                                }

                            let binding0 =
                                {
                                    Schema =
                                        if parent.IsSome then
                                            ExecutorWire.routeBindingSchemaV2
                                        else
                                            ExecutorWire.routeBindingSchema
                                    BindingSha256 = ""
                                    WorkItemPersistenceId = WorkItemIdentity.persistenceId workItemId
                                    RouteId = request.RouteId
                                    RouteOperationId = request.ProcessOperationId
                                    ProcessOperationId = request.ProcessOperationId
                                    AssignmentId = request.ProcessOperationId
                                    AttemptId = request.AttemptId
                                    CandidateId = request.CandidateId
                                    Generation = Id.generationValue generation
                                    RepositoryBinding = request.RepositoryBinding
                                    BaselineObjectId = request.BaselineObjectId
                                    PromptDigest = inputDigest
                                    WorkspaceManifestSha256 = workspaceDigest
                                    ExecutorBinding = request.ExecutorBinding
                                    ParentAttemptId = parent |> Option.map _.AttemptId |> Option.toNullable
                                    ParentGeneration = parent |> Option.map _.Generation |> Option.toNullable
                                    TelemetryRelation = parent |> Option.map _.Relation |> Option.toObj
                                }

                            let binding =
                                { binding0 with
                                    BindingSha256 = ExecutorWire.routeBindingDigest binding0
                                }

                            let inputManifest =
                                {
                                    Schema = ExecutorWire.inputManifestSchema
                                    InputDigest = inputDigest
                                    MediaType = request.InputMediaType
                                    SizeBytes = inputBytes.LongLength
                                    ChunkBytes = max 1 (min inputBytes.Length ExecutorWire.maximumContentBytes)
                                }

                            let executionReservation =
                                SubscriptionPilot.reserve
                                    request.SelectedAt
                                    request.ExecutionReservationId
                                    request.ProcessOperationId
                                    request.AttemptId
                                    (Id.generationValue generation)
                                    1L
                                    budget

                            match executionReservation with
                            | Error reason -> return Error reason
                            | Ok executionReservation ->
                                let! input =
                                    executions.StageInput(
                                        ExecutorWire.encodeInputManifest inputManifest,
                                        inputBytes,
                                        token
                                    )

                                let! stagedWorkspace = executions.StageWorkspaceManifest(workspaceBytes, token)

                                match input, stagedWorkspace with
                                | Error reason, _
                                | _, Error reason -> return Error reason
                                | Ok(), Ok digest when digest <> workspaceDigest ->
                                    return Error "main-admission-preparation-workspace-digest-refused"
                                | Ok(), Ok _ ->
                                    let! bound = executions.BindRoute(ExecutorWire.encodeRouteBinding binding, token)

                                    match bound with
                                    | Error reason -> return Error reason
                                    | Ok _ ->
                                        let! intent =
                                            executionJournal.AppendAttempt(
                                                request.ProcessOperationId,
                                                request.AttemptId,
                                                0L,
                                                LaunchIntentRecorded launch,
                                                token
                                            )

                                        match intent with
                                        | AppendConflict ->
                                            return Error "main-admission-preparation-launch-intent-conflict"
                                        | Appended
                                        | DuplicateEvent ->
                                            let! reserved =
                                                executions.ReserveSubscription(
                                                    SubscriptionAccountingCodec.encodeReservation executionReservation,
                                                    1,
                                                    1,
                                                    token
                                                )

                                            match reserved with
                                            | SubscriptionReserved
                                            | SubscriptionDuplicate ->
                                                let! verified = HostedWriterJournal.recover workItems workItemId token

                                                match verified with
                                                | Error failures -> return Error(sprintf "%A" failures)
                                                | Ok durable ->
                                                    let state = durable.State

                                                    let clean =
                                                        state.Snapshot = Some snapshot
                                                        && state.SubscriptionBudget = Some budget
                                                        && state.Generation = generation
                                                        && state.HostedRoute = Some route
                                                        && state.Reservation.IsNone
                                                        && state.Attempts.IsEmpty
                                                        && state.Operations.IsEmpty
                                                        && (match state.Control with
                                                            | Paused _ -> true
                                                            | _ -> false)
                                                        && not state.ReadbackCurrent

                                                    if not clean then
                                                        return Error "main-admission-preparation-durable-state-refused"
                                                    else
                                                        let preparation =
                                                            {
                                                                Snapshot = snapshot
                                                                Budget = budget
                                                                Reservation =
                                                                    {
                                                                        ReservationId =
                                                                            Id.reservation request.ReservationId
                                                                        Generation = generation
                                                                        ExpiresAt = budget.DeliveryDeadline
                                                                        RequiredClaimIds = set[request.ClaimResourceId]
                                                                    }
                                                                Route = route
                                                                Readback =
                                                                    {
                                                                        RouteId = request.RouteId
                                                                        WorkItemId = workItemId
                                                                        RepositoryNodeId = repositoryNodeId
                                                                        ProviderRevision = request.RouteProviderRevision
                                                                        EvidenceSha256 = request.RouteEvidenceSha256
                                                                        Generation = generation
                                                                        WorkflowRevision = snapshot.WorkflowRevision
                                                                        ObservedAt = request.SelectedAt
                                                                    }
                                                                Runner =
                                                                    {
                                                                        RunnerId = Id.runner request.RunnerId
                                                                        PrincipalId = principal
                                                                        FingerprintSha256 =
                                                                            request.RunnerFingerprintSha256
                                                                        Generation = generation
                                                                        ExpiresAt = budget.ExecutionDeadline
                                                                    }
                                                                SessionId = Id.session request.SessionId
                                                                LaunchIntent = launch
                                                                Binding = binding
                                                                InputManifest = inputManifest
                                                                InputBytes = inputBytes
                                                                WorkspaceManifest = workspace
                                                                ExecutionReservation = executionReservation
                                                            }

                                                        let bytes =
                                                            MainRouteAdmission.encode
                                                                (MainRouteAdmission.commandId preparation)
                                                                preparation

                                                        match MainRouteAdmission.decode workItemId principal bytes with
                                                        | Ok decoded when decoded = preparation -> return Ok bytes
                                                        | _ -> return Error "main-admission-preparation-output-refused"
                                            | other ->
                                                return
                                                    Error(
                                                        $"main-admission-preparation-subscription-reservation-refused:{other}"
                                                    )
        }

    let prepare
        clock
        workItems
        executions
        executionJournal
        workItemId
        principal
        request
        (inputBytes: byte array)
        token
        =
        if isNull inputBytes || inputBytes.Length = 0 || inputBytes.Length > 1024 * 1024 then
            Task.FromResult(Error "main-admission-preparation-input-size-refused")
        else
            let inputDigest = RunnerWire.sha256 inputBytes

            let inputManifest =
                {
                    Schema = ExecutorWire.inputManifestSchema
                    InputDigest = inputDigest
                    MediaType = request.InputMediaType
                    SizeBytes = inputBytes.LongLength
                    ChunkBytes = max 1 (min inputBytes.Length ExecutorWire.maximumContentBytes)
                }

            let workspace =
                {
                    Schema = ExecutorWire.workspaceManifestSchema
                    Workspace = request.Workspace
                    RepositoryBinding = request.RepositoryBinding
                    BaselineObjectId = request.BaselineObjectId
                    AllowedPaths = request.AllowedPaths
                    Validations = request.Validations
                    InputDigest = inputDigest
                }

            match
                ExecutorWire.encodeInputManifest inputManifest
                |> ExecutorWire.parseInputManifest,
                ExecutorWire.encodeWorkspaceManifest workspace
                |> ExecutorWire.parseWorkspaceManifest
            with
            | Ok _, Ok _ ->
                task {
                    try
                        return! prepareBounded clock workItems executions executionJournal workItemId principal request inputBytes token
                    with TelemetryParentRefused reason ->
                        return Error reason
                }
            | Error reason, _
            | _, Error reason -> Task.FromResult(Error reason)

    let writeAtomicPrivate (path: string) (bytes: byte array) =
        let directory = Path.GetDirectoryName path
        let privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

        let ensurePrivate (existingPath: string) =
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(existingPath, privateMode)

                if File.GetUnixFileMode(existingPath) = privateMode then
                    Ok()
                else
                    Error "main-admission-output-permissions-refused"
            else
                Ok()

        if String.IsNullOrWhiteSpace directory || not (Directory.Exists directory) then
            Error "main-admission-output-directory-refused"
        else
            try
                if File.Exists path then
                    let existing = File.ReadAllBytes path

                    if ReadOnlySpan<byte>(existing).SequenceEqual(ReadOnlySpan<byte>(bytes)) then
                        ensurePrivate path
                    else
                        Error "main-admission-output-conflict"
                else
                    let temporary =
                        Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")

                    try
                        let streamOptions =
                            FileStreamOptions(
                                Mode = FileMode.CreateNew,
                                Access = FileAccess.Write,
                                Share = FileShare.None,
                                BufferSize = 4096,
                                Options = FileOptions.WriteThrough
                            )

                        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                            streamOptions.UnixCreateMode <- Nullable privateMode

                        use stream = new FileStream(temporary, streamOptions)

                        stream.Write bytes
                        stream.Flush true
                        stream.Close()
                        File.Move(temporary, path, false)
                        ensurePrivate path
                    finally
                        if File.Exists temporary then
                            File.Delete temporary
            with
            | :? IOException ->
                if
                    File.Exists path
                    && ReadOnlySpan<byte>(File.ReadAllBytes path).SequenceEqual(ReadOnlySpan<byte>(bytes))
                then
                    ensurePrivate path
                else
                    Error "main-admission-output-conflict"
            | :? UnauthorizedAccessException -> Error "main-admission-output-permissions-refused"
