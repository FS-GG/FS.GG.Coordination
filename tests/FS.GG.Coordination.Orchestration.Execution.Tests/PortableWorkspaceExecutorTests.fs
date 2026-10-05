namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open Xunit

type private FakeRunner
    (run: PortableProcessRequest -> PortableProcessObservation, ?cleanup: PortableProcessRequest -> bool) =
    let mutable calls = 0
    let mutable cleanupCalls = 0
    let cleanup = defaultArg cleanup (fun _ -> true)

    member _.Calls = calls
    member _.CleanupCalls = cleanupCalls

    interface IPortableProcessRunner with
        member _.RunAsync(request, _) =
            calls <- calls + 1
            Task.FromResult(run request)

        member _.RecoverAsync(request, _) = Task.FromResult(run request)

        member _.CleanupAsync(request, _) =
            cleanupCalls <- cleanupCalls + 1
            Task.FromResult(cleanup request)

type private IsolatedFixture(root: string) =
    member _.Root = root

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                Directory.Delete(root, true)

module private PortableExecutorFixtures =
    let sourceRevision = "0123456789abcdef0123456789abcdef01234567"

    let image =
        "ghcr.io/fs-gg/polyglot@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

    let verificationSha256 =
        "123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0"

    let recipeSha256 =
        "23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef01"

    let now = DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)

    let fixtureComponent id language directory toolchain version build test =
        {
            Id = id
            Language = language
            WorkingDirectory = directory
            Toolchain = { Id = toolchain; Version = version }
            EntryPoints =
                {
                    Build = build
                    Test = test
                    Lint = None
                    Artifact = None
                }
        }

    let profile scope components =
        {
            ProfileId = "portable-executor-v1"
            Revision = 7UL
            WorkspaceScope = scope
            SourceRevision = sourceRevision
            QualifiedImage = image
            Components = components
            ProductBuild = "product-build"
            ProductTest = "product-test"
            ProductJourney = "frontend-backend-journey"
            MaximumRuntimeSeconds = 20UL
            MaximumOutputBytes = 65536UL
        }

    let authority scope =
        {
            WorkspaceScope = scope
            WorkflowRevision = 41UL
            FenceGeneration = 9UL
            ObservedAt = now.AddMinutes(-1.0)
        }

    let command (profile: PortableWorkspaceProfile) id operation componentId =
        {
            CommandId = id
            IdempotencyId = "delivery-" + id.ToString("N")
            WorkspaceScope = profile.WorkspaceScope
            ProfileId = profile.ProfileId
            ProfileRevision = profile.Revision
            SourceRevision = profile.SourceRevision
            ExpectedWorkflowRevision = 41UL
            FenceGeneration = 9UL
            CausationId = None
            Deadline = now.AddMinutes(2.0)
            Operation = operation
            ComponentId = componentId
        }

    let operation entry identity selectedComponent directory tools executable arguments verification =
        {
            EntryPoint = entry
            OperationIdentity = identity
            ComponentId = selectedComponent
            WorkingDirectory = directory
            QualifiedImage = image
            RequiredToolchains = tools
            Executable = "/usr/local/bin/" + executable
            Arguments = arguments
            VerificationIdentity = verification
            VerificationPath = verification + ".json"
            VerificationSha256 = verificationSha256
            RecipeSha256 = recipeSha256
        }

    let runtimePolicy () =
        {
            GitExecutable = "/usr/bin/git"
            TarExecutable = "/usr/bin/tar"
            PodmanExecutable = "/usr/bin/podman"
            PodmanGlobalArguments = []
            StateRoot = Path.Combine(Path.GetTempPath(), "fsgg-portable-state-" + Guid.NewGuid().ToString("N"))
            ContainerPath = "/usr/local/bin:/usr/bin:/bin"
            ContainerUser = "32768:32768"
            HostEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/local/bin:/usr/bin:/bin"; "LANG", "C.UTF-8" ]
            ContainerEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/local/bin:/usr/bin:/bin" ]
            MaximumSnapshotBytes = 16UL * 1024UL * 1024UL
            TerminationGrace = TimeSpan.FromSeconds 5.0
        }

    let successfulObservation =
        {
            ExecutionStarted = true
            ExitCode = Some 0
            StandardOutput = Array.empty
            StandardError = Array.empty
            CancellationRequested = false
            TerminationObserved = true
            Interrupted = false
            OutputLimitExceeded = false
            OutputComplete = true
            SourceTree = Some verificationSha256
            SnapshotSha256 = Some verificationSha256
            RuntimeIdentity = Some "podman-test"
            ContainerIdentity = Some "container-test"
            VerificationObserved = true
            VerificationOutput = Some Array.empty
            VerificationCustodyLimitExceeded = false
            Refusal = None
        }

    let rec fixtureRoot (directory: DirectoryInfo) =
        let candidate =
            Path.Combine(directory.FullName, "tests", "portable-workspace", "executor")

        if Directory.Exists candidate then
            candidate
        elif isNull directory.Parent then
            failwith "portable executor fixtures were not found"
        else
            fixtureRoot directory.Parent

    let root () =
        fixtureRoot (DirectoryInfo AppContext.BaseDirectory)

    let rec repositoryRoot (directory: DirectoryInfo) =
        let marker = Path.Combine(directory.FullName, ".git")

        if Directory.Exists marker || File.Exists marker then
            directory.FullName
        elif isNull directory.Parent then
            failwith "repository root was not found"
        else
            repositoryRoot directory.Parent

    let isolatedCopy () =
        let source = root ()

        let destination =
            Path.Combine(Path.GetTempPath(), "fsgg-portable-executor-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory destination |> ignore

        for path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(source, path)

            if
                not (
                    relative.Split(Path.DirectorySeparatorChar)
                    |> Array.exists (fun segment -> segment = "artifact" || segment = "__pycache__")
                )
            then
                let target = Path.Combine(destination, relative)
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                File.Copy(path, target)

        new IsolatedFixture(destination)

open PortableExecutorFixtures

type PortableWorkspaceExecutorTests() =
    [<Fact>]
    member _.``source binding selects fixed Python build and test operations``() =
        task {
            use fixture = isolatedCopy ()
            let workspace = fixture.Root
            let current = DateTimeOffset.UtcNow
            let liveNow = DateTimeOffset(current.Ticks - current.Ticks % 10L, TimeSpan.Zero)

            let python =
                fixtureComponent "app" "python" "python" "cpython" "3.14.0" "python-build" "python-test"

            let selectedProfile = profile "fs-gg/python-fixture" [ python ]

            let operations =
                [
                    operation
                        "python-build"
                        "build"
                        (Some "app")
                        "python"
                        [ "cpython", "3.14.0" ]
                        "python3"
                        [ "build.py" ]
                        "python-bytecode-v1"
                    operation
                        "python-test"
                        "test"
                        (Some "app")
                        "python"
                        [ "cpython", "3.14.0" ]
                        "python3"
                        [ "test.py" ]
                        "python-greeting-v1"
                ]

            let runtime = runtimePolicy ()

            let policy =
                {
                    WorkspaceRoot = workspace
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = 131072UL
                    Operations = operations
                    Runtime = runtime
                }

            let fake =
                FakeRunner(fun request ->
                    Assert.True(Path.IsPathFullyQualified request.Executable)
                    Assert.Equal(selectedProfile.SourceRevision, request.SourceRevision)
                    Assert.Equal(selectedProfile.QualifiedImage, request.QualifiedImage)
                    Assert.Equal(recipeSha256, request.RecipeSha256)
                    successfulObservation)

            let executor = PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> liveNow))

            for index, operationName in [ 1, "build"; 2, "test" ] do
                let command =
                    command
                        selectedProfile
                        (Guid.Parse($"00000000-0000-0000-0000-{index:D12}"))
                        operationName
                        (Some "app")
                    |> fun value ->
                        { value with
                            Deadline = liveNow.AddMinutes(2.0)
                        }

                let liveAuthority =
                    { authority selectedProfile.WorkspaceScope with
                        ObservedAt = liveNow.AddMinutes(-1.0)
                    }

                let! outcome =
                    executor.ExecuteAsync(liveAuthority, selectedProfile, command, CancellationToken.None)

                match outcome with
                | Completed receipt ->
                    Assert.Equal(EvidenceKnown 0, receipt.Result.ExitCode)
                    Assert.True(receipt.TerminationObserved)
                | other -> failwithf "expected completed Python operation, got %A" other
        }

    [<Fact>]
    member _.``source binding selects component checks and frontend to backend journey``() =
        task {
            use fixture = isolatedCopy ()
            let workspace = Path.Combine(fixture.Root, "composed")
            let current = DateTimeOffset.UtcNow
            let liveNow = DateTimeOffset(current.Ticks - current.Ticks % 10L, TimeSpan.Zero)

            let frontend =
                fixtureComponent "frontend" "typescript" "frontend" "node" "24.8.0" "frontend-build" "frontend-test"

            let backend =
                fixtureComponent "backend" "python" "backend" "cpython" "3.14.0" "backend-build" "backend-test"

            let selectedProfile = profile "fs-gg/composed-fixture" [ frontend; backend ]
            let tools = [ "node", "24.8.0"; "cpython", "3.14.0" ]

            let operations =
                [
                    operation
                        "frontend-build"
                        "build"
                        (Some "frontend")
                        "frontend"
                        [ tools[0] ]
                        "node"
                        [ "build.mjs" ]
                        "frontend-artifact-v1"
                    operation
                        "frontend-test"
                        "test"
                        (Some "frontend")
                        "frontend"
                        [ tools[0] ]
                        "node"
                        [ "test.mjs" ]
                        "frontend-unit-v1"
                    operation
                        "backend-build"
                        "build"
                        (Some "backend")
                        "backend"
                        [ tools[1] ]
                        "python3"
                        [ "build.py" ]
                        "backend-bytecode-v1"
                    operation
                        "backend-test"
                        "test"
                        (Some "backend")
                        "backend"
                        [ tools[1] ]
                        "python3"
                        [ "test.py" ]
                        "backend-unit-v1"
                    operation
                        "frontend-backend-journey"
                        "journey"
                        None
                        "product"
                        tools
                        "python3"
                        [ "journey.py" ]
                        "frontend-backend-http-v1"
                ]

            let policy =
                {
                    WorkspaceRoot = workspace
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = 131072UL
                    Operations = operations
                    Runtime = runtimePolicy ()
                }

            let fake =
                FakeRunner(fun request ->
                    Assert.True(Path.IsPathFullyQualified request.Executable)
                    Assert.Equal(selectedProfile.SourceRevision, request.SourceRevision)
                    Assert.Equal(selectedProfile.QualifiedImage, request.QualifiedImage)
                    Assert.Equal(recipeSha256, request.RecipeSha256)
                    successfulObservation)

            let executor = PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> liveNow))

            let cases =
                [
                    "build", Some "frontend"
                    "test", Some "frontend"
                    "build", Some "backend"
                    "test", Some "backend"
                    "journey", None
                ]

            for index, (operationName, componentId) in List.indexed cases do
                let command =
                    command
                        selectedProfile
                        (Guid.Parse($"10000000-0000-0000-0000-{index + 1:D12}"))
                        operationName
                        componentId
                    |> fun value ->
                        { value with
                            Deadline = liveNow.AddMinutes(2.0)
                        }

                let liveAuthority =
                    { authority selectedProfile.WorkspaceScope with
                        ObservedAt = liveNow.AddMinutes(-1.0)
                    }

                let! outcome =
                    executor.ExecuteAsync(liveAuthority, selectedProfile, command, CancellationToken.None)

                match outcome with
                | Completed receipt ->
                    Assert.Equal(EvidenceKnown 0, receipt.Result.ExitCode)
                    Assert.Equal(EvidenceKnown receipt.VerificationIdentity, receipt.Result.ArtifactReference)
                | other -> failwithf "expected completed composed operation, got %A" other
        }

    [<Fact>]
    member _.``refusals happen before launch and duplicate delivery reuses exact receipt``() =
        task {
            let selectedProfile =
                profile
                    "fs-gg/refusal-fixture"
                    [
                        fixtureComponent "app" "python" "python" "cpython" "3.14.0" "python-build" "python-test"
                    ]

            let fake =
                FakeRunner(fun _ ->
                    {
                        ExecutionStarted = true
                        ExitCode = Some 0
                        StandardOutput = [| 1uy |]
                        StandardError = Array.empty
                        CancellationRequested = false
                        TerminationObserved = true
                        Interrupted = false
                        OutputLimitExceeded = false
                        OutputComplete = true
                        SourceTree = Some verificationSha256
                        SnapshotSha256 = Some verificationSha256
                        RuntimeIdentity = Some "podman-test"
                        ContainerIdentity = Some "container-test"
                        VerificationObserved = true
                        VerificationOutput = Some Array.empty
                        VerificationCustodyLimitExceeded = false
                        Refusal = None
                    })

            let reviewed =
                operation
                    "python-build"
                    "build"
                    (Some "app")
                    "python"
                    [ "cpython", "3.14.0" ]
                    "python3"
                    [ "build.py" ]
                    "python-bytecode-v1"

            let runtime = runtimePolicy ()

            let policy =
                {
                    WorkspaceRoot = root ()
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = 131072UL
                    Operations = [ reviewed ]
                    Runtime = runtime
                }

            let executor = PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> now))

            let valid =
                command selectedProfile (Guid.Parse "20000000-0000-0000-0000-000000000001") "build" (Some "app")

            let! first =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    valid,
                    CancellationToken.None
                )

            let! duplicate =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    valid,
                    CancellationToken.None
                )

            Assert.True(
                match first, duplicate with
                | Completed a, Duplicate b -> a = b
                | _ -> false
            )

            Assert.Equal(1, fake.Calls)

            let! duplicateAfterAuthorityMoved =
                executor.ExecuteAsync(
                    { authority selectedProfile.WorkspaceScope with
                        FenceGeneration = 99UL
                    },
                    selectedProfile,
                    valid,
                    CancellationToken.None
                )

            Assert.True(
                match duplicateAfterAuthorityMoved with
                | Duplicate _ -> true
                | _ -> false
            )

            let expiredExecutor =
                PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> valid.Deadline.AddMinutes(1.0)))

            let! duplicateAfterExpiry =
                expiredExecutor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    valid,
                    CancellationToken.None
                )

            Assert.True(
                match duplicateAfterExpiry with
                | Duplicate _ -> true
                | _ -> false
            )

            let changedBinding =
                { valid with
                    CommandId = Guid.Parse "20000000-0000-0000-0000-000000000099"
                }

            let! conflict =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    changedBinding,
                    CancellationToken.None
                )

            Assert.Equal(Refused "portable-executor-idempotency-conflict", conflict)

            let adverse =
                [
                    authority "fs-gg/foreign",
                    selectedProfile,
                    { valid with
                        IdempotencyId = "foreign-new"
                    }
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    { valid with
                        FenceGeneration = 10UL
                        IdempotencyId = "stale"
                    }
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    { valid with
                        Operation = "python3-build.py"
                        IdempotencyId = "injected"
                    }
                    authority selectedProfile.WorkspaceScope,
                    { selectedProfile with
                        Components =
                            [
                                { selectedProfile.Components.Head with
                                    Toolchain = { Id = "pypy"; Version = "7.3.19" }
                                }
                            ]
                    },
                    { valid with
                        IdempotencyId = "unsupported"
                    }
                ]

            for adverseAuthority, adverseProfile, adverseCommand in adverse do
                let! refused =
                    executor.ExecuteAsync(adverseAuthority, adverseProfile, adverseCommand, CancellationToken.None)

                Assert.True(
                    match refused with
                    | Refused _ -> true
                    | _ -> false
                )

            Assert.Equal(1, fake.Calls)

            let journalPath =
                Directory.GetFiles(Path.Combine(runtime.StateRoot, "journal-v1"), "*.bin")
                |> Array.exactlyOne

            File.WriteAllText(journalPath, "corrupt")

            let! corrupted =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    valid,
                    CancellationToken.None
                )

            Assert.Equal(Refused "portable-journal-unavailable", corrupted)
            Assert.Equal(1, fake.Calls)
        }

    [<Fact>]
    member _.``cancellation termination and interrupted reconciliation remain distinct``() =
        task {
            let selectedProfile =
                profile
                    "fs-gg/recovery-fixture"
                    [
                        fixtureComponent "app" "python" "python" "cpython" "3.14.0" "python-build" "python-test"
                    ]

            let mutable interrupted = true

            let fake =
                FakeRunner(fun _ ->
                    if interrupted then
                        {
                            ExecutionStarted = true
                            ExitCode = None
                            StandardOutput = Array.empty
                            StandardError = Array.empty
                            CancellationRequested = true
                            TerminationObserved = false
                            Interrupted = true
                            OutputLimitExceeded = true
                            OutputComplete = false
                            SourceTree = Some verificationSha256
                            SnapshotSha256 = Some verificationSha256
                            RuntimeIdentity = Some "podman-test"
                            ContainerIdentity = Some "container-test"
                            VerificationObserved = false
                            VerificationOutput = None
                            VerificationCustodyLimitExceeded = false
                            Refusal = None
                        }
                    else
                        {
                            ExecutionStarted = true
                            ExitCode = Some 137
                            StandardOutput = Array.empty
                            StandardError = Array.empty
                            CancellationRequested = true
                            TerminationObserved = true
                            Interrupted = false
                            OutputLimitExceeded = false
                            OutputComplete = true
                            SourceTree = Some verificationSha256
                            SnapshotSha256 = Some verificationSha256
                            RuntimeIdentity = Some "podman-test"
                            ContainerIdentity = Some "container-test"
                            VerificationObserved = false
                            VerificationOutput = None
                            VerificationCustodyLimitExceeded = false
                            Refusal = None
                        })

            let reviewed =
                operation
                    "python-build"
                    "build"
                    (Some "app")
                    "python"
                    [ "cpython", "3.14.0" ]
                    "python3"
                    [ "build.py" ]
                    "python-bytecode-v1"

            let policy =
                {
                    WorkspaceRoot = root ()
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = 131072UL
                    Operations = [ reviewed ]
                    Runtime = runtimePolicy ()
                }

            let executor = PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> now))

            let unknownCommand =
                command selectedProfile (Guid.Parse "30000000-0000-0000-0000-000000000001") "build" (Some "app")

            let! unknown =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    unknownCommand,
                    CancellationToken.None
                )

            match unknown with
            | Completed receipt ->
                Assert.Equal(EvidenceUnknown "process-termination-not-observed", receipt.Result.ExitCode)
                Assert.True(receipt.CancellationRequested)
                Assert.False(receipt.TerminationObserved)
            | other -> failwithf "expected unknown interrupted receipt, got %A" other

            let restarted = PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> now))

            let! pending =
                restarted.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    unknownCommand,
                    CancellationToken.None
                )

            Assert.Equal(PendingDuplicate unknownCommand.CommandId, pending)

            interrupted <- false


            let! reconciled =
                restarted.RecoverAsync(selectedProfile, unknownCommand, CancellationToken.None)

            match reconciled with
            | Completed receipt ->
                Assert.Equal(EvidenceKnown 137, receipt.Result.ExitCode)
                Assert.True(receipt.TerminationObserved)
            | other -> failwithf "expected recovered termination receipt, got %A" other

            let! recoveredDuplicate =
                restarted.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    unknownCommand,
                    CancellationToken.None
                )

            Assert.True(
                match recoveredDuplicate with
                | Duplicate receipt -> receipt.Result.ExitCode = EvidenceKnown 137
                | _ -> false
            )

            let cancelledCommand =
                command selectedProfile (Guid.Parse "30000000-0000-0000-0000-000000000002") "build" (Some "app")

            let! cancelled =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    cancelledCommand,
                    CancellationToken.None
                )

            match cancelled with
            | Completed receipt ->
                Assert.Equal("execution-cancelled", receipt.Result.Error.Value.Code)
                Assert.True(receipt.CancellationRequested)
                Assert.True(receipt.TerminationObserved)
            | other -> failwithf "expected observed cancellation receipt, got %A" other
        }

    [<Fact>]
    member _.``proven pre-start refusal settles and remains duplicate after reconstruction``() =
        task {
            let selectedProfile =
                profile
                    "fs-gg/prestart-refusal"
                    [
                        fixtureComponent "app" "python" "python" "cpython" "3.14.0" "python-build" "python-test"
                    ]

            let mutable cleanupAllowed = false
            let helperStderr = "image not known in the selected store\n" + String.replicate 5000 "x"

            let fake =
                FakeRunner(
                    (fun _ ->
                        {
                            ExecutionStarted = false
                            ExitCode = Some 125
                            StandardOutput = Array.empty
                            StandardError = Encoding.UTF8.GetBytes helperStderr
                            CancellationRequested = false
                            TerminationObserved = false
                            Interrupted = true
                            OutputLimitExceeded = false
                            OutputComplete = true
                            SourceTree = Some verificationSha256
                            SnapshotSha256 = Some verificationSha256
                            RuntimeIdentity = Some "podman-test"
                            ContainerIdentity = None
                            VerificationObserved = false
                            VerificationOutput = None
                            VerificationCustodyLimitExceeded = false
                            Refusal =
                                Some
                                    {
                                        Stage = PortableProcessRefusalStage.ImageInspection
                                        Reason = "portable-image-inspect-refused"
                                        HelperTimedOut = false
                                        HelperOutputComplete = true
                                        HelperReadStatus = "RanToCompletion/RanToCompletion/RanToCompletion"
                                        HelperReadFailure = None
                                    }
                        }),
                    cleanup = (fun _ -> cleanupAllowed)
                )

            let reviewed =
                operation
                    "python-build"
                    "build"
                    (Some "app")
                    "python"
                    [ "cpython", "3.14.0" ]
                    "python3"
                    [ "build.py" ]
                    "python-build-v1"

            let policy =
                {
                    WorkspaceRoot = root ()
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = 131072UL
                    Operations = [ reviewed ]
                    Runtime = runtimePolicy ()
                }

            let selectedCommand =
                command selectedProfile (Guid.Parse "35000000-0000-0000-0000-000000000001") "build" (Some "app")

            let firstExecutor =
                PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> now))

            let! first =
                firstExecutor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    selectedCommand,
                    CancellationToken.None
                )

            match first with
            | Completed receipt ->
                Assert.False(receipt.ExecutionStarted)
                Assert.False(receipt.CleanupCompleted)
                Assert.Equal("execution-refused-before-start", receipt.Result.Error.Value.Code)
                Assert.Equal("ImageInspection", receipt.Result.Error.Value.Details["refusalStage"])
                Assert.Equal("portable-image-inspect-refused", receipt.Result.Error.Value.Details["refusalReason"])
                Assert.Equal("125", receipt.Result.Error.Value.Details["helperExitCode"])
                Assert.Equal("False", receipt.Result.Error.Value.Details["helperTimedOut"])
                Assert.Equal("True", receipt.Result.Error.Value.Details["helperOutputComplete"])
                Assert.StartsWith("image not known in the selected store", receipt.Result.Error.Value.Details["helperStderr"])
                Assert.Equal(512, Encoding.UTF8.GetByteCount receipt.Result.Error.Value.Details["helperStderr"])
                Assert.Equal("True", receipt.Result.Error.Value.Details["helperStderrTruncated"])
            | other -> failwithf "expected durable pre-start refusal, got %A" other

            cleanupAllowed <- true

            let restarted =
                PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> selectedCommand.Deadline.AddHours 1.0))

            let! duplicate =
                restarted.ExecuteAsync(
                    { authority selectedProfile.WorkspaceScope with
                        FenceGeneration = UInt64.MaxValue
                    },
                    selectedProfile,
                    selectedCommand,
                    CancellationToken.None
                )

            match duplicate with
            | Duplicate receipt ->
                Assert.False(receipt.ExecutionStarted)
                Assert.True(receipt.CleanupCompleted)
                Assert.Equal("execution-refused-before-start", receipt.Result.Error.Value.Code)
                Assert.Equal("ImageInspection", receipt.Result.Error.Value.Details["refusalStage"])
                Assert.Equal("True", receipt.Result.Error.Value.Details["helperStderrTruncated"])
            | other -> failwithf "expected reconstructed duplicate refusal, got %A" other

            Assert.Equal(1, fake.Calls)
            Assert.Equal(2, fake.CleanupCalls)
        }

    [<Fact>]
    member _.``component operation cannot borrow a sibling toolchain``() =
        task {
            let frontend =
                fixtureComponent
                    "frontend"
                    "typescript"
                    "composed/frontend"
                    "node"
                    "24.8.0"
                    "frontend-build"
                    "frontend-test"

            let backend =
                fixtureComponent "backend" "python" "composed/backend" "cpython" "3.14.0" "backend-build" "backend-test"

            let selectedProfile = profile "fs-gg/component-toolchain" [ frontend; backend ]

            let fake =
                FakeRunner(fun _ -> failwith "a mismatched component toolchain must refuse before launch")

            let borrowed =
                operation
                    "frontend-build"
                    "build"
                    (Some "frontend")
                    "composed/frontend"
                    [ "cpython", "3.14.0" ]
                    "node"
                    [ "build.mjs" ]
                    "frontend-build-v1"

            let policy =
                {
                    WorkspaceRoot = root ()
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = 131072UL
                    Operations = [ borrowed ]
                    Runtime = runtimePolicy ()
                }

            let executor = PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> now))

            let selectedCommand =
                command selectedProfile (Guid.Parse "40000000-0000-0000-0000-000000000001") "build" (Some "frontend")

            let! outcome =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    selectedCommand,
                    CancellationToken.None
                )

            Assert.Equal(Refused "portable-executor-toolchain-refused", outcome)
            Assert.Equal(0, fake.Calls)
        }

    [<Fact>]
    member _.``verification custody bound roundtrips and oversized evidence stays truthful``() =
        task {
            let selectedProfile =
                { profile
                      "fs-gg/verification-custody"
                      [
                          fixtureComponent "app" "python" "python" "cpython" "3.14.0" "python-build" "python-test"
                      ] with
                    MaximumOutputBytes = 512UL * 1024UL
                }

            let retained = Array.zeroCreate<byte> PortableWorkspaceContract.maximumDocumentBytes

            let fake =
                FakeRunner(fun request ->
                    if request.Arguments = [ "build.py" ] then
                        { successfulObservation with
                            VerificationOutput = Some retained
                        }
                    else
                        { successfulObservation with
                            VerificationObserved = false
                            VerificationOutput = None
                            VerificationCustodyLimitExceeded = true
                            Refusal = None
                        })

            let reviewed =
                [
                    operation
                        "python-build"
                        "build"
                        (Some "app")
                        "python"
                        [ "cpython", "3.14.0" ]
                        "python3"
                        [ "build.py" ]
                        "python-build-v1"
                    operation
                        "python-test"
                        "test"
                        (Some "app")
                        "python"
                        [ "cpython", "3.14.0" ]
                        "python3"
                        [ "test.py" ]
                        "python-test-v1"
                ]

            let policy =
                {
                    WorkspaceRoot = root ()
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = selectedProfile.MaximumOutputBytes
                    Operations = reviewed
                    Runtime = runtimePolicy ()
                }

            let buildCommand =
                command selectedProfile (Guid.Parse "36000000-0000-0000-0000-000000000001") "build" (Some "app")

            let first = PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> now))

            let! completed =
                first.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    buildCommand,
                    CancellationToken.None
                )

            match completed with
            | Completed receipt ->
                Assert.Equal(retained.Length, receipt.VerificationOutput.Value.Length)
                Assert.True(receipt.CleanupCompleted)
            | other -> failwithf "expected retained verification receipt, got %A" other

            let reconstructed =
                PortableWorkspaceExecutor.Executor(policy, fake, (fun () -> now))

            let! duplicate =
                reconstructed.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    buildCommand,
                    CancellationToken.None
                )

            match duplicate with
            | Duplicate receipt -> Assert.Equal(retained, receipt.VerificationOutput.Value)
            | other -> failwithf "expected retained verification duplicate, got %A" other

            let testCommand =
                command selectedProfile (Guid.Parse "36000000-0000-0000-0000-000000000002") "test" (Some "app")

            let! oversized =
                first.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    testCommand,
                    CancellationToken.None
                )

            match oversized with
            | Completed receipt ->
                Assert.Equal("execution-verification-custody-limit", receipt.Result.Error.Value.Code)
                Assert.True(receipt.CleanupCompleted)
                Assert.True(receipt.VerificationOutput.IsNone)
            | other -> failwithf "expected truthful custody refusal, got %A" other

            let! oversizedDuplicate =
                reconstructed.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    testCommand,
                    CancellationToken.None
                )

            Assert.True(
                match oversizedDuplicate with
                | Duplicate receipt -> receipt.Result.Error.Value.Code = "execution-verification-custody-limit"
                | _ -> false
            )

            Assert.Equal(2, fake.Calls)
        }

    [<Fact>]
    member _.``maximum runtime arithmetic saturates and a deadline crossed before launch refuses``() =
        task {
            let selectedProfile =
                { profile
                      "fs-gg/runtime-bound"
                      [
                          fixtureComponent "app" "python" "python" "cpython" "3.14.0" "python-build" "python-test"
                      ] with
                    MaximumRuntimeSeconds = UInt64.MaxValue
                }

            let fake =
                FakeRunner(fun _ ->
                    {
                        ExecutionStarted = true
                        ExitCode = Some 0
                        StandardOutput = Array.empty
                        StandardError = Array.empty
                        CancellationRequested = false
                        TerminationObserved = true
                        Interrupted = false
                        OutputLimitExceeded = false
                        OutputComplete = true
                        SourceTree = Some verificationSha256
                        SnapshotSha256 = Some verificationSha256
                        RuntimeIdentity = Some "podman-test"
                        ContainerIdentity = Some "container-test"
                        VerificationObserved = true
                        VerificationOutput = Some Array.empty
                        VerificationCustodyLimitExceeded = false
                        Refusal = None
                    })

            let reviewed =
                operation
                    "python-build"
                    "build"
                    (Some "app")
                    "python"
                    [ "cpython", "3.14.0" ]
                    "python3"
                    [ "build.py" ]
                    "python-build-v1"

            let policy =
                {
                    WorkspaceRoot = root ()
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = UInt64.MaxValue
                    MaximumOutputBytes = 131072UL
                    Operations = [ reviewed ]
                    Runtime = runtimePolicy ()
                }

            let deadline = now.AddMinutes(1.0)
            let mutable calls = 0

            let tickingClock () =
                calls <- calls + 1
                if calls = 1 then now else deadline

            let executor = PortableWorkspaceExecutor.Executor(policy, fake, tickingClock)

            let selectedCommand =
                { command selectedProfile (Guid.Parse "40000000-0000-0000-0000-000000000002") "build" (Some "app") with
                    Deadline = deadline
                }

            let! outcome =
                executor.ExecuteAsync(
                    authority selectedProfile.WorkspaceScope,
                    selectedProfile,
                    selectedCommand,
                    CancellationToken.None
                )

            Assert.Equal(Refused "portable-executor-deadline-refused", outcome)
            Assert.Equal(0, fake.Calls)
        }

    [<Fact>]
    member _.``podman runner snapshots Git objects and emits only the fixed isolated create shape``() =
        task {
            let repositorySource = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)

            let temporary =
                Path.Combine(Path.GetTempPath(), "fsgg-podman-runner-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory temporary |> ignore
            let repository = Path.Combine(temporary, "clean-checkout")

            try
                let clone =
                    Diagnostics.ProcessStartInfo(
                        "/usr/bin/git",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    )

                for argument in [ "clone"; "--quiet"; "--no-local"; repositorySource; repository ] do
                    clone.ArgumentList.Add argument

                use cloneProcess = Diagnostics.Process.Start clone
                let cloneError = cloneProcess.StandardError.ReadToEnd()
                cloneProcess.WaitForExit()

                if cloneProcess.ExitCode <> 0 then
                    failwithf "clean Git checkout failed: %s" cloneError

                let script = Path.Combine(temporary, "podman-shim.py")

                File.WriteAllText(
                    script,
                    """#!/usr/bin/python3
import json, os, pathlib, sys
pathlib.Path("host-env.json").write_text(json.dumps(dict(os.environ)))
if any(name in os.environ for name in ("NODE_OPTIONS", "PYTHONPATH", "LD_PRELOAD", "HTTP_PROXY", "HTTPS_PROXY")):
    raise SystemExit(91)
args = sys.argv[1:]
if args[:2] == ["version", "--format"]:
    print("6.1.2")
elif args[:2] == ["image", "inspect"]:
    user = "65534:65534" if pathlib.Path(sys.argv[0]).name.startswith("bad-user") else "32768:32768"
    image_id = "0123456789abcdef" * 4
    if pathlib.Path(sys.argv[0]).name.startswith("invalid-uppercase"):
        image_id = image_id.upper()
    elif pathlib.Path(sys.argv[0]).name.startswith("invalid-short"):
        image_id = image_id[:-1]
    elif pathlib.Path(sys.argv[0]).name.startswith("invalid-nonhex"):
        image_id = "g" * 64
    elif not pathlib.Path(sys.argv[0]).name.startswith("raw-id"):
        image_id = "sha256:" + image_id
    print("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef|" + image_id + "|[\"HOME=/tmp\",\"PATH=/usr/local/bin:/usr/bin:/bin\"]|null|null|null|" + user)
elif args[:2] == ["unshare", "chown"]:
    with pathlib.Path("chown-args.jsonl").open("a") as stream:
        stream.write(json.dumps(args) + "\n")
    if "--recursive" in args and pathlib.Path(sys.argv[0]).name.startswith("reclaim-refused"):
        print("ownership handback refused", file=sys.stderr)
        raise SystemExit(93)
    if "--recursive" in args:
        pathlib.Path(args[-1], "generated").chmod(0o755)
elif args and args[0] == "create":
    pathlib.Path("create-args.json").write_text(json.dumps(args))
    volumes = [args[index + 1] for index, value in enumerate(args) if value == "--volume"]
    output = pathlib.Path([value for value in volumes if value.endswith(":/output:rw")][0].split(":/output:rw")[0])
    output.joinpath("verified.txt").write_bytes(b"verified")
    output.joinpath("generated").mkdir()
    output.joinpath("generated", "artifact.txt").write_bytes(b"container-owned")
    executable_name = pathlib.Path(sys.argv[0]).name
    if executable_name == "podman-shim.py" or executable_name.startswith("reclaim-refused"):
        output.joinpath("generated").chmod(0)
    print("container-id")
elif args[:2] == ["container", "inspect"] and ".Image" in args[-2]:
    print("sha256:" + "0123456789abcdef" * 4 + "|container-id|32768:32768")
elif args and args[0] == "start":
    print("isolated-operation-output")
elif args[:2] == ["container", "inspect"] and ".State.Status" in args[-2]:
    print("exited|0")
elif args and args[0] in ("stop", "kill"):
    pass
elif args[:2] == ["container", "rm"]:
    pass
else:
    print("unexpected:" + repr(args), file=sys.stderr)
    raise SystemExit(92)
"""
                )

                if not (OperatingSystem.IsWindows()) then
                    File.SetUnixFileMode(
                        script,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                let stateRoot = Path.Combine(temporary, "state")

                let head =
                    let start =
                        Diagnostics.ProcessStartInfo(
                            "/usr/bin/git",
                            WorkingDirectory = repository,
                            RedirectStandardOutput = true,
                            UseShellExecute = false
                        )

                    start.ArgumentList.Add "rev-parse"
                    start.ArgumentList.Add "HEAD"
                    use child = Diagnostics.Process.Start start
                    let value = child.StandardOutput.ReadToEnd().Trim()
                    child.WaitForExit()
                    value

                let runtime =
                    { runtimePolicy () with
                        PodmanExecutable = script
                        StateRoot = stateRoot
                        MaximumSnapshotBytes = 512UL * 1024UL * 1024UL
                    }

                let expectedVerification =
                    Encoding.UTF8.GetBytes "verified"
                    |> Security.Cryptography.SHA256.HashData
                    |> Convert.ToHexString
                    |> _.ToLowerInvariant()

                let request =
                    {
                        Executable = "/usr/local/bin/python3"
                        Arguments = [ "test.py" ]
                        WorkingDirectory = "tests/portable-workspace/executor/python"
                        Deadline = DateTimeOffset.UtcNow.AddMinutes 2.0
                        MaximumOutputBytes = 1024UL * 1024UL
                        WorkspaceScope = "fs-gg/podman-shape"
                        SourceRepository = repository
                        SourceRevision = head
                        QualifiedImage = image
                        ContainerName = "fsgg-portable-shape-test"
                        ContainerPath = runtime.ContainerPath
                        ContainerEnvironment = runtime.ContainerEnvironment
                        VerificationPath = "verified.txt"
                        VerificationSha256 = expectedVerification
                        RecipeSha256 = recipeSha256
                    }

                try
                    let runner = PortableWorkspacePodmanRunner runtime :> IPortableProcessRunner
                    let! observed = runner.RunAsync(request, CancellationToken.None)
                    Assert.True(observed.TerminationObserved)
                    Assert.True(observed.VerificationObserved)
                    Assert.True(observed.SourceTree.IsSome)
                    Assert.True(observed.SnapshotSha256.IsSome)

                    let createArguments = File.ReadAllText(Path.Combine(stateRoot, "create-args.json"))

                    Assert.Contains("--pull=never", createArguments)
                    Assert.Contains("--read-only", createArguments)
                    Assert.Contains("--network=none", createArguments)
                    Assert.Contains("--cap-drop=all", createArguments)
                    Assert.Contains("--user", createArguments)
                    Assert.Contains("32768:32768", createArguments)
                    Assert.DoesNotContain("--pid=host", createArguments)
                    Assert.DoesNotContain("--uts=host", createArguments)
                    Assert.DoesNotContain("--userns", createArguments)
                    Assert.DoesNotContain("NODE_OPTIONS", createArguments)

                    let hostEnvironment = File.ReadAllText(Path.Combine(stateRoot, "host-env.json"))
                    Assert.DoesNotContain("NODE_OPTIONS", hostEnvironment)
                    Assert.DoesNotContain("PYTHONPATH", hostEnvironment)
                    Assert.DoesNotContain("HTTP_PROXY", hostEnvironment)

                    let! cleaned = runner.CleanupAsync(request, CancellationToken.None)
                    Assert.True(cleaned)

                    let ownershipCommands =
                        File.ReadAllLines(Path.Combine(stateRoot, "chown-args.jsonl"))

                    Assert.Equal(2, ownershipCommands.Length)
                    Assert.DoesNotContain("--recursive", ownershipCommands[0])
                    Assert.Contains("--recursive", ownershipCommands[1])
                    Assert.Contains("--no-dereference", ownershipCommands[1])
                    Assert.Contains("0:0", ownershipCommands[1])

                    Assert.False(Directory.Exists(Path.Combine(stateRoot, "executions", request.ContainerName)))

                    let reclaimRefusedScript = Path.Combine(temporary, "reclaim-refused-podman-shim.py")
                    File.Copy(script, reclaimRefusedScript)

                    File.SetUnixFileMode(
                        reclaimRefusedScript,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                    let reclaimRefusedStateRoot = Path.Combine(temporary, "reclaim-refused-state")

                    let reclaimRefusedRuntime =
                        { runtime with
                            PodmanExecutable = reclaimRefusedScript
                            StateRoot = reclaimRefusedStateRoot
                        }

                    let reclaimRefusedRequest =
                        { request with
                            ContainerName = "fsgg-portable-reclaim-refused-test"
                        }

                    let reclaimRefusedRunner =
                        PortableWorkspacePodmanRunner reclaimRefusedRuntime :> IPortableProcessRunner

                    let! reclaimRefusedObserved =
                        reclaimRefusedRunner.RunAsync(reclaimRefusedRequest, CancellationToken.None)

                    Assert.True(reclaimRefusedObserved.TerminationObserved)

                    let! reclaimRefusedCleanup =
                        reclaimRefusedRunner.CleanupAsync(reclaimRefusedRequest, CancellationToken.None)

                    Assert.False(reclaimRefusedCleanup)

                    Assert.True(
                        Directory.Exists(
                            Path.Combine(
                                reclaimRefusedStateRoot,
                                "executions",
                                reclaimRefusedRequest.ContainerName
                            )
                        )
                    )

                    File.SetUnixFileMode(
                        Path.Combine(
                            reclaimRefusedStateRoot,
                            "executions",
                            reclaimRefusedRequest.ContainerName,
                            "output",
                            "generated"
                        ),
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                    let rawIdScript = Path.Combine(temporary, "raw-id-podman-shim.py")
                    File.Copy(script, rawIdScript)

                    File.SetUnixFileMode(
                        rawIdScript,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                    let rawIdRuntime =
                        { runtime with
                            PodmanExecutable = rawIdScript
                            StateRoot = Path.Combine(temporary, "raw-id-state")
                        }

                    let rawIdRequest =
                        { request with
                            ContainerName = "fsgg-portable-raw-id-test"
                        }

                    let rawIdRunner = PortableWorkspacePodmanRunner rawIdRuntime :> IPortableProcessRunner
                    let! rawIdObserved = rawIdRunner.RunAsync(rawIdRequest, CancellationToken.None)
                    Assert.True(rawIdObserved.TerminationObserved)
                    Assert.True(rawIdObserved.VerificationObserved)

                    for invalidId in [ "invalid-uppercase"; "invalid-short"; "invalid-nonhex" ] do
                        let invalidIdScript = Path.Combine(temporary, invalidId + "-podman-shim.py")
                        File.Copy(script, invalidIdScript)

                        File.SetUnixFileMode(
                            invalidIdScript,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                        )

                        let invalidIdStateRoot = Path.Combine(temporary, invalidId + "-state")

                        let invalidIdRuntime =
                            { runtime with
                                PodmanExecutable = invalidIdScript
                                StateRoot = invalidIdStateRoot
                            }

                        let invalidIdRequest =
                            { request with
                                ContainerName = "fsgg-portable-" + invalidId + "-test"
                            }

                        let invalidIdRunner =
                            PortableWorkspacePodmanRunner invalidIdRuntime :> IPortableProcessRunner

                        let! invalidIdObserved = invalidIdRunner.RunAsync(invalidIdRequest, CancellationToken.None)
                        Assert.False(invalidIdObserved.ExecutionStarted)
                        Assert.Equal(PortableProcessRefusalStage.ImageIdentity, invalidIdObserved.Refusal.Value.Stage)
                        Assert.Equal("portable-image-identity-refused", invalidIdObserved.Refusal.Value.Reason)
                        Assert.False(File.Exists(Path.Combine(invalidIdStateRoot, "create-args.json")))

                    let missingCommitStateRoot = Path.Combine(temporary, "missing-commit-state")

                    let missingCommitRuntime =
                        { runtime with
                            StateRoot = missingCommitStateRoot
                        }

                    let missingCommitRequest =
                        { request with
                            SourceRevision = String.replicate 40 "f"
                            ContainerName = "fsgg-portable-missing-commit-test"
                        }

                    let missingCommitRunner =
                        PortableWorkspacePodmanRunner missingCommitRuntime :> IPortableProcessRunner

                    let! missingCommitObserved =
                        missingCommitRunner.RunAsync(missingCommitRequest, CancellationToken.None)

                    Assert.False(missingCommitObserved.ExecutionStarted)
                    Assert.Equal(Some 128, missingCommitObserved.ExitCode)
                    Assert.Equal(PortableProcessRefusalStage.SourceSnapshot, missingCommitObserved.Refusal.Value.Stage)
                    Assert.Equal("portable-source-commit-refused", missingCommitObserved.Refusal.Value.Reason)
                    Assert.True(missingCommitObserved.Refusal.Value.HelperOutputComplete)
                    Assert.NotEmpty(missingCommitObserved.StandardError)
                    Assert.False(File.Exists(Path.Combine(missingCommitStateRoot, "create-args.json")))

                    let badUserScript = Path.Combine(temporary, "bad-user-podman-shim.py")
                    File.Copy(script, badUserScript)

                    File.SetUnixFileMode(
                        badUserScript,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                    let badStateRoot = Path.Combine(temporary, "bad-user-state")

                    let badRuntime =
                        { runtime with
                            PodmanExecutable = badUserScript
                            StateRoot = badStateRoot
                        }

                    let badRequest =
                        { request with
                            ContainerName = "fsgg-portable-bad-user-test"
                        }

                    let badRunner = PortableWorkspacePodmanRunner badRuntime :> IPortableProcessRunner
                    let! refusedUser = badRunner.RunAsync(badRequest, CancellationToken.None)
                    Assert.False(refusedUser.ExecutionStarted)
                    Assert.Equal(PortableProcessRefusalStage.ImageIdentity, refusedUser.Refusal.Value.Stage)
                    Assert.Equal("portable-image-identity-refused", refusedUser.Refusal.Value.Reason)

                    Assert.False(File.Exists(Path.Combine(badStateRoot, "create-args.json")))

                    let! badCleaned = badRunner.CleanupAsync(badRequest, CancellationToken.None)
                    Assert.True(badCleaned)
                finally
                    ()
            finally
                if Directory.Exists temporary then
                    Directory.Delete(temporary, true)
        }

    [<Fact>]
    member _.``podman runner refuses a Git tree containing a symbolic link before runtime launch``() =
        task {
            if not (OperatingSystem.IsWindows()) then
                let temporary =
                    Path.Combine(Path.GetTempPath(), "fsgg-portable-link-" + Guid.NewGuid().ToString("N"))

                let repository = Path.Combine(temporary, "repository")
                Directory.CreateDirectory repository |> ignore

                let runGit arguments =
                    let start =
                        Diagnostics.ProcessStartInfo(
                            "/usr/bin/git",
                            WorkingDirectory = repository,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        )

                    for argument in arguments do
                        start.ArgumentList.Add argument

                    use child = Diagnostics.Process.Start start
                    let output = child.StandardOutput.ReadToEnd().Trim()
                    let error = child.StandardError.ReadToEnd()
                    child.WaitForExit()

                    if child.ExitCode <> 0 then
                        failwithf "git failed: %s" error

                    output

                try
                    runGit [ "init"; "--quiet" ] |> ignore
                    runGit [ "config"; "user.email"; "portable@example.invalid" ] |> ignore
                    runGit [ "config"; "user.name"; "Portable Test" ] |> ignore
                    File.WriteAllText(Path.Combine(repository, "app.py"), "print('fixed')\n")

                    File.CreateSymbolicLink(Path.Combine(repository, "unreviewed.py"), "/etc/passwd")
                    |> ignore

                    runGit [ "add"; "--all" ] |> ignore
                    runGit [ "commit"; "--quiet"; "-m"; "fixture" ] |> ignore
                    let head = runGit [ "rev-parse"; "HEAD" ]

                    let runtime =
                        { runtimePolicy () with
                            PodmanExecutable = "/usr/bin/false"
                            StateRoot = Path.Combine(temporary, "state")
                        }

                    let request =
                        {
                            Executable = "/usr/local/bin/python3"
                            Arguments = [ "app.py" ]
                            WorkingDirectory = "."
                            Deadline = DateTimeOffset.UtcNow.AddMinutes 1.0
                            MaximumOutputBytes = 65536UL
                            WorkspaceScope = "fs-gg/link-refusal"
                            SourceRepository = repository
                            SourceRevision = head
                            QualifiedImage = image
                            ContainerName = "fsgg-portable-link-refusal"
                            ContainerPath = runtime.ContainerPath
                            ContainerEnvironment = runtime.ContainerEnvironment
                            VerificationPath = "verified.txt"
                            VerificationSha256 = verificationSha256
                            RecipeSha256 = recipeSha256
                        }

                    let runner = PortableWorkspacePodmanRunner runtime :> IPortableProcessRunner

                    for index in 1..32 do
                        let selected =
                            { request with
                                ContainerName = $"fsgg-portable-link-refusal-{index:D2}"
                            }

                        let! observed = runner.RunAsync(selected, CancellationToken.None)

                        Assert.False(observed.TerminationObserved)
                        Assert.Equal(PortableProcessRefusalStage.SourceSnapshot, observed.Refusal.Value.Stage)
                        Assert.Equal("portable-source-tree-refused", observed.Refusal.Value.Reason)
                        Assert.True(observed.Refusal.Value.HelperOutputComplete)
                finally
                    if Directory.Exists temporary then
                        Directory.Delete(temporary, true)
        }

    [<Fact>]
    member _.``qualification FSI host drains the production runner Git streams``() =
        if not (OperatingSystem.IsWindows()) then
            let repository = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)
            let temporary = Path.Combine(Path.GetTempPath(), "fsgg-portable-fsi-" + Guid.NewGuid().ToString("N"))
            let manifest = Path.Combine(temporary, "manifest.json")
            let state = Path.Combine(temporary, "state")
            let evidence = Path.Combine(temporary, "evidence.json")
            let store = Path.Combine(temporary, "store")
            let runRoot = Path.Combine(temporary, "runroot")

            try
                for path in [ temporary; state; store; runRoot ] do
                    Directory.CreateDirectory path |> ignore

                File.WriteAllText(
                    manifest,
                    """{"schema":"fsgg.portable-workspace-local-image/1","image":{"reference":"localhost/fsgg-portable-workspace:test@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","id":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","os":"linux","architecture":"amd64","user":"32768:32768"}}"""
                )

                let start =
                    Diagnostics.ProcessStartInfo(
                        Environment.ProcessPath,
                        WorkingDirectory = repository,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    )

                for argument in
                    [
                        "fsi"
                        "eng/portable-workspace-executor-qualification.fsx"
                        "--"
                        "--fixture"
                        "tests/portable-workspace/executor-qualification/fixture"
                        "--image-manifest"
                        manifest
                        "--state-dir"
                        state
                        "--evidence"
                        evidence
                        "--podman"
                        "/usr/bin/false"
                        "--git"
                        "/usr/bin/git"
                        "--tar"
                        "/usr/bin/tar"
                        "--root"
                        store
                        "--runroot"
                        runRoot
                    ] do
                    start.ArgumentList.Add argument

                use child = Diagnostics.Process.Start start
                let stdout = child.StandardOutput.ReadToEndAsync()
                let stderr = child.StandardError.ReadToEndAsync()
                child.WaitForExit()

                Assert.Equal(2, child.ExitCode)
                Assert.True(File.Exists evidence, stderr.GetAwaiter().GetResult())

                use document = JsonDocument.Parse(File.ReadAllBytes evidence)
                let first = document.RootElement.GetProperty("operations")[0]
                Assert.Equal("RuntimeVersion", first.GetProperty("refusalStage").GetString())
                Assert.Equal("True", first.GetProperty("helperOutputComplete").GetString())
                Assert.Equal("RanToCompletion/RanToCompletion/RanToCompletion", first.GetProperty("helperReadStatus").GetString())
                Assert.DoesNotContain("MissingMethodException", stdout.GetAwaiter().GetResult())
            finally
                if Directory.Exists temporary then
                    Directory.Delete(temporary, true)

module private CapsuleFixtures =
    let create () =
        let root = Path.Combine(Path.GetTempPath(), "fsgg-assembled-capsule-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let files =
            [ "capture_custody.py", "def prepare_capture(): return 'capture'\n"
              "stock_entry.py", "from capture_custody import prepare_capture\n"
              "test_capture.py", "import unittest\nfrom stock_entry import prepare_capture\nclass Capture(unittest.TestCase):\n def test_capture(self): raise RuntimeError('workload must never run during discovery')\n"
              "check.py", "import sys,json,unittest\nif sys.argv[1]=='imports':\n import stock_entry\n names=['stock_entry','capture_custody']\nelse:\n def ids(s):\n  for t in s:\n   if isinstance(t,unittest.TestSuite): yield from ids(t)\n   else: yield t.id()\n names=list(ids(unittest.defaultTestLoader.discover('.',pattern='test_*.py')))\nprint(json.dumps({'schema':'fsgg.capsule-observation/1','checkId':sys.argv[1],'discovered':names}))\n" ]
        files |> List.iter (fun (path, text) -> File.WriteAllText(Path.Combine(root,path),text))
        let python = FileInfo("/usr/bin/python3").ResolveLinkTarget(true).FullName
        let spec =
            { Root = root; Inputs = files |> List.map fst |> List.sort
              Checks =
                [ { Id = "imports"; Kind = CapsuleCheckKind.Import; Executable = python
                    Arguments = ["check.py"; "imports"]; ExpectedDiscoveries = ["stock_entry"; "capture_custody"] }
                  { Id = "discovery"; Kind = CapsuleCheckKind.Discovery; Executable = python
                    Arguments = ["check.py"; "discovery"]; ExpectedDiscoveries = ["test_capture.Capture.test_capture"] } ]
              Environment = Map [ "PATH", "/usr/bin:/bin"; "LANG", "C.UTF-8" ]
              MaximumInputBytes = 65536UL; MaximumOutputBytes = 4096UL; MaximumCheckSeconds = 2 }
        new IsolatedFixture(root), spec

    let prepare spec = PreparedAttempt.prepareAsync "runner-argv-config-source" (DateTimeOffset.UtcNow.AddSeconds 10.) spec CancellationToken.None
    let valid result = match result with Ok value -> value | Error reason -> failwith reason

    let executor spec (fake: FakeRunner) =
        let selectedProfile = profile "fs-gg/checked-capsule" [fixtureComponent "app" "python" "python" "cpython" "3.14.0" "python-build" "python-test"]
        let op = operation "python-build" "build" (Some "app") "python" ["cpython","3.14.0"] "python3" ["build.py"] "python-bytecode-v1"
        let policy =
            { WorkspaceRoot = spec.Root; WorkspaceScope = selectedProfile.WorkspaceScope
              SourceRevision = sourceRevision; QualifiedImage = image; MaximumRuntimeSeconds = 30UL
              MaximumOutputBytes = 65536UL; Operations = [op]; Runtime = runtimePolicy () }
        let utc = DateTimeOffset.UtcNow
        let current = DateTimeOffset(utc.Ticks - utc.Ticks % 10L, TimeSpan.Zero)
        let auth = { authority selectedProfile.WorkspaceScope with ObservedAt = current.AddSeconds(-1.) }
        let cmd = { command selectedProfile (Guid.NewGuid()) "build" (Some "app") with Deadline = current.AddMinutes 1. }
        let ex = PortableWorkspaceExecutor.Executor(policy, fake, prerequisites = Map [op.EntryPoint, PortablePrerequisiteRequirement.CapsuleRequired spec])
        ex, policy, auth, selectedProfile, cmd

open CapsuleFixtures

type PreparedAttemptTests() =
    [<Fact>]
    member _.``actual assembled import and discovery prepares without running discovered workload``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        let! result = prepare spec
        let value = valid result
        Assert.Equal(Ok (), PreparedAttempt.validate DateTimeOffset.UtcNow "runner-argv-config-source" spec value)
        Assert.False(Directory.Exists(Path.Combine(fixture.Root,"__pycache__")))
    }

    [<Fact>]
    member _.``opaque capability has no public constructor and cannot deserialize receipt``() =
        Assert.Empty(typeof<PreparedAttempt>.GetConstructors())
        Assert.Throws<NotSupportedException>(fun () -> JsonSerializer.Deserialize<PreparedAttempt>("{}") |> ignore) |> ignore

    [<Fact>]
    member _.``changed command configuration closure inode or deadline refuses consumption``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        let! result = prepare spec
        let value = valid result
        Assert.Equal(Error "preparation-binding-invalidated", PreparedAttempt.validate DateTimeOffset.UtcNow "changed-argv" spec value)
        Assert.Equal(Error "preparation-binding-invalidated", PreparedAttempt.validate DateTimeOffset.UtcNow "runner-argv-config-source" {spec with Environment = Map.add "LANG" "C" spec.Environment} value)
        Assert.Equal(Error "preparation-deadline-refused", PreparedAttempt.validate (DateTimeOffset.UtcNow.AddMinutes 1.) "runner-argv-config-source" spec value)
        let outside = Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"))
        try
            File.WriteAllText(outside,"unrelated")
            Assert.Equal(Ok (), PreparedAttempt.validate DateTimeOffset.UtcNow "runner-argv-config-source" spec value)
        finally File.Delete outside
        let target = Path.Combine(fixture.Root,"capture_custody.py")
        let content = File.ReadAllText target
        File.Move(target,target+".prior")
        File.WriteAllText(target,content)
        File.Delete(target+".prior")
        Assert.Equal(Error "preparation-input-invalidated", PreparedAttempt.validate DateTimeOffset.UtcNow "runner-argv-config-source" spec value)
    }

    [<Theory>]
    [<InlineData("missing")>]
    [<InlineData("empty")>]
    [<InlineData("unexpected")>]
    [<InlineData("malformed")>]
    [<InlineData("failed")>]
    [<InlineData("timeout")>]
    [<InlineData("tool")>]
    [<InlineData("output")>]
    member _.``bad or unknown actual checks have zero workload launches``(scenario: string) = task {
        let fixture, initial = create ()
        use fixture = fixture
        let mutable spec = initial
        match scenario with
        | "missing" ->
            File.Delete(Path.Combine(fixture.Root,"capture_custody.py"))
            spec <- {spec with Inputs = spec.Inputs |> List.filter ((<>) "capture_custody.py")}
        | "empty" -> File.WriteAllText(Path.Combine(fixture.Root,"test_capture.py"),"import unittest\n")
        | "unexpected" -> spec <- {spec with Checks = spec.Checks |> List.map (fun c -> if c.Kind = CapsuleCheckKind.Discovery then {c with ExpectedDiscoveries = ["wrong"]} else c)}
        | "malformed" -> File.WriteAllText(Path.Combine(fixture.Root,"check.py"),"print('unknown')\n")
        | "failed" -> File.WriteAllText(Path.Combine(fixture.Root,"check.py"),"raise RuntimeError('failed')\n")
        | "timeout" -> File.WriteAllText(Path.Combine(fixture.Root,"check.py"),"import time\ntime.sleep(20)\n")
        | "tool" -> spec <- {spec with Checks = spec.Checks |> List.map (fun c -> {c with Executable = "/absent/tool"})}
        | "output" -> File.WriteAllText(Path.Combine(fixture.Root,"check.py"),"print('X'*10000)\n")
        | _ -> failwith scenario
        let fake = FakeRunner(fun _ -> successfulObservation)
        let ex, _, auth, selectedProfile, cmd = executor spec fake
        let! outcome = ex.ExecuteAsync(auth,selectedProfile,cmd,CancellationToken.None)
        match outcome with Refused reason -> Assert.StartsWith("preparation-",reason) | other -> failwith $"unexpected launch {other}"
        Assert.Equal(0,fake.Calls)
        Assert.Equal(0,fake.CleanupCalls)
    }

    [<Theory>]
    [<InlineData("input")>]
    [<InlineData("deadline")>]
    member _.``prepared evidence owned observations and admission deadline compose before runner effects``(changed: string) = task {
        let fixture, spec = create ()
        use fixture = fixture
        let check = Path.Combine(fixture.Root,"check.py")
        let original = File.ReadAllText check
        let alteration =
            if changed = "input" then
                "if sys.argv[1]=='discovery':\n open('capture_custody.py','w').write(\"def prepare_capture(): return 'changed'\\n\")\n"
            else "import time\ntime.sleep(1)\n"
        File.WriteAllText(check,original.Replace("print(json.dumps",alteration + "print(json.dumps"))
        let fake = FakeRunner(fun _ -> successfulObservation)
        let ex,_,auth,selectedProfile,command = executor spec fake
        let selected = if changed = "deadline" then {command with Deadline=command.Deadline.AddSeconds(-59.5)} else command
        let! outcome = ex.ExecuteAsync(auth,selectedProfile,selected,CancellationToken.None)
        match outcome with
        | Refused reason -> Assert.StartsWith("preparation-",reason)
        | other -> failwith $"unexpected admission {other}"
        Assert.Equal(0,fake.Calls)
        Assert.False(Directory.Exists(Path.Combine(fixture.Root,"__pycache__")))
    }

    [<Fact>]
    member _.``checked execution and settled cleanup remain available when new checks unavailable``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        let mutable canClean = false
        let fake = FakeRunner((fun _ -> successfulObservation),cleanup = (fun _ -> canClean))
        let ex, policy, auth, selectedProfile, cmd = executor spec fake
        let! outcome = ex.ExecuteAsync(auth,selectedProfile,cmd,CancellationToken.None)
        match outcome with Completed receipt -> Assert.False(receipt.CleanupCompleted) | other -> failwith $"unexpected {other}"
        Assert.Equal(1,fake.Calls)
        canClean <- true
        let unavailable = PortableWorkspaceExecutor.Executor(policy,fake,prerequisites = Map ["python-build",PortablePrerequisiteRequirement.CapsuleUnavailable])
        let! duplicate = unavailable.ExecuteAsync(auth,selectedProfile,cmd,CancellationToken.None)
        match duplicate with Duplicate receipt -> Assert.True(receipt.CleanupCompleted) | other -> failwith $"unexpected {other}"
        let! refused = unavailable.ExecuteAsync(auth,selectedProfile,{cmd with CommandId=Guid.NewGuid(); IdempotencyId="new-attempt"},CancellationToken.None)
        Assert.Equal(Refused "preparation-required-check-unavailable",refused)
        Assert.Equal(1,fake.Calls)
        Assert.Equal(2,fake.CleanupCalls)
    }

module private ProbeControls =
    [<Struct; System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>]
    type OwnedPoll =
        val mutable Descriptor: int
        val mutable Events: int16
        val mutable Returned: int16
    [<System.Runtime.InteropServices.DllImport("libc", EntryPoint="poll", SetLastError=true)>]
    extern int waitOwned(OwnedPoll& descriptor, unativeint count, int milliseconds)

    [<System.Runtime.InteropServices.DllImport("libc", EntryPoint="pidfd_open", SetLastError=true)>]
    extern int pidfdOpen(int pid, uint32 flags)
    [<System.Runtime.InteropServices.DllImport("libc", EntryPoint="pidfd_send_signal", SetLastError=true)>]
    extern int pidfdSignal(int descriptor, int signal, nativeint info, uint32 flags)

    let readIdentity path =
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        document.RootElement.GetProperty("pid").GetInt32(),document.RootElement.GetProperty("start").GetString()
    let stopped path =
        let pid,start = readIdentity path
        try
            let stat = File.ReadAllText($"/proc/{pid}/stat")
            let fields = stat.Substring(stat.LastIndexOf(')')+2).Split(' ')
            fields[19] <> start || fields[0] = "Z"
        with :? FileNotFoundException | :? DirectoryNotFoundException -> true
    let cleanup path =
        if File.Exists path then
            let pid,start = readIdentity path
            let descriptor = pidfdOpen(pid,0u)
            if descriptor >= 0 then
                use handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint descriptor,true)
                try
                    let stat = File.ReadAllText($"/proc/{pid}/stat")
                    let fields = stat.Substring(stat.LastIndexOf(')')+2).Split(' ')
                    if fields[19] = start then pidfdSignal(descriptor,9,0n,0u) |> ignore
                with :? FileNotFoundException | :? DirectoryNotFoundException -> ()
            File.Delete path

    let auditScript = "import os,json,sys,time,subprocess\ndef note(pid,path):\n s=open('/proc/%d/stat'%pid).read();f=s[s.rfind(')')+2:].split();open(path,'w').write(json.dumps({'pid':pid,'start':f[19]}))\nnote(os.getpid(),os.environ['ROOT_AUDIT'])\np=subprocess.Popen([sys.executable,'-c','import time;time.sleep(20)'],start_new_session=os.environ['ESCAPE']=='yes')\nnote(p.pid,os.environ['CHILD_AUDIT'])\ntime.sleep(20)\n"

open ProbeControls

type ProbeCustodyTests() =
    [<Theory>]
    [<InlineData("fifo")>]
    [<InlineData("symlink")>]
    member _.``actual nonregular owned input refuses before open and hashing``(kind: string) = task {
        let fixture,spec = create ()
        use fixture = fixture
        let path = Path.Combine(fixture.Root,"nonregular")
        if kind = "fifo" then
            use helper = Diagnostics.Process.Start("/usr/bin/mkfifo",path)
            Assert.True(helper.WaitForExit(2000));Assert.Equal(0,helper.ExitCode)
        else File.CreateSymbolicLink(path,Path.Combine(fixture.Root,"capture_custody.py")) |> ignore
        let watch = Diagnostics.Stopwatch.StartNew()
        let! outcome = prepare {spec with Inputs=spec.Inputs @ ["nonregular"]}
        Assert.True(Result.isError outcome)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds 1.,$"nonregular refusal took {watch.Elapsed}")
    }

    [<Fact>]
    member _.``directory inode replacement also invalidates a prepared capsule``() = task {
        let fixture,spec = create ()
        use fixture = fixture
        let directory = Path.Combine(fixture.Root,"discovery-assets")
        Directory.CreateDirectory directory |> ignore
        let! observation = prepare spec
        let attempt = valid observation
        Directory.Move(directory,directory+".previous")
        Directory.CreateDirectory directory |> ignore
        Directory.Delete(directory+".previous")
        Assert.Equal(Error "preparation-input-invalidated",PreparedAttempt.validate DateTimeOffset.UtcNow "runner-argv-config-source" spec attempt)
    }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``owned group and escaped session controls never signal unrelated probe``(escape: bool) = task {
        let fixture,initial = create ()
        use fixture = fixture
        let rootAudit = Path.Combine(Path.GetTempPath(),"fsgg-owned-root-"+Guid.NewGuid().ToString("N"))
        let childAudit = Path.Combine(Path.GetTempPath(),"fsgg-owned-child-"+Guid.NewGuid().ToString("N"))
        let python = initial.Checks.Head.Executable
        let unrelatedInfo = Diagnostics.ProcessStartInfo(python)
        unrelatedInfo.ArgumentList.Add "-c";unrelatedInfo.ArgumentList.Add "import time;time.sleep(20)"
        use unrelated = Diagnostics.Process.Start unrelatedInfo
        try
            File.WriteAllText(Path.Combine(fixture.Root,"check.py"),auditScript)
            let spec = {initial with MaximumCheckSeconds=1
                                     Environment=initial.Environment |> Map.add "ROOT_AUDIT" rootAudit |> Map.add "CHILD_AUDIT" childAudit |> Map.add "ESCAPE" (if escape then "yes" else "no")}
            let fake = FakeRunner(fun _ -> successfulObservation)
            let ex,_,auth,selectedProfile,cmd = executor spec fake
            let! outcome = ex.ExecuteAsync(auth,selectedProfile,cmd,CancellationToken.None)
            Assert.Equal(0,fake.Calls)
            Assert.False(unrelated.HasExited)
            match outcome with Refused reason -> Assert.StartsWith("preparation-",reason) | _ -> failwith $"unexpected {outcome}"
            Assert.True(stopped rootAudit)
            // Both requested group forms are denied before a child can exist.
            Assert.False(File.Exists childAudit)
        finally
            cleanup childAudit;cleanup rootAudit
            if not unrelated.HasExited then unrelated.Kill();unrelated.WaitForExit(2000) |> ignore
    }

    [<Fact>]
    member _.``checker exit before census cannot admit orphaned escaped child``() = task {
        let fixture, initial = create ()
        use fixture = fixture
        let childAudit = Path.Combine(Path.GetTempPath(),"fsgg-fast-orphan-"+Guid.NewGuid().ToString("N"))
        let acknowledgement = childAudit + ".ack"
        let childCode = "import os,json,signal,time\ns=open('/proc/self/stat').read();f=s[s.rfind(')')+2:].split();p=os.environ['CHILD_AUDIT'];open(p+'.tmp','w').write(json.dumps({'pid':os.getpid(),'start':f[19]}));os.replace(p+'.tmp',p)\nos.kill(os.getpid(),signal.SIGSTOP)\ntime.sleep(20)\n"
        let script = "import sys,subprocess,json,os,time\nif sys.argv[1]=='imports':\n p=subprocess.Popen([sys.executable,'-c'," + JsonSerializer.Serialize(childCode) + "],start_new_session=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)\n until=time.monotonic()+10\n while not os.path.exists(os.environ['CHILD_ACK']):\n  if time.monotonic()>until: raise RuntimeError('controller custody missing')\n  time.sleep(.01)\n names=['stock_entry','capture_custody']\nelse: names=['test_capture.Capture.test_capture']\nprint(json.dumps({'schema':'fsgg.capsule-observation/1','checkId':sys.argv[1],'discovered':names}))\n"
        let mutable heldChild : Microsoft.Win32.SafeHandles.SafeFileHandle option = None
        try
            File.WriteAllText(Path.Combine(fixture.Root,"check.py"),script)
            let spec = {initial with Environment=initial.Environment |> Map.add "CHILD_AUDIT" childAudit |> Map.add "CHILD_ACK" acknowledgement}
            let fake = FakeRunner(fun _ -> successfulObservation)
            let ex,_,auth,selectedProfile,cmd = executor spec fake
            let pending = ex.ExecuteAsync(auth,selectedProfile,cmd,CancellationToken.None)
            let bound = Diagnostics.Stopwatch.StartNew()
            while not pending.IsCompleted && not (File.Exists childAudit) && bound.ElapsedMilliseconds < 1500L do
                do! Task.Delay 10
            if File.Exists childAudit then
                let pid,start = readIdentity childAudit
                let descriptor = pidfdOpen(pid,0u)
                Assert.True(descriptor >= 0,"test child pidfd custody unavailable")
                heldChild <- Some(new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint descriptor,true))
                let stat = File.ReadAllText($"/proc/{pid}/stat")
                let fields = stat.Substring(stat.LastIndexOf(')')+2).Split(' ')
                Assert.Equal(start,fields[19])
                // The root may exit only after this test controller owns the held child.
                File.WriteAllText(acknowledgement,"owned")
            let! outcome = pending
            match outcome with
            | Refused reason -> Assert.StartsWith("preparation-",reason)
            | other -> failwith $"orphaned checker admitted {other}"
            Assert.Equal(0,fake.Calls)
            if File.Exists childAudit then Assert.True(stopped childAudit)
        finally
            match heldChild with
            | Some handle ->
                pidfdSignal(handle.DangerousGetHandle().ToInt32(),9,0n,0u) |> ignore
                let mutable settled = Unchecked.defaultof<OwnedPoll>
                settled.Descriptor <- handle.DangerousGetHandle().ToInt32()
                settled.Events <- 1s
                Assert.Equal(1,waitOwned(&settled,1un,2000))
                Assert.True((settled.Returned &&& 1s) <> 0s,"held test child did not terminate")
                handle.Dispose()
            | None -> ()
            File.Delete acknowledgement
            File.Delete childAudit
            File.Delete(childAudit+".tmp")
    }

type ThreadGroupCustodyTests() =
    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``whole process custody waits for thread after raw leader exit``(endless: bool) = task {
        let fixture,initial = create ()
        use fixture = fixture
        let python=initial.Checks.Head.Executable
        let unrelatedInfo=Diagnostics.ProcessStartInfo(python)
        unrelatedInfo.ArgumentList.Add "-c"
        unrelatedInfo.ArgumentList.Add "import time;time.sleep(20)"
        use unrelated=Diagnostics.Process.Start unrelatedInfo
        let rootAudit=Path.Combine(Path.GetTempPath(),"fsgg-thread-group-"+Guid.NewGuid().ToString("N"))
        try
            let seconds=if endless then "20" else ".2"
            let script="import sys,json,os,time,threading,ctypes\ns=open('/proc/self/stat').read();f=s[s.rfind(')')+2:].split();open(os.environ['ROOT_AUDIT'],'w').write(json.dumps({'pid':os.getpid(),'start':f[19]}))\nt=threading.Thread(target=lambda:time.sleep("+seconds+"));t.start()\nnames=['stock_entry','capture_custody'] if sys.argv[1]=='imports' else ['test_capture.Capture.test_capture']\nprint(json.dumps({'schema':'fsgg.capsule-observation/1','checkId':sys.argv[1],'discovered':names}),flush=True)\nos.close(1);os.close(2)\nctypes.CDLL(None).syscall(60,0)\n"
            File.WriteAllText(Path.Combine(fixture.Root,"check.py"),script)
            let spec={initial with MaximumCheckSeconds=1;Environment=Map.add "ROOT_AUDIT" rootAudit initial.Environment}
            let watch=Diagnostics.Stopwatch.StartNew()
            if endless then
                let fake=FakeRunner(fun _ -> successfulObservation)
                let ex,_,auth,selectedProfile,cmd=executor spec fake
                let! outcome=ex.ExecuteAsync(auth,selectedProfile,cmd,CancellationToken.None)
                match outcome with Refused reason -> Assert.StartsWith("preparation-",reason) | value -> failwith $"live thread admitted {value}"
                Assert.Equal(0,fake.Calls)
                Assert.True(stopped rootAudit)
            else
                let! result=prepare spec
                valid result |> ignore
                // Two genuine checks each keep a thread alive after the leader exits.
                Assert.True(watch.ElapsedMilliseconds>=300L)
                Assert.True(stopped rootAudit)
            Assert.False(unrelated.HasExited)
        finally
            cleanup rootAudit
            if not unrelated.HasExited then unrelated.Kill();unrelated.WaitForExit(2000) |> ignore
    }

    [<Theory>]
    [<InlineData("output")>]
    [<InlineData("cancel")>]
    member _.``threaded output and cancellation settle group before refusal``(scenario: string) = task {
        let fixture,initial=create ()
        use fixture=fixture
        let rootAudit=Path.Combine(Path.GetTempPath(),"fsgg-thread-budget-"+Guid.NewGuid().ToString("N"))
        try
            let worker=if scenario="output" then "while True: print('X'*4096,flush=True)" else "time.sleep(20)"
            let script="import threading,time,json,os\ns=open('/proc/self/stat').read();f=s[s.rfind(')')+2:].split();open(os.environ['ROOT_AUDIT'],'w').write(json.dumps({'pid':os.getpid(),'start':f[19]}))\ndef worker():\n "+worker+"\nt=threading.Thread(target=worker);t.start();t.join()\n"
            File.WriteAllText(Path.Combine(fixture.Root,"check.py"),script)
            let spec={initial with Environment=Map.add "ROOT_AUDIT" rootAudit initial.Environment}
            use cancellation=new CancellationTokenSource()
            let pending=PreparedAttempt.prepareAsync "runner-argv-config-source" (DateTimeOffset.UtcNow.AddSeconds 5.) spec cancellation.Token
            if scenario="cancel" then
                let waiting=Diagnostics.Stopwatch.StartNew()
                while not(File.Exists rootAudit) && not pending.IsCompleted && waiting.ElapsedMilliseconds<1000L do do! Task.Delay 10
                Assert.True(File.Exists rootAudit,"checker did not start before cancellation control")
                cancellation.Cancel()
            let! result=pending
            match result with Error reason -> Assert.StartsWith("preparation-",reason) | _ -> failwith "threaded budget admitted"
            Assert.True(stopped rootAudit)
        finally cleanup rootAudit
    }

type PreparationDiagnosticsTests() =
    let declarations (spec: CapsulePreparation) =
        spec.Checks |> List.map (fun check ->
            { CheckId = check.Id; DependsOn = []; SharedStateScopes = [] })
    let detailed spec selected =
        PreparedAttempt.prepareDetailedAsync "runner-argv-config-source" (DateTimeOffset.UtcNow.AddSeconds 10.) spec selected CancellationToken.None
    let findings (report: PreparationReport) = report.Findings |> List.filter (fun finding -> finding.Stage = "check")

    [<Fact>]
    member _.``two independent actual prerequisite failures retain both causes and no readiness``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        File.WriteAllText(Path.Combine(spec.Root,"check.py"), "import sys\nif sys.argv[1]=='imports': import absent_import_one\nelse: import absent_import_two\n")
        let! report = detailed spec (declarations spec)
        Assert.True(report.Prepared.IsNone)
        Assert.Equal(Some "preparation-import-missing:absent_import_one", report.FirstFailure)
        Assert.Contains("preparation-import-missing:absent_import_two", report.AdditionalFailures)
        Assert.Equal<PreparationCheckOutcome list>([PreparationCheckOutcome.Failed; PreparationCheckOutcome.Failed], findings report |> List.map _.Outcome)
        Assert.All(findings report, fun finding -> Assert.Equal(Some 1,finding.ExitCode); Assert.Equal(Some true,finding.CleanupObserved))
        let! strict = prepare spec
        Assert.Equal(Error "preparation-import-missing:absent_import_one", strict)
        // The existing executor uses the same strict implementation and never reserves/launches a workload.
        let fake = FakeRunner(fun _ -> successfulObservation)
        let ex, _, auth, selectedProfile, cmd = executor spec fake
        let! result = ex.ExecuteAsync(auth,selectedProfile,cmd,CancellationToken.None)
        Assert.Equal(Refused "preparation-import-missing:absent_import_one",result)
        Assert.Equal(0,fake.Calls)
    }

    [<Theory>]
    [<InlineData("dependency")>]
    [<InlineData("scope")>]
    [<InlineData("unknown")>]
    [<InlineData("undeclared")>]
    member _.``invalid prerequisites and shared state block actual later checks``(scenario: string) = task {
        let fixture, spec = create ()
        use fixture = fixture
        File.WriteAllText(Path.Combine(spec.Root,"check.py"), "raise RuntimeError('first failure')\n")
        let selected = declarations spec
        let selected =
            match scenario with
            | "dependency" -> selected |> List.map (fun d -> if d.CheckId = "discovery" then {d with DependsOn = ["imports"]} else d)
            | "unknown" -> selected |> List.map (fun d -> if d.CheckId = "discovery" then {d with DependsOn = ["unknown-check"]} else d)
            | "scope" -> selected |> List.map (fun d -> {d with SharedStateScopes = ["same-state"]})
            | "undeclared" -> []
            | _ -> failwith scenario
        let! report = detailed spec selected
        let second = (findings report)[1]
        Assert.Equal(PreparationCheckOutcome.Blocked,second.Outcome)
        Assert.True(second.ExitCode.IsNone)
        Assert.True(second.CleanupObserved.IsNone)
        Assert.True(report.Prepared.IsNone)
    }

    [<Fact>]
    member _.``complete detailed success binds declarations and refuses stale input at consumption``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        let selected = declarations spec
        let! report = detailed spec selected
        Assert.True(report.FirstFailure.IsNone)
        let prepared = report.Prepared.Value
        Assert.All(findings report, fun finding -> Assert.Equal(PreparationCheckOutcome.Passed,finding.Outcome))
        Assert.Equal(Ok (),PreparedAttempt.validateDetailed DateTimeOffset.UtcNow "runner-argv-config-source" spec selected prepared)
        let changed = selected |> List.map (fun d -> {d with SharedStateScopes = ["changed"]})
        Assert.Equal(Error "preparation-dependencies-invalidated",PreparedAttempt.validateDetailed DateTimeOffset.UtcNow "runner-argv-config-source" spec changed prepared)
        File.AppendAllText(Path.Combine(spec.Root,"capture_custody.py"),"# changed\n")
        Assert.Equal(Error "preparation-input-invalidated",PreparedAttempt.validateDetailed DateTimeOffset.UtcNow "runner-argv-config-source" spec selected prepared)
    }

    [<Fact>]
    member _.``structural closure collects independent missing inputs without probes``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        File.Delete(Path.Combine(spec.Root,"capture_custody.py"))
        File.Delete(Path.Combine(spec.Root,"stock_entry.py"))
        let! report = detailed spec (declarations spec)
        Assert.Equal(2,report.Findings |> List.filter (fun finding -> finding.Stage = "closure") |> List.length)
        Assert.All(findings report, fun finding -> Assert.Equal(PreparationCheckOutcome.Blocked,finding.Outcome); Assert.True(finding.ExitCode.IsNone))
        Assert.True(report.Prepared.IsNone)
    }

    [<Fact>]
    member _.``unknown check result cannot prepare or permit later effects``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        File.WriteAllText(Path.Combine(spec.Root,"check.py"),"print('unknown')\n")
        let! report = detailed spec (declarations spec)
        Assert.Equal(PreparationCheckOutcome.Unknown,((findings report)[0]).Outcome)
        Assert.Equal(PreparationCheckOutcome.Blocked,((findings report)[1]).Outcome)
        Assert.True(report.Prepared.IsNone)
    }

    [<Fact>]
    member _.``failed checker input contamination blocks otherwise independent check``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        File.WriteAllText(Path.Combine(spec.Root,"check.py"),"open('capture_custody.py','a').write('# changed\\n')\nraise RuntimeError('first failure')\n")
        let! report = detailed spec (declarations spec)
        Assert.Equal(Some "preparation-check-failed:imports",report.FirstFailure)
        Assert.Contains("preparation-input-invalidated",report.AdditionalFailures)
        Assert.Equal(PreparationCheckOutcome.Blocked,((findings report)[1]).Outcome)
        Assert.True(report.Prepared.IsNone)
    }

    [<Fact>]
    member _.``first failure survives later cleanup uncertainty and continuation stops``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        // Disposable owned scratch is removed by the failed checker, making cleanup observation unavailable.
        File.WriteAllText(Path.Combine(spec.Root,"check.py"),"import os\nos.rmdir(os.environ['TMPDIR'])\nraise RuntimeError('first failure')\n")
        let! report = detailed spec (declarations spec)
        Assert.Equal(Some "preparation-check-failed:imports",report.FirstFailure)
        Assert.Contains("preparation-cleanup-unobserved",report.AdditionalFailures)
        Assert.Equal(Some false,((findings report)[0]).CleanupObserved)
        Assert.Equal(PreparationCheckOutcome.Blocked,((findings report)[1]).Outcome)
        Assert.True(report.Prepared.IsNone)
    }

    [<Fact>]
    member _.``failed checks consume cumulative output budget and preserve first cause``() = task {
        let fixture, initial = create ()
        use fixture = fixture
        let third = {initial.Checks[1] with Id = "third"}
        let spec = {initial with Checks = initial.Checks @ [third]}
        File.WriteAllText(Path.Combine(spec.Root,"check.py"),"import sys\nsys.stderr.write('x'*2200)\nsys.exit(1)\n")
        let! report = detailed spec (declarations spec)
        Assert.Equal(Some "preparation-check-failed:imports",report.FirstFailure)
        Assert.Equal(PreparationCheckOutcome.NotRunBound,((findings report)[2]).Outcome)
        Assert.True(report.Prepared.IsNone)
        Assert.True(report.AdditionalFailures |> List.exists (fun reason -> reason.Contains("budget")))
    }

    [<Fact>]
    member _.``original absolute deadline records checks not run because bound``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        let! report = PreparedAttempt.prepareDetailedAsync "runner-argv-config-source" (DateTimeOffset.UtcNow.AddSeconds(-1.)) spec (declarations spec) CancellationToken.None
        Assert.True(report.Prepared.IsNone)
        Assert.All(findings report, fun finding -> Assert.True(finding.ExitCode.IsNone))
    }

    [<Fact>]
    member _.``first checker failure survives bounded reporting failure``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        File.WriteAllText(Path.Combine(spec.Root,"check.py"),"import sys\nsys.stdout.write('x'*2200)\nsys.stderr.write('y'*2200)\nsys.exit(1)\n")
        let! report = detailed spec (declarations spec)
        Assert.Equal(Some "preparation-check-failed:imports",report.FirstFailure)
        Assert.Equal(Some "preparation-output-budget-refused",((findings report)[0]).ReportingFailure)
        Assert.Equal(PreparationCheckOutcome.NotRunBound,((findings report)[1]).Outcome)
        Assert.True(report.Prepared.IsNone)
    }

    [<Fact>]
    member _.``oversize candidate binding refuses before checker effects or serialization``() = task {
        let fixture, spec = create ()
        use fixture = fixture
        // If reached, a checker mutates the capsule. The invalid binding must fence both checks.
        File.WriteAllText(Path.Combine(spec.Root,"check.py"),"open('checker-launched','w').write('unexpected')\n")
        let selected = declarations spec
        let! report = PreparedAttempt.prepareDetailedAsync (String.replicate 4097 "x") (DateTimeOffset.UtcNow.AddSeconds 10.) spec selected CancellationToken.None
        Assert.True(report.Prepared.IsNone)
        Assert.True(report.FirstFailure.IsSome)
        Assert.Empty(report.Findings)
        Assert.Equal("unavailable",report.CandidateBinding)
        Assert.Equal(spec.Checks.Length,report.OmittedChecks)
        Assert.False(File.Exists(Path.Combine(spec.Root,"checker-launched")))
    }
