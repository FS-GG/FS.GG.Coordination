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

[<RequireQualifiedAccess>]
module PortableWorkspacePythonHelloPolicy =
    [<Literal>]
    let EnrollmentId = "local-python-hello-v1"

    [<Literal>]
    let OperationId = "p4-python-hello-test-v1"

    [<Literal>]
    let QualifiedImage =
        "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2@sha256:6cc4612ca9511c1910012284f3306992cb1cd0f6fe0a42ca8ec1090dba6c5aa8"

    [<Literal>]
    let VerificationSha256 = "2ed645adefe2c23308832036a3b5163dc39faaf152c2c9d1d3afb3bd637f146a"

    [<Literal>]
    let RecipeSha256 = "99b7190e0439059bb224408a44a81fdd52410c64287b3e5455171747ae7d75c6"

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

    let create (grant: PortableWorkspacePythonHelloGrant) =
        if grant.EnrollmentId <> EnrollmentId then
            Error "portable-runtime-enrollment-not-found"
        elif grant.QualifiedImage <> QualifiedImage || grant.ImageRecipeSha256 <> RecipeSha256 then
            Error "portable-runtime-image-binding-refused"
        else
            let profile = profile grant.WorkspaceScope grant.ReceiverCommit

            let profileSha256 =
                profileDigest grant.WorkspaceScope grant.ReceiverCommit

            match profileSha256 with
            | Error reason -> Error reason
            | Ok actual when actual <> grant.ProfileSha256 -> Error "portable-runtime-profile-digest-refused"
            | Ok _ ->
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
                        RecipeSha256 = RecipeSha256
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
                                        PodmanGlobalArguments = []
                                        StateRoot = grant.JournalStateRoot
                                        ContainerPath = "/usr/local/bin:/usr/bin:/bin"
                                        ContainerUser = "32768:32768"
                                        HostEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/local/bin:/usr/bin:/bin" ]
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
