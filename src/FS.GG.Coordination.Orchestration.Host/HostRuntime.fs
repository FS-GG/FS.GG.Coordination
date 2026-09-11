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

[<RequireQualifiedAccess>]
module HostRuntime =
    let private jsonOptions =
        let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8)
        options.PropertyNameCaseInsensitive <- false
        options.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        options

    let createStore (configuration: HostConfiguration) =
        let source = NpgsqlDataSource.Create configuration.ConnectionString
        let options =
            { DataSource = source; StoreId = configuration.StoreId; BackupIdentity = configuration.BackupIdentity
              MinimumGenerationFence = configuration.MinimumGenerationFence; RuntimeSchemaVersion = 1
              SupportedEventSchemaVersions = Set [ 1 ]; SupportedSerializerVersions = Set [ EventEnvelope.legacySerializerVersion; EventEnvelope.serializerVersion ]
              MaximumCandidateBytes = 104857600L }
        let postgres = PostgreSqlStore(options)
        let root = postgres :> IJournalStore
        let pilot = PostgreSqlPilotStore(options) :> IPilotJournalStore
        source,
        { CheckReadiness = root.CheckReadiness
          WorkItems = root
          Candidates = postgres :> ICandidateStore
          Recover = fun permit token -> pilot.RecoverPilot(permit, token)
          Append = fun request token -> pilot.AppendPilot(request, token) }

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

    let private handle clock configuration store (context: HttpListenerContext) cancellationToken = task {
        let request, response = context.Request, context.Response
        if request.HttpMethod = "GET" && request.Url.AbsolutePath = "/health/live" then
            do! writeJson response 200 {| schema = "fsgg.orchestration.host-liveness/1"; live = true; dispatchEnabled = false |} cancellationToken
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
                                return true,Result.map (fun () -> box {| schema="fsgg.orchestration.runner-ack-receipt/1";accepted=true |}) found }
                        | "POST","/v1/runner/candidate" ->
                            match RunnerWire.parseCandidate bytes with
                            | Error reason -> Task.FromResult(false,Error reason)
                            | Ok value -> task {
                                let! found=RunnerWireRuntime.submitCandidate clock wire configuration.WorkItemId value cancellationToken
                                return true,Result.map (fun () -> box {| schema="fsgg.orchestration.runner-candidate-receipt/1";accepted=true |}) found }
                        | _ -> Task.FromResult(false,Error "runner-route-not-found")
                    match result with
                    | Ok value -> do! writeJson response 200 value cancellationToken
                    | Error reason -> do! writeJson response (if parsed then 409 else 400) {| error=reason |} cancellationToken
        elif not (authorize configuration.Token (Option.ofObj request.Headers["Authorization"])) then
            do! writeJson response 401 {| error = "unauthorized" |} cancellationToken
        elif request.HttpMethod = "GET" && (request.Url.AbsolutePath = "/health/ready" || request.Url.AbsolutePath = "/v1/status") then
            let! current = status store configuration.PermitId cancellationToken
            do! writeJson response (if current.Ready then 200 else 503) current cancellationToken
        elif request.HttpMethod = "POST" && (request.Url.AbsolutePath = "/v1/pause" || request.Url.AbsolutePath = "/v1/revoke") then
            let! decoded = readControl request cancellationToken
            match decoded with
            | Error reason -> do! writeJson response 400 {| error = reason |} cancellationToken
            | Ok control ->
                let! result = applyControl clock store configuration.PermitId configuration.PilotPrincipalId control (request.Url.AbsolutePath = "/v1/revoke") cancellationToken
                match result with
                | Ok sequence -> do! writeJson response 200 {| schema = "fsgg.orchestration.host-control-receipt/1"; accepted = true; sequence = sequence |} cancellationToken
                | Error reason -> do! writeJson response 409 {| error = reason |} cancellationToken
        else do! writeJson response 404 {| error = "not-found" |} cancellationToken }

    let serve (clock: TimeProvider) (configuration: HostConfiguration) (store: HostStore) (cancellationToken: CancellationToken) = task {
        use listener = new HttpListener()
        use admission = new SemaphoreSlim(configuration.MaximumConcurrentRequests, configuration.MaximumConcurrentRequests)
        let active = ResizeArray<Task>()
        listener.Prefixes.Add configuration.Prefix
        listener.Start()
        try
            while not cancellationToken.IsCancellationRequested do
                    let! context = listener.GetContextAsync().WaitAsync(cancellationToken)
                    if admission.Wait(0) then
                        let pending = task {
                            use deadline = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                            deadline.CancelAfter configuration.RequestTimeout
                            try
                                try
                                    do! handle clock configuration store context deadline.Token
                                with
                                | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                                    do! writeEmergency context.Response 408 {| error = "request-timeout" |}
                                | _ -> do! writeEmergency context.Response 500 {| error = "request-refused" |}
                            finally admission.Release() |> ignore }
                        active.Add pending
                        active.RemoveAll(fun item -> item.IsCompleted) |> ignore
                    else
                        context.Response.StatusCode <- 503
                        context.Response.Close()
        with :? OperationCanceledException -> ()
        listener.Stop()
        try do! Task.WhenAll(active) with _ -> () }
