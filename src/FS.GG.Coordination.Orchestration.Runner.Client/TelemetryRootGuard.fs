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
    let private markerPath stateRoot (command: ExecutorCommandV2) =
        let digest =
            SHA256.HashData(Encoding.UTF8.GetBytes command.WorkItemPersistenceId)
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()

        let directory = Path.Combine(stateRoot, "telemetry-roots")
        directory, Path.Combine(directory, digest + ".json")

    /// Read only an already-claimed root for this exact controller command.
    let replay stateRoot (command: ExecutorCommandV2) =
        let _, path = markerPath stateRoot command

        try
            let info = FileInfo path

            if not info.Exists then
                Ok None
            elif
                not (isNull info.LinkTarget)
                || info.Length < 2L
                || info.Length > 512L
                || (OperatingSystem.IsLinux()
                    && File.GetUnixFileMode(path) <> (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
            then
                Error "telemetry-root-marker-unsafe"
            else
                let existing = JsonSerializer.Deserialize<TelemetryRootMarker>(File.ReadAllBytes path)

                if isNull (box existing) then
                    Error "telemetry-root-marker-unreadable"
                elif existing.AttemptId = command.AttemptId.ToString("N") && existing.Generation = command.Generation then
                    Ok(Some existing)
                elif
                    command.Schema = ExecutorWire.commandSchemaV3
                    && command.ParentAttemptId.HasValue
                    && command.ParentGeneration.HasValue
                    && command.Generation > existing.Generation
                    && command.ParentGeneration.Value >= existing.Generation
                then
                    Ok(Some existing)
                else
                    Error "telemetry-retry-lineage-unavailable"
        with _ ->
            Error "telemetry-root-marker-unreadable"

    let private claimRoot path (marker: TelemetryRootMarker) =
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
            Ok marker
        with :? IOException ->
            try
                let info = FileInfo path
                if
                    not info.Exists
                    || not (isNull info.LinkTarget)
                    || info.Length < 2L
                    || info.Length > 512L
                    || (OperatingSystem.IsLinux()
                        && File.GetUnixFileMode(path) <> (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
                then
                    Error "telemetry-root-marker-unsafe"
                else
                    let existing = JsonSerializer.Deserialize<TelemetryRootMarker>(File.ReadAllBytes path)
                    if isNull (box existing) then Error "telemetry-root-marker-unreadable"
                    elif existing.AttemptId = marker.AttemptId && existing.Generation = marker.Generation then Ok existing
                    else Error "telemetry-retry-lineage-unavailable"
            with _ -> Error "telemetry-root-marker-unreadable"

    /// An item gets one prospective root. A later attempt requires controller-provided parent lineage.
    let claim stateRoot (command: ExecutorCommandV2) now =
        let directory, path = markerPath stateRoot command
        Directory.CreateDirectory directory |> ignore

        if OperatingSystem.IsLinux() then
            File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        let marker =
            {
                AttemptId = command.AttemptId.ToString("N")
                Generation = command.Generation
                ActivatedAt = now
            }

        if command.Schema = ExecutorWire.commandSchemaV3 then
            match replay stateRoot command with
            | Ok(Some existing) -> Ok existing
            | Ok None -> Error "telemetry-parent-root-unavailable"
            | Error code -> Error code
        else
            claimRoot path marker
