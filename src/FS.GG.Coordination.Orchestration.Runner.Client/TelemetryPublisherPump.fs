namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Retries pending batches for the lifetime of the runner, including batches left by a prior process.
/// The owner-only status file makes an unresolved receipt visible without affecting executor delivery.
type TelemetryPublisherPump(publisher: TelemetryCliPublisher, stateRoot: string, interval: TimeSpan) =
    let stopping = new CancellationTokenSource()
    let statusPath = Path.Combine(stateRoot, "telemetry-publisher-status.json")

    let writeStatus pending outcomes =
        let result =
            outcomes
            |> List.map (function
                | Applied -> "applied"
                | AwaitingApplication -> "durably-received"
                | PublicationUnknown code -> code)

        let bytes =
            JsonSerializer.SerializeToUtf8Bytes
                {| Schema = "fsgg.coordination.telemetry-publisher-status/1"
                   PendingBatches = pending
                   LastOutcomes = result
                   ObservedAt = DateTimeOffset.UtcNow |}

        Directory.CreateDirectory stateRoot |> ignore
        let temporary = statusPath + "." + Guid.NewGuid().ToString("N") + ".tmp"
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
            stream.Write bytes
            stream.Flush true
            stream.Close()
            File.Move(temporary, statusPath, true)
        with _ ->
            try File.Delete temporary with _ -> ()

    let runner =
        task {
            try
                while not stopping.IsCancellationRequested do
                    let! outcomes =
                        task {
                            try
                                return! publisher.Flush stopping.Token
                            with
                            | :? OperationCanceledException -> return raise (OperationCanceledException())
                            | _ -> return [ PublicationUnknown "publisher-pump-failed" ]
                        }
                    writeStatus publisher.PendingCount outcomes
                    do! Task.Delay(interval, stopping.Token)
            with :? OperationCanceledException ->
                ()
        }

    member _.Completion = runner :> Task

    interface IDisposable with
        member _.Dispose() =
            stopping.Cancel()

            try runner.GetAwaiter().GetResult() with _ -> ()
            stopping.Dispose()
