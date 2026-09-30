namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.Text
open System.IO
open FS.GG.Coordination.Orchestration.Execution
open Xunit

type PortableWorkspaceContractTests() =
    let contractBytes path =
        let bytes = File.ReadAllBytes path

        if bytes.Length > 0 && bytes[bytes.Length - 1] = byte '\n' then
            bytes[.. bytes.Length - 2]
        else
            bytes

    let profile =
        {
            ProfileId = "web-service-v1"
            Revision = 3UL
            WorkspaceScope = "fs-gg/example-polyglot"
            SourceRevision = "0123456789abcdef"
            QualifiedImage =
                "ghcr.io/fs-gg/polyglot@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            Components =
                [
                    {
                        Id = "frontend"
                        Language = "typescript"
                        WorkingDirectory = "src/frontend"
                        Toolchain = { Id = "node"; Version = "24.8.0" }
                        EntryPoints =
                            {
                                Build = "frontend-build"
                                Test = "frontend-test"
                                Lint = Some "frontend-lint"
                                Artifact = Some "frontend-artifact"
                            }
                    }
                    {
                        Id = "backend"
                        Language = "python"
                        WorkingDirectory = "src/backend"
                        Toolchain = { Id = "cpython"; Version = "3.14.0" }
                        EntryPoints =
                            {
                                Build = "backend-build"
                                Test = "backend-test"
                                Lint = Some "backend-lint"
                                Artifact = None
                            }
                    }
                ]
            ProductBuild = "product-build"
            ProductTest = "product-test"
            ProductJourney = "browser-api-journey"
            MaximumRuntimeSeconds = 900UL
            MaximumOutputBytes = 1048576UL
        }

    let deadline =
        DateTimeOffset(2026, 9, 30, 12, 34, 56, 789, TimeSpan.Zero).AddTicks 1230L

    let command =
        {
            CommandId = Guid.Parse "11111111-2222-3333-4444-555555555555"
            IdempotencyId = "build-attempt-7"
            WorkspaceScope = profile.WorkspaceScope
            ProfileId = profile.ProfileId
            ProfileRevision = profile.Revision
            SourceRevision = profile.SourceRevision
            ExpectedWorkflowRevision = 41UL
            FenceGeneration = 9UL
            CausationId = None
            Deadline = deadline
            Operation = "journey"
            ComponentId = None
        }

    [<Fact>]
    member _.``mixed non dotnet profile has stable canonical bytes and round trips``() =
        let bytes =
            PortableWorkspaceContract.profileBytes profile |> Result.defaultWith failwith

        Assert.Equal(
            bytes,
            PortableWorkspaceContract.parseProfile bytes
            |> Result.bind PortableWorkspaceContract.profileBytes
            |> Result.defaultWith failwith
        )

        let json = Encoding.UTF8.GetString bytes
        Assert.Contains("\"language\":\"typescript\"", json)
        Assert.Contains("\"language\":\"python\"", json)
        Assert.DoesNotContain("dotnet", json)

    [<Theory>]
    [<InlineData("python.json")>]
    [<InlineData("typescript-python.json")>]
    member _.``published cross language examples are canonical contract bytes``(name: string) =
        let path = Path.Combine(AppContext.BaseDirectory, "Contracts", name)
        let bytes = contractBytes path

        Assert.Equal(
            bytes,
            PortableWorkspaceContract.parseProfile bytes
            |> Result.bind PortableWorkspaceContract.profileBytes
            |> Result.defaultWith failwith
        )

    [<Fact>]
    member _.``command fixes identifiers counters timestamps and absent values``() =
        let bytes =
            PortableWorkspaceContract.commandBytes command |> Result.defaultWith failwith

        let json = Encoding.UTF8.GetString bytes
        Assert.Contains("\"expectedWorkflowRevision\":\"41\"", json)
        Assert.Contains("\"fenceGeneration\":\"9\"", json)
        Assert.Contains("\"deadline\":\"2026-09-30T12:34:56.789123Z\"", json)
        Assert.DoesNotContain("causationId", json)
        Assert.DoesNotContain(":null", json)
        Assert.Equal(command, PortableWorkspaceContract.parseCommand bytes |> Result.defaultWith failwith)

    [<Fact>]
    member _.``adapter returns reviewed entry point only after every fence matches``() =
        let authority =
            {
                WorkspaceScope = profile.WorkspaceScope
                WorkflowRevision = 41UL
                FenceGeneration = 9UL
                ObservedAt = deadline.AddMinutes(-2.0)
            }

        let prepared =
            PortableWorkspaceAdapter.prepare (deadline.AddMinutes(-1.0)) authority profile command
            |> Result.defaultWith failwith

        Assert.Equal("product", prepared.WorkingDirectory)
        Assert.Equal("browser-api-journey", prepared.EntryPoint)

        Assert.Equal(
            Error "portable-workspace-scope-refused",
            PortableWorkspaceAdapter.prepare
                (deadline.AddMinutes(-1.0))
                { authority with
                    WorkspaceScope = "fs-gg/other"
                }
                profile
                command
        )

        Assert.Equal(
            Error "portable-workflow-revision-refused",
            PortableWorkspaceAdapter.prepare
                (deadline.AddMinutes(-1.0))
                { authority with
                    WorkflowRevision = 42UL
                }
                profile
                command
        )

        Assert.Equal(
            Error "portable-fence-generation-refused",
            PortableWorkspaceAdapter.prepare
                (deadline.AddMinutes(-1.0))
                { authority with
                    FenceGeneration = 10UL
                }
                profile
                command
        )

        Assert.Equal(
            Error "portable-deadline-refused",
            PortableWorkspaceAdapter.prepare deadline authority profile command
        )

    [<Fact>]
    member _.``missing unknown and structured errors round trip without null``() =
        let result =
            {
                CommandId = command.CommandId
                WorkflowRevision = 41UL
                FenceGeneration = 9UL
                CompletedAt = deadline
                ExitCode = EvidenceUnknown "process-lost-before-readback"
                ArtifactReference = EvidenceMissing "no-verified-artifact-observed"
                Error =
                    Some
                        {
                            Code = "execution-outcome-unknown"
                            Message = "The execution outcome could not be established from durable evidence."
                            Retryable = true
                            Details = Map [ "provider", "isolated-runner" ]
                        }
            }

        let bytes =
            PortableWorkspaceContract.resultBytes result |> Result.defaultWith failwith

        Assert.Equal(result, PortableWorkspaceContract.parseResult bytes |> Result.defaultWith failwith)
        Assert.DoesNotContain(":null", Encoding.UTF8.GetString bytes)

        let fixture =
            Path.Combine(AppContext.BaseDirectory, "Contracts", "result-unknown.json")
            |> contractBytes

        Assert.Equal(bytes, fixture)

    [<Theory>]
    [<InlineData("{\"schema\":\"fsgg.workspace.command/1\",\"commandId\":\"11111111-2222-3333-4444-555555555555\",\"idempotencyId\":\"x\",\"workspaceScope\":\"scope\",\"profileId\":\"p\",\"profileRevision\":3,\"sourceRevision\":\"abc\",\"expectedWorkflowRevision\":\"1\",\"fenceGeneration\":\"1\",\"deadline\":\"2026-09-30T12:34:56.000000Z\",\"operation\":\"build\"}")>]
    [<InlineData("{\"schema\":\"fsgg.workspace.command/1\",\"commandId\":\"11111111-2222-3333-4444-555555555555\",\"idempotencyId\":\"x\",\"workspaceScope\":\"scope\",\"profileId\":\"p\",\"profileRevision\":\"03\",\"sourceRevision\":\"abc\",\"expectedWorkflowRevision\":\"1\",\"fenceGeneration\":\"1\",\"deadline\":\"2026-09-30T12:34:56Z\",\"operation\":\"build\"}")>]
    [<InlineData("{\"schema\":\"fsgg.workspace.command/1\",\"commandId\":\"11111111-2222-3333-4444-555555555555\",\"idempotencyId\":\"x\",\"workspaceScope\":\"scope\",\"profileId\":\"p\",\"profileRevision\":\"3\",\"sourceRevision\":\"abc\",\"expectedWorkflowRevision\":\"1\",\"fenceGeneration\":\"1\",\"causationId\":null,\"deadline\":\"2026-09-30T12:34:56.000000Z\",\"operation\":\"build\"}")>]
    member _.``nonportable number timestamp and null encodings are refused``(json: string) =
        Assert.True(
            PortableWorkspaceContract.parseCommand (Encoding.UTF8.GetBytes json)
            |> Result.isError
        )

    [<Fact>]
    member _.``unknown fields duplicate component ids paths and arbitrary operations refuse``() =
        let commandBytes =
            PortableWorkspaceContract.commandBytes command |> Result.defaultWith failwith

        let changed =
            Encoding.UTF8
                .GetString(commandBytes)
                .Replace("\"operation\":\"journey\"", "\"extra\":\"x\",\"operation\":\"journey\"")
            |> Encoding.UTF8.GetBytes

        Assert.True(PortableWorkspaceContract.parseCommand changed |> Result.isError)

        Assert.True(
            PortableWorkspaceContract.validateProfile
                { profile with
                    Components = profile.Components @ [ profile.Components.Head ]
                }
            |> Result.isError
        )

        Assert.True(
            PortableWorkspaceContract.validateProfile
                { profile with
                    QualifiedImage = "ghcr.io/fs-gg/polyglot:latest"
                }
            |> Result.isError
        )

        Assert.True(
            PortableWorkspaceContract.validateProfile
                { profile with
                    Components =
                        [
                            { profile.Components.Head with
                                WorkingDirectory = "../outside"
                            }
                        ]
                }
            |> Result.isError
        )

        Assert.Equal(
            Error "portable-operation-refused",
            PortableWorkspaceAdapter.prepare
                (deadline.AddMinutes(-1.0))
                {
                    WorkspaceScope = profile.WorkspaceScope
                    WorkflowRevision = 41UL
                    FenceGeneration = 9UL
                    ObservedAt = deadline.AddMinutes(-2.0)
                }
                profile
                { command with
                    Operation = "uploaded-shell"
                }
        )
