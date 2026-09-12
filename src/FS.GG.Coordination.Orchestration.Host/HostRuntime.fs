namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Npgsql
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Pilot
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

type HostStore =
    { CheckReadiness: CancellationToken -> Task<Result<unit, ReadinessFailure list>>
      WorkItems: IJournalStore
      Candidates: ICandidateStore
      Recover: Guid -> CancellationToken -> Task<Result<PilotRecovery, PilotRecoveryFailure list>>
      Append: PilotAppendRequest -> CancellationToken -> Task<PilotAppendOutcome> }

type HostStatus =
    { Schema: string
      Ready: bool
      DispatchEnabled: bool
      Mode: string
      PermitId: string
      Phase: string option
      Sequence: int64 option
      ReadbackCurrent: bool option
      ActiveAssignments: int option
      UnknownOperations: int option
      Findings: string list }

type ControlRequest =
    { Schema: string
      PermitId: Guid
      CommandId: Guid
      ExpectedSequence: int64
      ExpectedGeneration: int64
      PrincipalId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Reason: string }

[<CLIMutable>]
type ExecutorRelayPoll = { Schema:string; MaximumWaitSeconds:int }
[<CLIMutable>]
type ExecutorRelayCompletion = { Schema:string; CommandId:Guid; FramesBase64:string array; Failure:string }

[<RequireQualifiedAccess>]
module HostRuntime =
    let private jsonOptions =
        let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8)
        options.PropertyNameCaseInsensitive <- false
        options.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        options

    let private storeOptions runtimeSchemaVersion (configuration:HostConfiguration) (source:NpgsqlDataSource) : StoreOptions =
            { DataSource = source; StoreId = configuration.StoreId; BackupIdentity = configuration.BackupIdentity
              MinimumGenerationFence = configuration.MinimumGenerationFence; RuntimeSchemaVersion = runtimeSchemaVersion
              SupportedEventSchemaVersions = Set [ 1 ]; SupportedSerializerVersions = Set [ EventEnvelope.legacySerializerVersion; EventEnvelope.serializerVersion ]
              MaximumCandidateBytes = 104857600L }

    let createStore (configuration: HostConfiguration) =
        let source = NpgsqlDataSource.Create configuration.ConnectionString
        let options = storeOptions 1 configuration source
        let postgres = PostgreSqlStore(options)
        let root = postgres :> IJournalStore
        let pilot = PostgreSqlPilotStore(options) :> IPilotJournalStore
        source,
        { CheckReadiness = root.CheckReadiness
          WorkItems = root
          Candidates = postgres :> ICandidateStore
          Recover = fun permit token -> pilot.RecoverPilot(permit, token)
          Append = fun request token -> pilot.AppendPilot(request, token) }

    /// Production composition exposes the execution journal/transport while retaining
    /// the legacy two-value factory for callers that only serve runner /1 routes.
    let createProductionStores (configuration:HostConfiguration) =
        let source,store=createStore configuration
        source,store,PostgreSqlExecutionStore(storeOptions 2 configuration source)

    let status (store: HostStore) permitId cancellationToken = task {
        let! readiness = store.CheckReadiness cancellationToken
        match readiness with
        | Error failures ->
            return
                { Schema = "fsgg.orchestration.host-status/1"; Ready = false; DispatchEnabled = false
                  Mode = "paused"; PermitId = string permitId; Phase = None; Sequence = None
                  ReadbackCurrent = None; ActiveAssignments = None; UnknownOperations = None
                  Findings = failures |> List.map (sprintf "%A") }
        | Ok () ->
            let! recovery = store.Recover permitId cancellationToken
            match recovery with
            | Error failures ->
                return
                    { Schema = "fsgg.orchestration.host-status/1"; Ready = false; DispatchEnabled = false
                      Mode = "paused"; PermitId = string permitId; Phase = None; Sequence = None
                      ReadbackCurrent = None; ActiveAssignments = None; UnknownOperations = None
                      Findings = failures |> List.map (sprintf "%A") }
            | Ok recovered ->
                let state = recovered.State
                return
                    { Schema = "fsgg.orchestration.host-status/1"; Ready = true; DispatchEnabled = false
                      Mode = "paused"; PermitId = string permitId; Phase = Some(string state.Phase)
                      Sequence = Some state.Sequence; ReadbackCurrent = Some state.ReadbackCurrent
                      ActiveAssignments = Some state.ActiveAssignments.Count
                      UnknownOperations = Some state.UnknownOperations.Count
                      Findings = if state.Permit.IsSome && not state.ReadbackCurrent then [ "fresh-reconnect-readback-required" ] else [] } }

    let authorize (expectedToken: string) (authorization: string option) =
        let supplied =
            match authorization with
            | Some value when value.StartsWith("Bearer ", StringComparison.Ordinal) -> value.Substring(7)
            | _ -> ""
        let expectedBytes, suppliedBytes = Encoding.UTF8.GetBytes expectedToken, Encoding.UTF8.GetBytes supplied
        expectedBytes.Length = suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes)

    let private control (now: DateTimeOffset) (request: ControlRequest) (state: PilotState) command =
        let envelope =
            { CommandId = request.CommandId; ExpectedSequence = request.ExpectedSequence; PrincipalId = request.PrincipalId
              IssuedAt = request.IssuedAt; ExpiresAt = request.ExpiresAt; Command = command }
        Pilot.decide now state envelope
        |> Result.map (PilotJournal.appendRequest (state.Permit |> Option.get |> _.PermitId) now envelope)

    let private envelope (request: ControlRequest) command =
        { CommandId = request.CommandId; ExpectedSequence = request.ExpectedSequence; PrincipalId = request.PrincipalId
          IssuedAt = request.IssuedAt; ExpiresAt = request.ExpiresAt; Command = command }

    let applyControl (clock: TimeProvider) (store: HostStore) configuredPermit configuredPrincipal (request: ControlRequest) revoke cancellationToken = task {
        if request.Schema <> "fsgg.orchestration.host-control/1" || request.CommandId = Guid.Empty
           || request.PermitId <> configuredPermit || request.ExpectedSequence < 0L
           || request.ExpectedGeneration <= 0L || request.PrincipalId <> configuredPrincipal
           || String.IsNullOrWhiteSpace request.Reason || request.Reason <> request.Reason.Trim() then
            return Error "invalid-control-request"
        else
            let! recovered = store.Recover configuredPermit cancellationToken
            match recovered with
            | Error failures -> return Error(sprintf "%A" failures)
            | Ok recovery ->
                match recovery.State.Permit with
                | None -> return Error "permit-required"
                | Some permit when permit.PermitId <> request.PermitId || permit.PilotOwnerId <> request.PrincipalId
                                   || Id.generationValue permit.Generation <> request.ExpectedGeneration ->
                    return Error "control-authority-mismatch"
                | Some _ ->
                    let command = if revoke then Revoke request.Reason else Pause request.Reason
                    let now = clock.GetUtcNow()
                    let probe = PilotJournal.appendRequest request.PermitId now (envelope request command) []
                    let! prior = store.Append probe cancellationToken
                    match prior with
                    | PilotDuplicate sequence -> return Ok sequence
                    | PilotConflict -> return Error "command-identity-conflict"
                    | PilotInvalidAppend "new-command-requires-events" ->
                        match control now request recovery.State command with
                        | Error failure -> return Error failure
                        | Ok appendRequest ->
                            let! appended = store.Append appendRequest cancellationToken
                            match appended with
                            | PilotAppended sequence | PilotDuplicate sequence -> return Ok sequence
                            | PilotConflict -> return Error "command-identity-conflict"
                            | other -> return Error(sprintf "%A" other)
                    | other -> return Error(sprintf "%A" other) }

    let private writeJson (response: HttpListenerResponse) statusCode value cancellationToken = task {
        try
            let bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)
            response.StatusCode <- statusCode
            response.ContentType <- "application/json"
            response.ContentLength64 <- int64 bytes.Length
            do! response.OutputStream.WriteAsync(ReadOnlyMemory bytes, cancellationToken).AsTask()
        finally response.Close() }

    let private writeEmergency response statusCode value = task {
        use deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds 500.)
        try do! writeJson response statusCode value deadline.Token
        with _ -> try response.Close() with _ -> () }

    let private readControl (request: HttpListenerRequest) cancellationToken = task {
        let mediaType =
            if isNull request.ContentType then ""
            else
                let parts = request.ContentType.Split([| ';' |], 2)
                parts[0].Trim()
        if request.ContentLength64 <= 0L || request.ContentLength64 > 4096L
           || not (String.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)) then
            return Error "invalid-content"
        else
            let size = int request.ContentLength64
            let bytes = Array.zeroCreate<byte> size
            let mutable read = 0
            while read < size do
                let! count = request.InputStream.ReadAsync(Memory(bytes, read, size - read), cancellationToken).AsTask()
                if count = 0 then read <- size + 1 else read <- read + count
            if read <> size then return Error "truncated-content"
            else
                try
                    use document = JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 8))
                    if document.RootElement.ValueKind <> JsonValueKind.Object then return Error "invalid-json"
                    else
                        let expected =
                            set [ "schema"; "permitId"; "commandId"; "expectedSequence"; "expectedGeneration"
                                  "principalId"; "issuedAt"; "expiresAt"; "reason" ]
                        let names = document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                        if names.Length <> expected.Count || Set.ofList names <> expected then return Error "invalid-json-shape"
                        else
                            let value = document.RootElement.Deserialize<ControlRequest>(jsonOptions)
                            if isNull (box value) then return Error "invalid-json" else return Ok value
                with :? JsonException -> return Error "invalid-json" }

    let private readRunner maximumBytes (request:HttpListenerRequest) cancellationToken = task {
        let mediaType = if isNull request.ContentType then "" else (request.ContentType.Split([|';'|],2)).[0].Trim()
        if request.ContentLength64<=0L || request.ContentLength64>int64 maximumBytes
           || not(String.Equals(mediaType,"application/json",StringComparison.OrdinalIgnoreCase)) then return Error "invalid-runner-content"
        else
            let bytes=Array.zeroCreate<byte>(int request.ContentLength64)
            let mutable offset=0
            while offset<bytes.Length do
                let! read=request.InputStream.ReadAsync(Memory(bytes,offset,bytes.Length-offset),cancellationToken).AsTask()
                if read=0 then offset <- bytes.Length+1 else offset <- offset+read
            if offset<>bytes.Length then return Error "truncated-runner-content" else return Ok bytes }

    let private runnerStore (store:HostStore) = { RunnerWireStore.WorkItems=store.WorkItems;Candidates=store.Candidates }

    let private exactObject (expected:Set<string>) (bytes:byte array) =
        use document=JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=8))
        if document.RootElement.ValueKind<>JsonValueKind.Object then false else
        let names=document.RootElement.EnumerateObject()|>Seq.map _.Name|>Seq.toList
        names.Length=expected.Count && Set.ofList names=expected

    let private handle clock configuration store (executorRelay:HostExecutorRelay option) (mainAdmission:IMainRouteAdmissionHandler option) (context: HttpListenerContext) cancellationToken = task {
        let request, response = context.Request, context.Response
        if request.HttpMethod = "GET" && request.Url.AbsolutePath = "/health/live" then
            // Liveness deliberately says nothing about mutation readiness. The
            // authenticated /health/ready and /v1/status routes own the
            // paused/admitted/reconciled dispatch state.
            do! writeJson response 200 {| schema = "fsgg.orchestration.host-liveness/2"; live = true; readinessRoute = "/health/ready" |} cancellationToken
        elif request.Url.AbsolutePath.StartsWith("/v1/executor/",StringComparison.Ordinal) then
            if not(authorize configuration.RunnerToken (Option.ofObj request.Headers["Authorization"])) || executorRelay.IsNone then
                do! writeJson response 401 {| error="unauthorized" |} cancellationToken
            else
                let! body=readRunner (2*1024*1024) request cancellationToken
                match body with
                | Error reason -> do! writeJson response 400 {| error=reason |} cancellationToken
                | Ok bytes ->
                    try
                        if request.HttpMethod="POST" && request.Url.AbsolutePath="/v1/executor/poll" then
                            let value=JsonSerializer.Deserialize<ExecutorRelayPoll>(ReadOnlySpan bytes,jsonOptions)
                            if not(exactObject (set["schema";"maximumWaitSeconds"]) bytes) || isNull(box value) || value.Schema<>"fsgg.orchestration.executor-relay-poll/1" || value.MaximumWaitSeconds<1 || value.MaximumWaitSeconds>30 then do! writeJson response 400 {|error="executor-relay-poll-refused"|} cancellationToken
                            else
                                use deadline=CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                                deadline.CancelAfter(TimeSpan.FromSeconds(float value.MaximumWaitSeconds))
                                let! found=executorRelay.Value.Poll(deadline.Token)
                                match found with
                                | None -> do! writeJson response 204 {|schema="fsgg.orchestration.executor-relay-empty/1"|} cancellationToken
                                | Some pending -> do! writeJson response 200 {|schema="fsgg.orchestration.executor-relay-request/1";commandId=pending.CommandId;framesBase64=pending.Frames|>List.map Convert.ToBase64String|>List.toArray|} cancellationToken
                        elif request.HttpMethod="POST" && request.Url.AbsolutePath="/v1/executor/complete" then
                            let value=JsonSerializer.Deserialize<ExecutorRelayCompletion>(ReadOnlySpan bytes,jsonOptions)
                            let frames=if isNull(box value)||isNull value.FramesBase64 then [] else value.FramesBase64|>Array.toList|>List.map(fun item->try Convert.FromBase64String item with _->Array.empty)
                            let result=if not(exactObject (set["schema";"commandId";"framesBase64";"failure"]) bytes) || isNull(box value) || value.Schema<>"fsgg.orchestration.executor-relay-complete/1" || value.CommandId=Guid.Empty then Error "executor-relay-completion-refused" elif not(String.IsNullOrWhiteSpace value.Failure) then executorRelay.Value.Fail(value.CommandId,value.Failure) else executorRelay.Value.Complete(value.CommandId,frames)
                            match result with
                            | Ok() -> do! writeJson response 200 {|schema="fsgg.orchestration.executor-relay-completion/1";accepted=true|} cancellationToken
                            | Error reason -> do! writeJson response 409 {|error=reason|} cancellationToken
                        else do! writeJson response 404 {|error="executor-relay-route-not-found"|} cancellationToken
                    with :? JsonException -> do! writeJson response 400 {|error="executor-relay-json-refused"|} cancellationToken
        elif request.Url.AbsolutePath.StartsWith("/v1/runner/",StringComparison.Ordinal) then
            if not(authorize configuration.RunnerToken (Option.ofObj request.Headers["Authorization"])) then
                do! writeJson response 401 {| error="unauthorized" |} cancellationToken
            else
                let maximum=if request.Url.AbsolutePath="/v1/runner/candidate" then 140*1024*1024 else 8192
                let! body=readRunner maximum request cancellationToken
                match body with
                | Error reason -> do! writeJson response 400 {| error=reason |} cancellationToken
                | Ok bytes ->
                    let wire=runnerStore store
                    let! parsed,result =
                        match request.HttpMethod,request.Url.AbsolutePath with
                        | "POST","/v1/runner/assignment" ->
                            match RunnerWire.parsePoll bytes with
                            | Error reason -> Task.FromResult(false,Error reason)
                            | Ok value -> task {
                                let! found=RunnerWireRuntime.poll clock wire configuration.WorkItemId value cancellationToken
                                return true,Result.map box found }
                        | "POST","/v1/runner/ack" ->
                            match RunnerWire.parseAck bytes with
                            | Error reason -> Task.FromResult(false,Error reason)
                            | Ok value -> task {
                                let! found=RunnerWireRuntime.acknowledge clock wire configuration.WorkItemId value cancellationToken
                                return true,Result.map (fun revision -> box {| schema="fsgg.orchestration.runner-ack-receipt/1";accepted=true;workflowRevision=revision |}) found }
                        | "POST","/v1/runner/candidate" ->
                            match RunnerWire.parseCandidate bytes with
                            | Error reason -> Task.FromResult(false,Error reason)
                            | Ok value -> task {
                                let! found=RunnerWireRuntime.submitCandidate clock wire configuration.WorkItemId value cancellationToken
                                return true,Result.map (fun revision -> box {| schema="fsgg.orchestration.runner-candidate-receipt/1";accepted=true;workflowRevision=revision |}) found }
                        | _ -> Task.FromResult(false,Error "runner-route-not-found")
                    match result with
                    | Ok value -> do! writeJson response 200 value cancellationToken
                    | Error reason -> do! writeJson response (if parsed then 409 else 400) {| error=reason |} cancellationToken
        elif not (authorize configuration.Token (Option.ofObj request.Headers["Authorization"])) then
            do! writeJson response 401 {| error = "unauthorized" |} cancellationToken
        elif request.HttpMethod="POST" && request.Url.AbsolutePath="/v1/main/admit" then
            match mainAdmission with
            | None->do! writeJson response 503 {|error="main-route-admission-not-configured"|} cancellationToken
            | Some admission->
                let! body=readRunner (2*1024*1024) request cancellationToken
                match body with
                | Error reason->do! writeJson response 400 {|error=reason|} cancellationToken
                | Ok bytes->
                    let! admitted=admission.Admit(bytes,cancellationToken)
                    match admitted with
                    | Ok()->do! writeJson response 200 {|schema="fsgg.orchestration.main-route-admission-receipt/1";accepted=true|} cancellationToken
                    | Error reason->do! writeJson response 409 {|error=reason|} cancellationToken
        elif request.HttpMethod = "GET" && (request.Url.AbsolutePath = "/health/ready" || request.Url.AbsolutePath = "/v1/status") then
            match mainAdmission with
            | None->
                let! current = status store configuration.PermitId cancellationToken
                do! writeJson response (if current.Ready then 200 else 503) current cancellationToken
            | Some admission->
                let! current=admission.Status cancellationToken
                match current with
                | Error reason->do! writeJson response 503 {|schema="fsgg.orchestration.main-host-status/1";ready=false;dispatchEnabled=false;mode="unavailable";findings=[|reason|]|} cancellationToken
                | Ok value->do! writeJson response (if value.Ready then 200 else 503) {|schema="fsgg.orchestration.main-host-status/1";ready=value.Ready;dispatchEnabled=value.DispatchEnabled;admitted=value.Admitted;mode=value.Mode;sequence=value.Sequence;generation=value.Generation;unknownOperations=value.UnknownOperations;findings=List.toArray value.Findings|} cancellationToken
        elif request.HttpMethod = "POST" && (request.Url.AbsolutePath = "/v1/pause" || request.Url.AbsolutePath = "/v1/resume" || request.Url.AbsolutePath = "/v1/revoke" || request.Url.AbsolutePath = "/v1/cancel") then
            let! decoded = readControl request cancellationToken
            match decoded with
            | Error reason -> do! writeJson response 400 {| error = reason |} cancellationToken
            | Ok control ->
                match mainAdmission with
                | None->
                    if request.Url.AbsolutePath="/v1/resume" || request.Url.AbsolutePath="/v1/cancel" then do! writeJson response 404 {|error="legacy-main-control-route-not-supported"|} cancellationToken else
                        let! result = applyControl clock store configuration.PermitId configuration.PilotPrincipalId control (request.Url.AbsolutePath = "/v1/revoke") cancellationToken
                        match result with
                        | Ok sequence -> do! writeJson response 200 {| schema = "fsgg.orchestration.host-control-receipt/1"; accepted = true; sequence = sequence |} cancellationToken
                        | Error reason -> do! writeJson response 409 {| error = reason |} cancellationToken
                | Some admission->
                    if control.Schema<>"fsgg.orchestration.host-control/1" then do! writeJson response 400 {|error="invalid-main-control-schema"|} cancellationToken
                    elif control.PermitId<>configuration.PermitId then do! writeJson response 409 {|error="control-authority-mismatch"|} cancellationToken else
                    let value={CommandId=control.CommandId;ExpectedSequence=control.ExpectedSequence;ExpectedGeneration=control.ExpectedGeneration;PrincipalId=control.PrincipalId;IssuedAt=control.IssuedAt;ExpiresAt=control.ExpiresAt;Reason=control.Reason;Action=request.Url.AbsolutePath.Substring(4)}
                    let! result=admission.Control(value,cancellationToken)
                    match result with
                    | Ok receipt->do! writeJson response 200 {|schema="fsgg.orchestration.main-host-control-receipt/1";accepted=true;sequence=receipt.Sequence;action=receipt.Action;requestPersisted=receipt.RequestPersisted;processTerminationObserved=receipt.ProcessTerminationObserved;detail=receipt.Detail|} cancellationToken
                    | Error reason->do! writeJson response 409 {|error=reason|} cancellationToken
        else do! writeJson response 404 {| error = "not-found" |} cancellationToken }

    let private serveInternal (clock: TimeProvider) (configuration: HostConfiguration) (store: HostStore) relay admission (cancellationToken: CancellationToken) = task {
        use listener = new HttpListener()
        use requestSlots = new SemaphoreSlim(configuration.MaximumConcurrentRequests, configuration.MaximumConcurrentRequests)
        let active = ResizeArray<Task>()
        listener.Prefixes.Add configuration.Prefix
        listener.Start()
        try
            while not cancellationToken.IsCancellationRequested do
                    let! context = listener.GetContextAsync().WaitAsync(cancellationToken)
                    if requestSlots.Wait(0) then
                        let pending = task {
                            use deadline = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                            deadline.CancelAfter configuration.RequestTimeout
                            try
                                try
                                    do! handle clock configuration store relay admission context deadline.Token
                                with
                                | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                                    do! writeEmergency context.Response 408 {| error = "request-timeout" |}
                                | _ -> do! writeEmergency context.Response 500 {| error = "request-refused" |}
                            finally requestSlots.Release() |> ignore }
                        active.Add pending
                        active.RemoveAll(fun item -> item.IsCompleted) |> ignore
                    else
                        context.Response.StatusCode <- 503
                        context.Response.Close()
        with :? OperationCanceledException -> ()
        listener.Stop()
        try do! Task.WhenAll(active) with _ -> () }

    let serve clock configuration store cancellationToken = serveInternal clock configuration store None None cancellationToken
    let serveProduction clock configuration store relay cancellationToken = serveInternal clock configuration store (Some relay) None cancellationToken
    let serveMain clock configuration store relay admission cancellationToken = serveInternal clock configuration store (Some relay) (Some admission) cancellationToken
