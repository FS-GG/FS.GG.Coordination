namespace FS.GG.Coordination.Orchestration.PostgreSql.Tests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Npgsql
open Xunit
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql

module private InstalledFixture =
    let sourceRevision = String.replicate 40 "0"

    let private environment name fallback =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> fallback
        | value -> value

    let mode = environment "FSGG_PG_MODE" "binary"

    let root =
        match Environment.GetEnvironmentVariable "FSGG_PG_ROOT" with
        | null
        | "" when mode = "binary" -> File.ReadAllText("/tmp/o0-postgresql-current-path").Trim()
        | null
        | "" -> "/tmp"
        | value -> value

    let host = environment "FSGG_PG_HOST" (Path.Combine(root, "socket"))
    let port = environment "FSGG_PG_PORT" "55439"
    let username = environment "FSGG_PG_USERNAME" "developer"

    let connectionString =
        $"Host={host};Port={port};Database=orchestration_o0;Username={username};Password=qualification-test-only;Pooling=false"

    let dataSource () =
        NpgsqlDataSource.Create connectionString

    let reset () =
        task {
            use source = dataSource ()
            use! connection = source.OpenConnectionAsync()

            use drop =
                new NpgsqlCommand("DROP SCHEMA IF EXISTS fsgg_orchestration CASCADE", connection)

            let! _ = drop.ExecuteNonQueryAsync()
            let! identity = PostgreSqlSchema.migrate source CancellationToken.None
            do! PostgreSqlPilotSchema.migrate source CancellationToken.None
            do! PostgreSqlExecutionSchema.migrate source CancellationToken.None
            return Guid.Parse identity
        }

    let rootDirectory =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then
                directory.FullName
            elif isNull directory.Parent then
                failwith "repository root not found"
            else
                find directory.Parent

        find (DirectoryInfo AppContext.BaseDirectory)

    let executable =
        Path.Combine(
            rootDirectory,
            "src/FS.GG.Coordination.Orchestration.Host/bin/Release/net10.0/linux-x64/fsgg-coord-orchestration-host"
        )

    let sha path =
        File.ReadAllBytes path
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let request (identity: Guid) (executableDigest: string) (now: DateTimeOffset) : InstalledAdoptionRequest =
        {
            Schema = InstalledAdoptionVerification.requestSchema
            OperationId = Guid.NewGuid()
            ExpectedSourceRevision = sourceRevision
            ExpectedExecutableSha256 = executableDigest
            StoreId = "o3-installed-qualification"
            ExpectedBackupIdentity = identity
            ExpectedSchemaVersion = 2
            ExpectedGenerationFence = 0L
            IssuedAt = now.AddSeconds -5.
            ExpiresAt = now.AddMinutes 5.
            OrdinaryCapacity = 1
            RecoveryCapacityPerProject = 1
            ProjectA =
                {
                    ProjectId = Guid.NewGuid()
                    RepositoryNodeId = "R_o3_a"
                    RepositoryDatabaseId = 9301L
                    IssueNodeId = "I_o3_a"
                    IssueDatabaseId = 9401L
                }
            ProjectB =
                {
                    ProjectId = Guid.NewGuid()
                    RepositoryNodeId = "R_o3_b"
                    RepositoryDatabaseId = 9302L
                    IssueNodeId = "I_o3_b"
                    IssueDatabaseId = 9402L
                }
        }

    let bytes request =
        let options =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

        JsonSerializer.SerializeToUtf8Bytes(request, options)

    let runtime executablePath now callback =
        {
            SourceRevision = sourceRevision
            ExecutablePath = executablePath
            Clock =
                { new TimeProvider() with
                    override _.GetUtcNow() = now
                }
            AfterAReserved = callback
        }

    let counts () =
        task {
            use source = dataSource ()
            use! connection = source.OpenConnectionAsync()

            use command =
                new NpgsqlCommand(
                    "SELECT (SELECT count(*) FROM fsgg_orchestration.execution_stream),(SELECT count(*) FROM fsgg_orchestration.execution_event),(SELECT count(*) FROM fsgg_orchestration.subscription_reservation WHERE active),(SELECT count(*) FROM fsgg_orchestration.executor_command),(SELECT count(*) FROM fsgg_orchestration.candidate),(SELECT count(*) FROM fsgg_orchestration.event WHERE effect_change <> 0)",
                    connection
                )

            use! row = command.ExecuteReaderAsync()
            Assert.True(row.Read())
            return [ for index in 0..5 -> row.GetInt64 index ]
        }

type InstalledAdoptionVerificationTests() =
    [<Fact>]
    member _.``compiled offline CLI proves serial A B adoption on a real empty store``() =
        task {
            let! identity = InstalledFixture.reset ()
            Assert.True(File.Exists InstalledFixture.executable, InstalledFixture.executable)
            let now = DateTimeOffset.UtcNow

            let request =
                InstalledFixture.request identity (InstalledFixture.sha InstalledFixture.executable) now

            let privateRoot =
                Path.Combine(Path.GetTempPath(), $"fsgg-o3-cli-{Guid.NewGuid():N}")

            Directory.CreateDirectory privateRoot |> ignore

            File.SetUnixFileMode(
                privateRoot,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            )

            let connectionFile = Path.Combine(privateRoot, "connection")
            let requestFile = Path.Combine(privateRoot, "request.json")
            File.WriteAllText(connectionFile, InstalledFixture.connectionString)
            File.WriteAllBytes(requestFile, InstalledFixture.bytes request)
            File.SetUnixFileMode(connectionFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            File.SetUnixFileMode(requestFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

            try
                let start = ProcessStartInfo(InstalledFixture.executable)
                start.ArgumentList.Add "verify-installed-adoption"
                start.ArgumentList.Add "--connection-file"
                start.ArgumentList.Add connectionFile
                start.ArgumentList.Add "--request-file"
                start.ArgumentList.Add requestFile
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                use child = Process.Start start
                let! output = child.StandardOutput.ReadToEndAsync()
                let! error = child.StandardError.ReadToEndAsync()
                do! child.WaitForExitAsync()
                Assert.True(child.ExitCode = 0, output + error)
                Assert.Equal("", error)
                use result = JsonDocument.Parse(output)

                Assert.Equal(
                    InstalledAdoptionVerification.resultSchema,
                    result.RootElement.GetProperty("schema").GetString()
                )

                Assert.True(result.RootElement.GetProperty("passed").GetBoolean())

                Assert.Equal(
                    0L,
                    result.RootElement.GetProperty("finalCounts").GetProperty("activeReservations").GetInt64()
                )

                Assert.Equal(0L, result.RootElement.GetProperty("finalCounts").GetProperty("commands").GetInt64())
                Assert.Equal(0L, result.RootElement.GetProperty("finalCounts").GetProperty("candidates").GetInt64())

                Assert.Equal(
                    0L,
                    result.RootElement.GetProperty("finalCounts").GetProperty("externalEffects").GetInt64()
                )
            finally
                Directory.Delete(privateRoot, true)
        }

    [<Fact>]
    member _.``binding expiry and nonempty refusals happen before verifier writes``() =
        task {
            let executable = InstalledFixture.executable
            let now = DateTimeOffset.UtcNow

            let cases request =
                [
                    { request with
                        ExpectedSourceRevision = String.replicate 40 "b"
                    },
                    "source-revision-mismatch"
                    { request with
                        ExpectedExecutableSha256 = String.replicate 64 "b"
                    },
                    "executable-digest-mismatch"
                    { request with
                        ExpectedBackupIdentity = Guid.NewGuid()
                    },
                    "store-identity-or-fence-mismatch"
                    { request with
                        IssuedAt = now.AddMinutes -20.
                        ExpiresAt = now.AddMinutes -10.
                    },
                    "request-time-window-refused"
                ]

            for mutate, expected in cases (InstalledFixture.request Guid.Empty (InstalledFixture.sha executable) now) do
                let! identity = InstalledFixture.reset ()

                let request =
                    if expected = "store-identity-or-fence-mismatch" then
                        mutate
                    else
                        { mutate with
                            ExpectedBackupIdentity = identity
                        }

                let! result =
                    InstalledAdoptionVerification.run
                        InstalledFixture.connectionString
                        (InstalledFixture.bytes request)
                        (InstalledFixture.runtime executable now None)
                        CancellationToken.None

                Assert.False result.Passed
                Assert.Equal(expected, result.Failure)
                let! counts = InstalledFixture.counts ()
                Assert.Equal<int64 list>([ 0L; 0L; 0L; 0L; 0L; 0L ], counts)

            let! identity = InstalledFixture.reset ()
            use source = InstalledFixture.dataSource ()
            use! connection = source.OpenConnectionAsync()

            use insert =
                new NpgsqlCommand(
                    "INSERT INTO fsgg_orchestration.execution_stream(assignment_id,attempt_id) VALUES($1,$2)",
                    connection
                )

            insert.Parameters.AddWithValue(Guid.NewGuid()) |> ignore
            insert.Parameters.AddWithValue(Guid.NewGuid()) |> ignore
            let! _ = insert.ExecuteNonQueryAsync()

            let request =
                InstalledFixture.request identity (InstalledFixture.sha executable) now

            let! nonempty =
                InstalledAdoptionVerification.run
                    InstalledFixture.connectionString
                    (InstalledFixture.bytes request)
                    (InstalledFixture.runtime executable now None)
                    CancellationToken.None

            Assert.False nonempty.Passed
            Assert.Equal("dedicated-store-not-empty", nonempty.Failure)
            let! after = InstalledFixture.counts ()
            Assert.Equal<int64 list>([ 1L; 0L; 0L; 0L; 0L; 0L ], after)
        }

    [<Fact>]
    member _.``concurrent verifier and interruption after A fail closed without cleanup``() =
        task {
            let executable = InstalledFixture.executable
            let now = DateTimeOffset.UtcNow
            let! identity = InstalledFixture.reset ()

            let request =
                InstalledFixture.request identity (InstalledFixture.sha executable) now

            use source = InstalledFixture.dataSource ()
            use! connection = source.OpenConnectionAsync()

            use lockCommand =
                new NpgsqlCommand(
                    "SELECT pg_advisory_lock(hashtextextended('fsgg-installed-adoption-verifier',0))",
                    connection
                )

            let! _ = lockCommand.ExecuteScalarAsync()

            let! concurrent =
                InstalledAdoptionVerification.run
                    InstalledFixture.connectionString
                    (InstalledFixture.bytes request)
                    (InstalledFixture.runtime executable now None)
                    CancellationToken.None

            Assert.False concurrent.Passed
            Assert.Equal("concurrent-verifier-refused", concurrent.Failure)
            let! zero = InstalledFixture.counts ()
            Assert.Equal<int64 list>([ 0L; 0L; 0L; 0L; 0L; 0L ], zero)

            use unlock =
                new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended('fsgg-installed-adoption-verifier',0))",
                    connection
                )

            let! _ = unlock.ExecuteScalarAsync()

            let interruptedRuntime =
                InstalledFixture.runtime
                    executable
                    now
                    (Some(fun () -> raise (OperationCanceledException "fixture-interruption")))

            let! interrupted =
                InstalledAdoptionVerification.run
                    InstalledFixture.connectionString
                    (InstalledFixture.bytes request)
                    interruptedRuntime
                    CancellationToken.None

            Assert.False interrupted.Passed
            Assert.Contains("fixture-interruption", interrupted.Failure)
            Assert.Equal("observed", interrupted.FinalCounts.State)
            Assert.Equal(1L, interrupted.FinalCounts.ActiveReservations.Value)
            Assert.Equal(0L, interrupted.FinalCounts.Commands.Value)
            Assert.Equal(0L, interrupted.FinalCounts.Candidates.Value)
            Assert.Equal(0L, interrupted.FinalCounts.ExternalEffects.Value)
            let! retained = InstalledFixture.counts ()
            Assert.Equal<int64 list>([ 2L; 2L; 1L; 0L; 0L; 0L ], retained)
        }
