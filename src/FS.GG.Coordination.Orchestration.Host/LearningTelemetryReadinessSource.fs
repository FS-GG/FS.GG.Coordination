namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution

type LearningTelemetryReadinessOptions =
    {
        PythonExecutable: string
        ReaderScript: string
        ReaderScriptSha256: string
        ProtectedHostExecutable: string
        ProtectedHostSha256: string
        ProtectedHostConfig: string
        AuthoritativeRoster: string
    }

[<RequireQualifiedAccess>]
module LearningTelemetryReadinessProcess =
    let private hex = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private sha256 (path: string) =
        use stream = File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let run (options: LearningTelemetryReadinessOptions) (token: CancellationToken) =
        task {
            let paths =
                [ options.PythonExecutable; options.ReaderScript; options.ProtectedHostExecutable
                  options.ProtectedHostConfig; options.AuthoritativeRoster ]

            if paths |> List.exists (fun path -> String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path)) then
                return Error "learning-telemetry-reader-path-invalid"
            elif not (hex.IsMatch options.ReaderScriptSha256) || not (hex.IsMatch options.ProtectedHostSha256) then
                return Error "learning-telemetry-reader-pin-invalid"
            else
                try
                    if sha256 options.ReaderScript <> options.ReaderScriptSha256 then
                        return Error "learning-telemetry-reader-script-pin-mismatch"
                    else
                        let start = ProcessStartInfo(options.PythonExecutable)
                        start.UseShellExecute <- false
                        start.RedirectStandardInput <- true
                        start.RedirectStandardOutput <- true
                        start.RedirectStandardError <- true
                        start.CreateNoWindow <- true
                        start.Environment.Clear()
                        start.Environment["PATH"] <- "/usr/bin:/bin"
                        start.Environment["LANG"] <- "C.UTF-8"
                        [ options.ReaderScript
                          "--protected-host-executable"; options.ProtectedHostExecutable
                          "--protected-host-sha256"; options.ProtectedHostSha256
                          "--protected-host-config"; options.ProtectedHostConfig
                          "--authoritative-roster"; options.AuthoritativeRoster ]
                        |> List.iter start.ArgumentList.Add
                        use proc = new Process(StartInfo = start)
                        if not (proc.Start()) then
                            return Error "learning-telemetry-reader-start-failed"
                        else
                            proc.StandardInput.Close()
                            let stdout = proc.StandardOutput.ReadToEndAsync()
                            let stderr = proc.StandardError.ReadToEndAsync()
                            use timeout = CancellationTokenSource.CreateLinkedTokenSource token
                            timeout.CancelAfter(TimeSpan.FromSeconds 55.)
                            try
                                do! proc.WaitForExitAsync timeout.Token
                            with :? OperationCanceledException ->
                                try proc.Kill true with _ -> ()
                                do! proc.WaitForExitAsync(CancellationToken.None)
                            let! output = stdout
                            let! error = stderr
                            if token.IsCancellationRequested then
                                return Error "learning-telemetry-reader-cancelled"
                            elif proc.ExitCode <> 0 then
                                return Error "learning-telemetry-reader-refused"
                            elif output.Length > 1024 * 1024 || error.Length > 4096 || error.Length > 0 then
                                return Error "learning-telemetry-reader-output-invalid"
                            else
                                return Ok output
                with
                | :? IOException -> return Error "learning-telemetry-reader-io-refused"
                | :? UnauthorizedAccessException -> return Error "learning-telemetry-reader-custody-refused"
                | :? System.ComponentModel.Win32Exception -> return Error "learning-telemetry-reader-start-failed"
        }

[<RequireQualifiedAccess>]
module LearningTelemetryReadinessDocument =
    let private exactFields expected (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then false
        else
            let fields = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            fields.Length = Set.count expected && Set.ofList fields = expected

    let private text (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) ->
            Ok(value.GetString())
        | _ -> Error("learning-telemetry-reader-field-invalid:" + name)

    let private source (element: JsonElement) =
        let fields = set [ "producerId"; "revision"; "recordId"; "observedAt" ]
        if not (exactFields fields element) then Error "learning-telemetry-reader-source-schema-invalid"
        else
            match text "producerId" element, text "revision" element, text "recordId" element, text "observedAt" element with
            | Ok producer, Ok revision, Ok record, Ok observed ->
                match DateTimeOffset.TryParse observed with
                | true, timestamp -> Ok { ProducerId = producer; Revision = revision; RecordId = record; ObservedAt = timestamp }
                | _ -> Error "learning-telemetry-reader-observed-at-invalid"
            | Error reason, _, _, _ | _, Error reason, _, _ | _, _, Error reason, _ | _, _, _, Error reason -> Error reason

    let parse (expectedKey: LearningOperationalWindowKey) (json: string) =
        try
            use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 32, AllowTrailingCommas = false))
            let root = document.RootElement
            let fields = set [ "schema"; "status"; "key"; "source"; "nativeDeliveryRevision"; "members"; "unavailable"; "workspaceSha256" ]
            if not (exactFields fields root) || root.GetProperty("schema").GetString() <> "fsgg.learn.telemetry-readiness-census/1" then
                Error "learning-telemetry-reader-schema-invalid"
            elif root.GetProperty("status").GetString() <> "ready" then
                let reasons = root.GetProperty("unavailable").EnumerateArray() |> Seq.map _.GetString() |> String.concat ","
                Error("learning-telemetry-evidence-unavailable:" + reasons)
            else
                let key = root.GetProperty "key"
                if (not (exactFields (set [ "windowId"; "originalItemId" ]) key) ||
                    key.GetProperty("windowId").GetString() <> expectedKey.WindowId ||
                    key.GetProperty("originalItemId").GetString() <> expectedKey.OriginalItemId) then
                    Error "learning-telemetry-reader-key-mismatch"
                else
                    match source (root.GetProperty "source") with
                    | Error reason -> Error reason
                    | Ok censusSource ->
                        let members = ResizeArray<LearningOperationalCensusMember>()
                        let identities = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                        let mutable failure = None
                        for censusMember in root.GetProperty("members").EnumerateArray() do
                            let memberFields = set [ "itemId"; "originalItemId"; "role"; "state"; "source"; "nativeUsageSha256"; "sharedCostSha256" ]
                            if failure.IsNone && not (exactFields memberFields censusMember) then
                                failure <- Some "learning-telemetry-reader-member-schema-invalid"
                            elif failure.IsNone then
                                match text "itemId" censusMember, text "originalItemId" censusMember, text "role" censusMember, text "state" censusMember, source (censusMember.GetProperty "source") with
                                | Ok item, Ok original, Ok role, Ok state, Ok memberSource when original = expectedKey.OriginalItemId && identities.Add item ->
                                    let optionalDigest (name: string) =
                                        let value = censusMember.GetProperty name
                                        if value.ValueKind = JsonValueKind.Null then None else Some(value.GetString())
                                    members.Add { ItemId = item; OriginalItemId = original; Role = role; State = state; Source = memberSource
                                                  NativeUsageSha256 = optionalDigest "nativeUsageSha256"
                                                  SharedCostSha256 = optionalDigest "sharedCostSha256" }
                                | _ -> failure <- Some "learning-telemetry-reader-member-invalid"
                        match failure with
                        | Some reason -> Error reason
                        | None when members.Count <> 6 -> Error "learning-telemetry-reader-member-count-invalid"
                        | None ->
                            Ok(censusSource, root.GetProperty("nativeDeliveryRevision").GetString(), List.ofSeq members)
        with
        | :? JsonException -> Error "learning-telemetry-reader-json-invalid"
        | :? InvalidOperationException -> Error "learning-telemetry-reader-json-invalid"

type LearningTelemetryReadinessSource
    (options: LearningTelemetryReadinessOptions,
     readInstalledCustody: LearningOperationalWindowKey * CancellationToken -> Task<Result<LearningOperationalProducerIdentity, string>>,
     readProviderCapability: LearningOperationalWindowKey * CancellationToken -> Task<Result<LearningOperationalProducerIdentity, string>>,
     ?runReader: LearningTelemetryReadinessOptions -> CancellationToken -> Task<Result<string, string>>) =

    let run = defaultArg runReader LearningTelemetryReadinessProcess.run

    interface ILearningOperationalCensusSource with
        member _.ReadLearningOperationalCensus(key, token) =
            task {
                let! result = run options token
                match result with
                | Error reason -> return Error reason
                | Ok json ->
                    match LearningTelemetryReadinessDocument.parse key json with
                    | Error reason -> return Error reason
                    | Ok(source, nativeRevision, members) ->
                        let! custody = readInstalledCustody(key, token)
                        let! capability = readProviderCapability(key, token)
                        match custody, capability with
                        | Ok installed, Ok provider ->
                            return Ok { Key = key; Source = source; InstalledCustody = Some installed
                                        ProviderCapability = Some provider; NativeDeliveryRevision = nativeRevision
                                        Members = members }
                        | Error reason, _ -> return Error("learning-installed-custody-unavailable:" + reason)
                        | _, Error reason -> return Error("learning-provider-capability-unavailable:" + reason)
            }
