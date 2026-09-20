namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Protocol

/// Rebuild compact outbox batches from native evidence after a runner restart.
[<RequireQualifiedAccess>]
module TelemetryJournalRecovery =
    let private optionalString (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let private requiredString (root: JsonElement) (name: string) =
        root.GetProperty(name).GetString()

    let private turn (root: JsonElement) : CodexTurnUsage =
        let input = root.GetProperty("Input").GetInt64()
        let cached = root.GetProperty("CachedInput").GetInt64()
        let output = root.GetProperty("Output").GetInt64()
        let total = root.GetProperty("Total").GetInt64()
        let reasoning =
            match root.TryGetProperty "Reasoning" with
            | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetInt64())
            | _ -> None

        if
            input < 0L
            || cached < 0L
            || output < 0L
            || cached > input
            || reasoning |> Option.exists (fun value -> value < 0L || value > output)
            || total <> Checked.(+) input output
        then
            invalidOp "telemetry-journal-counters-invalid"

        {
            ThreadId = requiredString root "ThreadId"
            TurnId = optionalString root "TurnId"
            TurnSequence = root.GetProperty("TurnSequence").GetInt64()
            Provider = optionalString root "Provider"
            ObservedModel = optionalString root "ObservedModel"
            ObservedEffort = optionalString root "ObservedEffort"
            Backend = optionalString root "Backend"
            Input = input
            CachedInput = cached
            Output = output
            Reasoning = reasoning
            Total = total
        }

    let private evidenceDirectory stateRoot (command: ExecutorCommandV2) =
        Path.Combine(
            stateRoot,
            "telemetry-turns",
            command.AssignmentId.ToString("N"),
            command.AttemptId.ToString("N"),
            string command.Generation
        )

    let requeue stateRoot (command: ExecutorCommandV2) (publisher: TelemetryCliPublisher) =
        let directory = evidenceDirectory stateRoot command

        if not (Directory.Exists directory) then
            []
        else
            let context = TelemetryFactBatches.rootInvocation command
            let files = Directory.GetFiles(directory, "*.json") |> Array.sort
            let errors = ResizeArray<string>()

            if files.Length > 4096 then
                errors.Add "telemetry-journal-capacity-exceeded"

            for path in files |> Array.truncate 4096 do
                try
                    let info = FileInfo path

                    if
                        not info.Exists
                        || not (isNull info.LinkTarget)
                        || info.Length < 2L
                        || info.Length > 4096L
                        || (OperatingSystem.IsLinux()
                            && File.GetUnixFileMode(path) <> (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
                    then
                        invalidOp "telemetry-journal-file-unsafe"

                    use document = JsonDocument.Parse(File.ReadAllBytes path, JsonDocumentOptions(MaxDepth = 8))
                    let evidence = document.RootElement
                    let filename = Path.GetFileName path

                    let batch =
                        if
                            filename.StartsWith("turn-", StringComparison.Ordinal)
                            && not (filename.StartsWith("turn-start-", StringComparison.Ordinal))
                        then
                            TelemetryFactBatches.completedTurn
                                context
                                (Option.ofObj command.RequestedModel)
                                (Option.ofObj command.RequestedEffort)
                                (turn evidence)
                        elif filename.StartsWith("gap-", StringComparison.Ordinal) then
                            TelemetryFactBatches.gap
                                context
                                (requiredString evidence "Identity")
                                (requiredString evidence "Code")
                        elif filename.StartsWith("process-start-", StringComparison.Ordinal) then
                            TelemetryFactBatches.processStart
                                context
                                (evidence.GetProperty("ProcessId").GetInt32())
                                (evidence.GetProperty("ObservedAt").GetDateTimeOffset())
                        elif filename.StartsWith("thread-start-", StringComparison.Ordinal) then
                            TelemetryFactBatches.threadStart
                                context
                                (evidence.GetProperty("ProcessId").GetInt32())
                                (requiredString evidence "ThreadId")
                        elif filename.StartsWith("turn-start-", StringComparison.Ordinal) then
                            TelemetryFactBatches.turnStart
                                context
                                (evidence.GetProperty("ProcessId").GetInt32())
                                (requiredString evidence "ThreadId")
                                (optionalString evidence "TurnId")
                                (evidence.GetProperty("TurnSequence").GetInt64())
                        elif filename.StartsWith("process-terminal-", StringComparison.Ordinal) then
                            TelemetryFactBatches.processTerminal
                                context
                                (evidence.GetProperty("ExitCode").GetInt32())
                                (optionalString evidence "ThreadId")
                                (evidence.GetProperty("ObservedAt").GetDateTimeOffset())
                        else
                            invalidOp "telemetry-journal-kind-unsupported"

                    match publisher.Queue batch with
                    | Ok _ -> ()
                    | Error code -> errors.Add code
                with _ ->
                    errors.Add "telemetry-journal-replay-failed"

            errors |> Seq.toList
