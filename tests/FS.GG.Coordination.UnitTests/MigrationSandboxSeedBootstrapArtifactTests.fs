module FS.GG.Coordination.MigrationSandboxSeedBootstrapArtifactTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Cli

let private raw (value: string) = Encoding.UTF8.GetBytes value

let private sha (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private candidate = String.replicate 40 "a"
let private workflow = String.replicate 40 "b"
let private nonce = $"71-2-{candidate}"
let private mint = raw "sanitized-mint-proof"
let private plan = raw "seed-plan"
let private corpus = raw "corpus"

let private source =
    MigrationSandboxSeedBootstrapArtifact.prepareSource
        {
            CandidateSha = candidate
            SeedPlanBytes = ReadOnlyMemory plan
            CorpusBytes = ReadOnlyMemory corpus
        }
    |> function
        | Ok value -> value
        | Error error -> failwithf "%A" error

let private declaration =
    raw
        $"{{\"schema\":\"fsgg.github-substrate-v2.sandbox-seed-execution-binding/2\",\"status\":\"bound-no-write-authority\",\"activation\":false,\"authority\":\"unavailable-without-protected-host-install-and-native-readback\",\"schemaJoin\":\"coordination-s1-provenance-interface-v1\",\"source\":{{\"repository\":\"FS-GG/.github\",\"workflowPath\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"workflowRef\":\"refs/heads/main\",\"workflowSha\":\"{workflow}\",\"providerWorkflowSha\":\"{workflow}\",\"candidateSha\":\"{candidate}\",\"runId\":71,\"runAttempt\":2,\"runNonce\":\"{nonce}\",\"seedJournalRef\":\"refs/heads/gs2-09-7/{nonce}/seed-journal\",\"approvedArtifactSourceSha256\":\"{source.ApprovedArtifactSourceSha256}\"}},\"provenanceInterface\":{{\"workflowRunId\":71,\"workflowRunAttempt\":2,\"workflowSha\":\"{workflow}\",\"approvedArtifactSourceSha256\":\"{source.ApprovedArtifactSourceSha256}\"}},\"sandbox\":{{\"repositoryId\":1353050537,\"repositoryNodeId\":\"R_kgDOUKXpqQ\",\"projectNodeId\":\"PVT_kwDOEYAWY84BiESo\"}},\"mint\":{{\"appId\":4166418,\"installationId\":143110413,\"proofSha256\":\"{sha mint}\"}},\"artifacts\":{{\"seedPlan\":{{\"sha256\":\"{sha plan}\"}},\"corpus\":{{\"sha256\":\"{sha corpus}\"}}}},\"journal\":{{\"profile\":{{\"ref\":\"refs/heads/gs2-09-7/{nonce}/seed-journal\",\"object\":{{\"path\":\"state.json\"}}}}}}}}"

let private prestate =
    {
        Complete = true
        RepositoryId = 1353050537L
        ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
        NonceIssueCount = 0
        NonceProjectItemCount = 0
        SnapshotSha256 = String.replicate 64 "c"
    }

let private request declarationBytes =
    {
        CandidateSha = candidate
        WorkflowRunId = 71L
        WorkflowRunAttempt = 2
        WorkflowSha = workflow
        S2DeclarationBytes = ReadOnlyMemory declarationBytes
        MintProofBytes = ReadOnlyMemory mint
        SourceManifestBytes = ReadOnlyMemory source.ManifestBytes
        SeedPlanBytes = ReadOnlyMemory plan
        CorpusBytes = ReadOnlyMemory corpus
        Prestate = prestate
    }

let private unwrap =
    function
    | Ok value -> value
    | Error error -> failwithf "%A" error

[<Fact>]
let ``offline composer emits deterministic exact generation zero artifacts without authority`` () =
    use sourceDocument = JsonDocument.Parse source.ManifestBytes
    Assert.Equal("source-only-no-authority", sourceDocument.RootElement.GetProperty("status").GetString())
    Assert.True(sourceDocument.RootElement.GetProperty("postMintSealRequired").GetBoolean())

    let first =
        MigrationSandboxSeedBootstrapArtifact.prepare (request declaration) |> unwrap

    let second =
        MigrationSandboxSeedBootstrapArtifact.prepare (request declaration) |> unwrap

    Assert.Equal(first.ManifestBytes, second.ManifestBytes)
    Assert.Equal(first.JournalPlan, second.JournalPlan)
    Assert.Equal(0L, first.JournalPlan.JournalGeneration)
    Assert.Equal(0L, first.JournalPlan.StateGeneration)
    Assert.True(first.JournalPlan.ExpectedParent.IsNone)

    Assert.Equal<string list>(
        [
            "s2-declaration.json"
            "seed-plan.json"
            "corpus.json"
            "journal/state.json"
            "journal/tree.raw"
            "journal/commit.raw"
        ],
        first.Files |> List.map fst
    )

    use document = JsonDocument.Parse first.ManifestBytes
    let root = document.RootElement
    Assert.Equal("source-only-no-authority", root.GetProperty("status").GetString())
    Assert.False(root.GetProperty("bootstrapAuthority").GetBoolean())
    Assert.False(root.GetProperty("providerEffectsAuthorized").GetBoolean())
    Assert.Equal(first.JournalPlan.CommitOid, root.GetProperty("commitOid").GetString())

[<Fact>]
let ``composer refuses declaration drift unsafe prestate and malformed identity`` () =
    let changed =
        Encoding.UTF8.GetString(declaration).Replace("state.json", "journal.json")
        |> raw

    Assert.Equal(
        Error MigrationSandboxSeedBootstrapArtifactFailure.S2DeclarationMismatch,
        MigrationSandboxSeedBootstrapArtifact.prepare (request changed)
    )

    Assert.Equal(
        Error MigrationSandboxSeedBootstrapArtifactFailure.InvalidInput,
        MigrationSandboxSeedBootstrapArtifact.prepare
            { request declaration with
                CandidateSha = String.replicate 40 "A"
            }
    )

    Assert.True(
        MigrationSandboxSeedBootstrapArtifact.prepare
            { request declaration with
                Prestate = { prestate with NonceIssueCount = 1 }
            }
        |> Result.isError
    )

    let changedSource = Array.copy source.ManifestBytes
    changedSource[changedSource.Length - 2] <- byte '1'

    Assert.True(
        MigrationSandboxSeedBootstrapArtifact.prepare
            { request declaration with
                SourceManifestBytes = ReadOnlyMemory changedSource
            }
        |> Result.isError
    )

[<Fact>]
let ``command separates static prepare from protected post mint seal`` () =
    let root =
        Path.Combine(Path.GetTempPath(), $"fsgg-seed-bootstrap-{Guid.NewGuid():N}")

    let inputs = Path.Combine(root, "inputs")
    let prepared = Path.Combine(root, "prepared")
    let sealedOutput = Path.Combine(root, "sealed")
    Directory.CreateDirectory inputs |> ignore

    let write (name: string) (bytes: byte array) =
        let path = Path.Combine(inputs, name)
        File.WriteAllBytes(path, bytes)
        path

    let planPath = write "seed-plan.json" plan
    let corpusPath = write "corpus.json" corpus

    try
        Assert.Equal(
            0,
            MigrationSandboxSeedBootstrapCommand.run
                [|
                    "prepare"
                    "--candidate-sha"
                    candidate
                    "--seed-plan"
                    planPath
                    "--corpus"
                    corpusPath
                    "--output-dir"
                    prepared
                |]
        )

        let sourcePath = Path.Combine(prepared, "source-manifest.json")
        Assert.True(File.Exists sourcePath)
        let declarationPath = write "s2-declaration.json" declaration
        let mintPath = write "mint-proof.json" mint

        let prestatePath =
            write
                "prestate.json"
                (raw
                    $"{{\"schema\":\"fsgg.gs2-09-7.sandbox-seed-prestate/1\",\"complete\":true,\"repositoryId\":1353050537,\"projectNodeId\":\"PVT_kwDOEYAWY84BiESo\",\"nonceIssueCount\":0,\"nonceProjectItemCount\":0,\"snapshotSha256\":\"{prestate.SnapshotSha256}\"}}")

        Assert.Equal(
            0,
            MigrationSandboxSeedBootstrapCommand.run
                [|
                    "seal"
                    "--candidate-sha"
                    candidate
                    "--workflow-run-id"
                    "71"
                    "--workflow-run-attempt"
                    "2"
                    "--workflow-sha"
                    workflow
                    "--source-manifest"
                    sourcePath
                    "--s2-declaration"
                    declarationPath
                    "--mint-proof"
                    mintPath
                    "--seed-plan"
                    planPath
                    "--corpus"
                    corpusPath
                    "--prestate"
                    prestatePath
                    "--output-dir"
                    sealedOutput
                |]
        )

        Assert.True(File.Exists(Path.Combine(sealedOutput, "bootstrap-manifest.json")))
        Assert.True(File.Exists(Path.Combine(sealedOutput, "journal", "state.json")))
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)
