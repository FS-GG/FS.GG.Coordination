namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.Orchestration.Execution

[<RequireQualifiedAccess>]
module PortableWorkspacePythonHelloQualification =
    type private SingleEnrollmentSource(enrollment: PortableWorkspaceRuntimeEnrollment) =
        interface IPortableWorkspaceRuntimeEnrollmentSource with
            member _.Resolve enrollmentId =
                if enrollmentId = enrollment.EnrollmentId then
                    Ok enrollment
                else
                    Error "portable-runtime-enrollment-not-found"

    [<Literal>]
    let EnrollmentId = "local-python-hello-v1"

    [<Literal>]
    let OperationId = "p4-python-hello-test-v1"

    [<Literal>]
    let SourceRevision = "48b8540051593f30383944c7f86b015189e5d8bd"

    [<Literal>]
    let QualifiedImage =
        "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2@sha256:6cc4612ca9511c1910012284f3306992cb1cd0f6fe0a42ca8ec1090dba6c5aa8"

    let portableClock () =
        let value = DateTimeOffset.UtcNow
        DateTimeOffset(value.Ticks - value.Ticks % 10L, TimeSpan.Zero)

    let create workspaceRoot stateRoot observedAt runtime =
        let profile =
            {
                ProfileId = "portable-python-hello-v1"
                Revision = 1UL
                WorkspaceScope = "fs-gg/local-python-hello"
                SourceRevision = SourceRevision
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

        let runtime = { runtime with StateRoot = stateRoot }

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
                VerificationSha256 = "2ed645adefe2c23308832036a3b5163dc39faaf152c2c9d1d3afb3bd637f146a"
                RecipeSha256 = "99b7190e0439059bb224408a44a81fdd52410c64287b3e5455171747ae7d75c6"
            }

        {
            EnrollmentId = EnrollmentId
            Profile = profile
            Authority =
                {
                    WorkspaceScope = profile.WorkspaceScope
                    WorkflowRevision = 1UL
                    FenceGeneration = 1UL
                    ObservedAt = observedAt
                }
            Policy =
                {
                    WorkspaceRoot = workspaceRoot
                    WorkspaceScope = profile.WorkspaceScope
                    SourceRevision = profile.SourceRevision
                    QualifiedImage = profile.QualifiedImage
                    MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                    MaximumOutputBytes = profile.MaximumOutputBytes
                    Operations = [ operation ]
                    Runtime = runtime
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

    let dependencies enrollment =
        {
            Enrollments = SingleEnrollmentSource enrollment
            CreateRunner = fun runtime -> PortableWorkspacePodmanRunner runtime
            Clock = portableClock
        }
