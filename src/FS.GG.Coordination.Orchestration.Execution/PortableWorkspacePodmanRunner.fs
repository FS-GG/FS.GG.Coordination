namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type private PortableToolObservation =
    {
        ExitCode: int option
        StandardOutput: byte array
        StandardError: byte array
        TimedOut: bool
        OutputLimitExceeded: bool
        OutputComplete: bool
    }

type private PortableSnapshotFailure =
    {
        Reason: string
        Helper: PortableToolObservation option
    }

[<RequireQualifiedAccess>]
module private PortablePodmanRuntime =
    let sha256 (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let deadlineAfter (now: DateTimeOffset) (duration: TimeSpan) =
        let availableTicks = DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks

        if duration.Ticks >= availableTicks then
            DateTimeOffset.MaxValue
        else
            now.AddTicks duration.Ticks

    let private cleanStartInfo (runtime: PortableRuntimePolicy) executable arguments workingDirectory =
        let start =
            ProcessStartInfo(
                executable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        start.Environment.Clear()

        for KeyValue(name, value) in runtime.HostEnvironment do
            start.Environment.Add(name, value)

        for argument in arguments do
            start.ArgumentList.Add argument

        start

    let runTool
        (runtime: PortableRuntimePolicy)
        executable
        arguments
        workingDirectory
        (deadline: DateTimeOffset)
        (maximumOutputBytes: uint64)
        (cancellationToken: CancellationToken)
        =
        task {
            use child = new Process()
            child.StartInfo <- cleanStartInfo runtime executable arguments workingDirectory

            if cancellationToken.IsCancellationRequested || deadline <= DateTimeOffset.UtcNow then
                return
                    {
                        ExitCode = None
                        StandardOutput = Array.empty
                        StandardError = Array.empty
                        TimedOut = true
                        OutputLimitExceeded = false
                        OutputComplete = false
                    }
            elif not (child.Start()) then
                return
                    {
                        ExitCode = None
                        StandardOutput = Array.empty
                        StandardError = Array.empty
                        TimedOut = false
                        OutputLimitExceeded = false
                        OutputComplete = false
                    }
            else
                let outputGate = obj ()
                let mutable capturedBytes = 0UL
                let mutable outputLimitExceeded = false
                use readCancellation = new CancellationTokenSource()

                let readBounded (source: Stream) =
                    task {
                        use captured = new MemoryStream()
                        let buffer = Array.zeroCreate<byte> 4096
                        let mutable reading = true

                        try
                            while reading do
                                let! count =
                                    source.ReadAsync(buffer.AsMemory(0, buffer.Length), readCancellation.Token)

                                if count = 0 then
                                    reading <- false
                                else
                                    let allowed, exceeded =
                                        lock outputGate (fun () ->
                                            let remaining =
                                                if capturedBytes >= maximumOutputBytes then
                                                    0
                                                else
                                                    int (min (uint64 count) (maximumOutputBytes - capturedBytes))

                                            capturedBytes <- capturedBytes + uint64 remaining

                                            if remaining < count then
                                                outputLimitExceeded <- true

                                            remaining, outputLimitExceeded)

                                    if allowed > 0 then
                                        captured.Write(buffer, 0, allowed)

                                    if exceeded && not child.HasExited then
                                        try
                                            child.Kill(true)
                                        with _ ->
                                            ()
                        with :? OperationCanceledException ->
                            ()

                        return captured.ToArray()
                    }

                let stdout = readBounded child.StandardOutput.BaseStream
                let stderr = readBounded child.StandardError.BaseStream
                let remaining = deadline - DateTimeOffset.UtcNow
                use budget = new CancellationTokenSource()

                budget.CancelAfter(
                    if remaining <= TimeSpan.Zero then
                        TimeSpan.Zero
                    else
                        remaining
                )

                use combined =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token)

                let mutable timedOut = false

                try
                    do! child.WaitForExitAsync(combined.Token)
                with :? OperationCanceledException ->
                    timedOut <- true

                    try
                        child.Kill(true)
                    with _ ->
                        ()

                let reads = Task.WhenAll(stdout, stderr)
                let! drained = Task.WhenAny(reads, Task.Delay(runtime.TerminationGrace))

                if not (Object.ReferenceEquals(drained, reads)) then
                    readCancellation.Cancel()

                    try
                        child.StandardOutput.Close()
                        child.StandardError.Close()
                    with _ ->
                        ()

                    let! _ = Task.WhenAny(reads, Task.Delay(runtime.TerminationGrace))
                    ()

                let output, error =
                    if reads.IsCompletedSuccessfully then
                        reads.Result[0], reads.Result[1]
                    else
                        Array.empty, Array.empty

                return
                    {
                        ExitCode = if child.HasExited then Some child.ExitCode else None
                        StandardOutput = output
                        StandardError = error
                        TimedOut = timedOut
                        OutputLimitExceeded = outputLimitExceeded
                        OutputComplete = reads.IsCompletedSuccessfully && not outputLimitExceeded
                    }
        }

    let text (observation: PortableToolObservation) =
        Encoding.UTF8.GetString(observation.StandardOutput).Trim()

    let runGit runtime request arguments cancellationToken =
        runTool
            runtime
            runtime.GitExecutable
            ([ "-C"; request.SourceRepository ] @ arguments)
            request.SourceRepository
            request.Deadline
            request.MaximumOutputBytes
            cancellationToken

    let runPodman runtime request arguments cancellationToken =
        runTool
            runtime
            runtime.PodmanExecutable
            (runtime.PodmanGlobalArguments @ arguments)
            runtime.StateRoot
            request.Deadline
            request.MaximumOutputBytes
            cancellationToken

    let requireSuccess reason observation =
        if observation.ExitCode = Some 0 && observation.OutputComplete then
            Ok observation
        else
            Error reason

    let canonicalImageId (value: string) =
        let digest =
            if value.StartsWith("sha256:", StringComparison.Ordinal) then
                value[7..]
            else
                value

        if
            digest.Length = 64
            && digest |> Seq.forall (fun character -> Char.IsAsciiHexDigit character && not (Char.IsUpper character))
        then
            Some digest
        else
            None

    let safeStateRoot runtime =
        try
            Directory.CreateDirectory runtime.StateRoot |> ignore

            if File.GetAttributes(runtime.StateRoot) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint then
                Error "portable-runtime-state-root-refused"
            else
                if not (OperatingSystem.IsWindows()) then
                    File.SetUnixFileMode(
                        runtime.StateRoot,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                Ok()
        with
        | :? IOException
        | :? UnauthorizedAccessException -> Error "portable-runtime-state-root-refused"

    let parseTreeEntries (bytes: byte array) =
        let records =
            Encoding.UTF8.GetString(bytes).Split('\000', StringSplitOptions.RemoveEmptyEntries)

        if
            records
            |> Array.forall (fun record ->
                let separator = record.IndexOf('\t')

                if separator <= 0 then
                    false
                else
                    let metadata =
                        record[.. separator - 1].Split(' ', StringSplitOptions.RemoveEmptyEntries)

                    let path = record[separator + 1 ..]

                    metadata.Length = 3
                    && (metadata[0] = "100644" || metadata[0] = "100755")
                    && metadata[1] = "blob"
                    && path.Split('/')
                       |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> ".."))
        then
            Ok records
        else
            Error "portable-source-tree-refused"

    let snapshotDigest maximumBytes root =
        try
            let files =
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.sort
                |> Seq.toList

            use canonical = new MemoryStream()
            let mutable total = 0UL

            for file in files do
                let attributes = File.GetAttributes file

                if attributes &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint then
                    invalidOp "portable-source-link-refused"

                let bytes = File.ReadAllBytes file
                total <- total + uint64 bytes.LongLength

                if total > maximumBytes then
                    invalidOp "portable-source-size-refused"

                let relative =
                    Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')

                let name = Encoding.UTF8.GetBytes relative
                canonical.Write name
                canonical.WriteByte 0uy
                canonical.Write(Encoding.UTF8.GetBytes(bytes.LongLength.ToString(CultureInfo.InvariantCulture)))
                canonical.WriteByte 0uy
                canonical.Write(Encoding.ASCII.GetBytes(sha256 bytes))
                canonical.WriteByte 10uy

            Ok(sha256 (canonical.ToArray()))
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? InvalidOperationException as error -> Error error.Message

    let prepareSnapshot runtime request cancellationToken =
        task {
            match safeStateRoot runtime with
            | Error reason -> return Error { Reason = reason; Helper = None }
            | Ok() ->
                let executionRoot =
                    Path.Combine(runtime.StateRoot, "executions", request.ContainerName)

                let snapshot = Path.Combine(executionRoot, "source")
                let output = Path.Combine(executionRoot, "output")
                let archive = Path.Combine(executionRoot, "source.tar")

                if Directory.Exists executionRoot || File.Exists executionRoot then
                    return Error { Reason = "portable-runtime-execution-root-exists"; Helper = None }
                else
                    Directory.CreateDirectory snapshot |> ignore
                    Directory.CreateDirectory output |> ignore

                    if not (OperatingSystem.IsWindows()) then
                        File.SetUnixFileMode(
                            output,
                            UnixFileMode.UserRead
                            ||| UnixFileMode.UserWrite
                            ||| UnixFileMode.UserExecute
                            ||| UnixFileMode.GroupRead
                            ||| UnixFileMode.GroupWrite
                            ||| UnixFileMode.GroupExecute
                            ||| UnixFileMode.OtherRead
                            ||| UnixFileMode.OtherWrite
                            ||| UnixFileMode.OtherExecute
                        )

                    let! commit =
                        runGit runtime request [ "rev-parse"; request.SourceRevision + "^{commit}" ] cancellationToken

                    match requireSuccess "portable-source-commit-refused" commit with
                    | Error reason -> return Error { Reason = reason; Helper = Some commit }
                    | Ok commit when text commit <> request.SourceRevision ->
                        return
                            Error
                                {
                                    Reason = "portable-source-commit-refused"
                                    Helper = Some commit
                                }
                    | Ok _ ->
                        let! tree =
                            runGit runtime request [ "rev-parse"; request.SourceRevision + "^{tree}" ] cancellationToken

                        let! entries =
                            runGit
                                runtime
                                request
                                [ "ls-tree"; "-r"; "-z"; "--full-tree"; request.SourceRevision ]
                                cancellationToken

                        match
                            requireSuccess "portable-source-tree-refused" tree,
                            requireSuccess "portable-source-tree-refused" entries
                        with
                        | Ok treeResult, Ok entryResult ->
                            match parseTreeEntries entryResult.StandardOutput with
                            | Error reason -> return Error { Reason = reason; Helper = Some entryResult }
                            | Ok _ ->
                                let! archived =
                                    runGit
                                        runtime
                                        request
                                        [ "archive"; "--format=tar"; "--output=" + archive; request.SourceRevision ]
                                        cancellationToken

                                match requireSuccess "portable-source-archive-refused" archived with
                                | Error reason -> return Error { Reason = reason; Helper = Some archived }
                                | Ok _ when uint64 (FileInfo(archive).Length) > runtime.MaximumSnapshotBytes ->
                                    return Error { Reason = "portable-source-size-refused"; Helper = None }
                                | Ok _ ->
                                    let! extracted =
                                        runTool
                                            runtime
                                            runtime.TarExecutable
                                            [
                                                "-xf"
                                                archive
                                                "--directory"
                                                snapshot
                                                "--no-same-owner"
                                                "--no-same-permissions"
                                            ]
                                            runtime.StateRoot
                                            request.Deadline
                                            request.MaximumOutputBytes
                                            cancellationToken

                                    match requireSuccess "portable-source-extract-refused" extracted with
                                    | Error reason -> return Error { Reason = reason; Helper = Some extracted }
                                    | Ok _ ->
                                        match snapshotDigest runtime.MaximumSnapshotBytes snapshot with
                                        | Error reason -> return Error { Reason = reason; Helper = None }
                                        | Ok digest ->
                                            File.WriteAllText(
                                                Path.Combine(executionRoot, "source-tree"),
                                                text treeResult
                                            )

                                            File.WriteAllText(Path.Combine(executionRoot, "snapshot-sha256"), digest)
                                            return Ok(snapshot, output, text treeResult, digest)
                        | Error _, _ ->
                            return
                                Error
                                    {
                                        Reason = "portable-source-tree-refused"
                                        Helper = Some tree
                                    }
                        | _, Error _ ->
                            return
                                Error
                                    {
                                        Reason = "portable-source-tree-refused"
                                        Helper = Some entries
                                    }
        }

    let imageDigest (image: string) =
        let marker = "@sha256:"
        image[(image.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length) ..]

    let cleanImageEnvironment (value: string) =
        try
            use document = JsonDocument.Parse value

            match document.RootElement.ValueKind with
            | JsonValueKind.Null -> true
            | JsonValueKind.Array ->
                document.RootElement.EnumerateArray()
                |> Seq.forall (fun (item: JsonElement) ->
                    if item.ValueKind <> JsonValueKind.String then
                        false
                    else
                        let entry: string = item.GetString()
                        let separator = entry.IndexOf '='
                        let name = if separator < 0 then entry else entry[.. separator - 1]

                        name = "HOME"
                        || name = "PATH"
                        || name = "LANG"
                        || name = "LC_ALL"
                        || name = "PYTHON_SHA256"
                        || name = "PYTHON_VERSION"
                        || name = "container")
            | _ -> false
        with :? JsonException ->
            false

    let emptyImageSetting (value: string) =
        value = "null" || value = "[]" || value = "{}"

    let readRecoveryMetadata runtime request =
        try
            let executionRoot =
                Path.Combine(runtime.StateRoot, "executions", request.ContainerName)

            let snapshot = Path.Combine(executionRoot, "source")
            let output = Path.Combine(executionRoot, "output")
            let treePath = Path.Combine(executionRoot, "source-tree")
            let digestPath = Path.Combine(executionRoot, "snapshot-sha256")

            if
                not (Directory.Exists snapshot)
                || not (Directory.Exists output)
                || not (File.Exists treePath)
                || not (File.Exists digestPath)
                || File.GetAttributes(treePath) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint
                || File.GetAttributes(digestPath) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint
            then
                Error "portable-recovery-state-refused"
            else
                let sourceTree = File.ReadAllText(treePath).Trim()
                let recordedDigest = File.ReadAllText(digestPath).Trim()

                match snapshotDigest runtime.MaximumSnapshotBytes snapshot with
                | Ok observedDigest when observedDigest = recordedDigest ->
                    Ok(snapshot, output, sourceTree, recordedDigest)
                | _ -> Error "portable-recovery-snapshot-refused"
        with
        | :? IOException
        | :? UnauthorizedAccessException -> Error "portable-recovery-state-refused"

    let verificationOutput outputRoot (request: PortableProcessRequest) =
        try
            let outputRoot = Path.GetFullPath outputRoot

            let verificationFile =
                Path.GetFullPath(Path.Combine(outputRoot, request.VerificationPath))

            let outputPrefix =
                outputRoot.TrimEnd(Path.DirectorySeparatorChar)
                + string Path.DirectorySeparatorChar

            let relativeParts =
                Path.GetRelativePath(outputRoot, verificationFile).Split(Path.DirectorySeparatorChar)

            let mutable current = outputRoot

            let noLinks =
                relativeParts
                |> Array.forall (fun part ->
                    current <- Path.Combine(current, part)

                    (File.Exists current || Directory.Exists current)
                    && File.GetAttributes(current) &&& FileAttributes.ReparsePoint
                       <> FileAttributes.ReparsePoint)

            if
                verificationFile.StartsWith(outputPrefix, StringComparison.Ordinal)
                && noLinks
                && File.Exists verificationFile
            then
                let length = uint64 (FileInfo(verificationFile).Length)

                if length > uint64 PortableWorkspaceContract.maximumDocumentBytes then
                    None, true
                elif length > request.MaximumOutputBytes then
                    None, false
                else
                    let bytes = File.ReadAllBytes verificationFile

                    if sha256 bytes = request.VerificationSha256 then
                        Some bytes, false
                    else
                        None, false
            else
                None, false
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None, false

type PortableWorkspacePodmanRunner(runtime: PortableRuntimePolicy) =
    interface IPortableProcessRunner with
        member _.RunAsync(request, cancellationToken) =
            task {
                let unknown
                    (stage: PortableProcessRefusalStage)
                    reason
                    (helper: PortableToolObservation option)
                    cancelled
                    sourceTree
                    snapshot
                    runtimeIdentity
                    containerIdentity
                    =
                    let stdout, stderr, exitCode, timedOut, outputLimitExceeded, outputComplete =
                        match helper with
                        | Some observation ->
                            observation.StandardOutput,
                            observation.StandardError,
                            observation.ExitCode,
                            observation.TimedOut,
                            observation.OutputLimitExceeded,
                            observation.OutputComplete
                        | None -> Array.empty, Array.empty, None, false, false, false

                    {
                        ExecutionStarted = false
                        ExitCode = exitCode
                        StandardOutput = stdout
                        StandardError = stderr
                        CancellationRequested = cancelled || timedOut
                        TerminationObserved = false
                        Interrupted = true
                        OutputLimitExceeded = outputLimitExceeded
                        OutputComplete = outputComplete
                        SourceTree = sourceTree
                        SnapshotSha256 = snapshot
                        RuntimeIdentity = runtimeIdentity
                        ContainerIdentity = containerIdentity
                        VerificationObserved = false
                        VerificationOutput = None
                        VerificationCustodyLimitExceeded = false
                        Refusal =
                            Some
                                {
                                    Stage = stage
                                    Reason = reason
                                    HelperTimedOut = timedOut
                                    HelperOutputComplete = outputComplete
                                }
                    }

                match! PortablePodmanRuntime.prepareSnapshot runtime request cancellationToken with
                | Error failure ->
                    return
                        unknown
                            PortableProcessRefusalStage.SourceSnapshot
                            failure.Reason
                            failure.Helper
                            cancellationToken.IsCancellationRequested
                            None
                            None
                            None
                            None
                | Ok(snapshotRoot, outputRoot, sourceTree, snapshotDigest) ->
                    let! version =
                        PortablePodmanRuntime.runPodman
                            runtime
                            request
                            [ "version"; "--format"; "{{.Client.Version}}" ]
                            cancellationToken

                    let! image =
                        PortablePodmanRuntime.runPodman
                            runtime
                            request
                            [
                                "image"
                                "inspect"
                                "--format"
                                "{{.Digest}}|{{.Id}}|{{json .Config.Env}}|{{json .Config.Entrypoint}}|{{json .Config.Cmd}}|{{json .Config.Volumes}}|{{.Config.User}}"
                                request.QualifiedImage
                            ]
                            cancellationToken

                    match
                        PortablePodmanRuntime.requireSuccess "portable-runtime-version-refused" version,
                        PortablePodmanRuntime.requireSuccess "portable-image-inspect-refused" image
                    with
                    | Ok versionResult, Ok imageResult ->
                        let imageParts =
                            PortablePodmanRuntime.text imageResult |> fun value -> value.Split('|', 7)

                        if
                            imageParts.Length <> 7
                            || imageParts[0]
                               <> "sha256:" + PortablePodmanRuntime.imageDigest request.QualifiedImage
                            || (PortablePodmanRuntime.canonicalImageId imageParts[1] |> Option.isNone)
                            || not (PortablePodmanRuntime.cleanImageEnvironment imageParts[2])
                            || not (PortablePodmanRuntime.emptyImageSetting imageParts[3])
                            || not (PortablePodmanRuntime.emptyImageSetting imageParts[4])
                            || not (PortablePodmanRuntime.emptyImageSetting imageParts[5])
                            || imageParts[6] <> runtime.ContainerUser
                        then
                            return
                                unknown
                                    PortableProcessRefusalStage.ImageIdentity
                                    "portable-image-identity-refused"
                                    (Some imageResult)
                                    false
                                    (Some sourceTree)
                                    (Some snapshotDigest)
                                    None
                                    None
                        else
                            let runtimeIdentity = PortablePodmanRuntime.sha256 versionResult.StandardOutput
                            let imageId = imageParts[1]

                            let! ownership =
                                PortablePodmanRuntime.runPodman
                                    runtime
                                    request
                                    [ "unshare"; "chown"; runtime.ContainerUser; outputRoot ]
                                    cancellationToken

                            let environmentArguments =
                                request.ContainerEnvironment
                                |> Map.toList
                                |> List.collect (fun (name, value) -> [ "--env"; name + "=" + value ])

                            let createArguments =
                                [
                                    "create"
                                    "--name"
                                    request.ContainerName
                                    "--pull=never"
                                    "--read-only"
                                    "--network=none"
                                    "--cap-drop=all"
                                    "--security-opt=no-new-privileges"
                                    "--user"
                                    runtime.ContainerUser
                                    "--volume"
                                    snapshotRoot + ":/source:ro"
                                    "--volume"
                                    outputRoot + ":/output:rw"
                                    "--tmpfs"
                                    "/tmp:rw,noexec,nosuid,nodev,size=64m"
                                    "--workdir"
                                    "/source/" + request.WorkingDirectory
                                    "--entrypoint"
                                    request.Executable
                                ]
                                @ environmentArguments
                                @ [ request.QualifiedImage ]
                                @ request.Arguments

                            let! created =
                                if ownership.ExitCode = Some 0 && ownership.OutputComplete then
                                    PortablePodmanRuntime.runPodman runtime request createArguments cancellationToken
                                else
                                    Task.FromResult ownership

                            let createStage, createReason =
                                if ownership.ExitCode = Some 0 && ownership.OutputComplete then
                                    PortableProcessRefusalStage.ContainerCreation, "portable-container-create-refused"
                                else
                                    PortableProcessRefusalStage.OutputOwnership, "portable-output-ownership-refused"

                            match PortablePodmanRuntime.requireSuccess createReason created with
                            | Error reason ->
                                return
                                    unknown
                                        createStage
                                        reason
                                        (Some created)
                                        cancellationToken.IsCancellationRequested
                                        (Some sourceTree)
                                        (Some snapshotDigest)
                                        (Some runtimeIdentity)
                                        None
                            | Ok _ ->
                                let! container =
                                    PortablePodmanRuntime.runPodman
                                        runtime
                                        request
                                        [
                                            "container"
                                            "inspect"
                                            "--format"
                                            "{{.Image}}|{{.Id}}|{{.Config.User}}"
                                            request.ContainerName
                                        ]
                                        cancellationToken

                                let containerParts =
                                    PortablePodmanRuntime.text container |> fun value -> value.Split('|', 3)

                                let containerImageMatches =
                                    match
                                        PortablePodmanRuntime.canonicalImageId imageId,
                                        if containerParts.Length = 3 then
                                            PortablePodmanRuntime.canonicalImageId containerParts[0]
                                        else
                                            None
                                    with
                                    | Some expected, Some actual -> expected = actual
                                    | _ -> false

                                if container.ExitCode <> Some 0 || not container.OutputComplete then
                                    return
                                        unknown
                                            PortableProcessRefusalStage.ContainerInspection
                                            "portable-container-inspect-refused"
                                            (Some container)
                                            cancellationToken.IsCancellationRequested
                                            (Some sourceTree)
                                            (Some snapshotDigest)
                                            (Some runtimeIdentity)
                                            None
                                elif
                                    containerParts.Length <> 3
                                    || not containerImageMatches
                                    || containerParts[2] <> runtime.ContainerUser
                                then
                                    return
                                        unknown
                                            PortableProcessRefusalStage.ContainerInspection
                                            "portable-container-image-refused"
                                            (Some container)
                                            false
                                            (Some sourceTree)
                                            (Some snapshotDigest)
                                            (Some runtimeIdentity)
                                            None
                                else
                                    let containerIdentity = containerParts[1]

                                    let! started =
                                        PortablePodmanRuntime.runPodman
                                            runtime
                                            request
                                            [ "start"; "--attach"; request.ContainerName ]
                                            cancellationToken

                                    if started.TimedOut || started.OutputLimitExceeded then
                                        let recoveryRequest =
                                            { request with
                                                Deadline =
                                                    PortablePodmanRuntime.deadlineAfter
                                                        DateTimeOffset.UtcNow
                                                        runtime.TerminationGrace
                                            }

                                        let! stopped =
                                            PortablePodmanRuntime.runPodman
                                                runtime
                                                recoveryRequest
                                                [ "stop"; "--time"; "5"; request.ContainerName ]
                                                CancellationToken.None

                                        if stopped.ExitCode <> Some 0 || not stopped.OutputComplete then
                                            let killRequest =
                                                { request with
                                                    Deadline =
                                                        PortablePodmanRuntime.deadlineAfter
                                                            DateTimeOffset.UtcNow
                                                            runtime.TerminationGrace
                                                }

                                            let! _ =
                                                PortablePodmanRuntime.runPodman
                                                    runtime
                                                    killRequest
                                                    [ "kill"; request.ContainerName ]
                                                    CancellationToken.None

                                            ()

                                    let stateRequest =
                                        { request with
                                            Deadline =
                                                PortablePodmanRuntime.deadlineAfter
                                                    DateTimeOffset.UtcNow
                                                    runtime.TerminationGrace
                                        }

                                    let! state =
                                        PortablePodmanRuntime.runPodman
                                            runtime
                                            stateRequest
                                            [
                                                "container"
                                                "inspect"
                                                "--format"
                                                "{{.State.Status}}|{{.State.ExitCode}}"
                                                request.ContainerName
                                            ]
                                            CancellationToken.None

                                    let stateParts =
                                        PortablePodmanRuntime.text state |> fun value -> value.Split('|', 2)

                                    let mutable observedExit = 0

                                    let terminationObserved =
                                        state.ExitCode = Some 0
                                        && stateParts.Length = 2
                                        && stateParts[0] = "exited"
                                        && Int32.TryParse(stateParts[1], &observedExit)

                                    let verificationOutput, verificationCustodyLimitExceeded =
                                        if terminationObserved then
                                            PortablePodmanRuntime.verificationOutput outputRoot request
                                        else
                                            None, false

                                    return
                                        {
                                            ExecutionStarted = true
                                            ExitCode = if terminationObserved then Some observedExit else None
                                            StandardOutput = started.StandardOutput
                                            StandardError = started.StandardError
                                            CancellationRequested =
                                                started.TimedOut || cancellationToken.IsCancellationRequested
                                            TerminationObserved = terminationObserved
                                            Interrupted = not terminationObserved
                                            OutputLimitExceeded = started.OutputLimitExceeded
                                            OutputComplete = started.OutputComplete
                                            SourceTree = Some sourceTree
                                            SnapshotSha256 = Some snapshotDigest
                                            RuntimeIdentity = Some runtimeIdentity
                                            ContainerIdentity = Some containerIdentity
                                            VerificationObserved = verificationOutput.IsSome
                                            VerificationOutput = verificationOutput
                                            VerificationCustodyLimitExceeded = verificationCustodyLimitExceeded
                                            Refusal = None
                                        }
                    | Error reason, _ ->
                        return
                            unknown
                                PortableProcessRefusalStage.RuntimeVersion
                                reason
                                (Some version)
                                cancellationToken.IsCancellationRequested
                                (Some sourceTree)
                                (Some snapshotDigest)
                                None
                                None
                    | _, Error reason ->
                        return
                            unknown
                                PortableProcessRefusalStage.ImageInspection
                                reason
                                (Some image)
                                cancellationToken.IsCancellationRequested
                                (Some sourceTree)
                                (Some snapshotDigest)
                                None
                                None
            }

        member _.RecoverAsync(request, cancellationToken) =
            task {
                let unknown sourceTree snapshot runtimeIdentity containerIdentity =
                    {
                        ExecutionStarted = true
                        ExitCode = None
                        StandardOutput = Array.empty
                        StandardError = Array.empty
                        CancellationRequested = cancellationToken.IsCancellationRequested
                        TerminationObserved = false
                        Interrupted = true
                        OutputLimitExceeded = false
                        OutputComplete = false
                        SourceTree = sourceTree
                        SnapshotSha256 = snapshot
                        RuntimeIdentity = runtimeIdentity
                        ContainerIdentity = containerIdentity
                        VerificationObserved = false
                        VerificationOutput = None
                        VerificationCustodyLimitExceeded = false
                        Refusal = None
                    }

                match PortablePodmanRuntime.safeStateRoot runtime with
                | Error _ -> return unknown None None None None
                | Ok() ->
                    match PortablePodmanRuntime.readRecoveryMetadata runtime request with
                    | Error _ -> return unknown None None None (Some request.ContainerName)
                    | Ok(_, outputRoot, sourceTree, snapshotDigest) ->
                        let! version =
                            PortablePodmanRuntime.runPodman
                                runtime
                                request
                                [ "version"; "--format"; "{{.Client.Version}}" ]
                                cancellationToken

                        let! image =
                            PortablePodmanRuntime.runPodman
                                runtime
                                request
                                [
                                    "image"
                                    "inspect"
                                    "--format"
                                    "{{.Digest}}|{{.Id}}|{{json .Config.Env}}|{{json .Config.Entrypoint}}|{{json .Config.Cmd}}|{{json .Config.Volumes}}|{{.Config.User}}"
                                    request.QualifiedImage
                                ]
                                cancellationToken

                        match
                            PortablePodmanRuntime.requireSuccess "portable-runtime-version-refused" version,
                            PortablePodmanRuntime.requireSuccess "portable-image-inspect-refused" image
                        with
                        | Ok versionResult, Ok imageResult ->
                            let imageParts =
                                PortablePodmanRuntime.text imageResult |> fun value -> value.Split('|', 7)

                            let imageValid =
                                imageParts.Length = 7
                                && imageParts[0] = "sha256:" + PortablePodmanRuntime.imageDigest request.QualifiedImage
                                && PortablePodmanRuntime.cleanImageEnvironment imageParts[2]
                                && PortablePodmanRuntime.emptyImageSetting imageParts[3]
                                && PortablePodmanRuntime.emptyImageSetting imageParts[4]
                                && PortablePodmanRuntime.emptyImageSetting imageParts[5]
                                && imageParts[6] = runtime.ContainerUser

                            let runtimeIdentity =
                                Some(PortablePodmanRuntime.sha256 versionResult.StandardOutput)

                            if not imageValid then
                                return
                                    unknown
                                        (Some sourceTree)
                                        (Some snapshotDigest)
                                        runtimeIdentity
                                        (Some request.ContainerName)
                            else
                                let! state =
                                    PortablePodmanRuntime.runPodman
                                        runtime
                                        request
                                        [
                                            "container"
                                            "inspect"
                                            "--format"
                                            "{{.Image}}|{{.Id}}|{{.State.Status}}|{{.State.ExitCode}}|{{.Config.User}}"
                                            request.ContainerName
                                        ]
                                        cancellationToken

                                let stateParts =
                                    PortablePodmanRuntime.text state |> fun value -> value.Split('|', 5)

                                let mutable observedExit = 0

                                let terminated =
                                    state.ExitCode = Some 0
                                    && state.OutputComplete
                                    && stateParts.Length = 5
                                    && stateParts[0] = imageParts[1]
                                    && stateParts[2] = "exited"
                                    && Int32.TryParse(stateParts[3], &observedExit)
                                    && stateParts[4] = runtime.ContainerUser

                                if not terminated then
                                    return
                                        unknown
                                            (Some sourceTree)
                                            (Some snapshotDigest)
                                            runtimeIdentity
                                            (if stateParts.Length >= 2 then
                                                 Some stateParts[1]
                                             else
                                                 Some request.ContainerName)
                                else
                                    let! logs =
                                        PortablePodmanRuntime.runPodman
                                            runtime
                                            request
                                            [ "logs"; request.ContainerName ]
                                            cancellationToken

                                    let verificationOutput, verificationCustodyLimitExceeded =
                                        PortablePodmanRuntime.verificationOutput outputRoot request

                                    return
                                        {
                                            ExecutionStarted = true
                                            ExitCode = Some observedExit
                                            StandardOutput = logs.StandardOutput
                                            StandardError = logs.StandardError
                                            CancellationRequested = cancellationToken.IsCancellationRequested
                                            TerminationObserved = true
                                            Interrupted = logs.ExitCode <> Some 0 || not logs.OutputComplete
                                            OutputLimitExceeded = logs.OutputLimitExceeded
                                            OutputComplete = logs.OutputComplete
                                            SourceTree = Some sourceTree
                                            SnapshotSha256 = Some snapshotDigest
                                            RuntimeIdentity = runtimeIdentity
                                            ContainerIdentity = Some stateParts[1]
                                            VerificationObserved = verificationOutput.IsSome
                                            VerificationOutput = verificationOutput
                                            VerificationCustodyLimitExceeded = verificationCustodyLimitExceeded
                                            Refusal = None
                                        }
                        | _ -> return unknown (Some sourceTree) (Some snapshotDigest) None (Some request.ContainerName)
            }

        member _.CleanupAsync(request, cancellationToken) =
            task {
                try
                    let! removed =
                        PortablePodmanRuntime.runPodman
                            runtime
                            request
                            [ "container"; "rm"; "--force"; request.ContainerName ]
                            cancellationToken

                    let! absent =
                        if removed.ExitCode = Some 0 then
                            Task.FromResult true
                        else
                            task {
                                let! exists =
                                    PortablePodmanRuntime.runPodman
                                        runtime
                                        request
                                        [ "container"; "exists"; request.ContainerName ]
                                        cancellationToken

                                return exists.ExitCode = Some 1
                            }

                    if not absent then
                        return false
                    else
                        let executionRoot =
                            Path.Combine(runtime.StateRoot, "executions", request.ContainerName)

                        if Directory.Exists executionRoot then
                            Directory.Delete(executionRoot, true)

                        return not (Directory.Exists executionRoot) && not (File.Exists executionRoot)
                with
                | :? IOException
                | :? UnauthorizedAccessException -> return false
            }
