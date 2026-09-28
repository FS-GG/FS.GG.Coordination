module FS.GG.Coordination.MigrationSandboxSeedNativeProvenanceTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli

let private raw (text: string) = Encoding.UTF8.GetBytes text

let private sha (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private head = String.replicate 40 "a"
let private workflow = raw "protected-workflow"
let private builder = raw "protected-builder"
let private plan = raw "seed-plan"
let private corpus = raw "corpus"
let private sourceArtifact =
    MigrationSandboxSeedBootstrapArtifact.prepareSource
        {
            CandidateSha = head
            SeedPlanBytes = ReadOnlyMemory plan
            CorpusBytes = ReadOnlyMemory corpus
        }
    |> Result.map _.ManifestBytes
    |> Result.defaultWith (fun error -> failwithf "%A" error)
let private token = raw "protected-app-token"

let private mintResponse =
    raw
        "{\"repository_selection\":\"selected\",\"token\":\"protected-app-token\",\"expires_at\":\"2026-09-28T12:00:00Z\",\"permissions\":{\"administration\":\"write\",\"contents\":\"write\",\"issues\":\"write\",\"metadata\":\"read\",\"organization_projects\":\"write\",\"pull_requests\":\"write\"}}"

let private viewerResponse =
    raw "{\"data\":{\"viewer\":{\"login\":\"fs-gg-cross-repo-dispatch[bot]\",\"databaseId\":297630107}}}"

let private proof =
    raw
        $"{{\"schema\":\"fsgg.github-substrate-v2.sandbox-mint-grants/1\",\"appId\":4166418,\"installationId\":143110413,\"appSlug\":\"fs-gg-cross-repo-dispatch\",\"repositorySelection\":\"selected\",\"actor\":{{\"login\":\"fs-gg-cross-repo-dispatch[bot]\",\"databaseId\":297630107}},\"repository\":{{\"id\":1353050537,\"nodeId\":\"R_kgDOUKXpqQ\",\"fullName\":\"FS-GG/FS.GG.GitHub.Substrate.Sandbox\"}},\"permissions\":{{\"administration\":\"write\",\"contents\":\"write\",\"issues\":\"write\",\"metadata\":\"read\",\"organization_projects\":\"write\",\"pull_requests\":\"write\"}},\"tokenSha256\":\"{sha token}\",\"expiresAt\":\"2026-09-28T12:00:00Z\",\"mintResponseSha256\":\"{sha mintResponse}\",\"viewerResponseSha256\":\"{sha viewerResponse}\"}}"

let private nonce = $"7-2-{head}"
let private refName = $"refs/heads/gs2-09-7/{nonce}/seed-journal"

let private binding =
    raw
        $"{{\"schema\":\"fsgg.github-substrate-v2.sandbox-seed-execution-binding/2\",\"status\":\"bound-no-write-authority\",\"activation\":false,\"authority\":\"unavailable-without-protected-host-install-and-native-readback\",\"schemaJoin\":\"coordination-s1-provenance-interface-v1\",\"source\":{{\"repository\":\"FS-GG/.github\",\"workflowPath\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"workflowRef\":\"refs/heads/main\",\"workflowSha\":\"{head}\",\"providerWorkflowSha\":\"{head}\",\"approvedArtifactSourceSha256\":\"{sha sourceArtifact}\",\"seedJournalRef\":\"{refName}\",\"runId\":7,\"runAttempt\":2,\"candidateSha\":\"{head}\",\"runNonce\":\"{nonce}\",\"builderPath\":\"scripts/gs2-09-7-seed-execution-binding.py\",\"builderSha256\":\"{sha builder}\",\"protectedCheckout\":{{\"checkoutHead\":\"{head}\",\"workflow\":{{\"path\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"sha256\":\"{sha workflow}\"}},\"builder\":{{\"path\":\"scripts/gs2-09-7-seed-execution-binding.py\",\"sha256\":\"{sha builder}\"}}}}}},\"sandbox\":{{\"repositoryId\":1353050537,\"repositoryNodeId\":\"R_kgDOUKXpqQ\",\"projectNodeId\":\"PVT_kwDOEYAWY84BiESo\"}},\"mint\":{{\"appId\":4166418,\"installationId\":143110413,\"proofSha256\":\"{sha proof}\",\"tokenSha256\":\"{sha token}\"}},\"artifacts\":{{\"seedPlan\":{{\"sha256\":\"{sha plan}\"}},\"corpus\":{{\"sha256\":\"{sha corpus}\"}}}},\"journal\":{{\"profile\":{{\"ref\":\"{refName}\"}}}}}}"

let private bootstrapSnapshot = raw "{}"
let private bootstrapSnapshotSha = sha bootstrapSnapshot

let private bootstrapPrestate =
    raw
        $"{{\"schema\":\"fsgg.gs2-09-7.sandbox-seed-prestate/1\",\"complete\":true,\"repositoryId\":1353050537,\"projectNodeId\":\"PVT_kwDOEYAWY84BiESo\",\"nonceIssueCount\":0,\"nonceProjectItemCount\":0,\"snapshotSha256\":\"{bootstrapSnapshotSha}\"}}"

let private bootstrapPrestateEvidence =
    let request url status =
        $"{{\"method\":\"GET\",\"url\":\"{url}\",\"status\":{status},\"link\":null,\"bodyBase64\":\"e30=\"}}"

    let requests =
        [
            request "https://api.github.com/repos/FS-GG/FS.GG.GitHub.Substrate.Sandbox" 200
            request "https://api.github.com/repos/FS-GG/FS.GG.GitHub.Substrate.Sandbox/issues?state=all&per_page=100" 200
            request "https://api.github.com/graphql?query=project" 200
            request "https://api.github.com/graphql?query=items" 200
            request "https://api.github.com/repos/FS-GG/FS.GG.GitHub.Substrate.Sandbox/git/ref/heads/main" 200
            request $"https://api.github.com/repos/FS-GG/FS.GG.GitHub.Substrate.Sandbox/git/ref/heads/gs2-09-7/{nonce}/seed-journal" 404
        ]
        |> String.concat ","

    let pass = $"{{\"snapshot\":{{}},\"requests\":[{requests}]}}"

    raw
        $"{{\"schema\":\"fsgg.gs2-09-7.sandbox-seed-prestate-evidence/1\",\"runNonce\":\"{nonce}\",\"refName\":\"{refName}\",\"repositoryId\":1353050537,\"projectNodeId\":\"PVT_kwDOEYAWY84BiESo\",\"expectedRefAbsent\":true,\"snapshotSha256\":\"{bootstrapSnapshotSha}\",\"summarySha256\":\"{sha bootstrapPrestate}\",\"observedAt\":\"2026-09-28T10:00:00Z\",\"passes\":[{pass},{pass}],\"complete\":true}}"

let private bootstrapProposal =
    let request =
        {
            CandidateSha = head
            WorkflowRunId = 7L
            WorkflowRunAttempt = 2
            RunNonce = nonce
            CorpusSha256 = sha corpus
        }

    let draft: MigrationSandboxSeedExecutionBinding =
        {
            Request = request
            WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
            WorkflowRef = "refs/heads/main"
            WorkflowSha = head
            RepositoryId = 1353050537L
            RepositoryNodeId = "R_kgDOUKXpqQ"
            ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
            MintProofSha256 = sha proof
            ProtectedHostReceiptSha256 = sha binding
            SeedPlanSha256 = sha plan
            CorpusSha256 = sha corpus
            Prestate =
                {
                    Complete = true
                    RepositoryId = 1353050537L
                    ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
                    NonceIssueCount = 0
                    NonceProjectItemCount = 0
                    SnapshotSha256 = bootstrapSnapshotSha
                }
            AdmittedEffects =
                [
                    MigrationSandboxSeedEffectKind.CreateNonceIssue
                    MigrationSandboxSeedEffectKind.AddProjectMembership
                    MigrationSandboxSeedEffectKind.RemoveProjectMembership
                    MigrationSandboxSeedEffectKind.DeleteNonceIssue
                ]
            Seal = ""
        }

    let sealedBinding =
        MigrationSandboxSeedExecutor.sealBinding
            (ReadOnlyMemory proof)
            (ReadOnlyMemory binding)
            (ReadOnlyMemory plan)
            (ReadOnlyMemory corpus)
            draft
        |> Result.defaultWith (fun error -> failwithf "%A" error)

    let state =
        MigrationSandboxSeedExecutor.create sealedBinding 0L (String.replicate 40 "9")
        |> Result.defaultWith (fun error -> failwithf "%A" error)

    MigrationSandboxSeedJournal.plan None state
    |> Result.defaultWith (fun error -> failwithf "%A" error)

let private bootstrapAdmission =
    raw
        $"{{\"schema\":\"fsgg.gs2-09-7.seed-admission-request/1\",\"phase\":\"final\",\"subject\":{{\"workflowRepository\":\"FS-GG/.github\",\"workflowPath\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"environment\":\"github-substrate-v2-sandbox\",\"workflowSha\":\"{head}\",\"runId\":7,\"runAttempt\":2,\"candidateSha\":\"{head}\",\"runNonce\":\"{nonce}\",\"approvedArtifactSourceSha256\":\"{sha sourceArtifact}\",\"sourceManifestSha256\":\"{sha sourceArtifact}\",\"prestateSha256\":\"{sha bootstrapPrestate}\",\"prestateSnapshotSha256\":\"{bootstrapSnapshotSha}\",\"prestateEvidenceSha256\":\"{sha bootstrapPrestateEvidence}\",\"expectedRefAbsent\":true,\"sandboxRepositoryId\":1353050537,\"sandboxRepositoryNodeId\":\"R_kgDOUKXpqQ\",\"projectNodeId\":\"PVT_kwDOEYAWY84BiESo\",\"appId\":4166418,\"installationId\":143110413,\"seedPlanSha256\":\"{sha plan}\",\"s2DeclarationSha256\":\"{sha binding}\",\"refName\":\"{refName}\",\"mintProofSha256\":\"{sha proof}\",\"tokenSha256\":\"{sha token}\",\"blobOid\":\"{bootstrapProposal.BlobOid}\",\"treeOid\":\"{bootstrapProposal.TreeOid}\",\"commitOid\":\"{bootstrapProposal.CommitOid}\",\"expectedOldOid\":null,\"operation\":\"genesis-nonce-seed-journal\"}}}}"

let private run =
    raw
        $"{{\"id\":7,\"run_attempt\":2,\"head_sha\":\"{head}\",\"head_branch\":\"main\",\"event\":\"workflow_dispatch\",\"path\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"repository\":{{\"full_name\":\"FS-GG/.github\"}}}}"

let private repo =
    raw
        "{\"id\":1353050537,\"node_id\":\"R_kgDOUKXpqQ\",\"full_name\":\"FS-GG/FS.GG.GitHub.Substrate.Sandbox\",\"private\":true,\"description\":\"fsgg-sandbox-gs2-04-9 disposable qualification target; never production\"}"

let private project =
    raw
        "{\"organization\":\"FS-GG\",\"number\":2,\"id\":\"PVT_kwDOEYAWY84BiESo\",\"title\":\"fsgg-sandbox-gs2-04-9\",\"private\":true,\"closed\":false}"

let private commit = String.replicate 40 "c"
let private tree = String.replicate 40 "d"
let private blob = String.replicate 40 "e"
let private payload = String.replicate 64 "f"

let private casAt observedAt =
    raw
        $"{{\"schema\":\"fsgg.gs2-09-7.sandbox-nonce-ref-cas-readback/1\",\"status\":\"readback-complete\",\"outcome\":\"applied-or-already-applied\",\"complete\":true,\"repositoryId\":1353050537,\"repository\":{{\"id\":1353050537,\"nodeId\":\"R_kgDOUKXpqQ\",\"fullName\":\"FS-GG/FS.GG.GitHub.Substrate.Sandbox\"}},\"refName\":\"{refName}\",\"runId\":7,\"runAttempt\":2,\"runNonce\":\"{nonce}\",\"candidateSha\":\"{head}\",\"workflowSha\":\"{head}\",\"s2DeclarationSha256\":\"{sha binding}\",\"seedPlanSha256\":\"{sha plan}\",\"oldOid\":null,\"newOid\":\"{commit}\",\"commitOid\":\"{commit}\",\"commitParentOid\":null,\"treeOid\":\"{tree}\",\"blobOid\":\"{blob}\",\"payloadSha256\":\"{payload}\",\"journalGeneration\":0,\"stateGeneration\":0,\"observedRefOid\":\"{commit}\",\"observedCommitParentOid\":null,\"observedTreeOid\":\"{tree}\",\"observedBlobOid\":\"{blob}\",\"observedPayloadSha256\":\"{payload}\",\"readbackSource\":\"fresh-git-fetch-cat-file-terminal-ref-reread\",\"observedAt\":\"{observedAt}\"}}"

let private cas = casAt "2026-09-28T10:00:00Z"
let private freshCas = casAt "2026-09-28T10:01:00Z"

type private Reads(
    ?casResult: Result<byte[], string>,
    ?repoResult: Result<byte[], string>,
    ?bootstrapEvidenceResult: Result<byte[], string>
) =
    interface IMigrationSandboxSeedNativeProvenanceRead with
        member _.ReadRunAttempt(_, _) = Ok run

        member _.ReadGitBlob(_, path) =
            Ok(if path.StartsWith(".github/") then workflow else builder)

        member _.ReadRetained name =
            match name with
            | "mint-proof" -> Ok proof
            | "seed-plan" -> Ok plan
            | "corpus" -> Ok corpus
            | "approved-source" -> Ok sourceArtifact
            | "bootstrap-final-admission" -> Ok bootstrapAdmission
            | "bootstrap-prestate" -> Ok bootstrapPrestate
            | "bootstrap-prestate-evidence" -> Ok bootstrapPrestateEvidence
            | _ -> Error "unknown-retained-name"

        member _.ReadPrivateEphemeral name =
            match name with
            | "mint-response" -> Ok mintResponse
            | "viewer-response" -> Ok viewerResponse
            | _ -> Error "unknown-private-name"

        member _.CurrentTokenSha256() = Ok(sha token)
        member _.ReadSandboxRepository() = defaultArg repoResult (Ok repo)
        member _.ReadSandboxProject() = Ok project
        member _.ReadBootstrapPrestateEvidence() =
            defaultArg bootstrapEvidenceResult (Ok bootstrapPrestateEvidence)
        member _.ReadNativeCasReadback _ = defaultArg casResult (Ok freshCas)

let private evidence =
    {
        BindingBytes = binding
        NativeCasReadbackBytes = cas
        BootstrapAdmissionBytes = [||]
        BootstrapPrestateBytes = [||]
        WorkflowRunId = 7L
        WorkflowRunAttempt = 2
        WorkflowSha = head
        ApprovedArtifactSourceSha256 = sha sourceArtifact
    }

let private verify reader value =
    (MigrationSandboxSeedNativeProvenanceVerifier(reader) :> IMigrationSandboxSeedIsolatedProvenanceVerifier)
        .VerifyExact
        value

let private verifyBootstrap reader value =
    (MigrationSandboxSeedNativeProvenanceVerifier(reader) :> IMigrationSandboxSeedIsolatedProvenanceVerifier)
        .VerifyBootstrapExact(value, bootstrapProposal)

[<Fact>]
let ``immutable inactive S2 declaration qualifies only bootstrap input`` () =
    Assert.True(
        verifyBootstrap
            (Reads())
            { evidence with
                NativeCasReadbackBytes = [||]
                BootstrapAdmissionBytes = bootstrapAdmission
                BootstrapPrestateBytes = bootstrapPrestate
            }
    )

    Assert.False(verifyBootstrap (Reads()) evidence)

[<Fact>]
let ``bootstrap admission refuses altered tuple and changed fresh prestate`` () =
    let admitted =
        { evidence with
            NativeCasReadbackBytes = [||]
            BootstrapAdmissionBytes = bootstrapAdmission
            BootstrapPrestateBytes = bootstrapPrestate
        }

    let alteredAdmission =
        Encoding.UTF8.GetString(bootstrapAdmission).Replace(
            bootstrapProposal.CommitOid,
            String.replicate 40 "8"
        )
        |> raw

    Assert.False(verifyBootstrap (Reads()) { admitted with BootstrapAdmissionBytes = alteredAdmission })

    let changedFresh =
        Encoding.UTF8.GetString(bootstrapPrestateEvidence).Replace(
            bootstrapSnapshotSha,
            String.replicate 64 "8"
        )
        |> raw

    Assert.False(
        verifyBootstrap
            (Reads(bootstrapEvidenceResult = Ok changedFresh))
            admitted
    )

[<Fact>]
let ``exact protected source and isolated CAS readback qualify controlled input`` () =
    Assert.True(verify (Reads()) evidence)

[<Fact>]
let ``native run branch must corroborate protected main declaration`` () =
    let nonMain = Encoding.UTF8.GetString(run).Replace("\"head_branch\":\"main\"", "\"head_branch\":\"topic\"") |> raw

    let reader =
        { new IMigrationSandboxSeedNativeProvenanceRead with
            member _.ReadRunAttempt(_, _) = Ok nonMain
            member _.ReadGitBlob(_, path) = Ok(if path.StartsWith(".github/") then workflow else builder)
            member _.ReadRetained name = (Reads() :> IMigrationSandboxSeedNativeProvenanceRead).ReadRetained name
            member _.ReadPrivateEphemeral name =
                (Reads() :> IMigrationSandboxSeedNativeProvenanceRead).ReadPrivateEphemeral name
            member _.CurrentTokenSha256() = Ok(sha token)
            member _.ReadSandboxRepository() = Ok repo
            member _.ReadSandboxProject() = Ok project
            member _.ReadBootstrapPrestateEvidence() = Ok bootstrapPrestateEvidence
            member _.ReadNativeCasReadback _ = Ok freshCas }

    Assert.False(verify reader evidence)

[<Fact>]
let ``old policy bytes cannot substitute for native CAS or selected scope`` () =
    let sourceOnly =
        Encoding.UTF8.GetString(binding).Replace("coordination-s1-provenance-interface-v1", "wrong-schema-join")
        |> raw

    Assert.False(
        verify
            (Reads())
            { evidence with
                BindingBytes = sourceOnly
            }
    )

    Assert.False(
        verify
            (Reads())
            { evidence with
                NativeCasReadbackBytes = raw "{\"installed\":true,\"authority\":\"authenticated-protected-readback\"}"
            }
    )

    Assert.False(verify (Reads(casResult = Error "ref read unavailable")) evidence)
    Assert.False(verify (Reads(casResult = Ok cas)) evidence)

    let driftedFresh =
        Encoding.UTF8.GetString(freshCas).Replace(payload, String.replicate 64 "9")
        |> raw

    Assert.False(verify (Reads(casResult = Ok driftedFresh)) evidence)
    Assert.False(verify (Reads(repoResult = Error "private scope 403")) evidence)

    Assert.False(
        verify
            (Reads())
            { evidence with
                WorkflowSha = String.replicate 40 "b"
            }
    )
