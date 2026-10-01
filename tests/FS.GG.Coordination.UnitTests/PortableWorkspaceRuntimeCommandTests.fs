namespace FS.GG.Coordination.PortableWorkspace.RuntimeCommandTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Cli
open FS.GG.Coordination.Orchestration.Execution
open Xunit

type private EnrollmentSource(enrollment: PortableWorkspaceRuntimeEnrollment) =
    interface IPortableWorkspaceRuntimeEnrollmentSource with
        member _.Resolve enrollmentId =
            if enrollmentId = enrollment.EnrollmentId then
                Ok enrollment
            else
                Error "portable-runtime-enrollment-not-found"

type private PythonHelloRunner(fixtureRoot: string) =
    let mutable calls = 0
    let mutable cleanupAllowed = true
    let mutable cleanupThrows = false
    let mutable observedRequest: PortableProcessRequest option = None
    let mutable observedOutput = ""

    do
        File.WriteAllText(
            Path.Combine(fixtureRoot, "python", "build.py"),
            """import os
import pathlib
import py_compile

target = pathlib.Path(os.environ.get("PORTABLE_OUTPUT_ROOT", "/output")) / "python/app.pyc"
target.parent.mkdir(parents=True, exist_ok=True)
py_compile.compile(
    "app.py",
    cfile=target,
    dfile="/source/tests/portable-workspace/image/fixture/python/app.py",
    doraise=True,
    invalidation_mode=py_compile.PycInvalidationMode.CHECKED_HASH,
)
print("python-build-ok")
"""
        )

    let run request cancellationToken =
        task {
            calls <- calls + 1
            observedRequest <- Some request

            let start =
                ProcessStartInfo(
                    "/usr/bin/python3",
                    WorkingDirectory = Path.Combine(fixtureRoot, request.WorkingDirectory),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                )

            start.Environment.Clear()
            start.Environment.Add("PATH", "/usr/sbin:/usr/bin:/bin")
            start.Environment.Add("PORTABLE_OUTPUT_ROOT", Path.Combine(fixtureRoot, "artifact"))

            for argument in request.Arguments do
                start.ArgumentList.Add argument

            use child = Process.Start start
            do! child.WaitForExitAsync cancellationToken
            let! stdout = child.StandardOutput.ReadToEndAsync cancellationToken
            let! stderr = child.StandardError.ReadToEndAsync cancellationToken
            observedOutput <- stdout

            return
                {
                    ExecutionStarted = true
                    ExitCode = Some child.ExitCode
                    StandardOutput = Encoding.UTF8.GetBytes stdout
                    StandardError = Encoding.UTF8.GetBytes stderr
                    CancellationRequested = false
                    TerminationObserved = true
                    Interrupted = false
                    OutputLimitExceeded = false
                    OutputComplete = true
                    SourceTree = Some PortableWorkspacePythonHelloQualification.SourceRevision
                    SnapshotSha256 = Some(String.replicate 64 "a")
                    RuntimeIdentity = Some "compiled-test-host-python"
                    ContainerIdentity = Some "compiled-test-process"
                    VerificationObserved = true
                    VerificationOutput =
                        Some(Encoding.UTF8.GetBytes "{\"outcome\":\"passed\",\"verification\":\"python-test-v1\"}\n")
                    VerificationCustodyLimitExceeded = false
                    Refusal = None
                }
        }

    member _.Calls = calls
    member _.ObservedRequest = observedRequest
    member _.ObservedOutput = observedOutput
    member _.CleanupAllowed
        with set value = cleanupAllowed <- value
    member _.CleanupThrows
        with set value = cleanupThrows <- value

    interface IPortableProcessRunner with
        member _.RunAsync(request, cancellationToken) = run request cancellationToken

        member _.RecoverAsync(request, cancellationToken) =
            run request cancellationToken

        member _.CleanupAsync(_, _) =
            if cleanupThrows then
                raise (IOException "scripted cleanup failure")
            Task.FromResult cleanupAllowed

type private TemporaryDirectory() =
    let path = Path.Combine(Path.GetTempPath(), "portable-runtime-command-" + Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory path |> ignore
    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists path then
                Directory.Delete(path, true)

type PortableWorkspaceRuntimeCommandTests() =
    let portableNow () =
        let value = DateTimeOffset.UtcNow
        DateTimeOffset(value.Ticks - value.Ticks % 10L, TimeSpan.Zero)

    let runtimePolicy stateRoot =
        {
            GitExecutable = "/usr/bin/git"
            TarExecutable = "/usr/bin/tar"
            PodmanExecutable = "/usr/bin/podman"
            PodmanGlobalArguments = []
            StateRoot = stateRoot
            ContainerPath = "/usr/local/bin:/usr/bin:/bin"
            ContainerUser = "32768:32768"
            HostEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/local/bin:/usr/bin:/bin" ]
            ContainerEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/local/bin:/usr/bin:/bin" ]
            MaximumSnapshotBytes = 16UL * 1024UL * 1024UL
            TerminationGrace = TimeSpan.FromSeconds 5.0
        }

    let command (profile: PortableWorkspaceProfile) (now: DateTimeOffset) =
        {
            CommandId = Guid.NewGuid()
            IdempotencyId = "p4-python-hello-" + Guid.NewGuid().ToString("N")
            WorkspaceScope = profile.WorkspaceScope
            ProfileId = profile.ProfileId
            ProfileRevision = profile.Revision
            SourceRevision = profile.SourceRevision
            ExpectedWorkflowRevision = 1UL
            FenceGeneration = 1UL
            CausationId = None
            Deadline = now.AddMinutes 2.0
            Operation = "test"
            ComponentId = Some "python"
        }

    let writeInputs root (profile: PortableWorkspaceProfile) (command: PortableWorkspaceCommand) =
        let profilePath = Path.Combine(root, "profile.json")
        let commandPath = Path.Combine(root, "command.json")
        File.WriteAllBytes(profilePath, PortableWorkspaceContract.profileBytes profile |> Result.defaultWith failwith)
        File.WriteAllBytes(commandPath, PortableWorkspaceContract.commandBytes command |> Result.defaultWith failwith)
        profilePath, commandPath

    let rec repositoryRoot (directory: DirectoryInfo) =
        let candidate = Path.Combine(directory.FullName, "tests", "portable-workspace", "trusted-provider", "provider_facts.py")
        if File.Exists candidate then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else repositoryRoot directory.Parent

    [<Fact>]
    member _.``compiled command invokes the fixed Python Hello journey and replays without relaunch``() =
        task {
            use temporary = new TemporaryDirectory()
            let fixture = Path.Combine(AppContext.BaseDirectory, "portable-python-hello")
            let state = Path.Combine(temporary.Path, "state")
            let now = portableNow ()

            let enrollment =
                PortableWorkspacePythonHelloQualification.create
                    temporary.Path
                    state
                    (now.AddMinutes(-1.0))
                    (runtimePolicy state)

            let selected = command enrollment.Profile now
            let profilePath, commandPath = writeInputs temporary.Path enrollment.Profile selected
            let runner = PythonHelloRunner fixture

            let dependencies =
                {
                    Enrollments = EnrollmentSource enrollment
                    CreateRunner = fun _ -> runner
                    Clock = portableNow
                }

            let arguments =
                [|
                    "execute"
                    "--enrollment"
                    PortableWorkspacePythonHelloQualification.EnrollmentId
                    "--operation-id"
                    PortableWorkspacePythonHelloQualification.OperationId
                    "--profile"
                    profilePath
                    "--command"
                    commandPath
                |]

            let! first = PortableWorkspaceRuntimeCommand.executeAsync dependencies arguments CancellationToken.None
            let! duplicate = PortableWorkspaceRuntimeCommand.executeAsync dependencies arguments CancellationToken.None

            Assert.True(first.ExitCode = 0, first.StandardError)
            Assert.Contains("\"outcome\":\"completed\"", first.StandardOutput)
            Assert.Equal(0, duplicate.ExitCode)
            Assert.Contains("\"outcome\":\"duplicate\"", duplicate.StandardOutput)
            Assert.Equal(1, runner.Calls)

            let request = runner.ObservedRequest |> Option.defaultWith (fun () -> failwith "missing request")
            Assert.Equal("/usr/local/bin/python3", request.Executable)
            Assert.Equal<string list>([ "test.py" ], request.Arguments)
            Assert.Equal("python", request.WorkingDirectory)
            Assert.Equal("python-build-ok\npython-test-ok\n", runner.ObservedOutput)
        }

    [<Fact>]
    member _.``completed process remains incomplete until cleanup recovery is observed``() =
        task {
            use temporary = new TemporaryDirectory()
            let fixture = Path.Combine(AppContext.BaseDirectory, "portable-python-hello")
            let state = Path.Combine(temporary.Path, "state")
            let now = portableNow ()

            let enrollment =
                PortableWorkspacePythonHelloQualification.create
                    temporary.Path
                    state
                    (now.AddMinutes(-1.0))
                    (runtimePolicy state)

            let selected = command enrollment.Profile now
            let profilePath, commandPath = writeInputs temporary.Path enrollment.Profile selected
            let runner = PythonHelloRunner fixture
            runner.CleanupAllowed <- false

            let dependencies =
                {
                    Enrollments = EnrollmentSource enrollment
                    CreateRunner = fun _ -> runner
                    Clock = portableNow
                }

            let executeArguments =
                [|
                    "execute"; "--enrollment"; PortableWorkspacePythonHelloQualification.EnrollmentId
                    "--operation-id"; PortableWorkspacePythonHelloQualification.OperationId
                    "--profile"; profilePath; "--command"; commandPath
                |]

            let recoverArguments = Array.copy executeArguments
            recoverArguments[0] <- "recover"

            let! first = PortableWorkspaceRuntimeCommand.executeAsync dependencies executeArguments CancellationToken.None
            runner.CleanupThrows <- true
            let! duplicate = PortableWorkspaceRuntimeCommand.executeAsync dependencies executeArguments CancellationToken.None
            let! unresolvedRecovery = PortableWorkspaceRuntimeCommand.executeAsync dependencies recoverArguments CancellationToken.None

            for incomplete in [ first; duplicate; unresolvedRecovery ] do
                Assert.Equal(5, incomplete.ExitCode)
                Assert.Equal("portable-runtime-cleanup-incomplete", incomplete.StandardError)
                Assert.Contains("\"outcome\":\"cleanup-incomplete\"", incomplete.StandardOutput)
                Assert.Contains("\"cleanupCompleted\":false", incomplete.StandardOutput)
                use document = JsonDocument.Parse incomplete.StandardOutput
                let exitCode = document.RootElement.GetProperty("result").GetProperty("exitCode")
                Assert.Equal("known", exitCode.GetProperty("state").GetString())
                Assert.Equal(0, exitCode.GetProperty("value").GetInt32())

            runner.CleanupThrows <- false
            runner.CleanupAllowed <- true
            let! settledRecovery = PortableWorkspaceRuntimeCommand.executeAsync dependencies recoverArguments CancellationToken.None

            Assert.Equal(0, settledRecovery.ExitCode)
            Assert.Equal("", settledRecovery.StandardError)
            Assert.Contains("\"outcome\":\"duplicate\"", settledRecovery.StandardOutput)
            Assert.Contains("\"cleanupCompleted\":true", settledRecovery.StandardOutput)
            Assert.Equal(1, runner.Calls)
        }

    [<Fact>]
    member _.``unreviewed operation and altered profile refuse before runner creation``() =
        task {
            use temporary = new TemporaryDirectory()
            let state = Path.Combine(temporary.Path, "state")
            let now = portableNow ()

            let enrollment =
                PortableWorkspacePythonHelloQualification.create
                    temporary.Path
                    state
                    (now.AddMinutes(-1.0))
                    (runtimePolicy state)

            let selected = command enrollment.Profile now
            let profilePath, commandPath = writeInputs temporary.Path enrollment.Profile selected
            let mutable runnerCreations = 0

            let dependencies =
                {
                    Enrollments = EnrollmentSource enrollment
                    CreateRunner = fun _ ->
                        runnerCreations <- runnerCreations + 1
                        PythonHelloRunner temporary.Path
                    Clock = portableNow
                }

            let! unreviewed =
                PortableWorkspaceRuntimeCommand.executeAsync
                    dependencies
                    [|
                        "execute"
                        "--enrollment"
                        PortableWorkspacePythonHelloQualification.EnrollmentId
                        "--operation-id"
                        "caller-chosen-command"
                        "--profile"
                        profilePath
                        "--command"
                        commandPath
                    |]
                    CancellationToken.None

            let alteredProfile = { enrollment.Profile with QualifiedImage = enrollment.Profile.QualifiedImage.Replace("6cc4", "7cc4") }
            let alteredPath, _ = writeInputs temporary.Path alteredProfile selected

            let! altered =
                PortableWorkspaceRuntimeCommand.executeAsync
                    dependencies
                    [|
                        "execute"
                        "--enrollment"
                        PortableWorkspacePythonHelloQualification.EnrollmentId
                        "--operation-id"
                        PortableWorkspacePythonHelloQualification.OperationId
                        "--profile"
                        alteredPath
                        "--command"
                        commandPath
                    |]
                    CancellationToken.None

            Assert.Equal(3, unreviewed.ExitCode)
            Assert.Equal("portable-runtime-operation-not-enrolled", unreviewed.StandardError)
            Assert.Equal(3, altered.ExitCode)
            Assert.Equal("portable-runtime-profile-not-enrolled", altered.StandardError)
            Assert.Equal(0, runnerCreations)
        }

    [<Fact>]
    member _.``provider contract generator round trips through production codecs``() =
        use temporary = new TemporaryDirectory()
        let root = repositoryRoot (DirectoryInfo(Environment.CurrentDirectory))
        let factsPath = Path.Combine(temporary.Path, "facts.json")
        let grantPath = Path.Combine(temporary.Path, "grant.json")
        let profilePath = Path.Combine(temporary.Path, "profile.json")
        let commandPath = Path.Combine(temporary.Path, "command.json")
        let receiver = String.replicate 40 "a"
        File.WriteAllText(factsPath, JsonSerializer.Serialize({| receiver = {| commit = receiver |} |}))
        File.WriteAllText(grantPath, """{"workflowRevision":7,"fenceGeneration":9}""")
        let start = ProcessStartInfo("/usr/bin/python3", UseShellExecute = false)
        for argument in
            [ "tests/portable-workspace/trusted-provider/provider_facts.py"; "contract"
              "--facts"; factsPath; "--grant"; grantPath; "--receiver-commit"; receiver
              "--profile"; profilePath; "--command"; commandPath ] do
            start.ArgumentList.Add argument
        start.WorkingDirectory <- root
        use child = Process.Start start
        child.WaitForExit()
        Assert.Equal(0, child.ExitCode)
        match PortableWorkspaceContract.parseProfile(File.ReadAllBytes profilePath) with
        | Error reason -> failwith reason
        | Ok profile -> Assert.Equal(receiver, profile.SourceRevision)
        match PortableWorkspaceContract.parseCommand(File.ReadAllBytes commandPath) with
        | Error reason -> failwith reason
        | Ok command ->
            Assert.Equal(7UL, command.ExpectedWorkflowRevision)
            Assert.Equal(9UL, command.FenceGeneration)
