module FS.GG.Coordination.Orchestration.Host.Tests.LearningInstalledReadinessSourceTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Host

type private FixedClock(value: DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow() = value

type private ReceiptSource
    (factory: LearningInstalledProducerReceiptQuery -> Result<LearningInstalledProducerReceipt, string>) =
    let mutable calls = 0
    member _.Calls = calls

    interface ILearningInstalledProducerReceiptSource with
        member _.ReadLearningInstalledProducerReceipt(query, _) =
            calls <- calls + 1
            Task.FromResult(factory query)

[<DllImport("libc", EntryPoint = "geteuid")>]
extern uint32 private effectiveUserId()

let private jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private shaBytes (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private shaFile (path: string) =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()

let private writePrivate (path: string) (bytes: byte array) =
    File.WriteAllBytes(path, bytes)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

let private writeJson path value =
    JsonSerializer.SerializeToUtf8Bytes(value) |> writePrivate path

type private Fixture =
    {
        Root: string
        Config: string
        Executable: string
        Evidence: string
        SourceRoot: string
        Now: DateTimeOffset
        Query: LearningSelectionQuery
        Options: LearningInstalledReadinessOptions
    }

    interface IDisposable with
        member this.Dispose() = Directory.Delete(this.Root, true)

let private writeSparseExecutable length path =
    use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    stream.SetLength length
    stream.Position <- 0L
    let header = [| 0x7fuy; byte 'E'; byte 'L'; byte 'F'; 1uy; 2uy; 3uy |]
    stream.Write(header, 0, header.Length)

let private fixtureWithExecutable writeExecutable =
    let root = Directory.CreateTempSubdirectory("learning-installed-").FullName
    let privateRoot = Directory.CreateDirectory(Path.Combine(root, "private")).FullName

    let evidence =
        Directory.CreateDirectory(Path.Combine(privateRoot, "evidence")).FullName

    let sourceRoot =
        Directory.CreateDirectory(Path.Combine(privateRoot, "codex-home")).FullName

    File.SetUnixFileMode(privateRoot, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    File.SetUnixFileMode(evidence, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    File.SetUnixFileMode(sourceRoot, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let executable = Path.Combine(privateRoot, "codex")
    writeExecutable executable
    File.SetUnixFileMode(executable, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let executableSha = shaFile executable
    let config = Path.Combine(privateRoot, "host.json")

    let credential =
        {|
            Reference = "learn-native-collector-v1"
            SecretFile = Path.Combine(privateRoot, "collector.token")
            WorkspaceId = "learn-workspace"
            ProducerId = "installed-owner"
            StreamId = "runtime"
            Role = "native-collector"
            GrantId = "grant-learn-native"
            GrantGeneration = 7L
            Revoked = false
        |}

    let host =
        {|
            Schema = "fsgg.telemetry.host-config/2"
            ListenUrl = "https://127.0.0.1:7443"
            CertificatePath = Path.Combine(privateRoot, "receiver.pfx")
            CertificatePasswordFile = Path.Combine(privateRoot, "certificate-password")
            ServiceLockPath = Path.Combine(privateRoot, "host.lock")
            Stores = [||]
            Credentials = [| credential |]
            BrowserPrincipals = [||]
            BrowserSession =
                {|
                    IdleSeconds = 300
                    AbsoluteSeconds = 600
                    MaximumSessions = 2
                    LoginAttemptsPerMinute = 2
                    LoginAdmission = 1
                    QueryAdmission = 1
                    QueryTimeoutSeconds = 10
                |}
        |}

    writeJson config host

    let sidecar =
        {|
            Schema = "fsgg.telemetry.native-collector-installation/2"
            CredentialReference = credential.Reference
            ExecutablePath = executable
            CodexHome = sourceRoot
            EvidenceRoot = evidence
            Provider = "openai"
            Model = FixedNativeCapabilityDiagnostic.requestedModel
            Effort = FixedNativeCapabilityDiagnostic.requestedEffort
            ExecutableSha256 = executableSha
        |}

    let sidecarPath = config + ".native-collector.json"
    writeJson sidecarPath sidecar

    let receipt =
        {|
            schema = "fsgg.telemetry.native-collector-installation-receipt/2"
            status = "installed"
            ownerUid = effectiveUserId ()
            hostConfigSha256 = shaFile config
            sidecarSha256 = shaFile sidecarPath
            executableSha256 = executableSha
            credentialReference = credential.Reference
            workspaceId = credential.WorkspaceId
            producerId = credential.ProducerId
            streamId = credential.StreamId
            grantId = credential.GrantId
            grantGeneration = credential.GrantGeneration
            sourceVerification = "unknown"
            snapshotOrigin = "unknown"
            sharedCostCompleteness = "unknown"
            activationAuthorized = false
        |}

    writeJson (config + ".native-collector.receipt.json") receipt
    let now = DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)
    let workspace = Path.Combine(root, "disposable-workspace")

    let profile: FixedNativeCapabilityProfile =
        {
            Schema = FixedNativeCapabilityDiagnostic.profileSchema
            Operation = FixedNativeCapabilityDiagnostic.operation
            Revision = "installed-capability-1"
            HostExecutableSha256 = String.replicate 64 "a"
            ProviderExecutable = executable
            ProviderExecutableSha256 = executableSha
            ExpectedAdapterVersion = FixedNativeCapabilityDiagnostic.adapterVersion
            ExpectedCodexVersion = "0.154.0"
            EnvironmentAllowList = [| "PATH" |]
            CredentialScope = FixedNativeCapabilityDiagnostic.credentialScope
            MaximumRuntimeSeconds = 10
            MaximumStreamBytes = 65536
            ExpiresAt = now.AddMinutes 4.
            DisposableWorkspace = workspace
            Cleanup = FixedNativeCapabilityDiagnostic.cleanupKind
        }

    let profileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, jsonOptions)
    let profilePath = Path.Combine(evidence, "fixed-native-capability-profile.json")
    writePrivate profilePath profileBytes
    let observed = now.AddMinutes(-1.)
    let expires = now.AddMinutes 3.

    let cleanup: FixedNativeCapabilityCleanup =
        {
            ProcessTreeTerminationRequired = true
            ProcessTreeTerminated = true
            WorkspaceRemovalAttempted = true
            WorkspaceRemoved = true
        }

    let result: FixedNativeCapabilityResult =
        {
            Schema = FixedNativeCapabilityDiagnostic.resultSchema
            Operation = FixedNativeCapabilityDiagnostic.operation
            ProfileRevision = profile.Revision
            ProfileSha256 = shaBytes profileBytes
            HostExecutableSha256 = profile.HostExecutableSha256
            ProviderExecutableSha256 = executableSha
            AdapterVersion = profile.ExpectedAdapterVersion
            CredentialScope = profile.CredentialScope
            EnvironmentAllowList = profile.EnvironmentAllowList
            MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
            MaximumStreamBytes = profile.MaximumStreamBytes
            RequestedModel = FixedNativeCapabilityDiagnostic.requestedModel
            RequestedEffort = FixedNativeCapabilityDiagnostic.requestedEffort
            StartedAt = observed.AddSeconds(-2.)
            CompletedAt = observed
            Disposition = "advertised-supported"
            Detail = "requested-selection-advertised"
            AuthenticationState = "authenticated"
            AuthenticationProvenance = "installed-account"
            EvidenceSchema = LearningSelectionEvidence.schema
            EvidenceProvenance = "codex-cli-pinned-discovery"
            EvidenceObservedAt = Nullable observed
            EvidenceExpiresAt = Nullable expires
            ModelSessionStarts = 0
            Cleanup = cleanup
        }

    writePrivate
        (Path.Combine(evidence, "fixed-native-capability-result.json"))
        (JsonSerializer.SerializeToUtf8Bytes(result, jsonOptions))

    let captureDigest = String.replicate 64 "c"

    let capture =
        {|
            schema = "fsgg.learn.native-source-capture/1"
            outcome = "native-census-and-usage-reconciled-at-capture"
            rootThreadId = "11111111-1111-1111-1111-111111111111"
            limits = {| maximum = 1 |}
            initialExchanges = [||]
            confirmationExchanges = [||]
            rollouts = [||]
            projection =
                {|
                    threads =
                        [|
                            {|
                                provider = "openai"
                                model = FixedNativeCapabilityDiagnostic.requestedModel
                                effort = FixedNativeCapabilityDiagnostic.requestedEffort
                            |}
                        |]
                    turnUsage = [||]
                |}
            captureDigest = captureDigest
        |}

    writePrivate (Path.Combine(evidence, "native-source-capture.json")) (JsonSerializer.SerializeToUtf8Bytes capture)

    let verification =
        {|
            schema = "fsgg.learn.native-source-verification/1"
            status = "verified"
            outcome = "native-census-and-usage-reconciled-at-capture"
            captureDigest = captureDigest
            missingDescendants = [||]
            foreignDescendants = [||]
            mismatchedThreads = [||]
            missingTurns = [||]
            foreignTurns = [||]
            missingUsage = [||]
            foreignUsage = [||]
            mismatchedUsage = [||]
        |}

    writeJson (Path.Combine(evidence, "native-source-verification.json")) verification

    let sourceReference =
        {|
            schema = "fsgg.telemetry.persistent-source-references/2"
            profileSha256 = String.replicate 64 "d"
            nativeSourceVolume = "learn-native-source-v1"
            developmentTarget = "/producer/native-source"
            collectorReadOnlyTarget = sourceRoot
            readerProfileSha256 = shaBytes profileBytes
            captureQualified = true
        |}

    writeJson (Path.Combine(privateRoot, "source-reference.json")) sourceReference

    let query =
        {
            Provider =
                {
                    Provider = "openai"
                    AdapterVersion = FixedNativeCapabilityDiagnostic.adapterVersion
                }
            Executable =
                {
                    Path = executable
                    Version = Some profile.ExpectedCodexVersion
                    Sha256 = Some executableSha
                }
            Requested =
                {
                    Model = Some FixedNativeCapabilityDiagnostic.requestedModel
                    Effort = Some FixedNativeCapabilityDiagnostic.requestedEffort
                }
            MaximumAge = TimeSpan.FromMinutes 5.
        }

    {
        Root = root
        Config = config
        Executable = executable
        Evidence = evidence
        SourceRoot = sourceRoot
        Now = now
        Query = query
        Options =
            {
                Enabled = true
                HostConfigPath = config
                ExpectedOwnerUid = effectiveUserId ()
                ExpectedExecutableOwnerUid = effectiveUserId ()
                MaximumCapabilityAge = TimeSpan.FromMinutes 5.
            }
    }

let private fixture () =
    fixtureWithExecutable (fun path ->
        writePrivate path [| 0x7fuy; byte 'E'; byte 'L'; byte 'F'; 1uy; 2uy; 3uy |])

let private successReceipt observed expires (query: LearningInstalledProducerReceiptQuery) =
    Ok
        {
            Source =
                {
                    ProducerId = query.ProducerId
                    Revision = string query.GrantGeneration
                    RecordId = "producer-receipt-1"
                    ObservedAt = observed
                }
            WorkspaceId = query.WorkspaceId
            ProducerId = query.ProducerId
            StreamId = query.StreamId
            Role = query.Role
            GrantId = query.GrantId
            GrantGeneration = query.GrantGeneration
            ManagerReceiptSha256 = query.ManagerReceiptSha256
            CapabilityProfileSha256 = query.CapabilityProfileSha256
            CapabilityResultSha256 = query.CapabilityResultSha256
            NativeCaptureSha256 = query.NativeCaptureSha256
            NativeVerificationSha256 = query.NativeVerificationSha256
            CapabilityObservedAt = observed
            CapabilityExpiresAt = expires
        }

[<Fact>]
let ``disabled source touches neither private custody nor producer receipt`` () =
    task {
        let receipts =
            ReceiptSource(fun _ -> failwith "disabled source called receipt authority")

        let options =
            {
                Enabled = false
                HostConfigPath = "/missing/private/host.json"
                ExpectedOwnerUid = effectiveUserId ()
                ExpectedExecutableOwnerUid = effectiveUserId ()
                MaximumCapabilityAge = TimeSpan.FromMinutes 5.
            }

        let source =
            LearningInstalledReadinessSource(FixedClock(DateTimeOffset.UtcNow), options, receipts)
            :> ILearningInstalledReadinessSource

        let! result =
            source.ReadLearningInstalledReadiness(Unchecked.defaultof<LearningSelectionQuery>, CancellationToken.None)

        Assert.Equal(Error "learning-installed-readiness-disabled", result)
        Assert.Equal(0, receipts.Calls)
    }

[<Fact>]
let ``installed source joins manager grant capability and actual native capture through producer receipt`` () =
    task {
        use value = fixture ()
        let observed = value.Now.AddMinutes(-1.)
        let expires = value.Now.AddMinutes 3.
        let receipts = ReceiptSource(successReceipt observed expires)

        let source =
            LearningInstalledReadinessSource(FixedClock(value.Now), value.Options, receipts)
            :> ILearningInstalledReadinessSource

        let! result =
            source.ReadLearningInstalledReadiness(value.Query, CancellationToken.None)

        let snapshot = result |> Result.defaultWith failwith
        Assert.Equal(1, receipts.Calls)
        Assert.Equal(observed, snapshot.CapabilityEvidence.ObservedAt)
        Assert.Equal(expires, snapshot.ExpiresAt)
        Assert.Equal(LearningCapabilityStatus.Supported, snapshot.CapabilityEvidence.Status)
        Assert.Equal(Some FixedNativeCapabilityDiagnostic.requestedModel, snapshot.NativeSelection.Model)
        Assert.True(snapshot.InstalledCustody.RecordId <> snapshot.ProviderCapability.RecordId)
        Assert.True(snapshot.ProviderCapability.RecordId <> snapshot.NativeCapture.RecordId)
    }

[<Fact>]
let ``installed source streams the exact pinned Codex executable size`` () =
    task {
        use value =
            fixtureWithExecutable (writeSparseExecutable 286594376L)

        let observed = value.Now.AddMinutes(-1.)
        let expires = value.Now.AddMinutes 3.
        let receipts = ReceiptSource(successReceipt observed expires)

        let source =
            LearningInstalledReadinessSource(FixedClock(value.Now), value.Options, receipts)
            :> ILearningInstalledReadinessSource

        let! result =
            source.ReadLearningInstalledReadiness(value.Query, CancellationToken.None)

        Assert.True(Result.isOk result)
        Assert.Equal(1, receipts.Calls)
    }

[<Fact>]
let ``installed source refuses executable above the finite stream ceiling before producer authority`` () =
    task {
        use value = fixture ()
        File.Delete value.Executable
        writeSparseExecutable (512L * 1024L * 1024L + 1L) value.Executable
        File.SetUnixFileMode(
            value.Executable,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        let receipts =
            ReceiptSource(successReceipt (value.Now.AddMinutes(-1.)) (value.Now.AddMinutes 3.))

        let source =
            LearningInstalledReadinessSource(FixedClock(value.Now), value.Options, receipts)
            :> ILearningInstalledReadinessSource

        let! result =
            source.ReadLearningInstalledReadiness(value.Query, CancellationToken.None)

        Assert.Equal(Error "learning-installed-file-custody-refused", result)
        Assert.Equal(0, receipts.Calls)
    }

let private expectLocalRefusal mutate =
    task {
        use value = fixture ()
        mutate value

        let receipts =
            ReceiptSource(successReceipt (value.Now.AddMinutes(-1.)) (value.Now.AddMinutes 3.))

        let source =
            LearningInstalledReadinessSource(FixedClock(value.Now), value.Options, receipts)
            :> ILearningInstalledReadinessSource

        let! result =
            source.ReadLearningInstalledReadiness(value.Query, CancellationToken.None)

        Assert.True(Result.isError result)
        Assert.Equal(0, receipts.Calls)
    }

[<Fact>]
let ``installed source refuses digest mismatch replacement and truncation before producer authority`` () =
    task {
        do!
            expectLocalRefusal (fun value ->
                use stream = File.Open(value.Executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                stream.Position <- stream.Length - 1L
                stream.WriteByte 0xffuy)

        for mutation in [ "replacement"; "truncation" ] do
            use value = fixtureWithExecutable (writeSparseExecutable (128L * 1024L * 1024L))

            let receipts =
                ReceiptSource(successReceipt (value.Now.AddMinutes(-1.)) (value.Now.AddMinutes 3.))

            let source =
                LearningInstalledReadinessSource(FixedClock(value.Now), value.Options, receipts)
                :> ILearningInstalledReadinessSource

            let operation: Task<Result<LearningInstalledReadinessSnapshot, string>> =
                Task.Run<Result<LearningInstalledReadinessSnapshot, string>>(
                    Func<Task<Result<LearningInstalledReadinessSnapshot, string>>>(fun () ->
                        source.ReadLearningInstalledReadiness(value.Query, CancellationToken.None))
                )

            let mutable mutations = 0

            while not operation.IsCompleted && mutations < 64 do
                if mutation = "replacement" then
                    let replacement = value.Executable + $".replacement-%d{mutations}"
                    writeSparseExecutable (128L * 1024L * 1024L) replacement
                    File.SetUnixFileMode(
                        replacement,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )
                    File.Move(replacement, value.Executable, true)
                else
                    use stream = File.Open(value.Executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                    stream.SetLength(1024L + int64 mutations)

                mutations <- mutations + 1
                Thread.Yield() |> ignore

            let! result = operation
            Assert.True(mutations > 0)
            Assert.True(Result.isError result)
            Assert.Equal(0, receipts.Calls)
    }


[<Fact>]
let ``changed executable revoked grant stale advertisement and foreign native capture refuse before producer authority``
    ()
    =
    task {
        do! expectLocalRefusal (fun value -> File.AppendAllText(value.Executable, "changed"))

        do!
            expectLocalRefusal (fun value ->
                let node = JsonNode.Parse(File.ReadAllBytes value.Config).AsObject()
                let credentials = node["Credentials"].AsArray()
                let credential = credentials[0].AsObject()
                credential["Revoked"] <- true
                writePrivate value.Config (JsonSerializer.SerializeToUtf8Bytes node))

        do!
            expectLocalRefusal (fun value ->
                let path = Path.Combine(value.Evidence, "fixed-native-capability-result.json")
                let node = JsonNode.Parse(File.ReadAllBytes path).AsObject()
                node["disposition"] <- "advertised-unsupported"
                writePrivate path (JsonSerializer.SerializeToUtf8Bytes node))

        do!
            expectLocalRefusal (fun value ->
                let path = Path.Combine(value.Evidence, "fixed-native-capability-result.json")
                let node = JsonNode.Parse(File.ReadAllBytes path).AsObject()
                node["evidenceObservedAt"] <- value.Now.AddMinutes(-10.).ToString("O")
                node["evidenceExpiresAt"] <- value.Now.AddMinutes(-5.).ToString("O")
                writePrivate path (JsonSerializer.SerializeToUtf8Bytes node))

        do!
            expectLocalRefusal (fun value ->
                let path = value.Config + ".native-collector.receipt.json"
                let node = JsonNode.Parse(File.ReadAllBytes path).AsObject()
                node["ownerUid"] <- int64 (effectiveUserId () + 1u)
                writePrivate path (JsonSerializer.SerializeToUtf8Bytes node))

        do!
            expectLocalRefusal (fun value ->
                let path = Path.Combine(value.Evidence, "native-source-capture.json")
                let node = JsonNode.Parse(File.ReadAllBytes path).AsObject()
                let projection = node["projection"].AsObject()
                let threads = projection["threads"].AsArray()
                let thread = threads[0].AsObject()
                thread["model"] <- "foreign-model"
                writePrivate path (JsonSerializer.SerializeToUtf8Bytes node))

        do!
            expectLocalRefusal (fun value ->
                let path = Path.Combine(Path.GetDirectoryName value.Config, "source-reference.json")
                let node = JsonNode.Parse(File.ReadAllBytes path).AsObject()
                node["captureQualified"] <- false
                writePrivate path (JsonSerializer.SerializeToUtf8Bytes node))
    }

[<Fact>]
let ``self consistent private evidence without authenticated producer receipt stays unavailable`` () =
    task {
        use value = fixture ()
        let receipts = ReceiptSource(fun _ -> Error "no authenticated producer receipt")

        let source =
            LearningInstalledReadinessSource(FixedClock(value.Now), value.Options, receipts)
            :> ILearningInstalledReadinessSource

        let! result =
            source.ReadLearningInstalledReadiness(value.Query, CancellationToken.None)

        Assert.Equal(Error "learning-installed-producer-receipt-unavailable", result)
        Assert.Equal(1, receipts.Calls)
    }

[<Fact>]
let ``foreign producer receipt cannot relabel validated installed bytes`` () =
    task {
        use value = fixture ()

        let receipts =
            ReceiptSource(fun query ->
                successReceipt (value.Now.AddMinutes(-1.)) (value.Now.AddMinutes 3.) query
                |> Result.map (fun receipt ->
                    { receipt with
                        ProducerId = "foreign-producer"
                    }))

        let source =
            LearningInstalledReadinessSource(FixedClock(value.Now), value.Options, receipts)
            :> ILearningInstalledReadinessSource

        let! result =
            source.ReadLearningInstalledReadiness(value.Query, CancellationToken.None)

        Assert.Equal(Error "learning-installed-producer-receipt-refused", result)
        Assert.Equal(1, receipts.Calls)
    }
