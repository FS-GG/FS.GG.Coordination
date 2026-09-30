namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open Xunit

type private FakeRunner(run: PortableProcessRequest -> PortableProcessObservation) =
    let mutable calls = 0

    member _.Calls = calls

    interface IPortableProcessRunner with
        member _.RunAsync(request, _) =
            calls <- calls + 1
            Task.FromResult(run request)

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
            Executable = executable
            Arguments = arguments
            VerificationIdentity = verification
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
    member _.``fixed executor runs real Python build and test``() =
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

            let policy =
                {
                    WorkspaceRoot = workspace
                    WorkspaceScope = selectedProfile.WorkspaceScope
                    SourceRevision = sourceRevision
                    QualifiedImage = image
                    MaximumRuntimeSeconds = 30UL
                    MaximumOutputBytes = 131072UL
                    Operations = operations
                }

            let executor =
                PortableWorkspaceExecutor.Executor(
                    policy,
                    PortableWorkspaceExecutor.SystemProcessRunner(),
                    (fun () -> liveNow)
                )

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
    member _.``fixed executor runs component checks and frontend to backend journey``() =
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
                }

            let executor =
                PortableWorkspaceExecutor.Executor(
                    policy,
                    PortableWorkspaceExecutor.SystemProcessRunner(),
                    (fun () -> liveNow)
                )

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
                        ExitCode = Some 0
                        StandardOutput = [| 1uy |]
                        StandardError = Array.empty
                        CancellationRequested = false
                        TerminationObserved = true
                        Interrupted = false
                        OutputLimitExceeded = false
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

            let adverse =
                [
                    authority "fs-gg/foreign", selectedProfile, valid
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
                            ExitCode = None
                            StandardOutput = Array.empty
                            StandardError = Array.empty
                            CancellationRequested = true
                            TerminationObserved = false
                            Interrupted = true
                            OutputLimitExceeded = false
                        }
                    else
                        {
                            ExitCode = Some 137
                            StandardOutput = Array.empty
                            StandardError = Array.empty
                            CancellationRequested = true
                            TerminationObserved = true
                            Interrupted = false
                            OutputLimitExceeded = false
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

            let reconciled =
                executor.Reconcile(
                    unknownCommand.IdempotencyId,
                    {
                        CommandId = unknownCommand.CommandId
                        SourceRevision = sourceRevision
                        QualifiedImage = image
                        VerificationIdentity = "python-bytecode-v1"
                        ObservedAt = now.AddMinutes(1.0)
                        ExitCode = Some 0
                        TerminationObserved = true
                    }
                )

            Assert.True(
                match reconciled with
                | Completed receipt -> receipt.Result.ExitCode = EvidenceKnown 0
                | _ -> false
            )

            interrupted <- false

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
                        ExitCode = Some 0
                        StandardOutput = Array.empty
                        StandardError = Array.empty
                        CancellationRequested = false
                        TerminationObserved = true
                        Interrupted = false
                        OutputLimitExceeded = false
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
