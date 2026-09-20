namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Orchestration.Execution

/// A root-owned, read-only mount supplies the exact old runner's bounded terminal
/// files. The manifest is an operator attestation of process termination; hashes
/// bind it to the copied bytes rather than to the writable runner state mount.
[<RequireQualifiedAccess>]
module MainTerminalEvidence =
    let defaultDirectory = "/srv/recovery/old-attempt"

    let private digest (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private readBounded (path: string) (maximum: int64) =
        try
            let attributes = File.GetAttributes path
            let info = FileInfo path

            if
                attributes.HasFlag FileAttributes.ReparsePoint
                || attributes.HasFlag FileAttributes.Directory
                || info.Length < 1L
                || info.Length > maximum
            then
                Error "terminal-evidence-file-shape-refused"
            else
                Ok(File.ReadAllBytes path)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> Error "terminal-evidence-file-unavailable"

    let private text (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Option.ofObj (value.GetString())
        | _ -> None

    let private number (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt64() with
            | true, number -> Some number
            | _ -> None
        | _ -> None

    let verify directory (intent: LaunchIntent) (now: DateTimeOffset) =
        try
            let root = Path.GetFullPath directory

            if File.GetAttributes(root).HasFlag FileAttributes.ReparsePoint then
                Error "terminal-evidence-directory-link-refused"
            else
                match
                    readBounded (Path.Combine(root, "manifest.json")) 4096L,
                    readBounded (Path.Combine(root, "final.json")) 16384L,
                    readBounded (Path.Combine(root, "stdout.jsonl")) (4L * 1024L * 1024L)
                with
                | Ok manifestBytes, Ok finalBytes, Ok stdoutBytes ->
                    use manifest = JsonDocument.Parse(ReadOnlyMemory manifestBytes, JsonDocumentOptions(MaxDepth = 8))
                    use final = JsonDocument.Parse(ReadOnlyMemory finalBytes, JsonDocumentOptions(MaxDepth = 8))
                    let item = manifest.RootElement

                    let expected =
                        set [
                            "schema"; "assignmentId"; "attemptId"; "generation"
                            "finalSha256"; "stdoutSha256"; "finalBytes"; "stdoutBytes"
                            "capturedAt"; "processTerminationObserved"
                        ]

                    let names = item.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    let terminal =
                        match item.TryGetProperty "processTerminationObserved" with
                        | true, value when value.ValueKind = JsonValueKind.True -> true
                        | _ -> false

                    let captured =
                        text item "capturedAt"
                        |> Option.bind (fun value ->
                            match DateTimeOffset.TryParse value with
                            | true, parsed -> Some parsed
                            | _ -> None)

                    let bound =
                        names.Length = expected.Count
                        && Set.ofList names = expected
                        && text item "schema" = Some "fsgg.orchestration.terminal-evidence/1"
                        && text item "assignmentId" = Some(string intent.Key.AssignmentId)
                        && text item "attemptId" = Some(string intent.Key.AttemptId)
                        && number item "generation" = Some intent.Key.Generation
                        && text item "finalSha256" = Some(digest finalBytes)
                        && text item "stdoutSha256" = Some(digest stdoutBytes)
                        && number item "finalBytes" = Some(int64 finalBytes.Length)
                        && number item "stdoutBytes" = Some(int64 stdoutBytes.Length)
                        && (captured
                            |> Option.exists (fun value ->
                                value.Offset = TimeSpan.Zero
                                && value >= intent.RecordedAt
                                && value <= now))
                        && terminal

                    if not bound then
                        Error "terminal-evidence-manifest-refused"
                    elif text final.RootElement "status" <> Some "completed" then
                        Error "terminal-evidence-final-not-completed"
                    else
                        let lines = Encoding.UTF8.GetString stdoutBytes |> fun value -> value.Split('\n')
                        let mutable completed = 0
                        let mutable started = 0
                        let mutable malformed = false

                        for line in lines do
                            if not (String.IsNullOrWhiteSpace line) then
                                try
                                    use event = JsonDocument.Parse(line, JsonDocumentOptions(MaxDepth = 8))

                                    match text event.RootElement "type" with
                                    | Some "turn.completed" -> completed <- completed + 1
                                    | Some "turn.started" -> started <- started + 1
                                    | Some "turn.failed" -> malformed <- true
                                    | Some _ -> ()
                                    | None -> malformed <- true
                                with :? JsonException ->
                                    malformed <- true

                        if malformed || started <> 1 || completed <> 1 then
                            Error "terminal-evidence-turn-ambiguous"
                        else
                            Ok(digest manifestBytes)
                | Error reason, _, _
                | _, Error reason, _
                | _, _, Error reason -> Error reason
        with
        | :? JsonException -> Error "terminal-evidence-json-refused"
        | :? InvalidOperationException -> Error "terminal-evidence-json-refused"
        | :? IOException
        | :? UnauthorizedAccessException -> Error "terminal-evidence-unavailable"
