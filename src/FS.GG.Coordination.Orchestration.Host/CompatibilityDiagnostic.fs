namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open FS.GG.Coordination.Orchestration.Runner.Protocol

type ServedCompatibilityDiagnostic =
    {
        Schema: string
        Scope: string
        CorrelationId: Guid
        HostExecutableSha256: string
        RunnerExecutableSha256: string
        ProviderExecutableSha256: string
        Provider: string
        AdapterVersion: string
        VersionState: string
        ObservedVersion: string
        AuthenticationState: string
        AuthenticationProvenance: string
        SupportsResume: bool
        ObservedAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
module ServedCompatibilityDiagnostic =
    let private scrubEnvironment (allowList: Set<string>) (start: ProcessStartInfo) =
        let retained =
            start.Environment
            |> Seq.choose (fun pair ->
                if allowList.Contains pair.Key then
                    Some(pair.Key, pair.Value)
                else
                    None)
            |> Seq.toArray

        start.Environment.Clear()
        retained |> Array.iter (fun (key, value) -> start.Environment[key] <- value)

    let private sha256 path =
        try
            use stream = File.OpenRead path
            SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant() |> Some
        with _ ->
            None

    let private readBounded (stream: Stream) maximumBytes cancellationToken =
        task {
            let buffer = Array.zeroCreate<byte> 4096
            use captured = new MemoryStream(min maximumBytes 4096)
            let mutable complete = false

            while not complete do
                let! count = stream.ReadAsync(buffer.AsMemory(), cancellationToken)

                if count = 0 then
                    complete <- true
                else
                    let remaining = maximumBytes - int captured.Length

                    if remaining > 0 then
                        captured.Write(buffer, 0, min remaining count)

            return Encoding.UTF8.GetString(captured.ToArray())
        }

    let private terminate (child: Process) =
        task {
            try
                if not child.HasExited then
                    child.Kill(true)

                use cleanup = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
                do! child.WaitForExitAsync(cleanup.Token)
                return child.HasExited
            with _ ->
                return false
        }

    let run (configuration: CompatibilityDiagnosticConfiguration) =
        task {
            let hostPath = Environment.ProcessPath |> Option.ofObj
            let hostHash = hostPath |> Option.bind sha256

            if hostHash.IsNone then
                return Error "compatibility-host-identity-unavailable"
            elif
                not (File.Exists configuration.RunnerExecutable)
                || not (File.Exists configuration.ProviderExecutable)
            then
                return Error "compatibility-component-missing"
            elif sha256 configuration.RunnerExecutable <> Some configuration.RunnerSha256 then
                return Error "compatibility-runner-pin-refused"
            elif sha256 configuration.ProviderExecutable <> Some configuration.ProviderSha256 then
                return Error "compatibility-provider-pin-refused"
            else
                use deadline = new CancellationTokenSource(configuration.Timeout)

                let start =
                    ProcessStartInfo(
                        configuration.RunnerExecutable,
                        WorkingDirectory = configuration.WorkingDirectory,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    )

                for value in
                    [
                        "diagnostic-stdio"
                        "--provider-executable"
                        configuration.ProviderExecutable
                        "--provider-sha256"
                        configuration.ProviderSha256
                        "--expected-codex-version"
                        configuration.ExpectedCodexVersion
                        "--timeout-seconds"
                        string (int configuration.Timeout.TotalSeconds)
                    ] do
                    start.ArgumentList.Add value

                scrubEnvironment configuration.EnvironmentAllowList start

                use child = new Process(StartInfo = start)

                let! outcome =
                    task {
                        try
                            if not (child.Start()) then
                                return Error "compatibility-runner-start-refused"
                            else
                                let stderr = readBounded child.StandardError.BaseStream 4096 deadline.Token

                                let request =
                                    {
                                        Schema = CompatibilityDiagnosticWire.requestSchema
                                        CorrelationId = Guid.NewGuid()
                                    }

                                let encoded = CompatibilityDiagnosticWire.encodeRequest request
                                let header = Array.zeroCreate<byte> 4
                                BinaryPrimitives.WriteInt32BigEndian(header, encoded.Length)
                                do! child.StandardInput.BaseStream.WriteAsync(header, deadline.Token)
                                do! child.StandardInput.BaseStream.WriteAsync(encoded, deadline.Token)
                                child.StandardInput.Close()
                                let responseHeader = Array.zeroCreate<byte> 4
                                do! child.StandardOutput.BaseStream.ReadExactlyAsync(responseHeader, deadline.Token)
                                let size = BinaryPrimitives.ReadInt32BigEndian responseHeader

                                if size < 1 || size > CompatibilityDiagnosticWire.maximumBytes then
                                    return Error "compatibility-response-frame-refused"
                                else
                                    let responseBytes = Array.zeroCreate<byte> size
                                    do! child.StandardOutput.BaseStream.ReadExactlyAsync(responseBytes, deadline.Token)
                                    let extra = Array.zeroCreate<byte> 1

                                    let! extraCount =
                                        child.StandardOutput.BaseStream.ReadAsync(extra.AsMemory(), deadline.Token)

                                    do! child.WaitForExitAsync(deadline.Token)
                                    let! diagnosticError = stderr

                                    if child.ExitCode <> 0 then
                                        return
                                            Error(
                                                if String.IsNullOrWhiteSpace diagnosticError then
                                                    "compatibility-runner-unsupported"
                                                else
                                                    diagnosticError.Trim()
                                            )
                                    elif extraCount <> 0 then
                                        return Error "compatibility-extra-response-refused"
                                    elif sha256 configuration.RunnerExecutable <> Some configuration.RunnerSha256 then
                                        return Error "compatibility-runner-changed"
                                    elif
                                        sha256 configuration.ProviderExecutable <> Some configuration.ProviderSha256
                                    then
                                        return Error "compatibility-provider-changed"
                                    else
                                        match CompatibilityDiagnosticWire.parseResponse responseBytes with
                                        | Error reason -> return Error reason
                                        | Ok response when response.CorrelationId <> request.CorrelationId ->
                                            return Error "compatibility-correlation-refused"
                                        | Ok response when
                                            response.ProviderExecutableSha256 <> configuration.ProviderSha256
                                            ->
                                            return Error "compatibility-provider-pin-response-refused"
                                        | Ok response ->
                                            return
                                                Ok
                                                    {
                                                        Schema = "fsgg.orchestration.served-compatibility-diagnostic/1"
                                                        Scope = CompatibilityDiagnosticWire.scope
                                                        CorrelationId = response.CorrelationId
                                                        HostExecutableSha256 = hostHash.Value
                                                        RunnerExecutableSha256 = configuration.RunnerSha256
                                                        ProviderExecutableSha256 = configuration.ProviderSha256
                                                        Provider = response.Provider
                                                        AdapterVersion = response.AdapterVersion
                                                        VersionState = response.VersionState
                                                        ObservedVersion = response.ObservedVersion
                                                        AuthenticationState = response.AuthenticationState
                                                        AuthenticationProvenance = response.AuthenticationProvenance
                                                        SupportsResume = response.SupportsResume
                                                        ObservedAt = response.ObservedAt
                                                    }
                        with
                        | :? OperationCanceledException -> return Error "compatibility-diagnostic-timeout"
                        | :? EndOfStreamException -> return Error "compatibility-runner-unsupported"
                        | :? IOException when child.WaitForExit(TimeSpan.FromMilliseconds 100.) && child.ExitCode = 2 ->
                            return Error "compatibility-runner-unsupported"
                        | _ -> return Error "compatibility-diagnostic-unavailable"
                    }

                let! processCleanupSucceeded = terminate child

                if processCleanupSucceeded then
                    return outcome
                else
                    return Error "compatibility-process-cleanup-refused"
        }
