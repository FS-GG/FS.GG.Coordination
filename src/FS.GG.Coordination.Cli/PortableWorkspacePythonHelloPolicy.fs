namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.Orchestration.Execution

type PortableWorkspaceTrustedExecutable = { Path: string; Sha256: string }

type PortableWorkspaceTrustedPayloadFile = { Path: string; Sha256: string }

type PortableWorkspacePythonHelloGrant =
    {
        EnrollmentId: string
        GrantId: string
        AllowedUserId: uint32
        CliVersion: string
        CliPackageSha256: string
        CliPayloadSha256: string
        ProviderVersion: string
        ProviderPackageSha256: string
        ProducerSourceRevision: string
        WorkspaceRoot: string
        ReceiverCommit: string
        ReceiverTree: string
        ProjectedPayload: PortableWorkspaceTrustedPayloadFile list
        ProfileSha256: string
        WorkspaceScope: string
        WorkflowRevision: uint64
        FenceGeneration: uint64
        ObservedAt: DateTimeOffset
        JournalStateRoot: string
        Git: PortableWorkspaceTrustedExecutable
        Tar: PortableWorkspaceTrustedExecutable
        Podman: PortableWorkspaceTrustedExecutable
        QualifiedImage: string
        ImageArchiveSha256: string
        ImageManifestDigest: string
        ImageConfigDigest: string
        ImageRecipeSha256: string
    }

type internal PortableWorkspacePrivateRuntimeLayout =
    {
        Root: string
        Home: string
        ConfigRoot: string
        RuntimeRoot: string
        StorageRoot: string
        RunRoot: string
        ContainersConfig: string
        StorageConfig: string
    }

[<RequireQualifiedAccess>]
module PortableWorkspacePythonHelloPolicy =
    [<Literal>]
    let EnrollmentId = "local-python-hello-v1"

    [<Literal>]
    let OperationId = "p4-python-hello-test-v1"

    [<Literal>]
    let QualifiedImage =
        "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2@sha256:40085dd0a7c3c16af6b24e247cec47707bc957d6453f7e15d82636fcbf6f0755"

    [<Literal>]
    let ImageManifestDigest = "40085dd0a7c3c16af6b24e247cec47707bc957d6453f7e15d82636fcbf6f0755"

    [<Literal>]
    let ImageConfigDigest = "371d2b5db7c9708812ca8c3d752376e38aa81432a8bcbe7d99146414636dd872"

    [<Literal>]
    let VerificationSha256 = "2ed645adefe2c23308832036a3b5163dc39faaf152c2c9d1d3afb3bd637f146a"

    let private validSha256 (value: string) =
        not (isNull value)
        && value.Length = 64
        && (value |> Seq.forall (fun character -> character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))

    let portableClock () =
        let value = DateTimeOffset.UtcNow
        DateTimeOffset(value.Ticks - value.Ticks % 10L, TimeSpan.Zero)

    let internal profile workspaceScope sourceRevision =
        {
            ProfileId = "portable-python-hello-v1"
            Revision = 1UL
            WorkspaceScope = workspaceScope
            SourceRevision = sourceRevision
            QualifiedImage = QualifiedImage
            Components =
                [
                    {
                        Id = "python"
                        Language = "python"
                        WorkingDirectory = "python"
                        Toolchain = { Id = "cpython"; Version = "3.14.0" }
                        EntryPoints =
                            {
                                Build = "python-build"
                                Test = "python-test"
                                Lint = None
                                Artifact = None
                            }
                    }
                ]
            ProductBuild = "unsupported-product-build"
            ProductTest = "unsupported-product-test"
            ProductJourney = "unsupported-product-journey"
            MaximumRuntimeSeconds = 60UL
            MaximumOutputBytes = 262144UL
        }

    let internal profileDigest workspaceScope sourceRevision =
        profile workspaceScope sourceRevision
        |> PortableWorkspaceContract.profileBytes
        |> Result.map PortableWorkspaceContract.digest

    let internal privateRuntimeLayout stateRoot =
        let root = IO.Path.Combine(stateRoot, "runtime-v1")
        let config = IO.Path.Combine(root, "xdg-config")
        let runtime = IO.Path.Combine(root, "xdg-runtime")
        {
            Root = root
            Home = IO.Path.Combine(root, "home")
            ConfigRoot = config
            RuntimeRoot = runtime
            StorageRoot = IO.Path.Combine(root, "storage")
            RunRoot = IO.Path.Combine(runtime, "containers-runroot")
            ContainersConfig = IO.Path.Combine(config, "containers", "containers.conf")
            StorageConfig = IO.Path.Combine(config, "containers", "storage.conf")
        }

    let create (grant: PortableWorkspacePythonHelloGrant) =
        if grant.EnrollmentId <> EnrollmentId then
            Error "portable-runtime-enrollment-not-found"
        elif grant.QualifiedImage <> QualifiedImage
             || grant.ImageManifestDigest <> ImageManifestDigest
             || grant.ImageConfigDigest <> ImageConfigDigest
             || not (validSha256 grant.ImageArchiveSha256)
             || not (validSha256 grant.ImageRecipeSha256) then
            Error "portable-runtime-image-binding-refused"
        else
            let profile = profile grant.WorkspaceScope grant.ReceiverCommit

            let profileSha256 =
                profileDigest grant.WorkspaceScope grant.ReceiverCommit

            match profileSha256 with
            | Error reason -> Error reason
            | Ok actual when actual <> grant.ProfileSha256 -> Error "portable-runtime-profile-digest-refused"
            | Ok _ ->
                let layout = privateRuntimeLayout grant.JournalStateRoot
                let operation =
                    {
                        EntryPoint = "python-test"
                        OperationIdentity = "test"
                        ComponentId = Some "python"
                        WorkingDirectory = "python"
                        QualifiedImage = QualifiedImage
                        RequiredToolchains = [ "cpython", "3.14.0" ]
                        Executable = "/usr/local/bin/python3"
                        Arguments = [ "test.py" ]
                        VerificationIdentity = "python-test-v1"
                        VerificationPath = "python-test.json"
                        VerificationSha256 = VerificationSha256
                        RecipeSha256 = grant.ImageRecipeSha256
                    }

                Ok
                    {
                        EnrollmentId = EnrollmentId
                        Profile = profile
                        Authority =
                            {
                                WorkspaceScope = profile.WorkspaceScope
                                WorkflowRevision = grant.WorkflowRevision
                                FenceGeneration = grant.FenceGeneration
                                ObservedAt = grant.ObservedAt
                            }
                        Policy =
                            {
                                WorkspaceRoot = grant.WorkspaceRoot
                                WorkspaceScope = profile.WorkspaceScope
                                SourceRevision = profile.SourceRevision
                                QualifiedImage = profile.QualifiedImage
                                MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                                MaximumOutputBytes = profile.MaximumOutputBytes
                                Operations = [ operation ]
                                Runtime =
                                    {
                                        GitExecutable = grant.Git.Path
                                        TarExecutable = grant.Tar.Path
                                        PodmanExecutable = grant.Podman.Path
                                        PodmanGlobalArguments =
                                            [ "--storage-driver=vfs"; "--root"; layout.StorageRoot; "--runroot"; layout.RunRoot ]
                                        StateRoot = grant.JournalStateRoot
                                        ContainerPath = "/usr/local/bin:/usr/bin:/bin"
                                        ContainerUser = "32768:32768"
                                        HostEnvironment =
                                            Map [
                                                "HOME", layout.Home
                                                "PATH", "/usr/local/bin:/usr/bin:/bin"
                                                "CONTAINERS_CONF", layout.ContainersConfig
                                                "CONTAINERS_STORAGE_CONF", layout.StorageConfig
                                            ]
                                        ContainerEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/local/bin:/usr/bin:/bin" ]
                                        MaximumSnapshotBytes = 16UL * 1024UL * 1024UL
                                        TerminationGrace = TimeSpan.FromSeconds 5.0
                                    }
                            }
                        Selections =
                            [
                                {
                                    OperationId = OperationId
                                    OperationIdentity = "test"
                                    ComponentId = Some "python"
                                    EntryPoint = "python-test"
                                }
                            ]
                    }
