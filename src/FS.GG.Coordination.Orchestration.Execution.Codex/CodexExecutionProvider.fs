namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution

type ICodexExecutionInput =
    abstract member ReadUtf8: digest: string * CancellationToken -> Task<Result<byte array, string>>

type ICodexCandidateInspector =
    abstract member CandidateId: Guid
    abstract member Verify: workspace: string * CandidateReference * CancellationToken -> Task<Result<unit, string>>
    abstract member CreateCandidate: workspace: string * CancellationToken -> Task<Result<CandidateReference, string>>

/// Receives validated native counters and typed gaps without affecting execution.
type ICodexTurnObserver =
    abstract member TurnCompleted: CodexTurnUsage -> unit
    abstract member Gap: string -> unit
    abstract member ProcessStarted: int * DateTimeOffset -> unit
    abstract member ProcessTerminal: int * string option * DateTimeOffset -> unit
    abstract member ThreadStarted: int * string * DateTimeOffset -> unit
    abstract member NativeTurnStarted: int * string * string option * int64 * DateTimeOffset -> unit

type CodexExecutionProviderOptions =
    {
        Executable: string
        ExpectedVersion: string
        StateRoot: string
        MaximumStreamBytes: int
        StartupTimeout: TimeSpan
        EnvironmentAllowList: Set<string>
        TurnObserver: ICodexTurnObserver option
    }

[<RequireQualifiedAccess>]
module CodexExecutionProviderOptions =
    let create executable stateRoot =
        {
            Executable = executable
            ExpectedVersion = "codex-cli 0.154.0"
            StateRoot = stateRoot
            MaximumStreamBytes = 1024 * 1024
            StartupTimeout = TimeSpan.FromSeconds 15.
            EnvironmentAllowList =
                set
                    [
                        "HOME"
                        "PATH"
                        "LANG"
                        "LC_ALL"
                        "TERM"
                        "TMPDIR"
                        "CODEX_HOME"
                        "XDG_CONFIG_HOME"
                        "XDG_DATA_HOME"
                        "XDG_CACHE_HOME"
                    ]
            TurnObserver = None
        }

module private CodexLearningConfiguration =
    let private keys =
        set [ "CODEX_HOME"; "HOME"; "XDG_CONFIG_HOME"; "XDG_DATA_HOME"; "XDG_CACHE_HOME" ]

    let environment (options: CodexExecutionProviderOptions) =
        options.EnvironmentAllowList
        |> Seq.sort
        |> Seq.choose (fun key ->
            Environment.GetEnvironmentVariable key
            |> Option.ofObj
            |> Option.map (fun value -> key, value))
        |> Seq.toArray

    let digest (environment: (string * string) array) =
        environment
        |> Array.filter (fun (key, _) -> keys.Contains key)
        |> Array.map (fun (key, value) -> key + "=" + value)
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let provenance environment =
        $"codex-app-server:model/list;config-sha256={digest environment}"

/// Bounded native capability discovery over the pinned Codex app-server stdio protocol.
/// The response is account scoped by app-server; provenance binds the non-secret local
/// configuration boundary used to select that account without disclosing its values.
type CodexAppServerLearningCapabilityDiscovery
    (
        options: CodexExecutionProviderOptions,
        clock: TimeProvider
    ) =
    let maximumPages = 8
    let pageSize = 100
    let maximumCursorBytes = 512

    let sha256File path =
        try
            if File.Exists path then
                use stream = File.OpenRead path
                Some(SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant())
            else
                None
        with _ ->
            None

    let executableUnchanged (query: LearningSelectionQuery) =
        query.Executable.Version = Some options.ExpectedVersion
        && query.Executable.Sha256.IsSome
        && sha256File query.Executable.Path = query.Executable.Sha256

    let writeMessage (writer: StreamWriter) (value: string) =
        task {
            do! writer.WriteLineAsync(value)
            do! writer.FlushAsync()
        }

    let readLineBounded
        (stream: Stream)
        (totalBytes: int ref)
        (cancellationToken: CancellationToken)
        =
        task {
            use line = new MemoryStream()
            let one = Array.zeroCreate<byte> 1
            let mutable complete = false
            let mutable ended = false

            while not complete do
                let! count = stream.ReadAsync(one.AsMemory(), cancellationToken)

                if count = 0 then
                    complete <- true
                    ended <- true
                else
                    totalBytes.Value <- totalBytes.Value + 1

                    if totalBytes.Value > options.MaximumStreamBytes then
                        raise (InvalidDataException "codex-model-list-bytes-exceeded")

                    if one[0] = 10uy then
                        complete <- true
                    elif one[0] <> 13uy then
                        line.WriteByte one[0]

            if ended && line.Length = 0L then
                return None
            else
                return Some(Encoding.UTF8.GetString(line.ToArray()))
        }

    let drainBounded (stream: Stream) maximumBytes (cancellationToken: CancellationToken) =
        task {
            let buffer = Array.zeroCreate<byte> 4096
            let mutable observed = 0
            let mutable complete = false

            while not complete do
                let! count = stream.ReadAsync(buffer.AsMemory(), cancellationToken)

                if count = 0 then
                    complete <- true
                else
                    observed <- observed + count

                    if observed > maximumBytes then
                        raise (InvalidDataException "codex-model-list-bytes-exceeded")
        }

    let response
        (stream: Stream)
        (totalBytes: int ref)
        requestId
        (cancellationToken: CancellationToken)
        =
        task {
            let mutable result = None

            while result.IsNone do
                let! line = readLineBounded stream totalBytes cancellationToken

                match line with
                | None -> raise (EndOfStreamException "codex-app-server-response-incomplete")
                | Some text ->
                    use document = JsonDocument.Parse text
                    let root = document.RootElement

                    match root.TryGetProperty "id" with
                    | false, _ -> () // Notifications are unrelated to this read-only request.
                    | true, id when id.ValueKind = JsonValueKind.Number && id.GetInt32() = requestId ->
                        match root.TryGetProperty "error", root.TryGetProperty "result" with
                        | (false, _), (true, value) when value.ValueKind = JsonValueKind.Object ->
                            result <- Some(value.Clone())
                        | _ -> raise (InvalidDataException "codex-app-server-request-refused")
                    | true, _ -> raise (InvalidDataException "codex-app-server-response-id-invalid")

            return result.Value
        }

    let unknown query code environment =
        { CodexLearningProviderCapability.unknown (clock.GetUtcNow()) query code with
            Provenance = CodexLearningConfiguration.provenance environment
        }

    interface ICodexLearningCapabilityDiscovery with
        member _.Discover(query, cancellationToken) =
            task {
                let environment = CodexLearningConfiguration.environment options

                if options.MaximumStreamBytes < 1 || options.StartupTimeout <= TimeSpan.Zero then
                    return unknown query "codex-model-list-bounds-invalid" environment
                elif not (executableUnchanged query) then
                    return unknown query "codex-model-list-executable-changed" environment
                else
                    use deadline = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                    deadline.CancelAfter options.StartupTimeout
                    let token = deadline.Token

                    let start =
                        ProcessStartInfo(
                            options.Executable,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        )

                    start.ArgumentList.Add "app-server"
                    start.ArgumentList.Add "--strict-config"
                    start.ArgumentList.Add "--stdio"
                    start.Environment.Clear()
                    environment |> Array.iter (fun (key, value) -> start.Environment[key] <- value)

                    use proc = new Process(StartInfo = start)

                    let finish value =
                        try
                            if not proc.HasExited then
                                proc.Kill(true)
                        with _ ->
                            ()

                        value

                    try
                        if not (proc.Start()) then
                            return unknown query "codex-model-list-transport-unavailable" environment
                        else
                            let stderrDrain = drainBounded proc.StandardError.BaseStream options.MaximumStreamBytes token
                            use writer = proc.StandardInput
                            let totalBytes = ref 0

                            let readResponse requestId =
                                task {
                                    let pending =
                                        response proc.StandardOutput.BaseStream totalBytes requestId token

                                    let! winner =
                                        Task.WhenAny([| pending :> Task; stderrDrain :> Task |])

                                    if obj.ReferenceEquals(winner, stderrDrain) then
                                        do! stderrDrain
                                        return raise (EndOfStreamException "codex-app-server-stderr-closed")
                                    else
                                        return! pending
                                }

                            do!
                                writeMessage
                                    writer
                                    "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"fs_gg_coordination\",\"title\":\"FS.GG Coordination\",\"version\":\"1\"}}}"

                            let! _ = readResponse 1
                            do! writeMessage writer "{\"method\":\"initialized\",\"params\":{}}"

                            let pages = ResizeArray<JsonElement>()
                            let pageDigests = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                            let cursors = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                            let mutable cursor: string option = None
                            let mutable finished = false
                            let mutable failure: string option = None
                            let mutable pageNumber = 0

                            while not finished && failure.IsNone && pageNumber < maximumPages do
                                pageNumber <- pageNumber + 1
                                let requestId = pageNumber + 1

                                let cursorProperty =
                                    cursor
                                    |> Option.map (fun value -> $",\"cursor\":{JsonSerializer.Serialize value}")
                                    |> Option.defaultValue ""

                                do!
                                    writeMessage
                                        writer
                                        $"{{\"method\":\"model/list\",\"id\":{requestId},\"params\":{{\"limit\":{pageSize},\"includeHidden\":true{cursorProperty}}}}}"

                                let! page = readResponse requestId
                                let pageBytes = Encoding.UTF8.GetBytes(page.GetRawText())
                                let pageDigest = Convert.ToHexString(SHA256.HashData pageBytes)

                                if not (pageDigests.Add pageDigest) then
                                    failure <- Some "codex-model-list-duplicate-page"
                                else
                                    match page.TryGetProperty "data", page.TryGetProperty "nextCursor" with
                                    | (true, data), (true, next) when data.ValueKind = JsonValueKind.Array ->
                                        data.EnumerateArray() |> Seq.iter (fun item -> pages.Add(item.Clone()))

                                        match next.ValueKind with
                                        | JsonValueKind.Null -> finished <- true
                                        | JsonValueKind.String ->
                                            let value = next.GetString()

                                            if
                                                String.IsNullOrWhiteSpace value
                                                || Encoding.UTF8.GetByteCount value > maximumCursorBytes
                                                || not (cursors.Add value)
                                            then
                                                failure <- Some "codex-model-list-duplicate-page"
                                            else
                                                cursor <- Some value
                                        | _ -> failure <- Some "codex-model-list-invalid"
                                    | _ -> failure <- Some "codex-model-list-invalid"

                            if failure.IsNone && not finished then
                                failure <- Some "codex-model-list-incomplete"

                            let currentEnvironment = CodexLearningConfiguration.environment options

                            match failure with
                            | Some code -> return finish (unknown query code environment)
                            | None when
                                CodexLearningConfiguration.digest currentEnvironment
                                <> CodexLearningConfiguration.digest environment
                                ->
                                return finish (unknown query "codex-model-list-config-changed" environment)
                            | None when not (executableUnchanged query) ->
                                return finish (unknown query "codex-model-list-executable-changed" environment)
                            | None ->
                                use output = new MemoryStream()
                                use json = new Utf8JsonWriter(output)
                                json.WriteStartObject()
                                json.WritePropertyName "data"
                                json.WriteStartArray()
                                pages |> Seq.iter (fun item -> item.WriteTo json)
                                json.WriteEndArray()
                                json.WriteNull "nextCursor"
                                json.WriteEndObject()
                                json.Flush()

                                let evidence =
                                    CodexLearningProviderCapability.fromModelList
                                        (clock.GetUtcNow())
                                        query
                                        (output.ToArray())

                                return finish
                                    { evidence with
                                        Provenance = CodexLearningConfiguration.provenance environment
                                    }
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        return finish (unknown query "codex-model-list-cancelled" environment)
                    | :? OperationCanceledException
                    | :? TimeoutException ->
                        return finish (unknown query "codex-model-list-deadline-exceeded" environment)
                    | :? InvalidDataException as error -> return finish (unknown query error.Message environment)
                    | :? JsonException -> return finish (unknown query "codex-model-list-invalid" environment)
                    | _ -> return finish (unknown query "codex-model-list-transport-unavailable" environment)
            }

type CodexCommand =
    {
        FileName: string
        Arguments: string list
        WorkingDirectory: string
    }

[<RequireQualifiedAccess>]
module CodexCommand =
    let private common (options: CodexExecutionProviderOptions) (intent: LaunchIntent) schemaPath outputPath =
        [
            "exec"
            "--ignore-user-config"
            "--strict-config"
            "--json"
            "--color"
            "never"
            "--sandbox"
            "workspace-write"
            "-C"
            intent.Workspace
            "--output-schema"
            schemaPath
            "--output-last-message"
            outputPath
        ]
        @ (intent.Requested.Model
           |> Option.map (fun value -> [ "--model"; value ])
           |> Option.defaultValue [])
        @ (intent.Requested.Effort
           |> Option.map (fun value -> [ "-c"; $"model_reasoning_effort={JsonSerializer.Serialize value}" ])
           |> Option.defaultValue [])

    let launch options intent schemaPath outputPath =
        {
            FileName = options.Executable
            Arguments = common options intent schemaPath outputPath @ [ "-" ]
            WorkingDirectory = intent.Workspace
        }

    let resume options intent schemaPath outputPath threadId =
        {
            FileName = options.Executable
            Arguments = common options intent schemaPath outputPath @ [ "resume"; threadId; "-" ]
            WorkingDirectory = intent.Workspace
        }

type private CompletionEnvelope = { Status: string; Summary: string }

type private RunningProcess =
    {
        Intent: LaunchIntent
        Reference: ProviderSessionReference
        Process: Process
        Directory: string
        StdoutPath: string
        StderrPath: string
        FinalPath: string
        StartedAt: DateTimeOffset
        Completion: Task<SessionObservation>
        CancelRequested: bool ref
    }

type CodexExecutionProvider
    (
        options: CodexExecutionProviderOptions,
        input: ICodexExecutionInput,
        candidateInspector: ICodexCandidateInspector,
        clock: TimeProvider,
        ?learningCapabilityDiscovery: ICodexLearningCapabilityDiscovery
    ) as this =
    let identity =
        {
            Provider = "Codex"
            AdapterVersion = "codex-subscription-exec/1"
        }

    let sessions = ConcurrentDictionary<string, RunningProcess>()

    let sha256File path =
        use stream = File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()

    let resolveExecutablePath () =
        if
            Path.IsPathRooted options.Executable
            || options.Executable.Contains(Path.DirectorySeparatorChar)
        then
            Path.GetFullPath options.Executable
        else
            Environment.GetEnvironmentVariable "PATH"
            |> Option.ofObj
            |> Option.defaultValue ""
            |> fun value -> value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun directory -> Path.Combine(directory, options.Executable))
            |> Array.tryFind File.Exists
            |> Option.defaultValue options.Executable

    let executableIdentity version =
        let path = resolveExecutablePath ()

        let digest =
            try
                if File.Exists path then Some(sha256File path) else None
            with _ ->
                None

        {
            Path = path
            Version = version
            Sha256 = digest
        }

    let sessionValue reference =
        ProviderSessionReference.value reference

    let attemptDirectory (intent: LaunchIntent) =
        Path.Combine(
            options.StateRoot,
            intent.Key.AssignmentId.ToString("N"),
            intent.Key.AttemptId.ToString("N"),
            intent.Key.Generation.ToString(CultureInfo.InvariantCulture)
        )

    let scrubEnvironment (start: ProcessStartInfo) =
        let retained =
            start.Environment
            |> Seq.choose (fun pair ->
                if options.EnvironmentAllowList.Contains pair.Key then
                    Some(pair.Key, pair.Value)
                else
                    None)
            |> Seq.toArray

        start.Environment.Clear()
        retained |> Array.iter (fun (key, value) -> start.Environment[key] <- value)

    let processStartInfo (command: CodexCommand) =
        let start =
            ProcessStartInfo(
                command.FileName,
                WorkingDirectory = command.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            )

        command.Arguments |> List.iter start.ArgumentList.Add
        scrubEnvironment start
        start

    let readBounded (stream: Stream) maximumBytes =
        task {
            let buffer = Array.zeroCreate<byte> 4096
            use captured = new MemoryStream(min maximumBytes 4096)
            let mutable complete = false

            while not complete do
                let! count = stream.ReadAsync(buffer.AsMemory())

                if count = 0 then
                    complete <- true
                else
                    let remaining = maximumBytes - int captured.Length

                    if remaining > 0 then
                        captured.Write(buffer, 0, min remaining count)

            return Encoding.UTF8.GetString(captured.ToArray())
        }

    let pumpBounded (stream: Stream) path maximumBytes (onLine: string -> unit) (onOverflow: unit -> unit) =
        task {
            let buffer = Array.zeroCreate<byte> 4096
            let line = Array.zeroCreate<byte> maximumBytes
            let mutable lineLength = 0
            let mutable lineOverflow = false
            let mutable written = 0
            use output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)

            let emit () =
                if lineOverflow then
                    onOverflow ()
                elif lineLength > 0 then
                    let length =
                        if line[lineLength - 1] = 13uy then
                            lineLength - 1
                        else
                            lineLength

                    onLine (Encoding.UTF8.GetString(line, 0, length))

                lineLength <- 0
                lineOverflow <- false

            let mutable complete = false

            while not complete do
                let! count = stream.ReadAsync(buffer.AsMemory())

                if count = 0 then
                    complete <- true
                else
                    let remaining = maximumBytes - written

                    if remaining > 0 then
                        let countToWrite = min remaining count
                        do! output.WriteAsync(buffer.AsMemory(0, countToWrite))
                        written <- written + countToWrite

                    for index in 0 .. count - 1 do
                        if buffer[index] = 10uy then
                            emit ()
                        elif lineLength < line.Length then
                            line[lineLength] <- buffer[index]
                            lineLength <- lineLength + 1
                        else
                            lineOverflow <- true

            emit ()
        }

    let probe arguments (cancellationToken: CancellationToken) =
        task {
            let start =
                ProcessStartInfo(
                    options.Executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            arguments |> List.iter start.ArgumentList.Add
            scrubEnvironment start
            use proc = new Process(StartInfo = start)

            if not (proc.Start()) then
                raise (InvalidOperationException "codex-probe-start-refused")

            let output = readBounded proc.StandardOutput.BaseStream 4096
            let error = readBounded proc.StandardError.BaseStream 4096
            let exit = proc.WaitForExitAsync()

            let! winner =
                Task.WhenAny(exit, Task.Delay(options.StartupTimeout, cancellationToken))

            if not (obj.ReferenceEquals(winner, exit)) then
                try
                    proc.Kill(true)
                with _ ->
                    ()

                do! proc.WaitForExitAsync()
                cancellationToken.ThrowIfCancellationRequested()
                raise (TimeoutException "codex-probe-timeout")

            let! stdout = output
            let! stderr = error
            return proc.ExitCode, stdout, stderr
        }

    let readiness cancellationToken =
        task {
            try
                let! versionExit, versionOut, versionError = probe [ "--version" ] cancellationToken

                if
                    versionExit <> 0
                    || (versionOut + versionError).Trim() <> options.ExpectedVersion
                then
                    return AuthenticationUnknown "codex-version-mismatch"
                else
                    let! loginExit, stdout, stderr = probe [ "login"; "status" ] cancellationToken

                    if
                        loginExit = 0
                        && (stdout + stderr).Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase)
                    then
                        return Authenticated "codex-login-status:chatgpt-subscription"
                    elif loginExit = 0 then
                        return AuthenticationUnknown "codex-login-status-unrecognized-success"
                    else
                        return NotAuthenticated "codex-login-status-refused"
            with
            | :? OperationCanceledException -> return AuthenticationUnknown "codex-login-status-cancelled"
            | _ -> return AuthenticationUnknown "codex-login-status-unavailable"
        }

    let learningCapability requested cancellationToken =
        task {
            let observedAt = clock.GetUtcNow()

            let! version, unavailable =
                task {
                    try
                        let! exitCode, stdout, stderr = probe [ "--version" ] cancellationToken
                        let value = (stdout + stderr).Trim()

                        if exitCode = 0 && not (String.IsNullOrWhiteSpace value) then
                            return Some value, None
                        else
                            return None, Some "codex-version-unavailable"
                    with
                    | :? OperationCanceledException -> return None, Some "codex-version-cancelled"
                    | _ -> return None, Some "codex-version-unavailable"
                }

            let query =
                {
                    Provider = identity
                    Executable = executableIdentity version
                    Requested = requested
                    MaximumAge = TimeSpan.FromMinutes 5.
                }

            let! evidence =
                match unavailable, version, learningCapabilityDiscovery with
                | Some code, _, _ -> Task.FromResult(CodexLearningProviderCapability.unknown observedAt query code)
                | None, Some actual, _ when actual <> options.ExpectedVersion ->
                    Task.FromResult(CodexLearningProviderCapability.unknown observedAt query "codex-version-mismatch")
                | None, Some _, Some discovery -> discovery.Discover(query, cancellationToken)
                | None, Some _, None ->
                    Task.FromResult(
                        CodexLearningProviderCapability.unknown
                            observedAt
                            query
                            "codex-native-selection-capability-unavailable"
                    )
                | _ ->
                    Task.FromResult(
                        CodexLearningProviderCapability.unknown observedAt query "codex-version-unavailable"
                    )

            return query, evidence
        }

    let writeSchema path =
        File.WriteAllText(
            path,
            """{"type":"object","additionalProperties":false,"required":["status","summary"],"properties":{"status":{"type":"string","enum":["completed"]},"summary":{"type":"string","minLength":1,"maxLength":4096}}}"""
        )

    let unknownUsage provenance =
        {
            Values = Map["provider-usage", UsageUnknown provenance]
            Cost = CostNotApplicable "codex-chatgpt-subscription-no-per-invocation-price"
        }

    let parseUsage (lines: seq<string>) =
        lines
        |> Seq.tryPick (fun line ->
            try
                use document = JsonDocument.Parse line
                let root = document.RootElement

                if
                    root.GetProperty("type").GetString() <> "turn.completed"
                    || not (root.TryGetProperty("usage") |> fst)
                then
                    None
                else
                    let usage = root.GetProperty "usage"

                    let value (name: string) =
                        try
                            let item = usage.GetProperty name in

                            if item.ValueKind = JsonValueKind.Number then
                                Some(item.GetInt64())
                            else
                                None
                        with _ ->
                            None

                    let values =
                        [
                            "input_tokens"
                            "cached_input_tokens"
                            "output_tokens"
                            "reasoning_output_tokens"
                        ]
                        |> List.map (fun name -> name, value name)

                    if values |> List.exists (snd >> Option.isNone) then
                        None
                    else
                        Some
                            {
                                Values =
                                    values
                                    |> List.map (fun (name, value) ->
                                        name, UsageKnown(value.Value, "tokens", "codex-exec-jsonl:turn.completed"))
                                    |> Map.ofList
                                Cost = CostNotApplicable "codex-chatgpt-subscription-no-per-invocation-price"
                            }
            with _ ->
                None)
        |> Option.defaultValue (unknownUsage "codex-exec-jsonl:usage-absent-or-malformed")

    let fatalClassification (lines: seq<string>) =
        lines
        |> Seq.tryPick (fun line ->
            try
                use document = JsonDocument.Parse line
                let root = document.RootElement
                let kind = root.GetProperty("type").GetString()

                let message =
                    if kind = "error" then
                        root.GetProperty("message").GetString()
                    elif kind = "turn.failed" then
                        root.GetProperty("error").GetProperty("message").GetString()
                    else
                        null

                if isNull message then
                    None
                elif
                    message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                then
                    Some "codex-quota-error"
                else
                    Some "codex-fatal-error"
            with _ ->
                None)

    let startProcess
        (intent: LaunchIntent)
        (commandBuilder: string -> string -> CodexCommand)
        (prompt: byte array)
        cancellationToken
        =
        task {
            let directory = attemptDirectory intent
            Directory.CreateDirectory directory |> ignore
            let schemaPath = Path.Combine(directory, "completion-schema.json")
            let finalPath = Path.Combine(directory, "final.json")
            let stdoutPath = Path.Combine(directory, "stdout.jsonl")
            let stderrPath = Path.Combine(directory, "stderr.log")
            let fatalPath = Path.Combine(directory, "fatal.receipt")
            writeSchema schemaPath
            let command = commandBuilder schemaPath finalPath
            let receiptPath = Path.Combine(directory, "spawn-intent.json")

            let receipt =
                $"{{\"schema\":\"fsgg.codex.spawn-intent/1\",\"assignmentId\":\"{intent.Key.AssignmentId}\",\"attemptId\":\"{intent.Key.AttemptId}\",\"generation\":{intent.Key.Generation},\"inputDigest\":\"{intent.InputDigest}\"}}"

            try
                use marker =
                    new FileStream(
                        receiptPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        4096,
                        FileOptions.WriteThrough
                    )

                let bytes = Encoding.UTF8.GetBytes receipt
                marker.Write(bytes, 0, bytes.Length)
                marker.Flush(true)
            with :? IOException ->
                raise (InvalidOperationException "codex-existing-launch-ambiguous")

            let proc =
                new Process(StartInfo = processStartInfo command, EnableRaisingEvents = true)

            if not (proc.Start()) then
                raise (InvalidOperationException "codex-process-start-refused")

            let startedAt = clock.GetUtcNow()
            let createdPath = Path.Combine(directory, "process-created.json")

            try
                use created =
                    new FileStream(
                        createdPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        4096,
                        FileOptions.WriteThrough
                    )

                let evidence =
                    Encoding.UTF8.GetBytes(
                        $"{{\"schema\":\"fsgg.codex.process-created/1\",\"pid\":{proc.Id},\"processStartUtcTicks\":{proc.StartTime.ToUniversalTime().Ticks},\"observedAt\":\"{startedAt:O}\"}}"
                    )

                created.Write(evidence, 0, evidence.Length)
                created.Flush(true)
            with _ ->
                try
                    proc.Kill(true)
                with _ ->
                    ()

                raise (InvalidOperationException "codex-process-creation-evidence-ambiguous")

            let runtimeDeadline =
                min intent.Limits.Deadline (intent.RecordedAt.Add intent.Limits.MaximumRuntime)

            let remaining = max TimeSpan.Zero (runtimeDeadline - startedAt)
            let cancelRequested = ref false

            let threadStarted =
                TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

            let mutable completedUsage: NormalizedUsage option = None
            let mutable fatalEvent: string option = None
            let mutable turnCompleted = false
            let mutable telemetryThread: string option = None
            let mutable telemetryTurnSequence = 0L

            let telemetryGap code =
                options.TurnObserver
                |> Option.iter (fun observer ->
                    try
                        observer.Gap code
                    with _ ->
                        ())

            options.TurnObserver
            |> Option.iter (fun observer ->
                try
                    observer.ProcessStarted(proc.Id, startedAt)
                with _ ->
                    telemetryGap "process-start-observer-failed")

            let stdoutPump =
                pumpBounded
                    proc.StandardOutput.BaseStream
                    stdoutPath
                    options.MaximumStreamBytes
                    (fun line ->
                        let usage = parseUsage [ line ]

                        if not (usage.Values.ContainsKey "provider-usage") then
                            completedUsage <- Some usage

                        fatalClassification [ line ]
                        |> Option.iter (fun value -> fatalEvent <- Some value)

                        try
                            use document = JsonDocument.Parse line
                            let eventType = document.RootElement.GetProperty("type").GetString()

                            if eventType = "thread.started" then
                                let nativeThread = document.RootElement.GetProperty("thread_id").GetString()
                                telemetryThread <- Some nativeThread
                                threadStarted.TrySetResult nativeThread |> ignore

                                options.TurnObserver
                                |> Option.iter (fun observer ->
                                    try
                                        observer.ThreadStarted(proc.Id, nativeThread, DateTimeOffset.UtcNow)
                                    with _ ->
                                        telemetryGap "thread-start-observer-failed")
                            elif eventType = "turn.started" then
                                let nativeThread =
                                    match document.RootElement.TryGetProperty "thread_id" with
                                    | true, value when value.ValueKind = JsonValueKind.String ->
                                        Some(value.GetString())
                                    | _ -> telemetryThread

                                let nativeTurn =
                                    match document.RootElement.TryGetProperty "turn_id" with
                                    | true, value when value.ValueKind = JsonValueKind.String ->
                                        Some(value.GetString())
                                    | _ -> None

                                match nativeThread with
                                | Some thread ->
                                    options.TurnObserver
                                    |> Option.iter (fun observer ->
                                        try
                                            observer.NativeTurnStarted(
                                                proc.Id,
                                                thread,
                                                nativeTurn,
                                                telemetryTurnSequence + 1L,
                                                DateTimeOffset.UtcNow
                                            )
                                        with _ ->
                                            telemetryGap "turn-start-observer-failed")
                                | None -> telemetryGap "missing-turn-thread"
                            elif eventType = "turn.completed" then
                                turnCompleted <- true
                        with _ ->
                            ()

                        match CodexTurnProjection.project telemetryThread (telemetryTurnSequence + 1L) line with
                        | Some(Ok turn) ->
                            telemetryTurnSequence <- telemetryTurnSequence + 1L

                            options.TurnObserver
                            |> Option.iter (fun observer ->
                                try
                                    observer.TurnCompleted turn
                                with _ ->
                                    telemetryGap "turn-observer-failed")
                        | Some(Error code) ->
                            telemetryTurnSequence <- telemetryTurnSequence + 1L
                            telemetryGap code
                        | None -> ())
                    (fun () -> telemetryGap "oversized-jsonl-line")

            let stderrPump =
                pumpBounded proc.StandardError.BaseStream stderrPath options.MaximumStreamBytes ignore ignore

            let inputWrite =
                task {
                    try
                        do! proc.StandardInput.BaseStream.WriteAsync(ReadOnlyMemory prompt, cancellationToken)
                        proc.StandardInput.Close()
                    with _ ->
                        try
                            proc.StandardInput.Close()
                        with _ ->
                            ()
                }

            let completion =
                task {
                    let exit = proc.WaitForExitAsync()
                    let timeout = Task.Delay(remaining)
                    let! first = Task.WhenAny(exit, timeout)
                    let mutable deadlineExceeded = false

                    if obj.ReferenceEquals(first, timeout) then
                        deadlineExceeded <- true

                        try
                            proc.Kill(true)
                        with _ ->
                            ()

                        do! proc.WaitForExitAsync()

                    do! inputWrite
                    do! stdoutPump
                    do! stderrPump

                    options.TurnObserver
                    |> Option.iter (fun observer ->
                        try
                            observer.ProcessTerminal(proc.ExitCode, telemetryThread, DateTimeOffset.UtcNow)
                        with _ ->
                            telemetryGap "process-terminal-observer-failed")

                    let usage, fatal = completedUsage, fatalEvent

                    let! lifecycle, candidate =
                        task {
                            if deadlineExceeded then
                                return DeadlineExceeded, None
                            elif cancelRequested.Value then
                                return OutcomeUnknown, None
                            elif fatal.IsSome then
                                return Failed, None
                            elif proc.ExitCode <> 0 then
                                return Failed, None
                            elif not turnCompleted then
                                return OutcomeUnknown, None
                            elif not (File.Exists finalPath) then
                                return OutcomeUnknown, None
                            elif FileInfo(finalPath).Length > int64 options.MaximumStreamBytes then
                                return OutcomeUnknown, None
                            else
                                try
                                    use stream = File.OpenRead finalPath
                                    use document = JsonDocument.Parse(stream, JsonDocumentOptions(MaxDepth = 8))
                                    let root = document.RootElement
                                    let properties = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray
                                    let propertySet = Set.ofArray properties

                                    let allowed =
                                        set["status"
                                            "summary"
                                            "inputDigest"
                                            "candidateId"]

                                    let optionalTextIsBounded (name: string) =
                                        match root.TryGetProperty name with
                                        | false, _ -> true
                                        | true, value ->
                                            value.ValueKind = JsonValueKind.String
                                            && (value.GetString()
                                                |> Option.ofObj
                                                |> Option.exists (fun text -> text.Length <= 4096))

                                    if
                                        root.ValueKind <> JsonValueKind.Object
                                        || properties.Length <> propertySet.Count
                                        || not (Set.isSubset propertySet allowed)
                                        || not (propertySet.Contains "status" && propertySet.Contains "summary")
                                        || not (
                                            optionalTextIsBounded "inputDigest" && optionalTextIsBounded "candidateId"
                                        )
                                    then
                                        return OutcomeUnknown, None
                                    else
                                        let envelope =
                                            {
                                                Status = root.GetProperty("status").GetString()
                                                Summary = root.GetProperty("summary").GetString()
                                            }

                                        if
                                            envelope.Status <> "completed"
                                            || String.IsNullOrWhiteSpace envelope.Summary
                                            || envelope.Summary.Length > 4096
                                        then
                                            return OutcomeUnknown, None
                                        else
                                            let! candidate =
                                                candidateInspector.CreateCandidate(
                                                    intent.Workspace,
                                                    CancellationToken.None
                                                )

                                            match candidate with
                                            | Ok value when value.CandidateId = candidateInspector.CandidateId ->
                                                return Succeeded, Some value
                                            | _ -> return OutcomeUnknown, None
                                with _ ->
                                    return OutcomeUnknown, None
                        }

                    let session =
                        match threadStarted.Task.IsCompletedSuccessfully with
                        | true ->
                            ProviderSessionReference.create ("codex-thread:" + threadStarted.Task.Result)
                            |> Result.defaultWith failwith
                        | false ->
                            ProviderSessionReference.create (
                                $"codex-process:{proc.Id}:{startedAt.ToUnixTimeMilliseconds()}"
                            )
                            |> Result.defaultWith failwith

                    let outputs =
                        [
                            if File.Exists finalPath then
                                {
                                    Kind = "final-output"
                                    Reference = finalPath
                                    Digest = Some(sha256File finalPath)
                                }
                            {
                                Kind = "codex-jsonl"
                                Reference = stdoutPath
                                Digest = Some(sha256File stdoutPath)
                            }
                            {
                                Kind = "codex-stderr"
                                Reference = stderrPath
                                Digest = Some(sha256File stderrPath)
                            }
                        ]

                    fatal
                    |> Option.iter (fun kind -> File.WriteAllText(fatalPath, kind + Environment.NewLine))

                    let lifecycleReferences =
                        fatal
                        |> Option.map (fun kind ->
                            [
                                {
                                    Kind = kind
                                    Reference = fatalPath
                                    Digest = Some(sha256File fatalPath)
                                }
                            ])
                        |> Option.defaultValue []

                    return
                        {
                            Provider = identity
                            Session = session
                            Resolved =
                                {
                                    Model = intent.Requested.Model
                                    Effort = intent.Requested.Effort
                                }
                            Lifecycle = lifecycle
                            Output = outputs
                            LifecycleReferences = lifecycleReferences
                            Usage =
                                usage
                                |> Option.defaultValue (unknownUsage "codex-exec-jsonl:usage-absent-or-malformed")
                            Candidate = candidate
                            ObservedAt = clock.GetUtcNow()
                        }
                }

            let! first =
                Task.WhenAny(threadStarted.Task, completion, Task.Delay(options.StartupTimeout, cancellationToken))

            if threadStarted.Task.IsCompletedSuccessfully then
                let! threadId = threadStarted.Task

                let reference =
                    ProviderSessionReference.create ("codex-thread:" + threadId)
                    |> Result.defaultWith failwith

                let running =
                    {
                        Intent = intent
                        Reference = reference
                        Process = proc
                        Directory = directory
                        StdoutPath = stdoutPath
                        StderrPath = stderrPath
                        FinalPath = finalPath
                        StartedAt = startedAt
                        Completion = completion
                        CancelRequested = cancelRequested
                    }

                sessions[sessionValue reference] <- running
                return Ok running
            elif obj.ReferenceEquals(first, completion) then
                return Error "codex-completed-before-thread-started"
            else
                try
                    proc.Kill(true)
                with _ ->
                    ()

                do! completion :> Task
                return Error "codex-thread-start-timeout"
        }

    interface ILearningExecutionProvider with
        member _.ObserveLearningSelection(requested, cancellationToken) =
            task {
                let! _, evidence = learningCapability requested cancellationToken
                return evidence
            }

        member _.LaunchLearning(intent, cancellationToken) =
            task {
                let! query, evidence = learningCapability intent.Requested cancellationToken

                match LearningSelectionEvidence.authorize (clock.GetUtcNow()) query evidence with
                | Error code -> return LaunchRefused code
                | Ok() when executableIdentity query.Executable.Version <> query.Executable ->
                    return LaunchRefused "learning-selection-executable-identity-changed"
                | Ok() when
                    evidence.Provenance.StartsWith(
                        "codex-app-server:model/list;config-sha256=",
                        StringComparison.Ordinal
                    )
                    && evidence.Provenance
                       <> CodexLearningConfiguration.provenance (
                           CodexLearningConfiguration.environment options
                       )
                    ->
                    return LaunchRefused "learning-selection-config-boundary-changed"
                | Ok() -> return! (this :> IExecutionProvider).Launch(intent, cancellationToken)
            }

    interface IExecutionProvider with
        member _.ObserveReadiness cancellationToken =
            task {
                let! authentication = readiness cancellationToken

                return
                    {
                        Identity = identity
                        Authentication = authentication
                        SupportsResume = true
                        ObservedAt = clock.GetUtcNow()
                    }
            }

        member _.Launch(intent, cancellationToken) =
            task {
                if
                    options.MaximumStreamBytes < 1
                    || options.StartupTimeout <= TimeSpan.Zero
                    || String.IsNullOrWhiteSpace options.StateRoot
                then
                    return LaunchRefused "codex-adapter-options-invalid"
                else
                    let! authentication = readiness cancellationToken

                    match authentication with
                    | Authenticated _ ->
                        let! bytes = input.ReadUtf8(intent.InputDigest, cancellationToken)

                        match bytes with
                        | Error reason -> return LaunchRefused reason
                        | Ok prompt when
                            not (
                                String.Equals(
                                    Convert.ToHexString(SHA256.HashData prompt),
                                    intent.InputDigest,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            ->
                            return LaunchRefused "codex-input-digest-mismatch"
                        | Ok prompt ->
                            try
                                let! started =
                                    startProcess
                                        intent
                                        (fun schema output -> CodexCommand.launch options intent schema output)
                                        prompt
                                        cancellationToken

                                match started with
                                | Error reason -> return LaunchAmbiguous reason
                                | Ok running ->
                                    return
                                        LaunchStarted
                                            {
                                                Provider = identity
                                                Session = running.Reference
                                                Resolved =
                                                    {
                                                        Model = intent.Requested.Model
                                                        Effort = intent.Requested.Effort
                                                    }
                                                Lifecycle = Running
                                                Output = []
                                                LifecycleReferences = []
                                                Usage = unknownUsage "codex-turn-not-completed"
                                                Candidate = None
                                                ObservedAt = clock.GetUtcNow()
                                            }
                            with error ->
                                return LaunchAmbiguous error.Message
                    | NotAuthenticated provenance -> return LaunchRefused provenance
                    | AuthenticationUnknown provenance -> return LaunchRefused provenance
            }

        member _.Observe(reference, _) =
            task {
                match sessions.TryGetValue(sessionValue reference) with
                | false, _ -> return Error "codex-session-not-supervised"
                | true, running when running.Completion.IsCompleted -> return Ok running.Completion.Result
                | true, running ->
                    return
                        Ok
                            {
                                Provider = identity
                                Session = reference
                                Resolved =
                                    {
                                        Model = running.Intent.Requested.Model
                                        Effort = running.Intent.Requested.Effort
                                    }
                                Lifecycle =
                                    (if running.CancelRequested.Value then
                                         Cancelling
                                     else
                                         Running)
                                Output = []
                                LifecycleReferences = []
                                Usage = unknownUsage "codex-turn-not-completed"
                                Candidate = None
                                ObservedAt = clock.GetUtcNow()
                            }
            }

        member _.Reconcile(intent, _) =
            task {
                match sessions.Values |> Seq.tryFind (fun running -> running.Intent.Key = intent.Key) with
                | Some running when running.Completion.IsCompleted -> return Reconciled running.Completion.Result
                | Some running ->
                    return
                        Reconciled
                            {
                                Provider = identity
                                Session = running.Reference
                                Resolved =
                                    {
                                        Model = intent.Requested.Model
                                        Effort = intent.Requested.Effort
                                    }
                                Lifecycle = Running
                                Output = []
                                LifecycleReferences = []
                                Usage = unknownUsage "codex-turn-not-completed"
                                Candidate = None
                                ObservedAt = clock.GetUtcNow()
                            }
                | None when File.Exists(Path.Combine(attemptDirectory intent, "spawn-intent.json")) ->
                    return ReconcileUnknown "codex-spawn-receipt-without-local-supervisor"
                | None -> return ConfirmedAbsent
            }

        member _.Cancel(reference, _) =
            task {
                match sessions.TryGetValue(sessionValue reference) with
                | false, _ -> return CancelUnknown "codex-session-not-supervised"
                | true, running when running.Process.HasExited -> return CancelRefused "codex-session-already-terminal"
                | true, running ->
                    running.CancelRequested.Value <- true

                    try
                        running.Process.Kill(true)
                        return CancelAccepted
                    with _ ->
                        return CancelUnknown "codex-process-tree-termination-ambiguous"
            }

[<RequireQualifiedAccess>]
module CodexExecution =
    let provider options input candidateInspector clock =
        let discovery =
            CodexAppServerLearningCapabilityDiscovery(options, clock)
            :> ICodexLearningCapabilityDiscovery

        CodexExecutionProvider(
            options,
            input,
            candidateInspector,
            clock,
            learningCapabilityDiscovery = discovery
        )
        :> IExecutionProvider

    let coordinator options input candidateInspector journal clock =
        ExecutionSessionCoordinator(provider options input candidateInspector clock, journal, clock)

    let supervisedActorProps options input candidateInspector journal clock =
        ExecutionSessionActor.Props(coordinator options input candidateInspector journal clock)
