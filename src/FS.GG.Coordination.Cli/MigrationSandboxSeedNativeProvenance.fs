namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text.Json

/// New Q4 seam. The old installed-policy evidence/interface must never be adapted to this type.
type MigrationSandboxSeedIsolatedProvenanceEvidence =
    { BindingBytes: byte array
      NativeCasReadbackBytes: byte array
      WorkflowRunId: int64
      WorkflowRunAttempt: int
      WorkflowSha: string
      ApprovedArtifactSourceSha256: string }

type IMigrationSandboxSeedIsolatedProvenanceVerifier =
    abstract VerifyExact: MigrationSandboxSeedIsolatedProvenanceEvidence -> bool

/// Protected-host implementation only. Reads use the current App token or immutable evidence
/// directory, reject redirects/partial responses/cache hits, and return raw bytes. A 403, 404,
/// missing grant or inaccessible Project is an error. CurrentTokenSha256 hashes the actual token.
/// ReadNativeCasReadback rereads the exact nonce ref through the authenticated Git transport.
type IMigrationSandboxSeedNativeProvenanceRead =
    abstract ReadRunAttempt: runId:int64 * attempt:int -> Result<byte array, string>
    abstract ReadGitBlob: commitSha:string * path:string -> Result<byte array, string>
    abstract ReadRetained: name:string -> Result<byte array, string>
    abstract CurrentTokenSha256: unit -> Result<string, string>
    abstract ReadSandboxRepository: unit -> Result<byte array, string>
    abstract ReadSandboxProject: unit -> Result<byte array, string>
    abstract ReadNativeCasReadback: refName:string -> Result<byte array, string>

/// No ambient-token constructor or default transport exists. Installation and CAS remain separate.
type MigrationSandboxSeedNativeProvenanceVerifier(read: IMigrationSandboxSeedNativeProvenanceRead) =
    let sha (value: byte[]) = SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()
    let hex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
    let bounded limit (value: byte[]) = not (isNull value) && value.Length > 0 && value.Length <= limit
    let required limit result =
        match result with
        | Ok raw when bounded limit raw -> raw
        | _ -> invalidOp "protected-read-unavailable"
    let parse raw =
        if not (bounded (1024 * 1024) raw) then invalidOp "json-size"
        let document = JsonDocument.Parse raw
        let rec unique (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                if names.Length <> (names |> Set.ofList |> Set.count) then invalidOp "duplicate-member"
                value.EnumerateObject() |> Seq.iter (fun property -> unique property.Value)
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.iter unique
            | _ -> ()
        unique document.RootElement
        document
    let str (name: string) (value: JsonElement) = value.GetProperty(name).GetString()
    let num (name: string) (value: JsonElement) = value.GetProperty(name).GetInt64()
    let field (name: string) (value: JsonElement) = value.GetProperty name
    let retained name digest limit =
        let raw = read.ReadRetained name |> required limit
        if sha raw <> digest then invalidOp (name + "-custody")
        raw

    let verify (evidence: MigrationSandboxSeedIsolatedProvenanceEvidence) =
        if isNull (box read) || not (bounded (1024 * 1024) evidence.BindingBytes)
           || not (bounded (1024 * 1024) evidence.NativeCasReadbackBytes)
           || evidence.WorkflowRunId <= 0L || evidence.WorkflowRunAttempt <= 0
           || not (hex 40 evidence.WorkflowSha)
           || not (hex 64 evidence.ApprovedArtifactSourceSha256) then false
        else
            use binding = parse evidence.BindingBytes
            let root = binding.RootElement
            let source = field "source" root
            let sandbox = field "sandbox" root
            let mint = field "mint" root
            let artifacts = field "artifacts" root
            let journal = field "journal" root
            let candidate = str "candidateSha" source
            let nonce = $"{evidence.WorkflowRunId}-{evidence.WorkflowRunAttempt}-{candidate}"
            let refName = $"refs/heads/gs2-09-7/{nonce}/seed-journal"
            if str "schema" root <> "fsgg.github-substrate-v2.sandbox-seed-execution-binding/1"
               || str "repository" source <> "FS-GG/.github"
               || str "workflowPath" source <> ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
               || str "workflowRef" source <> "refs/heads/main"
               || str "workflowSha" source <> evidence.WorkflowSha
               || str "providerWorkflowSha" source <> evidence.WorkflowSha
               || str "approvedArtifactSourceSha256" source <> evidence.ApprovedArtifactSourceSha256
               || num "runId" source <> evidence.WorkflowRunId
               || num "runAttempt" source <> int64 evidence.WorkflowRunAttempt
               || not (hex 40 candidate) || str "runNonce" source <> nonce
               || num "repositoryId" sandbox <> 1353050537L
               || str "repositoryNodeId" sandbox <> "R_kgDOUKXpqQ"
               || str "projectNodeId" sandbox <> "PVT_kwDOEYAWY84BiESo"
               || num "appId" mint <> 4166418L
               || num "installationId" mint <> 143110413L
               || str "identity" journal <> refName then false
            else
                let runRaw = read.ReadRunAttempt(evidence.WorkflowRunId, evidence.WorkflowRunAttempt) |> required (1024 * 1024)
                use runDocument = parse runRaw
                let run = runDocument.RootElement
                if num "id" run <> evidence.WorkflowRunId
                   || num "run_attempt" run <> int64 evidence.WorkflowRunAttempt
                   || str "head_sha" run <> evidence.WorkflowSha
                   || str "event" run <> "workflow_dispatch"
                   || str "path" run <> ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                   || str "full_name" (field "repository" run) <> "FS-GG/.github" then false
                else
                    let workflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                    let builderPath = "scripts/gs2-09-7-seed-execution-binding.py"
                    let workflow = read.ReadGitBlob(evidence.WorkflowSha, workflowPath) |> required (1024 * 1024)
                    let builder = read.ReadGitBlob(evidence.WorkflowSha, builderPath) |> required (1024 * 1024)
                    let checkout = field "protectedCheckout" source
                    if str "checkoutHead" checkout <> evidence.WorkflowSha
                       || str "path" (field "workflow" checkout) <> workflowPath
                       || str "sha256" (field "workflow" checkout) <> sha workflow
                       || str "path" (field "builder" checkout) <> builderPath
                       || str "sha256" (field "builder" checkout) <> sha builder
                       || str "builderPath" source <> builderPath
                       || str "builderSha256" source <> sha builder then false
                    else
                        let proofRaw = retained "mint-proof" (str "proofSha256" mint) (1024 * 1024)
                        use proofDocument = parse proofRaw
                        let proof = proofDocument.RootElement
                        let actor = field "actor" proof
                        let selected = field "repository" proof
                        let grants = field "permissions" proof
                        let expectedGrants =
                            [ "administration", "write"; "contents", "write"; "issues", "write"
                              "metadata", "read"; "organization_projects", "write"; "pull_requests", "write" ]
                        let actualGrantNames = grants.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
                        if str "schema" proof <> "fsgg.github-substrate-v2.sandbox-mint-grants/1"
                           || num "appId" proof <> 4166418L
                           || num "installationId" proof <> 143110413L
                           || str "appSlug" proof <> "fs-gg-cross-repo-dispatch"
                           || str "repositorySelection" proof <> "selected"
                           || str "login" actor <> "fs-gg-cross-repo-dispatch[bot]"
                           || num "databaseId" actor <> 297630107L
                           || num "id" selected <> 1353050537L
                           || str "nodeId" selected <> "R_kgDOUKXpqQ"
                           || str "fullName" selected <> "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
                           || actualGrantNames <> (expectedGrants |> List.map fst |> Set.ofList)
                           || not (expectedGrants |> List.forall (fun (name, value) -> str name grants = value))
                           || str "tokenSha256" proof <> str "tokenSha256" mint then false
                        else
                            let mintResponse = retained "mint-response" (str "mintResponseSha256" proof) (1024 * 1024)
                            let viewerResponse = retained "viewer-response" (str "viewerResponseSha256" proof) (1024 * 1024)
                            use mintDocument = parse mintResponse
                            use viewerDocument = parse viewerResponse
                            let minted = mintDocument.RootElement
                            let viewer = viewerDocument.RootElement
                            let mintGrants = field "permissions" minted
                            let mintedGrantNames = mintGrants.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
                            let tokenExact =
                                match read.CurrentTokenSha256() with
                                | Ok digest -> hex 64 digest && digest = str "tokenSha256" proof
                                | _ -> false
                            if not tokenExact
                               || str "repository_selection" minted <> "selected"
                               || sha (System.Text.Encoding.UTF8.GetBytes(str "token" minted)) <> str "tokenSha256" proof
                               || str "expires_at" minted <> str "expiresAt" proof
                               || mintedGrantNames <> (expectedGrants |> List.map fst |> Set.ofList)
                               || not (expectedGrants |> List.forall (fun (name, value) -> str name mintGrants = value))
                               || str "login" viewer <> "fs-gg-cross-repo-dispatch[bot]"
                               || num "id" viewer <> 297630107L then false
                            else
                                let repoRaw = read.ReadSandboxRepository() |> required (1024 * 1024)
                                let projectRaw = read.ReadSandboxProject() |> required (1024 * 1024)
                                use repoDocument = parse repoRaw
                                use projectDocument = parse projectRaw
                                let repo = repoDocument.RootElement
                                let project = projectDocument.RootElement
                                if num "id" repo <> 1353050537L
                                   || str "node_id" repo <> "R_kgDOUKXpqQ"
                                   || str "full_name" repo <> "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
                                   || not (repo.GetProperty("private").GetBoolean())
                                   || str "description" repo <> "fsgg-sandbox-gs2-04-9 disposable qualification target; never production"
                                   || str "organization" project <> "FS-GG"
                                   || num "number" project <> 2L
                                   || str "id" project <> "PVT_kwDOEYAWY84BiESo"
                                   || str "title" project <> "fsgg-sandbox-gs2-04-9"
                                   || not (project.GetProperty("private").GetBoolean())
                                   || project.GetProperty("closed").GetBoolean() then false
                                else
                                    let nativeCas = read.ReadNativeCasReadback refName |> required (1024 * 1024)
                                    if nativeCas <> evidence.NativeCasReadbackBytes then false else
                                    use casDocument = parse nativeCas
                                    let cas = casDocument.RootElement
                                    if str "schema" cas <> "fsgg.gs2-09-7.sandbox-nonce-ref-cas-readback/1"
                                       || str "refName" cas <> refName
                                       || num "repositoryId" cas <> 1353050537L
                                       || not (cas.GetProperty("complete").GetBoolean()) then false
                                    else
                                        let planDigest = str "sha256" (field "seedPlan" artifacts)
                                        let corpusDigest = str "sha256" (field "corpus" artifacts)
                                        hex 64 planDigest && hex 64 corpusDigest
                                        && (retained "seed-plan" planDigest (64 * 1024 * 1024)).Length > 0
                                        && (retained "corpus" corpusDigest (64 * 1024 * 1024)).Length > 0
                                        && (retained "approved-source" evidence.ApprovedArtifactSourceSha256 (64 * 1024 * 1024)).Length > 0

    interface IMigrationSandboxSeedIsolatedProvenanceVerifier with
        member _.VerifyExact evidence = try verify evidence with _ -> false
