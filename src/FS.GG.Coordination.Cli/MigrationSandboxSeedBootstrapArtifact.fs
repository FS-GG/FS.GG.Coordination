namespace FS.GG.Coordination.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationSandboxSeedBootstrapArtifactRequest =
    {
        CandidateSha: string
        WorkflowRunId: int64
        WorkflowRunAttempt: int
        WorkflowSha: string
        SourceManifestBytes: ReadOnlyMemory<byte>
        S2DeclarationBytes: ReadOnlyMemory<byte>
        MintProofBytes: ReadOnlyMemory<byte>
        SeedPlanBytes: ReadOnlyMemory<byte>
        CorpusBytes: ReadOnlyMemory<byte>
        Prestate: MigrationSandboxSeedPrestate
    }

type MigrationSandboxSeedBootstrapSourceRequest =
    {
        CandidateSha: string
        SeedPlanBytes: ReadOnlyMemory<byte>
        CorpusBytes: ReadOnlyMemory<byte>
    }

type MigrationSandboxSeedBootstrapSourceSet =
    {
        ManifestBytes: byte array
        Files: (string * byte array) list
        ApprovedArtifactSourceSha256: string
    }

type MigrationSandboxSeedBootstrapArtifactSet =
    {
        ManifestBytes: byte array
        Files: (string * byte array) list
        JournalPlan: MigrationSandboxSeedJournalPlan
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedBootstrapArtifactFailure =
    | InvalidInput
    | S2DeclarationMismatch
    | JournalPlanFailure

[<RequireQualifiedAccess>]
module MigrationSandboxSeedBootstrapArtifact =
    let private sha (bytes: ReadOnlyMemory<byte>) =
        SHA256.HashData(bytes.Span) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private hex length (value: string) =
        not (isNull value)
        && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private sourceManifest (request: MigrationSandboxSeedBootstrapSourceRequest) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.gs2-09-7.sandbox-seed-source-artifacts/1")
        writer.WriteString("status", "source-only-no-authority")
        writer.WriteString("candidateSha", request.CandidateSha)
        writer.WriteStartObject("seedPlan")
        writer.WriteString("path", "seed-plan.json")
        writer.WriteNumber("byteLength", request.SeedPlanBytes.Length)
        writer.WriteString("sha256", sha request.SeedPlanBytes)
        writer.WriteEndObject()
        writer.WriteStartObject("corpus")
        writer.WriteString("path", "corpus.json")
        writer.WriteNumber("byteLength", request.CorpusBytes.Length)
        writer.WriteString("sha256", sha request.CorpusBytes)
        writer.WriteEndObject()
        writer.WriteBoolean("postMintSealRequired", true)
        writer.WriteBoolean("bootstrapAuthority", false)
        writer.WriteBoolean("providerEffectsAuthorized", false)
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let prepareSource (request: MigrationSandboxSeedBootstrapSourceRequest) =
        let bounded (bytes: ReadOnlyMemory<byte>) limit =
            bytes.Length > 0 && bytes.Length <= limit

        if
            not (hex 40 request.CandidateSha)
            || not (bounded request.SeedPlanBytes (64 * 1024 * 1024))
            || not (bounded request.CorpusBytes (64 * 1024 * 1024))
        then
            Error MigrationSandboxSeedBootstrapArtifactFailure.InvalidInput
        else
            let files =
                [
                    "seed-plan.json", request.SeedPlanBytes.ToArray()
                    "corpus.json", request.CorpusBytes.ToArray()
                ]

            let bytes = sourceManifest request
            let approvedSource = sha (ReadOnlyMemory bytes)

            Ok
                {
                    ManifestBytes = bytes
                    Files = files
                    ApprovedArtifactSourceSha256 = approvedSource
                }

    let private sourceMatches (request: MigrationSandboxSeedBootstrapArtifactRequest) =
        try
            use document = JsonDocument.Parse request.SourceManifestBytes
            let root = document.RootElement
            let properties = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
            let plan = root.GetProperty("seedPlan")
            let corpus = root.GetProperty("corpus")

            let approvedSource = sha request.SourceManifestBytes

            properties =
                set
                    [
                        "schema"
                        "status"
                        "candidateSha"
                        "seedPlan"
                        "corpus"
                        "postMintSealRequired"
                        "bootstrapAuthority"
                        "providerEffectsAuthorized"
                    ]
            && root.GetProperty("schema").GetString() = "fsgg.gs2-09-7.sandbox-seed-source-artifacts/1"
            && root.GetProperty("status").GetString() = "source-only-no-authority"
            && root.GetProperty("candidateSha").GetString() = request.CandidateSha
            && root.GetProperty("postMintSealRequired").GetBoolean()
            && not (root.GetProperty("bootstrapAuthority").GetBoolean())
            && not (root.GetProperty("providerEffectsAuthorized").GetBoolean())
            && plan.GetProperty("path").GetString() = "seed-plan.json"
            && plan.GetProperty("byteLength").GetInt64() = int64 request.SeedPlanBytes.Length
            && plan.GetProperty("sha256").GetString() = sha request.SeedPlanBytes
            && corpus.GetProperty("path").GetString() = "corpus.json"
            && corpus.GetProperty("byteLength").GetInt64() = int64 request.CorpusBytes.Length
            && corpus.GetProperty("sha256").GetString() = sha request.CorpusBytes
        with _ ->
            false

    let private declarationMatches (request: MigrationSandboxSeedBootstrapArtifactRequest) =
        try
            use document = JsonDocument.Parse request.S2DeclarationBytes
            let root = document.RootElement
            let source = root.GetProperty "source"
            let sandbox = root.GetProperty "sandbox"
            let mint = root.GetProperty "mint"
            let artifacts = root.GetProperty "artifacts"
            let journal = root.GetProperty "journal"
            let provenance = root.GetProperty "provenanceInterface"

            let nonce =
                $"{request.WorkflowRunId}-{request.WorkflowRunAttempt}-{request.CandidateSha}"

            let approvedSource = sha request.SourceManifestBytes

            root.GetProperty("schema").GetString() = "fsgg.github-substrate-v2.sandbox-seed-execution-binding/2"
            && root.GetProperty("status").GetString() = "bound-no-write-authority"
            && not (root.GetProperty("activation").GetBoolean())
            && root.GetProperty("authority").GetString() =
                "unavailable-without-protected-host-install-and-native-readback"
            && root.GetProperty("schemaJoin").GetString() = "coordination-s1-provenance-interface-v1"
            && source.GetProperty("repository").GetString() = "FS-GG/.github"
            && source.GetProperty("workflowPath").GetString() =
                ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
            && source.GetProperty("workflowRef").GetString() = "refs/heads/main"
            && source.GetProperty("workflowSha").GetString() = request.WorkflowSha
            && source.GetProperty("providerWorkflowSha").GetString() = request.WorkflowSha
            && source.GetProperty("candidateSha").GetString() = request.CandidateSha
            && source.GetProperty("runId").GetInt64() = request.WorkflowRunId
            && source.GetProperty("runAttempt").GetInt32() = request.WorkflowRunAttempt
            && source.GetProperty("runNonce").GetString() = nonce
            && source.GetProperty("seedJournalRef").GetString() = $"refs/heads/gs2-09-7/{nonce}/seed-journal"
            && source.GetProperty("approvedArtifactSourceSha256").GetString() = approvedSource
            && provenance.GetProperty("workflowRunId").GetInt64() = request.WorkflowRunId
            && provenance.GetProperty("workflowRunAttempt").GetInt32() = request.WorkflowRunAttempt
            && provenance.GetProperty("workflowSha").GetString() = request.WorkflowSha
            && provenance.GetProperty("approvedArtifactSourceSha256").GetString() = approvedSource
            && sandbox.GetProperty("repositoryId").GetInt64() = 1353050537L
            && sandbox.GetProperty("repositoryNodeId").GetString() = "R_kgDOUKXpqQ"
            && sandbox.GetProperty("projectNodeId").GetString() = "PVT_kwDOEYAWY84BiESo"
            && mint.GetProperty("appId").GetInt64() = 4166418L
            && mint.GetProperty("installationId").GetInt64() = 143110413L
            && mint.GetProperty("proofSha256").GetString() = sha request.MintProofBytes
            && artifacts.GetProperty("seedPlan").GetProperty("sha256").GetString() = sha request.SeedPlanBytes
            && artifacts.GetProperty("corpus").GetProperty("sha256").GetString() = sha request.CorpusBytes
            && journal.GetProperty("profile").GetProperty("ref").GetString() =
                $"refs/heads/gs2-09-7/{nonce}/seed-journal"
            && journal.GetProperty("profile").GetProperty("object").GetProperty("path").GetString() = "state.json"
        with _ ->
            false

    let private manifest
        (request: MigrationSandboxSeedBootstrapArtifactRequest)
        (plan: MigrationSandboxSeedJournalPlan)
        (files: (string * byte array) list)
        =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.gs2-09-7.sandbox-seed-bootstrap-artifacts/1")
        writer.WriteString("status", "source-only-no-authority")
        writer.WriteString("candidateSha", request.CandidateSha)
        writer.WriteNumber("workflowRunId", request.WorkflowRunId)
        writer.WriteNumber("workflowRunAttempt", request.WorkflowRunAttempt)
        writer.WriteString("workflowSha", request.WorkflowSha)
        writer.WriteString("runNonce", plan.RunNonce)
        writer.WriteString("refName", plan.RefName)
        writer.WriteNumber("journalGeneration", plan.JournalGeneration)
        writer.WriteNumber("stateGeneration", plan.StateGeneration)
        writer.WriteNull("expectedParent")
        writer.WriteString("stateSha256", plan.StateSha256)
        writer.WriteString("blobOid", plan.BlobOid)
        writer.WriteString("treeOid", plan.TreeOid)
        writer.WriteString("commitOid", plan.CommitOid)
        writer.WriteStartArray("files")

        for path, bytes in files do
            writer.WriteStartObject()
            writer.WriteString("path", path)
            writer.WriteNumber("byteLength", bytes.LongLength)
            writer.WriteString("sha256", sha (ReadOnlyMemory bytes))
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteBoolean("bootstrapAuthority", false)
        writer.WriteBoolean("providerEffectsAuthorized", false)
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let prepare (request: MigrationSandboxSeedBootstrapArtifactRequest) =
        let bounded (bytes: ReadOnlyMemory<byte>) limit =
            bytes.Length > 0 && bytes.Length <= limit

        if
            not (hex 40 request.CandidateSha)
            || request.WorkflowRunId <= 0L
            || request.WorkflowRunAttempt <= 0
            || not (hex 40 request.WorkflowSha)
            || not (bounded request.SourceManifestBytes (1024 * 1024))
            || not (bounded request.S2DeclarationBytes (1024 * 1024))
            || not (bounded request.MintProofBytes (1024 * 1024))
            || not (bounded request.SeedPlanBytes (64 * 1024 * 1024))
            || not (bounded request.CorpusBytes (64 * 1024 * 1024))
        then
            Error MigrationSandboxSeedBootstrapArtifactFailure.InvalidInput
        elif not (sourceMatches request) || not (declarationMatches request) then
            Error MigrationSandboxSeedBootstrapArtifactFailure.S2DeclarationMismatch
        else
            let nonce =
                $"{request.WorkflowRunId}-{request.WorkflowRunAttempt}-{request.CandidateSha}"

            let draft =
                {
                    Request =
                        {
                            CandidateSha = request.CandidateSha
                            WorkflowRunId = request.WorkflowRunId
                            WorkflowRunAttempt = request.WorkflowRunAttempt
                            RunNonce = nonce
                            CorpusSha256 = sha request.CorpusBytes
                        }
                    WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                    WorkflowRef = "refs/heads/main"
                    WorkflowSha = request.WorkflowSha
                    RepositoryId = 1353050537L
                    RepositoryNodeId = "R_kgDOUKXpqQ"
                    ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
                    MintProofSha256 = sha request.MintProofBytes
                    ProtectedHostReceiptSha256 = sha request.S2DeclarationBytes
                    SeedPlanSha256 = sha request.SeedPlanBytes
                    CorpusSha256 = sha request.CorpusBytes
                    Prestate = request.Prestate
                    AdmittedEffects =
                        [
                            MigrationSandboxSeedEffectKind.CreateNonceIssue
                            MigrationSandboxSeedEffectKind.AddProjectMembership
                            MigrationSandboxSeedEffectKind.RemoveProjectMembership
                            MigrationSandboxSeedEffectKind.DeleteNonceIssue
                        ]
                    Seal = ""
                }

            MigrationSandboxSeedExecutor.sealBinding
                request.MintProofBytes
                request.S2DeclarationBytes
                request.SeedPlanBytes
                request.CorpusBytes
                draft
            |> Result.mapError (fun _ -> MigrationSandboxSeedBootstrapArtifactFailure.InvalidInput)
            |> Result.bind (fun binding ->
                let initialHead =
                    (sha (ReadOnlyMemory(Encoding.UTF8.GetBytes("fsgg.gs2-09-7.seed-state-head/1\n" + binding.Seal))))
                        .Substring(0, 40)

                MigrationSandboxSeedExecutor.create binding 0L initialHead
                |> Result.mapError (fun _ -> MigrationSandboxSeedBootstrapArtifactFailure.InvalidInput))
            |> Result.bind (fun state ->
                MigrationSandboxSeedJournal.plan None state
                |> Result.mapError (fun _ -> MigrationSandboxSeedBootstrapArtifactFailure.JournalPlanFailure))
            |> Result.map (fun plan ->
                let files =
                    [
                        "s2-declaration.json", request.S2DeclarationBytes.ToArray()
                        "seed-plan.json", request.SeedPlanBytes.ToArray()
                        "corpus.json", request.CorpusBytes.ToArray()
                        "journal/state.json", Array.copy plan.StateBytes
                        "journal/tree.raw", Array.copy plan.TreeBytes
                        "journal/commit.raw", Array.copy plan.CommitBytes
                    ]

                {
                    ManifestBytes = manifest request plan files
                    Files = files
                    JournalPlan = plan
                })
