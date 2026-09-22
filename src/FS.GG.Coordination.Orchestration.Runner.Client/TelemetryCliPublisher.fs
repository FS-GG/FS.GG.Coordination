namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

type TelemetryCliPublisherOptions =
    {
        Executable: string
        Config: string
        CredentialFile: string
        CertificateAuthorityFile: string
        Outbox: string
        Repository: string
        BindingDigest: string
    }

type TelemetryRunnerOptions =
    {
        Executable: string
        Config: string
        CredentialFile: string
        CertificateAuthorityFile: string
        Outbox: string
        BindingDigest: string
        Repository: string
    }

[<RequireQualifiedAccess>]
module TelemetryRunnerOptions =
    let toPublisher (options: TelemetryRunnerOptions) : TelemetryCliPublisherOptions =
        {
            Executable = options.Executable
            Config = options.Config
            CredentialFile = options.CredentialFile
            CertificateAuthorityFile = options.CertificateAuthorityFile
            Outbox = options.Outbox
            Repository = options.Repository
            BindingDigest = options.BindingDigest
        }

type TelemetryPublishOutcome =
    | Applied
    | AwaitingApplication
    | PublicationUnknown of string

/// Replays immutable batches through the released workspace client. A non-applied batch remains in the outbox.
type TelemetryCliPublisher(options: TelemetryCliPublisherOptions) =
    let flushLock = new SemaphoreSlim(1, 1)
    let admissionLock = obj ()
    let mutable flushCursor = 0
    let appliedDirectory = Path.Combine(options.Outbox, "applied")

    let digest (payload: byte array) =
        SHA256.HashData payload |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let markApplied (path: string) =
        try
            Directory.CreateDirectory appliedDirectory |> ignore

            if OperatingSystem.IsLinux() then
                File.SetUnixFileMode(
                    appliedDirectory,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

            let name = Path.GetFileNameWithoutExtension path
            let marker = Path.Combine(appliedDirectory, name + ".sha256")
            let expected = digest (File.ReadAllBytes path)
            let temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp"
            let options =
                FileStreamOptions(
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough
                )

            if OperatingSystem.IsLinux() then
                options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite

            try
                use stream = new FileStream(temporary, options)
                let bytes = Encoding.ASCII.GetBytes expected
                stream.Write bytes
                stream.Flush true
                stream.Close()
                File.Move(temporary, marker, false)
                true
            with :? IOException ->
                try File.Delete temporary with _ -> ()
                File.Exists marker
                && isNull (FileInfo(marker).LinkTarget)
                && FileInfo(marker).Length = 64L
                && File.ReadAllText marker = expected
        with _ ->
            false
    let validPrivateFile minimum maximum (path: string) =
        if not (Path.IsPathFullyQualified path) then
            false
        else
            let info = FileInfo path
            info.Exists
            && isNull info.LinkTarget
            && info.Length >= minimum
            && info.Length <= maximum
            && (not (OperatingSystem.IsLinux())
                || File.GetUnixFileMode(path) = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

    let saveUnlocked (name: string) (payload: byte array) =
        if
            String.IsNullOrWhiteSpace name
            || name.Length > 100
            || not (name |> Seq.forall (fun character -> Char.IsAsciiLetterOrDigit character || character = '-'))
        then
            Error "telemetry-batch-name-refused"
        elif payload.Length = 0 || payload.Length > 65536 then
            Error "telemetry-batch-size-refused"
        else
            Directory.CreateDirectory options.Outbox |> ignore

            if OperatingSystem.IsLinux() then
                File.SetUnixFileMode(options.Outbox, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

            let path = Path.Combine(options.Outbox, name + ".json")
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
            let marker = Path.Combine(appliedDirectory, name + ".sha256")
            let pending = Directory.GetFiles(options.Outbox, "*.json")
            let total = pending |> Array.sumBy (fun path -> FileInfo(path).Length)

            if File.Exists marker then
                if
                    isNull (FileInfo(marker).LinkTarget)
                    && FileInfo(marker).Length = 64L
                    && File.ReadAllText marker = digest payload
                then
                    Ok None
                else
                    Error "telemetry-applied-marker-conflict"
            elif not (File.Exists path) && (pending.Length >= 128 || total + int64 payload.Length > 8L * 1024L * 1024L) then
                Error "telemetry-outbox-overload"
            else
                let streamOptions =
                    FileStreamOptions(
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = 4096,
                        Options = FileOptions.WriteThrough
                    )

                if OperatingSystem.IsLinux() then
                    streamOptions.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite

                try
                    use stream = new FileStream(temporary, streamOptions)
                    stream.Write payload
                    stream.Flush true
                    stream.Close()
                    File.Move(temporary, path, false)
                    Ok(Some path)
                with :? IOException ->
                    try File.Delete temporary with _ -> ()

                    if File.Exists path && isNull (FileInfo(path).LinkTarget) && File.ReadAllBytes path = payload then
                        Ok(Some path)
                    else
                        Error "telemetry-batch-identity-conflict"

    // The count and byte bound must be checked in the same critical section as admission.
    let save name payload = lock admissionLock (fun () -> saveUnlocked name payload)

    let runCli path (cancellation: CancellationToken) =
        task {
            if
                not (Path.IsPathFullyQualified options.Executable)
                || not (File.Exists options.Executable)
                || not (validPrivateFile 1L 65536L options.Config)
                || not (validPrivateFile 0L 4096L (options.Config + ".lock"))
                || not (validPrivateFile 1L 4096L options.CredentialFile)
                || not (File.Exists options.CertificateAuthorityFile)
            then
                return PublicationUnknown "telemetry-client-unavailable"
            else
                let secret = File.ReadAllText(options.CredentialFile).Trim()

                if String.IsNullOrWhiteSpace secret then
                    return PublicationUnknown "telemetry-credential-empty"
                else
                    let start =
                        ProcessStartInfo(
                            options.Executable,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        )

                    start.Environment.Clear()
                    start.Environment["FSGG_TELEMETRY_CREDENTIAL_ORCHESTRATION"] <- secret
                    start.Environment["SSL_CERT_FILE"] <- options.CertificateAuthorityFile
                    start.ArgumentList.Add "telemetry"
                    start.ArgumentList.Add "workspace"
                    start.ArgumentList.Add "submit"

                    for argument in
                        [ "--input"; path
                          "--config"; options.Config
                          "--repository"; options.Repository
                          "--producer"; "orchestration-runner-main"
                          "--binding-digest"; options.BindingDigest ] do
                        start.ArgumentList.Add argument

                    try
                        use child = Process.Start start
                        use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellation
                        timeout.CancelAfter(TimeSpan.FromSeconds 40.)

                        try
                            let! output = child.StandardOutput.ReadToEndAsync(timeout.Token)
                            let! _ = child.StandardError.ReadToEndAsync(timeout.Token)
                            do! child.WaitForExitAsync(timeout.Token)

                            if child.ExitCode <> 0 then
                                return PublicationUnknown "telemetry-client-rejected"
                            else
                                match output.Trim() with
                                | "applied" -> return Applied
                                | "durably-received" -> return AwaitingApplication
                                | _ -> return PublicationUnknown "telemetry-receipt-unrecognized"
                        with :? OperationCanceledException ->
                            try child.Kill true with _ -> ()
                            return PublicationUnknown "telemetry-client-timeout"
                    with _ ->
                        return PublicationUnknown "telemetry-client-launch-failed"
        }

    member _.Publish(name: string, payload: byte array, cancellation: CancellationToken) =
        task {
            do! flushLock.WaitAsync cancellation

            try
                match save name payload with
                | Error reason -> return PublicationUnknown reason
                | Ok None -> return Applied
                | Ok(Some path) ->
                    let! outcome = runCli path cancellation

                    let settled =
                        if outcome = Applied then
                            if markApplied path then
                                try File.Delete path with _ -> ()
                                Applied
                            else
                                PublicationUnknown "telemetry-applied-marker-unavailable"
                        else
                            outcome

                    return settled
            finally
                flushLock.Release() |> ignore
        }

    member _.Queue(name: string, payload: byte array) = save name payload

    member _.PendingCount =
        if Directory.Exists options.Outbox then
            Directory.GetFiles(options.Outbox, "*.json").Length
        else
            0

    member _.Flush(cancellation: CancellationToken) =
        task {
            do! flushLock.WaitAsync cancellation

            try
                if not (Directory.Exists options.Outbox) then
                    return []
                else
                    let pending = Directory.GetFiles(options.Outbox, "*.json") |> Array.sort

                    if pending.Length = 0 then
                        return []
                    else
                        let start = flushCursor % pending.Length

                        let selected =
                            [| for offset in 0 .. min 15 (pending.Length - 1) -> pending[(start + offset) % pending.Length] |]

                        flushCursor <- (start + selected.Length) % pending.Length
                        let outcomes = ResizeArray<TelemetryPublishOutcome>()

                        for path in selected do
                            if isNull (FileInfo(path).LinkTarget) then
                                let! outcome = runCli path cancellation
                                outcomes.Add outcome

                                if outcome = Applied then
                                    if markApplied path then
                                        try File.Delete path with _ -> ()
                                    else
                                        outcomes.Add(PublicationUnknown "telemetry-applied-marker-unavailable")
                            else
                                outcomes.Add(PublicationUnknown "telemetry-outbox-unsafe")

                        return outcomes |> Seq.toList
            finally
                flushLock.Release() |> ignore
        }
