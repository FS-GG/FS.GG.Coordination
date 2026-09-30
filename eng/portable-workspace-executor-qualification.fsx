#r "../src/FS.GG.Coordination.Orchestration.Execution/bin/Debug/net10.0/FS.GG.Coordination.Orchestration.Execution.dll"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open FS.GG.Coordination.Orchestration.Execution

let fail message =
    raise (InvalidOperationException message)

let options =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.chunkBySize 2
    |> Array.map (function
        | [| name; value |] when name.StartsWith("--", StringComparison.Ordinal) -> name[2..], value
        | _ -> fail "arguments must be --name value pairs")
    |> Map.ofArray

let required name =
    options
    |> Map.tryFind name
    |> Option.defaultWith (fun () -> fail $"missing --{name}")

let full name = required name |> Path.GetFullPath
let sourceTemplate = full "fixture"
let manifestPath = full "image-manifest"
let stateRoot = full "state-dir"
let evidencePath = full "evidence"
let podman = full "podman"
let git = full "git"
let tar = full "tar"
let storeRoot = full "root"
let runRoot = full "runroot"

for path in [ sourceTemplate; manifestPath; podman; git; tar ] do
    if not (Directory.Exists path || File.Exists path) then
        fail $"required input is absent: {path}"

for path in [ stateRoot; storeRoot; runRoot; Path.GetDirectoryName evidencePath ] do
    Directory.CreateDirectory path |> ignore

let sha256 (bytes: byte array) =
    bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let manifestBytes = File.ReadAllBytes manifestPath
let recipeSha256 = sha256 manifestBytes
let manifest = JsonDocument.Parse manifestBytes
let root = manifest.RootElement

if
    root.GetProperty("schema").GetString()
    <> "fsgg.portable-workspace-local-image/1"
then
    fail "unsupported image manifest schema"

let image = root.GetProperty "image"
let imageReference = image.GetProperty("reference").GetString()
let imageId = image.GetProperty("id").GetString()

if
    image.GetProperty("os").GetString() <> "linux"
    || image.GetProperty("architecture").GetString() <> "amd64"
    || image.GetProperty("user").GetString() <> "32768:32768"
    || not (imageReference.Contains("@sha256:", StringComparison.Ordinal))
    || not (imageId.StartsWith("sha256:", StringComparison.Ordinal))
then
    fail "image manifest is outside the reviewed runtime profile"

let run executable arguments workingDirectory environment =
    use child = new Process()

    child.StartInfo <-
        ProcessStartInfo(
            executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    child.StartInfo.Environment.Clear()

    for name, value in environment do
        child.StartInfo.Environment.Add(name, value)

    for argument in arguments do
        child.StartInfo.ArgumentList.Add argument

    if not (child.Start()) then
        fail $"failed to start {executable}"

    let stdout = child.StandardOutput.ReadToEnd()
    let stderr = child.StandardError.ReadToEnd()

    if not (child.WaitForExit(30000)) then
        child.Kill true
        fail $"tool timed out: {executable}"

    if child.ExitCode <> 0 then
        fail $"tool failed: {executable}: {stderr}"

    stdout.Trim()

let boundedDiagnostic (value: string) =
    let bytes = Encoding.UTF8.GetBytes value
    let maximumBytes = 4096
    let retained = bytes |> Array.truncate maximumBytes
    Encoding.UTF8.GetString retained, sha256 bytes, bytes.Length > maximumBytes

let probe executable arguments workingDirectory environment =
    use child = new Process()

    child.StartInfo <-
        ProcessStartInfo(
            executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    child.StartInfo.Environment.Clear()

    for name, value in environment do
        child.StartInfo.Environment.Add(name, value)

    for argument in arguments do
        child.StartInfo.ArgumentList.Add argument

    let record = JsonObject()
    record["name"] <- String.concat " " arguments

    if not (child.Start()) then
        record["started"] <- false
    else
        record["started"] <- true
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        let exited = child.WaitForExit(30000)

        if not exited then
            child.Kill true

        let stdoutText, stdoutSha256, stdoutTruncated = boundedDiagnostic (stdout.GetAwaiter().GetResult())
        let stderrText, stderrSha256, stderrTruncated = boundedDiagnostic (stderr.GetAwaiter().GetResult())
        record["timedOut"] <- not exited
        record["exitCode"] <- if exited then child.ExitCode else -1
        record["stdout"] <- stdoutText
        record["stdoutSha256"] <- stdoutSha256
        record["stdoutTruncated"] <- stdoutTruncated
        record["stderr"] <- stderrText
        record["stderrSha256"] <- stderrSha256
        record["stderrTruncated"] <- stderrTruncated

    record

let fixtureRepository = Path.Combine(stateRoot, "fixture-repository")

if Directory.Exists fixtureRepository then
    Directory.Delete(fixtureRepository, true)

let rec copyDirectory source destination =
    Directory.CreateDirectory destination |> ignore

    for file in Directory.EnumerateFiles source do
        File.Copy(file, Path.Combine(destination, Path.GetFileName file))

    for directory in Directory.EnumerateDirectories source do
        copyDirectory directory (Path.Combine(destination, Path.GetFileName directory))

copyDirectory sourceTemplate fixtureRepository

let gitEnvironment =
    [
        "HOME", stateRoot
        "PATH", "/usr/local/bin:/usr/bin:/bin"
        "GIT_AUTHOR_DATE", "2000-01-01T00:00:00Z"
        "GIT_COMMITTER_DATE", "2000-01-01T00:00:00Z"
    ]

run git [ "init"; "--quiet" ] fixtureRepository gitEnvironment |> ignore

run git [ "config"; "user.email"; "portable@example.invalid" ] fixtureRepository gitEnvironment
|> ignore

run git [ "config"; "user.name"; "Portable Qualification" ] fixtureRepository gitEnvironment
|> ignore

run git [ "add"; "--all" ] fixtureRepository gitEnvironment |> ignore

run git [ "commit"; "--quiet"; "-m"; "reviewed portable fixture" ] fixtureRepository gitEnvironment
|> ignore

let sourceRevision =
    run git [ "rev-parse"; "HEAD" ] fixtureRepository gitEnvironment

let workspaceComponent id language directory tool version build test =
    {
        Id = id
        Language = language
        WorkingDirectory = directory
        Toolchain = { Id = tool; Version = version }
        EntryPoints =
            {
                Build = build
                Test = test
                Lint = None
                Artifact = None
            }
    }

let profile =
    {
        ProfileId = "portable-executor-native-v1"
        Revision = 1UL
        WorkspaceScope = "fs-gg/portable-executor-native"
        SourceRevision = sourceRevision
        QualifiedImage = imageReference
        Components =
            [
                workspaceComponent "python" "python" "python" "cpython" "3.14.0" "python-build" "python-test"
                workspaceComponent "frontend" "typescript" "frontend" "node" "24.8.0" "frontend-build" "frontend-test"
                workspaceComponent "backend" "python" "backend" "cpython" "3.14.0" "backend-build" "backend-test"
            ]
        ProductBuild = "product-build"
        ProductTest = "product-test"
        ProductJourney = "composed-journey"
        MaximumRuntimeSeconds = 60UL
        MaximumOutputBytes = 262144UL
    }

let reviewed entry identity componentId directory tools executable arguments verification path digest =
    {
        EntryPoint = entry
        OperationIdentity = identity
        ComponentId = componentId
        WorkingDirectory = directory
        QualifiedImage = imageReference
        RequiredToolchains = tools
        Executable = executable
        Arguments = arguments
        VerificationIdentity = verification
        VerificationPath = path
        VerificationSha256 = digest
        RecipeSha256 = recipeSha256
    }

let operations =
    [
        reviewed
            "python-build"
            "build"
            (Some "python")
            "python"
            [ ("cpython", "3.14.0") ]
            "/usr/local/bin/python3"
            [ "build.py" ]
            "python-build-v1"
            "python/app.pyc"
            "ab89c3c1b5404d87622387ba576e44ffb6e089b768a19f268886fc5b42ac3f93"
        reviewed
            "python-test"
            "test"
            (Some "python")
            "python"
            [ ("cpython", "3.14.0") ]
            "/usr/local/bin/python3"
            [ "test.py" ]
            "python-test-v1"
            "python-test.json"
            "2ed645adefe2c23308832036a3b5163dc39faaf152c2c9d1d3afb3bd637f146a"
        reviewed
            "frontend-build"
            "build"
            (Some "frontend")
            "frontend"
            [ ("node", "24.8.0") ]
            "/opt/typescript/bin/tsc"
            [ "--project"; "tsconfig.json"; "--outDir"; "/output/frontend" ]
            "frontend-build-v1"
            "frontend/app.js"
            "75d96c0c9851a8d87141ac50d3c4fc5059c0090ad0568be62682bc72681c3137"
        reviewed
            "backend-build"
            "build"
            (Some "backend")
            "backend"
            [ ("cpython", "3.14.0") ]
            "/usr/local/bin/python3"
            [ "build.py" ]
            "backend-build-v1"
            "backend/service.pyc"
            "cd136bcbbc1c5dbd87c0811c21d8e6d53d7ae873caef496f23664c62b09f2147"
        reviewed
            "backend-test"
            "test"
            (Some "backend")
            "backend"
            [ ("cpython", "3.14.0") ]
            "/usr/local/bin/python3"
            [ "test.py" ]
            "backend-test-v1"
            "backend-test.json"
            "96755bcdb1d9b0a64ecb07d8e01ea22070bdc889712002b58d69f11902a81502"
        reviewed
            "composed-journey"
            "journey"
            None
            "product"
            [ "node", "24.8.0"; "cpython", "3.14.0" ]
            "/usr/local/bin/python3"
            [ "journey.py" ]
            "composed-journey-v1"
            "composed-journey.json"
            "8fc03ef420b45054d93981848a4ba644e88a0fd6bdd47b124b18acc9b2b38b3d"
    ]

let runtime =
    {
        GitExecutable = git
        TarExecutable = tar
        PodmanExecutable = podman
        PodmanGlobalArguments = [ "--storage-driver=vfs"; "--root"; storeRoot; "--runroot"; runRoot ]
        StateRoot = Path.Combine(stateRoot, "executor")
        ContainerPath = "/usr/local/bin:/opt/typescript/bin:/usr/bin:/bin"
        ContainerUser = "32768:32768"
        HostEnvironment = Map [ "HOME", stateRoot; "PATH", "/usr/local/bin:/usr/bin:/bin"; "LANG", "C.UTF-8" ]
        ContainerEnvironment =
            Map
                [
                    "HOME", "/tmp"
                    "PATH", "/usr/local/bin:/opt/typescript/bin:/usr/bin:/bin"
                    "LANG", "C.UTF-8"
                    "PYTHONDONTWRITEBYTECODE", "1"
                    "PYTHONPYCACHEPREFIX", "/output/pycache"
                ]
        MaximumSnapshotBytes = 8UL * 1024UL * 1024UL
        TerminationGrace = TimeSpan.FromSeconds 10.0
    }

let policy =
    {
        WorkspaceRoot = fixtureRepository
        WorkspaceScope = profile.WorkspaceScope
        SourceRevision = sourceRevision
        QualifiedImage = imageReference
        MaximumRuntimeSeconds = 60UL
        MaximumOutputBytes = 262144UL
        Operations = operations
        Runtime = runtime
    }

let runtimeProbes = JsonArray()
let probeEnvironment = runtime.HostEnvironment |> Map.toList
Directory.CreateDirectory runtime.StateRoot |> ignore

for arguments in
    [
        runtime.PodmanGlobalArguments @ [ "version"; "--format"; "{{.Client.Version}}" ]
        runtime.PodmanGlobalArguments
        @ [ "image"; "inspect"; "--format"; "{{.Digest}}|{{.Id}}|{{.Config.User}}"; imageReference ]
        runtime.PodmanGlobalArguments @ [ "unshare"; "/usr/bin/true" ]
    ] do
    runtimeProbes.Add(probe podman arguments runtime.StateRoot probeEnvironment)

let now () =
    let value = DateTimeOffset.UtcNow
    DateTimeOffset(value.Ticks - value.Ticks % 10L, TimeSpan.Zero)

let authority =
    {
        WorkspaceScope = profile.WorkspaceScope
        WorkflowRevision = 1UL
        FenceGeneration = 1UL
        ObservedAt = now ()
    }

let command index operation componentId =
    {
        CommandId = Guid.Parse($"70000000-0000-0000-0000-{index:D12}")
        IdempotencyId = $"native-{index:D2}"
        WorkspaceScope = profile.WorkspaceScope
        ProfileId = profile.ProfileId
        ProfileRevision = profile.Revision
        SourceRevision = sourceRevision
        ExpectedWorkflowRevision = 1UL
        FenceGeneration = 1UL
        CausationId = None
        Deadline = now().AddSeconds 90.0
        Operation = operation
        ComponentId = componentId
    }

let runner = PortableWorkspacePodmanRunner runtime :> IPortableProcessRunner
let executor = PortableWorkspaceExecutor.Executor(policy, runner, now)

let cases =
    [
        "python-build", "build", Some "python"
        "python-test", "test", Some "python"
        "frontend-build", "build", Some "frontend"
        "backend-build", "build", Some "backend"
        "backend-test", "test", Some "backend"
        "composed-journey", "journey", None
    ]

let results = JsonArray()
let mutable passed = 0
let mutable unknown = 0
let mutable failed = 0
let mutable firstCommand = Unchecked.defaultof<PortableWorkspaceCommand>
let inheritedNodeOptions = Environment.GetEnvironmentVariable "NODE_OPTIONS"
Environment.SetEnvironmentVariable("NODE_OPTIONS", "--require=/tmp/unreviewed.js")

for index, (name, operation, componentId) in List.indexed cases do
    let selected = command (index + 1) operation componentId

    if index = 0 then
        firstCommand <- selected

    let outcome =
        executor.ExecuteAsync(authority, profile, selected, CancellationToken.None).GetAwaiter().GetResult()

    let record = JsonObject()
    record["name"] <- name

    match outcome with
    | Completed receipt when
        receipt.Result.Error.IsNone
        && receipt.CleanupCompleted
        && receipt.VerificationOutput.IsSome
        ->
        passed <- passed + 1
        record["outcome"] <- "passed"
        record["containerIdentity"] <- defaultArg receipt.ContainerIdentity ""
        record["verificationSha256"] <- sha256 receipt.VerificationOutput.Value
    | Completed receipt when
        receipt.Result.Error
        |> Option.exists (fun error -> error.Code = "execution-outcome-unknown")
        ->
        unknown <- unknown + 1
        record["outcome"] <- "unknown"
        record["error"] <- receipt.Result.Error.Value.Code
    | Completed receipt when not receipt.ExecutionStarted ->
        unknown <- unknown + 1
        record["outcome"] <- "unknown"
        record["error"] <- receipt.Result.Error.Value.Code

        for KeyValue(name, value) in receipt.Result.Error.Value.Details do
            record[name] <- value
    | other ->
        failed <- failed + 1
        record["outcome"] <- "failed"
        record["detail"] <- string other

    results.Add record

Environment.SetEnvironmentVariable("NODE_OPTIONS", inheritedNodeOptions)

let reconstructed = PortableWorkspaceExecutor.Executor(policy, runner, now)

let duplicate =
    reconstructed.ExecuteAsync(authority, profile, firstCommand, CancellationToken.None).GetAwaiter().GetResult()

let duplicateExact =
    match duplicate with
    | Duplicate _ -> true
    | PendingDuplicate _ ->
        match reconstructed.RecoverAsync(profile, firstCommand, CancellationToken.None).GetAwaiter().GetResult() with
        | PendingDuplicate _
        | Completed _ -> true
        | _ -> false
    | _ -> false

if not duplicateExact then
    failed <- failed + 1

let beforeRefusal =
    if Directory.Exists(Path.Combine(runtime.StateRoot, "executions")) then
        Directory.GetDirectories(Path.Combine(runtime.StateRoot, "executions")).Length
    else
        0

let stale =
    { command 90 "build" (Some "python") with
        SourceRevision = String.replicate 40 "f"
    }

let staleOutcome =
    executor.ExecuteAsync(authority, profile, stale, CancellationToken.None).GetAwaiter().GetResult()

let afterRefusal =
    if Directory.Exists(Path.Combine(runtime.StateRoot, "executions")) then
        Directory.GetDirectories(Path.Combine(runtime.StateRoot, "executions")).Length
    else
        0

let sourceFence =
    match staleOutcome with
    | Refused _ -> beforeRefusal = afterRefusal
    | _ -> false

if not sourceFence then
    failed <- failed + 1

let cancelled = new CancellationTokenSource()
cancelled.Cancel()

let cancelledOutcome =
    executor
        .ExecuteAsync(authority, profile, command 91 "test" (Some "python"), cancelled.Token)
        .GetAwaiter()
        .GetResult()

let cancellationFence =
    match cancelledOutcome with
    | Refused "portable-executor-deadline-refused" -> true
    | _ -> false

if not cancellationFence then
    failed <- failed + 1

let missingImage =
    "localhost/fsgg-portable-workspace:missing@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

let missingProfile =
    { profile with
        Revision = 2UL
        QualifiedImage = missingImage
    }

let missingOperations =
    operations
    |> List.map (fun operation ->
        { operation with
            QualifiedImage = missingImage
        })

let missingPolicy =
    { policy with
        QualifiedImage = missingImage
        Operations = missingOperations
    }

let missingExecutor = PortableWorkspaceExecutor.Executor(missingPolicy, runner, now)

let missingCommand =
    { command 92 "build" (Some "python") with
        ProfileRevision = 2UL
    }

let missingOutcome =
    missingExecutor
        .ExecuteAsync(authority, missingProfile, missingCommand, CancellationToken.None)
        .GetAwaiter()
        .GetResult()

let missingImageFence =
    match missingOutcome with
    | Completed receipt -> not receipt.ExecutionStarted && receipt.CleanupCompleted
    | _ -> false

if not missingImageFence then
    failed <- failed + 1

let overflowProfile =
    { profile with
        Revision = 3UL
        MaximumRuntimeSeconds = UInt64.MaxValue
    }

let overflowPolicy =
    { policy with
        MaximumRuntimeSeconds = UInt64.MaxValue
    }

let overflowExecutor =
    PortableWorkspaceExecutor.Executor(overflowPolicy, runner, now)

let overflowCommand =
    { command 93 "test" (Some "python") with
        ProfileRevision = 3UL
    }

let overflowOutcome =
    overflowExecutor
        .ExecuteAsync(authority, overflowProfile, overflowCommand, CancellationToken.None)
        .GetAwaiter()
        .GetResult()

let overflowArithmeticAccepted =
    match overflowOutcome with
    | Refused _ -> false
    | _ -> true

if not overflowArithmeticAccepted then
    failed <- failed + 1

let slowComponent =
    profile.Components
    |> List.map (fun value ->
        if value.Id = "python" then
            { value with
                EntryPoints =
                    { value.EntryPoints with
                        Test = "python-slow"
                    }
            }
        else
            value)

let slowProfile =
    { profile with
        Revision = 4UL
        Components = slowComponent
    }

let slowBytes =
    Encoding.UTF8.GetBytes("{\"outcome\":\"passed\",\"verification\":\"slow-v1\"}\n")

let slowOperation =
    reviewed
        "python-slow"
        "test"
        (Some "python")
        "python"
        [ ("cpython", "3.14.0") ]
        "/usr/local/bin/python3"
        [ "slow.py" ]
        "slow-v1"
        "slow.json"
        (sha256 slowBytes)

let slowPolicy =
    { policy with
        Operations = [ slowOperation ]
    }

let slowExecutor = PortableWorkspaceExecutor.Executor(slowPolicy, runner, now)

let slowCommand =
    { command 94 "test" (Some "python") with
        ProfileRevision = 4UL
        Deadline = now().AddSeconds 30.0
    }

let slowCancellation = new CancellationTokenSource()

let slowExecution =
    slowExecutor.ExecuteAsync(authority, slowProfile, slowCommand, slowCancellation.Token)

Thread.Sleep 250

let concurrentDelivery =
    PortableWorkspaceExecutor
        .Executor(slowPolicy, runner, now)
        .ExecuteAsync(authority, slowProfile, slowCommand, CancellationToken.None)
        .GetAwaiter()
        .GetResult()

slowCancellation.Cancel()
let interrupted = slowExecution.GetAwaiter().GetResult()

let interruptedRecoveryNoRelaunch =
    match concurrentDelivery with
    | PendingDuplicate _ ->
        match
            PortableWorkspaceExecutor
                .Executor(slowPolicy, runner, now)
                .RecoverAsync(slowProfile, slowCommand, CancellationToken.None)
                .GetAwaiter()
                .GetResult()
        with
        | PendingDuplicate _
        | Completed _
        | Duplicate _ -> true
        | _ -> false
    | Duplicate _ -> true
    | _ -> false

slowCancellation.Dispose()

if not interruptedRecoveryNoRelaunch then
    failed <- failed + 1

let remainingExecutionRoots =
    let root = Path.Combine(runtime.StateRoot, "executions")

    if Directory.Exists root then
        Directory.GetDirectories(root).Length
    else
        0

let evidence = JsonObject()
evidence["schema"] <- "fsgg.portable-workspace-executor-qualification/1"
evidence["sourceRevision"] <- sourceRevision
evidence["imageReference"] <- imageReference
evidence["imageId"] <- imageId
evidence["manifestSha256"] <- recipeSha256
evidence["runtimeProbes"] <- runtimeProbes
evidence["operations"] <- results
evidence["passed"] <- passed
evidence["unknown"] <- unknown
evidence["failed"] <- failed
evidence["duplicateOrPendingWithoutRelaunch"] <- duplicateExact
evidence["sourceFenceBeforeWrite"] <- sourceFence
evidence["preCancelledBeforeLaunch"] <- cancellationFence
evidence["hostEnvironmentInjectionCleared"] <- passed = cases.Length
evidence["missingImageRefusedBeforeStart"] <- missingImageFence
evidence["overflowArithmeticAccepted"] <- overflowArithmeticAccepted
evidence["interruptedRecoveryNoRelaunch"] <- interruptedRecoveryNoRelaunch
evidence["remainingExecutionRoots"] <- remainingExecutionRoots
evidence["strictAcceptance"] <- passed = cases.Length && failed = 0

evidence["outcome"] <-
    if failed > 0 then "failed"
    elif unknown > 0 then "unknown"
    else "passed"

File.WriteAllText(evidencePath, evidence.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")
cancelled.Dispose()
manifest.Dispose()
printfn "%s" (evidence.ToJsonString())

exit (
    if failed > 0 then 1
    elif unknown > 0 then 2
    else 0
)
