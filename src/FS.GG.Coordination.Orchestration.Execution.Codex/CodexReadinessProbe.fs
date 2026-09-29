namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution

type CodexReadinessProbeOptions =
    {
        Executable: string
        ExpectedVersion: string
        StartupTimeout: TimeSpan
        EnvironmentAllowList: Set<string>
    }

type CodexReadinessObservation =
    {
        Identity: ProviderIdentity
        VersionState: string
        ObservedVersion: string option
        Authentication: AuthenticationObservation
        SupportsResume: bool
        ObservedAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
module CodexReadinessProbe =
    let environmentAllowList =
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

    let private readBounded (stream: Stream) maximumBytes =
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

    let private probe (options: CodexReadinessProbeOptions) arguments (cancellationToken: CancellationToken) =
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
            scrubEnvironment options.EnvironmentAllowList start
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

    let observe (options: CodexReadinessProbeOptions) (clock: TimeProvider) cancellationToken =
        task {
            let identity =
                {
                    Provider = "Codex"
                    AdapterVersion = "codex-subscription-exec/1"
                }

            let mutable versionState = "unavailable"
            let mutable observedVersion = None

            let! authentication =
                task {
                    try
                        let! versionExit, versionOut, versionError =
                            probe options [ "--version" ] cancellationToken

                        let actual = (versionOut + versionError).Trim()

                        if not (String.IsNullOrWhiteSpace actual) then
                            observedVersion <- Some actual

                        if versionExit <> 0 || actual <> options.ExpectedVersion then
                            versionState <- "mismatch"
                            return AuthenticationUnknown "codex-version-mismatch"
                        else
                            versionState <- "matched"

                            let! loginExit, stdout, stderr =
                                probe options [ "login"; "status" ] cancellationToken

                            if
                                loginExit = 0
                                && (stdout + stderr)
                                    .Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase)
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

            return
                {
                    Identity = identity
                    VersionState = versionState
                    ObservedVersion = observedVersion
                    Authentication = authentication
                    SupportsResume = true
                    ObservedAt = clock.GetUtcNow()
                }
        }
