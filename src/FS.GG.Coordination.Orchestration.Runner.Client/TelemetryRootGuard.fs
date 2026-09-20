namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Orchestration.Runner.Protocol

[<CLIMutable>]
type TelemetryRootMarker =
    {
        AttemptId: string
        Generation: int64
        ActivatedAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
module TelemetryRootGuard =
    /// An item gets one prospective root. A later attempt requires controller-provided parent lineage.
    let claim stateRoot (command: ExecutorCommandV2) now =
        let digest =
            SHA256.HashData(Encoding.UTF8.GetBytes command.WorkItemPersistenceId)
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()

        let directory = Path.Combine(stateRoot, "telemetry-roots")
        Directory.CreateDirectory directory |> ignore

        if OperatingSystem.IsLinux() then
            File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        let path = Path.Combine(directory, digest + ".json")
        let marker =
            {
                AttemptId = command.AttemptId.ToString("N")
                Generation = command.Generation
                ActivatedAt = now
            }

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
            use stream = new FileStream(path, streamOptions)
            JsonSerializer.Serialize(stream, marker)
            stream.Flush true
            Ok now
        with :? IOException ->
            try
                let info = FileInfo path

                if not info.Exists || not (isNull info.LinkTarget) || info.Length < 2L || info.Length > 512L then
                    Error "telemetry-root-marker-unsafe"
                else
                    let existing = JsonSerializer.Deserialize<TelemetryRootMarker>(File.ReadAllBytes path)

                    if
                        isNull (box existing)
                        || existing.AttemptId <> marker.AttemptId
                        || existing.Generation <> marker.Generation
                    then
                        Error "telemetry-retry-lineage-unavailable"
                    else
                        Ok existing.ActivatedAt
            with _ ->
                Error "telemetry-root-marker-unreadable"
