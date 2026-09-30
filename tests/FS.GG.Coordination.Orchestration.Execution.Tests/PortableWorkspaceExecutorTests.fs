namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.IO
open System.Text
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
            let repository = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)

            let temporary =
                Path.Combine(Path.GetTempPath(), "fsgg-podman-runner-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory temporary |> ignore

            try
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
    pass
elif args and args[0] == "create":
    pathlib.Path("create-args.json").write_text(json.dumps(args))
    volumes = [args[index + 1] for index, value in enumerate(args) if value == "--volume"]
    output = pathlib.Path([value for value in volumes if value.endswith(":/output:rw")][0].split(":/output:rw")[0])
    output.joinpath("verified.txt").write_bytes(b"verified")
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

                    Assert.False(Directory.Exists(Path.Combine(stateRoot, "executions", request.ContainerName)))

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
                    let! observed = runner.RunAsync(request, CancellationToken.None)

                    Assert.False(observed.TerminationObserved)
                    Assert.Equal(PortableProcessRefusalStage.SourceSnapshot, observed.Refusal.Value.Stage)
                    Assert.Equal("portable-source-tree-refused", observed.Refusal.Value.Reason)
                finally
                    if Directory.Exists temporary then
                        Directory.Delete(temporary, true)
        }
