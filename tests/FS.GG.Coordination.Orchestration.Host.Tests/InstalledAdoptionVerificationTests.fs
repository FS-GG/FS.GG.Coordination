module FS.GG.Coordination.Orchestration.Host.Tests.InstalledAdoptionVerificationTests

open System
open System.IO
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Orchestration.Host

let private now = DateTimeOffset.Parse "2026-09-16T12:00:00Z"

let private request () : InstalledAdoptionRequest =
    {
        Schema = InstalledAdoptionVerification.requestSchema
        OperationId = Guid.Parse "70000000-0000-0000-0000-000000000001"
        ExpectedSourceRevision = String.replicate 40 "a"
        ExpectedExecutableSha256 = String.replicate 64 "b"
        StoreId = "qualification-store"
        ExpectedBackupIdentity = Guid.Parse "70000000-0000-0000-0000-000000000002"
        ExpectedSchemaVersion = 2
        ExpectedGenerationFence = 4L
        IssuedAt = now.AddMinutes -1.
        ExpiresAt = now.AddMinutes 5.
        OrdinaryCapacity = 1
        RecoveryCapacityPerProject = 1
        ProjectA =
            {
                ProjectId = Guid.Parse "70000000-0000-0000-0000-00000000000a"
                RepositoryNodeId = "R_A"
                RepositoryDatabaseId = 1L
                IssueNodeId = "I_A"
                IssueDatabaseId = 2L
            }
        ProjectB =
            {
                ProjectId = Guid.Parse "70000000-0000-0000-0000-00000000000b"
                RepositoryNodeId = "R_B"
                RepositoryDatabaseId = 3L
                IssueNodeId = "I_B"
                IssueDatabaseId = 4L
            }
    }

let private bytes value =
    JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

[<Fact>]
let ``installed adoption request is strict closed and bounded`` () =
    let valid = bytes (request ())
    Assert.True(InstalledAdoptionVerification.decodeRequest now valid |> Result.isOk)
    let text = Encoding.UTF8.GetString valid

    let unknown =
        Encoding.UTF8.GetBytes(text.Substring(0, text.Length - 1) + ",\"unknown\":1}")

    Assert.Equal(Error "request-shape-refused", InstalledAdoptionVerification.decodeRequest now unknown)

    let duplicate =
        Encoding.UTF8.GetBytes(text.Substring(0, text.Length - 1) + ",\"schema\":\"other\"}")

    Assert.Equal(Error "request-shape-refused", InstalledAdoptionVerification.decodeRequest now duplicate)

    let sameSubject =
        { request () with
            ProjectB =
                { (request ()).ProjectA with
                    ProjectId = Guid.Parse "70000000-0000-0000-0000-00000000000b"
                }
        }

    Assert.Equal(
        Error "canonical-subjects-must-be-distinct",
        InstalledAdoptionVerification.decodeRequest now (bytes sameSubject)
    )

    let tooLong =
        { request () with
            ExpiresAt = now.AddMinutes 11.
        }

    Assert.Equal(Error "request-time-window-refused", InstalledAdoptionVerification.decodeRequest now (bytes tooLong))

[<Fact>]
let ``installed adoption CLI accepts only private same owner input files`` () =
    let root = Path.Combine(Path.GetTempPath(), $"fsgg-o3-config-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore
    File.SetUnixFileMode(root, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let connection = Path.Combine(root, "connection")
    let requestFile = Path.Combine(root, "request.json")
    File.WriteAllText(connection, "Host=/private;Database=o3;Username=operator")
    File.WriteAllBytes(requestFile, bytes (request ()))
    File.SetUnixFileMode(connection, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    File.SetUnixFileMode(requestFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    try
        let arguments = [| "--connection-file"; connection; "--request-file"; requestFile |]
        Assert.True(HostConfiguration.parseInstalledAdoptionVerification arguments |> Result.isOk)
        File.SetUnixFileMode(requestFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead)

        Assert.Equal(
            Error "secret-file-permissions-too-broad",
            HostConfiguration.parseInstalledAdoptionVerification arguments
        )

        Assert.Equal(
            Error "unknown-option",
            HostConfiguration.parseInstalledAdoptionVerification
                [| "--connection-file"; connection; "--other"; requestFile |]
        )

        Assert.Equal(
            Error "duplicate-option",
            HostConfiguration.parseInstalledAdoptionVerification
                [|
                    "--connection-file"
                    connection
                    "--connection-file"
                    connection
                    "--request-file"
                    requestFile
                |]
        )
    finally
        Directory.Delete(root, true)
