namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Protocol

/// Durable, private native evidence. Publication is a separate step and may replay these files.
type TelemetryTurnJournal(stateRoot: string, command: ExecutorCommandV2) =
    let directory =
        Path.Combine(
            stateRoot,
            "telemetry-turns",
            command.AssignmentId.ToString("N"),
            command.AttemptId.ToString("N"),
            string command.Generation
        )

    let digest (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let privateWrite (path: string) (bytes: byte array) =
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

        use stream = new FileStream(path, options)

        stream.Write bytes
        stream.Flush true

    let save kind identity payload =
        Directory.CreateDirectory directory |> ignore

        if OperatingSystem.IsLinux() then
            File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        let bytes = JsonSerializer.SerializeToUtf8Bytes payload
        let path = Path.Combine(directory, kind + "-" + digest identity + ".json")

        try
            privateWrite path bytes
        with :? IOException ->
            if not (File.Exists path) || File.ReadAllBytes path <> bytes then
                raise (InvalidOperationException "telemetry-turn-journal-identity-conflict")

    member _.RecordGap code =
        let identity = Guid.NewGuid().ToString("N")
        save "gap" identity {| Identity = identity; Code = code; ObservedAt = DateTimeOffset.UtcNow |}
        identity

    member _.RecordGapOnce code =
        let identity = "recovery-" + code
        save "gap" identity {| Identity = identity; Code = code; ObservedAt = command.RecordedAt |}
        identity

    interface ICodexTurnObserver with
        member _.TurnCompleted turn =
            let nativeKey = turn.TurnId |> Option.defaultValue (string turn.TurnSequence)
            let identity = turn.ThreadId + "\u001f" + nativeKey
            save "turn" identity turn

        member this.Gap code =
            this.RecordGap code |> ignore

        member _.ProcessStarted(processId, at) =
            save "process-start" (string processId) {| ProcessId = processId; ObservedAt = at |}

        member _.ProcessTerminal(exitCode, threadId, at) =
            save "process-terminal" "terminal" {| ExitCode = exitCode; ThreadId = threadId; ObservedAt = at |}
