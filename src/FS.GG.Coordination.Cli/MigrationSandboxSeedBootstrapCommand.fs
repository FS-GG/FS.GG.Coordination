namespace FS.GG.Coordination.Cli

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module MigrationSandboxSeedBootstrapCommand =
    let private parse (arguments: string list) =
        let rec loop (values: Map<string, string>) =
            function
            | [] -> Some values
            | (name: string) :: value :: rest when name.StartsWith("--", StringComparison.Ordinal) ->
                loop (Map.add name value values) rest
            | _ -> None

        loop Map.empty arguments

    let private read path limit =
        let info = FileInfo path

        if
            not info.Exists
            || not (isNull info.LinkTarget)
            || info.Length <= 0L
            || info.Length > int64 limit
        then
            invalidOp "input-file"

        File.ReadAllBytes info.FullName

    let private prestate (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory bytes)
        let root = document.RootElement
        let names = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

        if
            names
            <> set
                [
                    "schema"
                    "complete"
                    "repositoryId"
                    "projectNodeId"
                    "nonceIssueCount"
                    "nonceProjectItemCount"
                    "snapshotSha256"
                ]
            || root.GetProperty("schema").GetString()
               <> "fsgg.gs2-09-7.sandbox-seed-prestate/1"
        then
            invalidOp "prestate-shape"

        {
            Complete = root.GetProperty("complete").GetBoolean()
            RepositoryId = root.GetProperty("repositoryId").GetInt64()
            ProjectNodeId = root.GetProperty("projectNodeId").GetString()
            NonceIssueCount = root.GetProperty("nonceIssueCount").GetInt32()
            NonceProjectItemCount = root.GetProperty("nonceProjectItemCount").GetInt32()
            SnapshotSha256 = root.GetProperty("snapshotSha256").GetString()
        }

    let private writeNew (root: string) (relative: string) (bytes: byte array) =
        let path = Path.Combine(root, relative)
        let parent = Path.GetDirectoryName path
        Directory.CreateDirectory parent |> ignore

        use stream =
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)

        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    let private writeDirectory output files manifestName manifestBytes =
        Directory.CreateDirectory output |> ignore

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(output, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        for relative, bytes in files do
            writeNew output relative bytes

        writeNew output manifestName manifestBytes
        printfn "%s" (Path.Combine(output, manifestName))

    let private prepare arguments =
        let values = parse arguments |> Option.defaultWith (fun () -> invalidOp "arguments")

        let required name =
            Map.tryFind name values |> Option.defaultWith (fun () -> invalidOp name)

        if values.Count <> 4 then
            invalidOp "argument-count"

        let output = Path.GetFullPath(required "--output-dir")

        if Directory.Exists output || File.Exists output then
            invalidOp "output-exists"

        let request =
            {
                CandidateSha = required "--candidate-sha"
                SeedPlanBytes = ReadOnlyMemory(read (required "--seed-plan") (64 * 1024 * 1024))
                CorpusBytes = ReadOnlyMemory(read (required "--corpus") (64 * 1024 * 1024))
            }

        match MigrationSandboxSeedBootstrapArtifact.prepareSource request with
        | Error error ->
            eprintfn "seed-bootstrap-artifacts prepare refused: %A" error
            3
        | Ok artifacts ->
            writeDirectory output artifacts.Files "source-manifest.json" artifacts.ManifestBytes
            0

    let private seal arguments =
        let values = parse arguments |> Option.defaultWith (fun () -> invalidOp "arguments")

        let required name =
            Map.tryFind name values |> Option.defaultWith (fun () -> invalidOp name)

        if values.Count <> 11 then
            invalidOp "argument-count"

        let output = Path.GetFullPath(required "--output-dir")

        if Directory.Exists output || File.Exists output then
            invalidOp "output-exists"

        let request =
            {
                CandidateSha = required "--candidate-sha"
                WorkflowRunId = Int64.Parse(required "--workflow-run-id", Globalization.CultureInfo.InvariantCulture)
                WorkflowRunAttempt =
                    Int32.Parse(required "--workflow-run-attempt", Globalization.CultureInfo.InvariantCulture)
                WorkflowSha = required "--workflow-sha"
                SourceManifestBytes = ReadOnlyMemory(read (required "--source-manifest") (1024 * 1024))
                S2DeclarationBytes = ReadOnlyMemory(read (required "--s2-declaration") (1024 * 1024))
                MintProofBytes = ReadOnlyMemory(read (required "--mint-proof") (1024 * 1024))
                SeedPlanBytes = ReadOnlyMemory(read (required "--seed-plan") (64 * 1024 * 1024))
                CorpusBytes = ReadOnlyMemory(read (required "--corpus") (64 * 1024 * 1024))
                Prestate = prestate (read (required "--prestate") (1024 * 1024))
            }

        match MigrationSandboxSeedBootstrapArtifact.prepare request with
        | Error error ->
            eprintfn "seed-bootstrap-artifacts seal refused: %A" error
            3
        | Ok artifacts ->
            writeDirectory output artifacts.Files "bootstrap-manifest.json" artifacts.ManifestBytes
            0

    let run arguments =
        try
            match Array.toList arguments with
            | "prepare" :: rest -> prepare rest
            | "seal" :: rest -> seal rest
            | _ -> 2
        with error ->
            eprintfn "seed-bootstrap-artifacts refused: %s" error.Message
            3
