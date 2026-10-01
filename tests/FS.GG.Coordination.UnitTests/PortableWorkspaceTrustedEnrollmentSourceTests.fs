namespace FS.GG.Coordination.PortableWorkspace.TrustedEnrollmentSourceTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Cli
open FS.GG.Coordination.Orchestration.Execution
open Xunit

type private GrantReader(bytes: byte array, userId: uint32) =
    interface IPortableWorkspaceTrustedGrantReader with
        member _.Read() = Ok { Bytes = bytes; EffectiveUserId = userId }

type private RefusingGrantReader(reason: string) =
    interface IPortableWorkspaceTrustedGrantReader with
        member _.Read() = Error reason

type private Inspector(result: Result<unit, string>) =
    let mutable calls = 0
    interface IPortableWorkspaceTrustedReceiverInspector with
        member _.Validate _ = calls <- calls + 1; result
    member _.Calls = calls

type private RuntimePreparer(result: Result<unit, string>) =
    let mutable calls = 0
    interface IPortableWorkspacePrivateRuntimePreparer with
        member _.Prepare _ = calls <- calls + 1; result
    member _.Calls = calls

type private RecordingRunner() =
    let mutable calls = 0
    let mutable cancellable = false
    let run (cancellationToken: CancellationToken) =
        calls <- calls + 1
        cancellable <- cancellationToken.CanBeCanceled
        Task.FromResult
            {
                ExecutionStarted = true; ExitCode = Some 0; StandardOutput = Encoding.UTF8.GetBytes "python-test-ok\n"
                StandardError = Array.empty; CancellationRequested = cancellationToken.IsCancellationRequested; TerminationObserved = true
                Interrupted = false; OutputLimitExceeded = false; OutputComplete = true
                SourceTree = Some(String.replicate 64 "a"); SnapshotSha256 = Some(String.replicate 64 "b")
                RuntimeIdentity = Some "trusted-source-test"; ContainerIdentity = Some "trusted-container-test"
                VerificationObserved = true
                VerificationOutput = Some(Encoding.UTF8.GetBytes "{\"outcome\":\"passed\",\"verification\":\"python-test-v1\"}\n")
                VerificationCustodyLimitExceeded = false; Refusal = None
            }
    interface IPortableProcessRunner with
        member _.RunAsync(_, cancellationToken) = run cancellationToken
        member _.RecoverAsync(_, cancellationToken) = run cancellationToken
        member _.CleanupAsync(_, _) = Task.FromResult true
    member _.Calls = calls
    member _.CancellableTokenObserved = cancellable

type private TemporaryDirectory() =
    let path = Path.Combine(Path.GetTempPath(), "trusted-portable-runtime-" + Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory path |> ignore
    member _.Path = path
    interface IDisposable with
        member _.Dispose() = if Directory.Exists path then Directory.Delete(path, true)

[<CollectionDefinition("trusted-portable-process-environment", DisableParallelization = true)>]
type TrustedPortableProcessEnvironmentCollection() = class end

module private Fixture =
    let sha = String.replicate 64 "a"
    let archiveSha = String.replicate 64 "b"
    let receiptSha = String.replicate 64 "c"
    let commit = "698b60638eb6c2a262601f75a7b99bf53ffed21a"
    let tree = "cc6bb7a315fde6761073f950e6fa87d7bf69555b"
    let scope = "fs-gg/receiver-python-hello"

    let bytes (edit: (JsonObject -> unit) option) =
        let profileSha = PortableWorkspacePythonHelloPolicy.profileDigest scope commit |> Result.defaultWith failwith
        let root =
            JsonNode.Parse(
                JsonSerializer.Serialize
                    {|
                        schema = "fsgg.portable-workspace-python-enrollment/v1"
                        enrollmentId = PortableWorkspacePythonHelloPolicy.EnrollmentId
                        grantId = "grant-python-1"
                        allowedUid = 1000u
                        cli = {| version = "0.2.1"; packageSha256 = sha; payloadSha256 = sha |}
                        provider = {| version = "0.1.0"; packageSha256 = sha; producerSourceRevision = commit |}
                        receiver =
                            {|
                                repositoryPath = "/tmp/receiver-python"
                                commit = commit
                                tree = tree
                                projectedPayload = [| {| path = "python/test.py"; sha256 = sha |} |]
                            |}
                        profileSha256 = profileSha
                        workspaceScope = scope
                        workflowRevision = 7UL
                        fenceGeneration = 9UL
                        observedAt = "2026-09-30T20:00:00.123456Z"
                        stateRoot = "/tmp/receiver-python-state"
                        executables =
                            {|
                                git = {| path = "/usr/bin/git"; sha256 = sha |}
                                tar = {| path = "/usr/bin/tar"; sha256 = sha |}
                                podman = {| path = "/usr/bin/podman"; sha256 = sha |}
                            |}
                        image =
                            {|
                                qualifiedImage = PortableWorkspacePythonHelloPolicy.QualifiedImage
                                archiveSha256 = archiveSha
                                manifestDigest = PortableWorkspacePythonHelloPolicy.ImageManifestDigest
                                configDigest = PortableWorkspacePythonHelloPolicy.ImageConfigDigest
                                recipeSha256 = receiptSha
                            |}
                    |})
                .AsObject()
        edit |> Option.iter (fun apply -> apply root)
        Encoding.UTF8.GetBytes(root.ToJsonString())

    let directGrant archive receipt =
        {
            EnrollmentId = PortableWorkspacePythonHelloPolicy.EnrollmentId; GrantId = "direct-policy"
            AllowedUserId = 1000u; CliVersion = "0.2.1"; CliPackageSha256 = sha; CliPayloadSha256 = sha
            ProviderVersion = "0.1.0"; ProviderPackageSha256 = sha; ProducerSourceRevision = commit
            WorkspaceRoot = "/tmp/receiver-python"; ReceiverCommit = commit; ReceiverTree = tree
            ProjectedPayload = [ { Path = "python/test.py"; Sha256 = sha } ]
            ProfileSha256 = PortableWorkspacePythonHelloPolicy.profileDigest scope commit |> Result.defaultWith failwith
            WorkspaceScope = scope; WorkflowRevision = 7UL; FenceGeneration = 9UL
            ObservedAt = DateTimeOffset(2026, 9, 30, 20, 0, 0, TimeSpan.Zero)
            JournalStateRoot = "/tmp/receiver-python-state"
            Git = { Path = "/usr/bin/git"; Sha256 = sha }
            Tar = { Path = "/usr/bin/tar"; Sha256 = sha }
            Podman = { Path = "/usr/bin/podman"; Sha256 = sha }
            QualifiedImage = PortableWorkspacePythonHelloPolicy.QualifiedImage
            ImageArchiveSha256 = archive
            ImageManifestDigest = PortableWorkspacePythonHelloPolicy.ImageManifestDigest
            ImageConfigDigest = PortableWorkspacePythonHelloPolicy.ImageConfigDigest
            ImageRecipeSha256 = receipt
        }

    let source bytes userId inspection =
        PortableWorkspaceTrustedEnrollmentSource(GrantReader(bytes, userId), inspection, RuntimePreparer(Ok()))
        :> IPortableWorkspaceRuntimeEnrollmentSource

    let command (profile: PortableWorkspaceProfile) workflow fence operation =
        let now = DateTimeOffset(2026, 9, 30, 20, 0, 1, TimeSpan.Zero)
        {
            CommandId = Guid.Parse "8aa50000-0000-0000-0000-000000000001"
            IdempotencyId = "trusted-python-test-1"
            WorkspaceScope = profile.WorkspaceScope; ProfileId = profile.ProfileId; ProfileRevision = profile.Revision
            SourceRevision = profile.SourceRevision; ExpectedWorkflowRevision = workflow; FenceGeneration = fence
            CausationId = None; Deadline = now.AddMinutes 1.0; Operation = operation; ComponentId = Some "python"
        }

    let fileSha path =
        use stream = File.OpenRead path
        Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

    let installedPayloadSha (entryPath: string) =
        let root = Path.GetDirectoryName entryPath
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.map (fun path ->
            let relative = Path.GetRelativePath(root, path).Replace('\\', '/')
            let size = FileInfo(path).Length
            relative, fileSha path, size)
        |> Seq.sortWith (fun (left, _, _) (right, _, _) -> StringComparer.Ordinal.Compare(left, right))
        |> Seq.map (fun (path, digest, size) -> $"%s{digest} %d{size} %s{path}\n")
        |> String.concat ""
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let userId () =
        let start = ProcessStartInfo("/usr/bin/id", RedirectStandardOutput = true, UseShellExecute = false)
        start.ArgumentList.Add "-u"
        use child = Process.Start start
        let value = child.StandardOutput.ReadToEnd().Trim() |> UInt32.Parse
        child.WaitForExit()
        Assert.Equal(0, child.ExitCode)
        value

    let git root arguments =
        let start = ProcessStartInfo("/usr/bin/git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
        start.ArgumentList.Add "-C"
        start.ArgumentList.Add root
        arguments |> List.iter start.ArgumentList.Add
        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd()
        child.StandardError.ReadToEnd() |> ignore
        child.WaitForExit()
        Assert.Equal(0, child.ExitCode)
        output.Trim()

[<Collection("trusted-portable-process-environment")>]
type PortableWorkspaceTrustedEnrollmentSourceTests() =
    [<Fact>]
    member _.``administrator path policy rejects links caller ownership and delegated writes``() =
        let baseline =
            {
                IsDirectory = true; IsSymbolicLink = false; OwnerUserId = 0u; GroupOrOtherWritable = false
                AccessAclAdministratorOnlyWritable = true; DefaultAclAdministratorOnlyWritable = true
            }
        Assert.True(PortableWorkspaceTrustedFileSecurity.validate true baseline)
        Assert.False(PortableWorkspaceTrustedFileSecurity.validate true { baseline with IsSymbolicLink = true })
        Assert.False(PortableWorkspaceTrustedFileSecurity.validate true { baseline with OwnerUserId = 1000u })
        Assert.False(PortableWorkspaceTrustedFileSecurity.validate true { baseline with GroupOrOtherWritable = true })
        Assert.False(PortableWorkspaceTrustedFileSecurity.validate true { baseline with AccessAclAdministratorOnlyWritable = false })
        Assert.False(PortableWorkspaceTrustedFileSecurity.validate true { baseline with DefaultAclAdministratorOnlyWritable = false })

    [<Fact>]
    member _.``exact trusted grant resolves only the compiled Python test policy``() =
        let inspector = Inspector(Ok())
        let source = Fixture.source (Fixture.bytes None) 1000u inspector
        let enrollment = source.Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId |> Result.defaultWith failwith
        Assert.Equal(1, inspector.Calls)
        Assert.Equal(Fixture.commit, enrollment.Profile.SourceRevision)
        let selection = Assert.Single enrollment.Selections
        Assert.Equal(PortableWorkspacePythonHelloPolicy.OperationId, selection.OperationId)
        let operation = Assert.Single enrollment.Policy.Operations
        Assert.Equal("/usr/local/bin/python3", operation.Executable)
        Assert.Equal<string list>([ "test.py" ], operation.Arguments)
        Assert.Equal(Fixture.receiptSha, operation.RecipeSha256)
        Assert.Equal<uint64>(7UL, enrollment.Authority.WorkflowRevision)
        Assert.Equal<uint64>(9UL, enrollment.Authority.FenceGeneration)

    [<Fact>]
    member _.``untrusted schema uid tree profile semantic image and custody shapes refuse before execution``() =
        let resolve bytes uid result =
            let inspector = Inspector(result)
            (Fixture.source bytes uid inspector).Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId
        let unknown = Fixture.bytes(Some(fun root -> root["schema"] <- "unknown/v2"))
        let extra = Fixture.bytes(Some(fun root -> root["callerApproval"] <- true))
        let wrongProfile = Fixture.bytes(Some(fun root -> root["profileSha256"] <- String.replicate 64 "f"))
        let wrongImage = Fixture.bytes(Some(fun root -> root["image"].AsObject()["qualifiedImage"] <- "localhost/unreviewed@sha256:" + Fixture.sha))
        let malformedArchive = Fixture.bytes(Some(fun root -> root["image"].AsObject()["archiveSha256"] <- String.replicate 64 "A"))
        let wrongManifest = Fixture.bytes(Some(fun root -> root["image"].AsObject()["manifestDigest"] <- Fixture.sha))
        let wrongConfig = Fixture.bytes(Some(fun root -> root["image"].AsObject()["configDigest"] <- Fixture.sha))
        let malformedRecipe = Fixture.bytes(Some(fun root -> root["image"].AsObject()["recipeSha256"] <- "short"))
        Assert.Equal(Error "portable-trusted-grant-schema-refused", resolve unknown 1000u (Ok()))
        Assert.Equal(Error "portable-trusted-grant-schema-refused", resolve extra 1000u (Ok()))
        Assert.Equal(Error "portable-trusted-runtime-uid-refused", resolve (Fixture.bytes None) 1001u (Ok()))
        Assert.Equal(Error "portable-trusted-receiver-tree-refused", resolve (Fixture.bytes None) 1000u (Error "portable-trusted-receiver-tree-refused"))
        Assert.Equal(Error "portable-runtime-profile-digest-refused", resolve wrongProfile 1000u (Ok()))
        Assert.Equal(Error "portable-runtime-image-binding-refused", resolve wrongImage 1000u (Ok()))
        Assert.Equal(Error "portable-trusted-grant-field-refused", resolve malformedArchive 1000u (Ok()))
        Assert.Equal(Error "portable-runtime-image-binding-refused", resolve wrongManifest 1000u (Ok()))
        Assert.Equal(Error "portable-runtime-image-binding-refused", resolve wrongConfig 1000u (Ok()))
        Assert.Equal(Error "portable-trusted-grant-field-refused", resolve malformedRecipe 1000u (Ok()))

    [<Fact>]
    member _.``authenticated transport varies while receipt remains in the durable operation binding``() =
        let changedArchive = String.replicate 64 "d"
        let changedReceipt = String.replicate 64 "e"
        let changed =
            Fixture.bytes(
                Some(fun root ->
                    root["image"].AsObject()["archiveSha256"] <- changedArchive
                    root["image"].AsObject()["recipeSha256"] <- changedReceipt))
        let enrollment =
            (Fixture.source changed 1000u (Inspector(Ok()))).Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId
            |> Result.defaultWith failwith
        let operation = Assert.Single enrollment.Policy.Operations
        Assert.Equal(changedReceipt, operation.RecipeSha256)
        Assert.NotEqual(Fixture.receiptSha, operation.RecipeSha256)
        Assert.Equal(
            Error "portable-runtime-image-binding-refused",
            PortableWorkspacePythonHelloPolicy.create (Fixture.directGrant "ABC" changedReceipt))
        Assert.Equal(
            Error "portable-runtime-image-binding-refused",
            PortableWorkspacePythonHelloPolicy.create (Fixture.directGrant changedArchive (String.replicate 64 "A")))

    [<Fact>]
    member _.``missing trusted grant refuses without resolving caller profile``() =
        let source = PortableWorkspaceTrustedEnrollmentSource(RefusingGrantReader("portable-trusted-grant-missing"), Inspector(Ok()), RuntimePreparer(Ok()))
        let outcome = (source :> IPortableWorkspaceRuntimeEnrollmentSource).Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId
        Assert.Equal(Error "portable-trusted-grant-missing", outcome)

    [<Fact>]
    member _.``production inspector verifies actual clean commit tree inventory executables and cli``() =
        use temporary = new TemporaryDirectory()
        let receiver = Path.Combine(temporary.Path, "receiver")
        Directory.CreateDirectory(Path.Combine(receiver, "python")) |> ignore
        File.WriteAllText(Path.Combine(receiver, "python", "test.py"), "print('ok')\n")
        File.WriteAllText(Path.Combine(receiver, ".gitattributes"), "python/test.py filter=hostile\n")
        Fixture.git receiver [ "init"; "-q" ] |> ignore
        Fixture.git receiver [ "config"; "user.email"; "source-test@example.invalid" ] |> ignore
        Fixture.git receiver [ "config"; "user.name"; "source-test" ] |> ignore
        Fixture.git receiver [ "add"; ".gitattributes"; "python/test.py" ] |> ignore
        Fixture.git receiver [ "commit"; "-q"; "-m"; "fixture" ] |> ignore
        let commit = Fixture.git receiver [ "rev-parse"; "HEAD" ]
        let tree = Fixture.git receiver [ "rev-parse"; "HEAD^{tree}" ]
        let entry = System.Reflection.Assembly.GetEntryAssembly()
        let value = entry.GetName().Version
        let cliVersion = $"%d{value.Major}.%d{value.Minor}.%d{value.Build}"
        let grant =
            {
                EnrollmentId = PortableWorkspacePythonHelloPolicy.EnrollmentId; GrantId = "actual-inspector"
                AllowedUserId = 1000u; CliVersion = cliVersion; CliPackageSha256 = Fixture.sha
                CliPayloadSha256 = Fixture.installedPayloadSha entry.Location; ProviderVersion = "0.1.0"
                ProviderPackageSha256 = Fixture.sha; ProducerSourceRevision = Fixture.commit
                WorkspaceRoot = receiver; ReceiverCommit = commit; ReceiverTree = tree
                ProjectedPayload =
                    [ { Path = ".gitattributes"; Sha256 = Fixture.fileSha(Path.Combine(receiver, ".gitattributes")) }
                      { Path = "python/test.py"; Sha256 = Fixture.fileSha(Path.Combine(receiver, "python", "test.py")) } ]
                ProfileSha256 = Fixture.sha; WorkspaceScope = Fixture.scope; WorkflowRevision = 1UL; FenceGeneration = 1UL
                ObservedAt = DateTimeOffset.UtcNow; JournalStateRoot = Path.Combine(temporary.Path, "state")
                Git = { Path = "/usr/bin/git"; Sha256 = Fixture.fileSha "/usr/bin/git" }
                Tar = { Path = "/usr/bin/tar"; Sha256 = Fixture.fileSha "/usr/bin/tar" }
                Podman = { Path = "/usr/bin/podman"; Sha256 = Fixture.fileSha "/usr/bin/podman" }
                QualifiedImage = PortableWorkspacePythonHelloPolicy.QualifiedImage
                ImageArchiveSha256 = Fixture.archiveSha
                ImageManifestDigest = PortableWorkspacePythonHelloPolicy.ImageManifestDigest
                ImageConfigDigest = PortableWorkspacePythonHelloPolicy.ImageConfigDigest
                ImageRecipeSha256 = Fixture.receiptSha
            }
        let inspector = LinuxPortableWorkspaceTrustedReceiverInspector() :> IPortableWorkspaceTrustedReceiverInspector
        let initialInspection = inspector.Validate grant
        Assert.True((initialInspection = Ok()), sprintf "Initial inspection failed: %A" initialInspection)

        let marker = Path.Combine(temporary.Path, "hostile-git-executed")
        let fsmonitor = Path.Combine(temporary.Path, "slow-oversized-git-helper")
        File.WriteAllText(fsmonitor, $"#!/bin/sh\ntouch '%s{marker}'\ndd if=/dev/zero bs=1048576 count=8 2>/dev/null\nsleep 30\n")
        File.SetUnixFileMode(fsmonitor, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        Fixture.git receiver [ "config"; "core.fsmonitor"; fsmonitor ] |> ignore
        Fixture.git receiver [ "config"; "filter.hostile.clean"; fsmonitor ] |> ignore
        File.SetLastWriteTimeUtc(Path.Combine(receiver, "python", "test.py"), DateTime.UtcNow.AddMinutes 1.0)
        let oldGitDir = Environment.GetEnvironmentVariable "GIT_DIR"
        let oldGitConfig = Environment.GetEnvironmentVariable "GIT_CONFIG_GLOBAL"
        let hostileGlobal = Path.Combine(temporary.Path, "hostile.gitconfig")
        File.WriteAllText(hostileGlobal, $"[core]\n\tfsmonitor = %s{fsmonitor}\n[filter \"hostile\"]\n\tclean = %s{fsmonitor}\n")
        try
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(temporary.Path, "redirected.git"))
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", hostileGlobal)
            let elapsed = Stopwatch.StartNew()
            Assert.Equal(Ok(), inspector.Validate grant)
            elapsed.Stop()
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds 4.0, $"Git inspection took %O{elapsed.Elapsed}")
            Assert.False(File.Exists marker)
        finally
            Environment.SetEnvironmentVariable("GIT_DIR", oldGitDir)
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", oldGitConfig)
        Assert.Equal(Error "portable-trusted-receiver-tree-refused", inspector.Validate { grant with ReceiverTree = Fixture.tree })

        let productFile = Path.Combine(receiver, "python", "test.py")
        File.Delete productFile
        let fifoStart = ProcessStartInfo("/usr/bin/mkfifo", UseShellExecute = false)
        fifoStart.ArgumentList.Add productFile
        use fifo = Process.Start fifoStart
        fifo.WaitForExit()
        Assert.Equal(0, fifo.ExitCode)
        let fifoElapsed = Stopwatch.StartNew()
        Assert.Equal(Error "portable-trusted-receiver-layout-refused", inspector.Validate grant)
        fifoElapsed.Stop()
        Assert.True(fifoElapsed.Elapsed < TimeSpan.FromSeconds 2.0, $"FIFO refusal took %O{fifoElapsed.Elapsed}")
        File.Delete productFile
        File.WriteAllText(productFile, "print('ok')\n")

        let extra = Path.Combine(receiver, "untracked.txt")
        File.WriteAllText(extra, "extra")
        Assert.Equal(Error "portable-trusted-receiver-layout-refused", inspector.Validate grant)
        File.Delete extra

        let mutable deep = receiver
        for index in 1 .. 66 do
            deep <- Path.Combine(deep, $"d%d{index}")
            Directory.CreateDirectory deep |> ignore
        Assert.Equal(Error "portable-trusted-receiver-layout-refused", inspector.Validate grant)
        Directory.Delete(Path.Combine(receiver, "d1"), true)

        use oversized = new FileStream(productFile, FileMode.Create, FileAccess.Write, FileShare.None)
        oversized.SetLength(16L * 1024L * 1024L + 1L)
        oversized.Dispose()
        Assert.Equal(Error "portable-trusted-receiver-layout-refused", inspector.Validate grant)
        File.WriteAllText(productFile, "print('ok')\n# changed\n")
        Assert.Equal(Error "portable-trusted-receiver-dirty-refused", inspector.Validate grant)

    [<Fact>]
    member _.``private runtime layout scopes Podman state and refuses tampered config``() =
        use temporary = new TemporaryDirectory()
        let stateRoot = Path.Combine(temporary.Path, "state")
        let uid = Fixture.userId()
        let bytes =
            Fixture.bytes(Some(fun root ->
                root["allowedUid"] <- uid
                root["stateRoot"] <- stateRoot))
        let source =
            PortableWorkspaceTrustedEnrollmentSource(
                GrantReader(bytes, uid), Inspector(Ok()), LinuxPortableWorkspacePrivateRuntimePreparer())
            :> IPortableWorkspaceRuntimeEnrollmentSource
        let oldHome = Environment.GetEnvironmentVariable "HOME"
        let oldContainers = Environment.GetEnvironmentVariable "CONTAINERS_CONF"
        let oldStorage = Environment.GetEnvironmentVariable "CONTAINERS_STORAGE_CONF"
        let enrollment =
            try
                Environment.SetEnvironmentVariable("HOME", "/tmp/hostile-home")
                Environment.SetEnvironmentVariable("CONTAINERS_CONF", "/tmp/hostile-containers.conf")
                Environment.SetEnvironmentVariable("CONTAINERS_STORAGE_CONF", "/tmp/hostile-storage.conf")
                source.Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId |> Result.defaultWith failwith
            finally
                Environment.SetEnvironmentVariable("HOME", oldHome)
                Environment.SetEnvironmentVariable("CONTAINERS_CONF", oldContainers)
                Environment.SetEnvironmentVariable("CONTAINERS_STORAGE_CONF", oldStorage)
        let layout = PortableWorkspacePythonHelloPolicy.privateRuntimeLayout stateRoot
        let expectedDirectoryMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        let expectedFileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        [ stateRoot; layout.Root; layout.Home; layout.ConfigRoot; Path.GetDirectoryName layout.ContainersConfig
          layout.RuntimeRoot; layout.StorageRoot; layout.RunRoot ]
        |> List.iter (fun path -> Assert.Equal(expectedDirectoryMode, File.GetUnixFileMode path))
        [ layout.ContainersConfig; layout.StorageConfig ]
        |> List.iter (fun path -> Assert.Equal(expectedFileMode, File.GetUnixFileMode path))
        Assert.Equal<string list>(
            [ "--storage-driver=vfs"; "--root"; layout.StorageRoot; "--runroot"; layout.RunRoot ],
            enrollment.Policy.Runtime.PodmanGlobalArguments)
        Assert.Equal(layout.Home, enrollment.Policy.Runtime.HostEnvironment["HOME"])
        Assert.Equal(layout.ContainersConfig, enrollment.Policy.Runtime.HostEnvironment["CONTAINERS_CONF"])
        Assert.Equal(layout.StorageConfig, enrollment.Policy.Runtime.HostEnvironment["CONTAINERS_STORAGE_CONF"])

        File.WriteAllText(layout.ContainersConfig, "[engine]\nhelper_binaries_dir=[\"/tmp/hostile\"]\n")
        Assert.Equal(Error "portable-trusted-runtime-config-refused", source.Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId)

    [<Fact>]
    member _.``private runtime layout refuses a preexisting symlink``() =
        use temporary = new TemporaryDirectory()
        let stateRoot = Path.Combine(temporary.Path, "state")
        Directory.CreateDirectory stateRoot |> ignore
        File.SetUnixFileMode(stateRoot, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        let outside = Path.Combine(temporary.Path, "outside")
        Directory.CreateDirectory outside |> ignore
        Directory.CreateSymbolicLink(Path.Combine(stateRoot, "runtime-v1"), outside) |> ignore
        let uid = Fixture.userId()
        let bytes =
            Fixture.bytes(Some(fun root ->
                root["allowedUid"] <- uid
                root["stateRoot"] <- stateRoot))
        let source =
            PortableWorkspaceTrustedEnrollmentSource(
                GrantReader(bytes, uid), Inspector(Ok()), LinuxPortableWorkspacePrivateRuntimePreparer())
            :> IPortableWorkspaceRuntimeEnrollmentSource
        Assert.Equal(Error "portable-trusted-runtime-layout-refused", source.Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId)

    [<Fact>]
    member _.``compiled Program routes portable workspace to fixed missing-grant refusal``() =
        use temporary = new TemporaryDirectory()
        let profilePath = Path.Combine(temporary.Path, "profile.json")
        let commandPath = Path.Combine(temporary.Path, "command.json")
        File.WriteAllText(profilePath, "{}")
        File.WriteAllText(commandPath, "{}")
        let cli = typeof<PortableWorkspaceRuntimeCommandResponse>.Assembly.Location
        let start = ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
        [ cli; "portable-workspace"; "execute"; "--enrollment"; PortableWorkspacePythonHelloPolicy.EnrollmentId;
          "--operation-id"; PortableWorkspacePythonHelloPolicy.OperationId; "--profile"; profilePath; "--command"; commandPath ]
        |> List.iter start.ArgumentList.Add
        use child = Process.Start start
        child.StandardOutput.ReadToEnd() |> ignore
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        Assert.Equal(3, child.ExitCode)
        Assert.StartsWith("portable-trusted-grant-", error)

    [<Fact>]
    member _.``resolver dispatch uses actual executor authority checks and forwards cancellation``() =
        task {
            use temporary = new TemporaryDirectory()
            let grantBytes = Fixture.bytes(Some(fun root -> root["stateRoot"] <- Path.Combine(temporary.Path, "state")))
            let source = Fixture.source grantBytes 1000u (Inspector(Ok()))
            let enrollment = source.Resolve PortableWorkspacePythonHelloPolicy.EnrollmentId |> Result.defaultWith failwith
            let profilePath = Path.Combine(temporary.Path, "profile.json")
            let commandPath = Path.Combine(temporary.Path, "command.json")
            File.WriteAllBytes(profilePath, PortableWorkspaceContract.profileBytes enrollment.Profile |> Result.defaultWith failwith)
            let runner = RecordingRunner()
            let dependencies =
                {
                    Enrollments = source
                    CreateRunner = fun _ -> runner :> IPortableProcessRunner
                    Clock = fun () -> DateTimeOffset(2026,9,30,20,0,2,TimeSpan.Zero)
                }
            let args =
                [| "execute"; "--enrollment"; PortableWorkspacePythonHelloPolicy.EnrollmentId; "--operation-id"; PortableWorkspacePythonHelloPolicy.OperationId; "--profile"; profilePath; "--command"; commandPath |]

            let wrongAuthority = Fixture.command enrollment.Profile 8UL 9UL "test"
            File.WriteAllBytes(commandPath, PortableWorkspaceContract.commandBytes wrongAuthority |> Result.defaultWith failwith)
            let! refused = PortableWorkspaceRuntimeCommand.executeAsync dependencies args CancellationToken.None
            Assert.Equal(3, refused.ExitCode)
            Assert.Equal(0, runner.Calls)

            let wrongOperation = Fixture.command enrollment.Profile 7UL 9UL "build"
            File.WriteAllBytes(commandPath, PortableWorkspaceContract.commandBytes wrongOperation |> Result.defaultWith failwith)
            let! operationRefused = PortableWorkspaceRuntimeCommand.executeAsync dependencies args CancellationToken.None
            Assert.Equal(3, operationRefused.ExitCode)
            Assert.Equal(0, runner.Calls)

            let accepted = Fixture.command enrollment.Profile 7UL 9UL "test"
            File.WriteAllBytes(commandPath, PortableWorkspaceContract.commandBytes accepted |> Result.defaultWith failwith)
            use cancellable = new CancellationTokenSource()
            let! completed = PortableWorkspaceRuntimeCommand.executeAsync dependencies args cancellable.Token
            Assert.True(completed.ExitCode = 0, completed.StandardError)
            Assert.True(runner.CancellableTokenObserved)
            Assert.Equal(1, runner.Calls)
        }

    [<Fact>]
    member _.``portable production clock is canonical UTC microseconds``() =
        let value = PortableWorkspacePythonHelloPolicy.portableClock()
        Assert.Equal(TimeSpan.Zero, value.Offset)
        Assert.Equal(0L, value.Ticks % 10L)
