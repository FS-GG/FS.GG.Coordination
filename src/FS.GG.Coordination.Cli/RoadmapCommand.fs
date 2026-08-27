namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
module RoadmapCommand =
    let private usage =
        "roadmap-work <inspect|prerequisites|manifest|gates> --index FILE --roadmap FILE --unit GS2-NN.N [operation options]"

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private parseOptions (arguments: string array) =
        let values = Dictionary<string, ResizeArray<string>>(StringComparer.Ordinal)
        let mutable index = 0
        let mutable error = None

        while index < arguments.Length && error.IsNone do
            let key = arguments[index]

            if
                not (key.StartsWith("--", StringComparison.Ordinal))
                || index + 1 >= arguments.Length
            then
                error <- Some $"expected --name value, observed '{key}'"
            else
                let value = arguments[index + 1]

                if value.StartsWith("--", StringComparison.Ordinal) then
                    error <- Some $"missing value for {key}"
                else
                    let bucket =
                        match values.TryGetValue key with
                        | true, existing -> existing
                        | _ ->
                            let created = ResizeArray()
                            values.Add(key, created)
                            created

                    bucket.Add value
                    index <- index + 2

        match error with
        | Some value -> Error value
        | None -> Ok values

    let private one required name (options: Dictionary<string, ResizeArray<string>>) =
        match options.TryGetValue name with
        | true, values when values.Count = 1 -> Ok values[0]
        | true, _ -> Error $"{name} must occur exactly once"
        | false, _ when required -> Error $"{name} is required"
        | false, _ -> Ok ""

    let private many name (options: Dictionary<string, ResizeArray<string>>) =
        match options.TryGetValue name with
        | true, values -> values |> Seq.toList
        | _ -> []

    let private allowedOptions operation =
        let common = Set.ofList [ "--index"; "--roadmap"; "--unit"; "--receipts" ]

        match operation with
        | "inspect" -> Set.ofList [ "--index"; "--roadmap"; "--unit" ]
        | "prerequisites" -> common
        | "manifest" -> Set.union common (Set.ofList [ "--repo"; "--created-at"; "--artifact"; "--output" ])
        | "gates" -> Set.union common (Set.ofList [ "--repo"; "--catalog"; "--manifest"; "--output" ])
        | _ -> Set.empty

    let private read path =
        File.ReadAllBytes path |> ReadOnlyMemory<byte>

    let private receipts path =
        if not (Directory.Exists path) then
            Error $"receipts directory does not exist: {path}"
        else
            Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly)
            |> Seq.sort
            |> Seq.map read
            |> Seq.toList
            |> Ok

    let private reportFindings (findings: RoadmapWorkFinding list) =
        for item in findings do
            eprintfn "%s path=%s message=%s" item.Code item.Path item.Message

        3

    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let private print value =
        JsonSerializer.Serialize(value, jsonOptions) |> printfn "%s"

    let private runProcess executable arguments workingDirectory =
        let startInfo = ProcessStartInfo(executable)

        for argument in arguments do
            startInfo.ArgumentList.Add argument

        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        use child = Process.Start startInfo
        let outputTask = child.StandardOutput.ReadToEndAsync()
        let errorTask = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        child.ExitCode, outputTask.Result, errorTask.Result

    let private git repo arguments =
        let exitCode, output, error = runProcess "git" arguments repo

        if exitCode = 0 then
            Ok(output.Trim())
        else
            Error(error.Trim())

    let private candidate repo =
        match git repo [ "status"; "--porcelain=v1"; "--untracked-files=all" ] with
        | Ok status when not (String.IsNullOrWhiteSpace status) -> Error "candidate worktree is not clean"
        | Error error -> Error $"cannot inspect candidate worktree: {error}"
        | Ok _ ->
            match git repo [ "rev-parse"; "HEAD" ], git repo [ "rev-parse"; "HEAD^{tree}" ] with
            | Ok commit, Ok tree -> Ok { Commit = commit; Tree = tree }
            | Error error, _
            | _, Error error -> Error $"cannot derive exact git candidate: {error}"

    let private containedRegularFile repo relative =
        let root = Path.GetFullPath repo
        let rootInfo = DirectoryInfo root
        let full = Path.GetFullPath(relative, root)

        let prefix =
            root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        if
            not (isNull rootInfo.LinkTarget)
            || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
        then
            Error "repository root symlink or reparse point is refused"
        elif not (full.StartsWith(prefix, StringComparison.Ordinal)) then
            Error "path escapes repository root"
        elif not (File.Exists full) then
            Error "file does not exist"
        else
            let rec containsDirectoryLink (current: DirectoryInfo) =
                if current.FullName = root then
                    false
                elif
                    not (isNull current.LinkTarget)
                    || current.Attributes.HasFlag(FileAttributes.ReparsePoint)
                then
                    true
                elif isNull current.Parent then
                    true
                else
                    containsDirectoryLink current.Parent

            let file = FileInfo full

            if
                not (isNull file.LinkTarget)
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || containsDirectoryLink file.Directory
            then
                Error "symlink or reparse-point path is refused"
            else
                Ok(full, File.ReadAllBytes full)

    let private trackedCandidateFile repo label path =
        match containedRegularFile repo path with
        | Error error -> Error $"{label}: {error}"
        | Ok(full, bytes) ->
            let relative = Path.GetRelativePath(repo, full).Replace('\\', '/')

            match git repo [ "ls-files"; "--error-unmatch"; "--"; relative ] with
            | Error _ -> Error $"{label} is not tracked by the candidate"
            | Ok _ -> Ok(full, relative, bytes)

    let private outputPath repo relative =
        let root = Path.GetFullPath repo
        let rootInfo = DirectoryInfo root
        let allowed = Path.GetFullPath(Path.Combine(root, "artifacts", "roadmap-work"))
        let full = Path.GetFullPath(relative, root)

        let prefix =
            allowed.TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        let rec firstExisting (directory: DirectoryInfo) =
            if directory.Exists || isNull directory.Parent then
                directory
            else
                firstExisting directory.Parent

        let rec containsLink (directory: DirectoryInfo) =
            if directory.FullName = root then
                not (isNull directory.LinkTarget)
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
            elif
                not (isNull directory.LinkTarget)
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
            then
                true
            elif isNull directory.Parent then
                true
            else
                containsLink directory.Parent

        if not (rootInfo.Exists) || not (full.StartsWith(prefix, StringComparison.Ordinal)) then
            Error "output must be contained beneath artifacts/roadmap-work"
        elif containsLink (firstExisting (DirectoryInfo(Path.GetDirectoryName full))) then
            Error "output parent symlink or reparse point is refused"
        else
            Ok full

    let private common options =
        match one true "--index" options, one true "--roadmap" options, one true "--unit" options with
        | Ok index, Ok roadmap, Ok unitId when File.Exists index && File.Exists roadmap ->
            Ok(read index, read roadmap, unitId)
        | Ok index, _, _ when not (File.Exists index) -> Error $"index does not exist: {index}"
        | _, Ok roadmap, _ when not (File.Exists roadmap) -> Error $"roadmap does not exist: {roadmap}"
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error -> Error error
        | _ -> Error "invalid common arguments"

    let private loadReceipts options =
        match one true "--receipts" options with
        | Error error -> Error error
        | Ok path -> receipts path

    let private validateManifestArtifacts repo (manifest: ReadOnlyMemory<byte>) =
        use document = JsonDocument.Parse manifest
        let mutable errors = []

        for artifact in document.RootElement.GetProperty("artifacts").EnumerateArray() do
            let name = artifact.GetProperty("name").GetString()
            let relative = artifact.GetProperty("path").GetString()
            let expected = artifact.GetProperty("sha256").GetString()

            match containedRegularFile repo relative with
            | Error error -> errors <- errors @ [ $"artifact {name}: {error}" ]
            | Ok(_, bytes) ->
                let actual = sha256 bytes

                if actual <> expected then
                    errors <- errors @ [ $"artifact {name}: expected {expected}, observed {actual}" ]

        if errors.IsEmpty then
            Ok()
        else
            Error(String.concat "; " errors)

    let private manifestBindings repo catalogPath (catalogBytes: byte array) (manifest: ReadOnlyMemory<byte>) =
        use document = JsonDocument.Parse manifest
        let root = document.RootElement

        let qGates =
            root.GetProperty("qGates").EnumerateArray()
            |> Seq.map _.GetString()
            |> Set.ofSeq

        let relativeCatalog = Path.GetRelativePath(repo, catalogPath).Replace('\\', '/')
        let catalogDigest = sha256 catalogBytes

        let bound =
            root.GetProperty("artifacts").EnumerateArray()
            |> Seq.exists (fun artifact ->
                artifact.GetProperty("path").GetString() = relativeCatalog
                && artifact.GetProperty("sha256").GetString() = catalogDigest)

        if bound then
            Ok(qGates, relativeCatalog, catalogDigest)
        else
            Error "the tracked gate catalog is not bound as a manifest artifact"

    let private runInspect index roadmap unitId =
        match RoadmapWork.inspect index roadmap unitId with
        | Ok result ->
            print result
            0
        | Error findings -> reportFindings findings

    let private runPrerequisites index roadmap unitId options =
        match loadReceipts options with
        | Error error ->
            eprintfn "%s" error
            2
        | Ok receiptBytes ->
            match RoadmapWork.checkPrerequisites index roadmap receiptBytes unitId with
            | Ok result ->
                print result
                0
            | Error findings -> reportFindings findings

    let private runManifest index roadmap unitId options =
        match
            loadReceipts options,
            one true "--repo" options,
            one true "--created-at" options,
            one true "--output" options,
            one true "--index" options
        with
        | Ok receiptBytes, Ok repo, Ok createdAt, Ok output, Ok indexPath ->
            match candidate repo, outputPath repo output, trackedCandidateFile repo "index" indexPath with
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error ->
                eprintfn "%s" error
                2
            | Ok candidateValue, Ok fullOutput, Ok(_, _, trackedIndex) ->
                if not ((index: ReadOnlyMemory<byte>).Span.SequenceEqual(trackedIndex.AsSpan())) then
                    eprintfn "index bytes differ from the tracked candidate file"
                    2
                else
                    let artifacts =
                        many "--artifact" options
                        |> List.map (fun value ->
                            match value.IndexOf('=') with
                            | indexValue when indexValue > 0 ->
                                let name, path = value.Substring(0, indexValue), value.Substring(indexValue + 1)

                                match
                                    containedRegularFile repo path,
                                    git repo [ "ls-files"; "--error-unmatch"; "--"; path ]
                                with
                                | Ok(_, bytes), Ok _ ->
                                    Ok
                                        { Name = name
                                          Path = path
                                          Bytes = ReadOnlyMemory<byte>(bytes) }
                                | Ok _, Error _ -> Error $"artifact {name}: file is not tracked by the candidate"
                                | Error error, _
                                | _, Error error -> Error $"artifact {name}: {error}"
                            | _ -> Error "--artifact must use name=repository-relative-path")

                    let errors =
                        artifacts
                        |> List.choose (function
                            | Error value -> Some value
                            | _ -> None)

                    if not errors.IsEmpty then
                        eprintfn "%s" (String.concat "; " errors)
                        2
                    else
                        match
                            RoadmapWork.createManifest
                                index
                                roadmap
                                receiptBytes
                                unitId
                                candidateValue
                                createdAt
                                (artifacts |> List.choose Result.toOption)
                        with
                        | Error findings -> reportFindings findings
                        | Ok bytes ->
                            Directory.CreateDirectory(Path.GetDirectoryName fullOutput) |> ignore
                            File.WriteAllBytes(fullOutput, bytes)

                            printfn
                                "{\"schema\":\"fsgg.coordination.roadmap-work-result/1\",\"operation\":\"manifest\",\"unitId\":\"%s\",\"output\":\"%s\",\"sha256\":\"%s\"}"
                                unitId
                                (Path.GetRelativePath(repo, fullOutput).Replace('\\', '/'))
                                (sha256 bytes)

                            0
        | Error error, _, _, _, _
        | _, Error error, _, _, _
        | _, _, Error error, _, _
        | _, _, _, Error error, _
        | _, _, _, _, Error error ->
            eprintfn "%s" error
            2

    let private runGates index roadmap unitId options =
        match
            loadReceipts options,
            one true "--repo" options,
            one true "--catalog" options,
            one true "--manifest" options,
            one true "--output" options,
            one true "--index" options
        with
        | Ok receiptBytes, Ok repo, Ok catalogPath, Ok manifestPath, Ok output, Ok indexPath ->
            match candidate repo, outputPath repo output, trackedCandidateFile repo "index" indexPath with
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error ->
                eprintfn "%s" error
                2
            | Ok candidateValue, Ok fullOutput, Ok(_, _, trackedIndex) when File.Exists manifestPath ->
                if not ((index: ReadOnlyMemory<byte>).Span.SequenceEqual(trackedIndex.AsSpan())) then
                    eprintfn "index bytes differ from the tracked candidate file"
                    2
                else
                    let manifestBytes = read manifestPath

                    match trackedCandidateFile repo "catalog" catalogPath with
                    | Error error ->
                        eprintfn "catalog: %s" error
                        3
                    | Ok(fullCatalog, _, catalogRaw) ->
                        match
                            RoadmapWork.validateManifest index roadmap receiptBytes unitId candidateValue manifestBytes
                        with
                        | Error findings -> reportFindings findings
                        | Ok expectedContracts ->
                            match
                                validateManifestArtifacts repo manifestBytes,
                                RoadmapWork.validateGateCatalog expectedContracts (ReadOnlyMemory<byte>(catalogRaw)),
                                manifestBindings repo fullCatalog catalogRaw manifestBytes
                            with
                            | Error error, _, _
                            | _, _, Error error ->
                                eprintfn "%s" error
                                3
                            | _, Error findings, _ -> reportFindings findings
                            | Ok _, Ok catalog, Ok(declaredQGates, relativeCatalog, catalogDigest) ->
                                let selectedQGates = catalog |> List.map _.QGate |> Set.ofList

                                if selectedQGates <> declaredQGates then
                                    eprintfn "catalog command Q gates do not exactly cover the manifest Q gates"
                                    3
                                else
                                    let mutable results = []
                                    let mutable failed = false

                                    for command in catalog do
                                        if not failed then
                                            let exitCode, outputText, errorText =
                                                runProcess command.Executable command.Arguments repo

                                            results <-
                                                results
                                                @ [ command,
                                                    exitCode,
                                                    sha256 (Encoding.UTF8.GetBytes outputText),
                                                    sha256 (Encoding.UTF8.GetBytes errorText) ]

                                            if exitCode <> 0 then
                                                failed <- true

                                    match validateManifestArtifacts repo manifestBytes, candidate repo with
                                    | Error error, _ ->
                                        eprintfn "candidate artifacts changed during gate execution: %s" error
                                        3
                                    | _, Error error ->
                                        eprintfn "candidate changed during gate execution: %s" error
                                        3
                                    | Ok _, Ok after when after <> candidateValue ->
                                        eprintfn "candidate commit or tree changed during gate execution"
                                        3
                                    | Ok _, Ok _ ->
                                        let root = JsonObject()
                                        root.Add("schema", "fsgg.coordination.gate-results/1")
                                        root.Add("unitId", unitId)
                                        root.Add("candidateCommit", candidateValue.Commit)
                                        root.Add("candidateTree", candidateValue.Tree)
                                        root.Add("manifestSha256", sha256 (manifestBytes.ToArray()))
                                        root.Add("catalogPath", relativeCatalog)
                                        root.Add("catalogSha256", catalogDigest)
                                        root.Add("stoppedAtUnitBoundary", true)

                                        let nodes =
                                            results
                                            |> List.map (fun (command, exitCode, stdoutSha, stderrSha) ->
                                                let node = JsonObject()
                                                node.Add("id", command.Id)
                                                node.Add("qGate", command.QGate)

                                                let identityBytes =
                                                    Encoding.UTF8.GetBytes(
                                                        String.concat
                                                            "\u0000"
                                                            (command.Executable :: command.Arguments)
                                                    )

                                                node.Add("commandSha256", sha256 identityBytes)
                                                node.Add("exitCode", exitCode)
                                                node.Add("stdoutSha256", stdoutSha)
                                                node.Add("stderrSha256", stderrSha)
                                                node :> JsonNode)
                                            |> List.toArray

                                        root.Add("results", JsonArray(nodes))
                                        Directory.CreateDirectory(Path.GetDirectoryName fullOutput) |> ignore

                                        File.WriteAllText(
                                            fullOutput,
                                            root.ToJsonString(jsonOptions),
                                            UTF8Encoding(false)
                                        )

                                        printfn "%s" (root.ToJsonString(jsonOptions))
                                        if failed then 3 else 0
            | _, _, _ ->
                eprintfn "catalog or manifest does not exist"
                2
        | Error error, _, _, _, _, _
        | _, Error error, _, _, _, _
        | _, _, Error error, _, _, _
        | _, _, _, Error error, _, _
        | _, _, _, _, Error error, _
        | _, _, _, _, _, Error error ->
            eprintfn "%s" error
            2

    let run arguments =
        match arguments |> Array.toList with
        | operation :: rest when Set.contains operation (Set.ofList [ "inspect"; "prerequisites"; "manifest"; "gates" ]) ->
            match parseOptions (List.toArray rest) with
            | Error error ->
                eprintfn "%s\n%s" error usage
                2
            | Ok options ->
                let unknown =
                    options.Keys
                    |> Seq.filter (fun key -> not (Set.contains key (allowedOptions operation)))
                    |> Seq.toList

                if not unknown.IsEmpty then
                    eprintfn "unknown option(s): %s" (String.concat ", " unknown)
                    2
                else
                    match common options with
                    | Error error ->
                        eprintfn "%s" error
                        2
                    | Ok(index, roadmap, unitId) ->
                        match operation with
                        | "inspect" -> runInspect index roadmap unitId
                        | "prerequisites" -> runPrerequisites index roadmap unitId options
                        | "manifest" -> runManifest index roadmap unitId options
                        | "gates" -> runGates index roadmap unitId options
                        | _ -> 2
        | _ ->
            eprintfn "%s" usage
            2
