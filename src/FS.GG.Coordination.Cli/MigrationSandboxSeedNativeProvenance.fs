namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text.Json

/// New Q4 seam. The old installed-policy evidence/interface must never be adapted to this type.
type MigrationSandboxSeedIsolatedProvenanceEvidence =
    {
        BindingBytes: byte array
        NativeCasReadbackBytes: byte array
        BootstrapAdmissionBytes: byte array
        BootstrapPrestateBytes: byte array
        WorkflowRunId: int64
        WorkflowRunAttempt: int
        WorkflowSha: string
        ApprovedArtifactSourceSha256: string
    }

type IMigrationSandboxSeedIsolatedProvenanceVerifier =
    /// Validate the immutable protected-host declaration before the journal exists. This
    /// authorizes only an exact generation-zero journal proposal; it is not an effect authority.
    abstract VerifyBootstrapExact:
        evidence: MigrationSandboxSeedIsolatedProvenanceEvidence * proposal: MigrationSandboxSeedJournalPlan -> bool

    abstract VerifyExact: MigrationSandboxSeedIsolatedProvenanceEvidence -> bool

/// Protected-host implementation only. Reads use the current App token or immutable evidence
/// directory, reject redirects/partial responses/cache hits, and return raw bytes. A 403, 404,
/// missing grant or inaccessible Project is an error. CurrentTokenSha256 hashes the actual token.
/// ReadNativeCasReadback rereads the exact nonce ref through the authenticated Git transport.
type IMigrationSandboxSeedNativeProvenanceRead =
    abstract ReadRunAttempt: runId: int64 * attempt: int -> Result<byte array, string>
    abstract ReadGitBlob: commitSha: string * path: string -> Result<byte array, string>
    abstract ReadRetained: name: string -> Result<byte array, string>
    /// Read an in-process, mode-0600 temporary response. Implementations must never publish,
    /// upload or persist this channel as a workflow artifact because the mint response has a token.
    abstract ReadPrivateEphemeral: name: string -> Result<byte array, string>
    abstract CurrentTokenSha256: unit -> Result<string, string>
    abstract ReadSandboxRepository: unit -> Result<byte array, string>
    abstract ReadSandboxProject: unit -> Result<byte array, string>
    /// Fresh post-decision two-pass issue/Project/ref-absence evidence from host-private custody.
    abstract ReadBootstrapPrestateEvidence: unit -> Result<byte array, string>
    abstract ReadNativeCasReadback: refName: string -> Result<byte array, string>

/// No ambient-token constructor or default transport exists. Installation and CAS remain separate.
type MigrationSandboxSeedNativeProvenanceVerifier(read: IMigrationSandboxSeedNativeProvenanceRead) =
    let sha (value: byte[]) =
        SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()

    let hex length (value: string) =
        not (isNull value)
        && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))

    let bounded limit (value: byte[]) =
        not (isNull value) && value.Length > 0 && value.Length <= limit

    let required limit result =
        match result with
        | Ok raw when bounded limit raw -> raw
        | _ -> invalidOp "protected-read-unavailable"

    let parseBounded limit raw =
        if not (bounded limit raw) then
            invalidOp "json-size"

        let document = JsonDocument.Parse raw

        let rec unique (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList

                if names.Length <> (names |> Set.ofList |> Set.count) then
                    invalidOp "duplicate-member"

                value.EnumerateObject() |> Seq.iter (fun property -> unique property.Value)
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.iter unique
            | _ -> ()

        unique document.RootElement
        document

    let parse raw = parseBounded (1024 * 1024) raw

    let str (name: string) (value: JsonElement) = value.GetProperty(name).GetString()
    let num (name: string) (value: JsonElement) = value.GetProperty(name).GetInt64()
    let field (name: string) (value: JsonElement) = value.GetProperty name

    let retained name digest limit =
        let raw = read.ReadRetained name |> required limit

        if sha raw <> digest then
            invalidOp (name + "-custody")

        raw

    let privateEphemeral name digest limit =
        let raw = read.ReadPrivateEphemeral name |> required limit

        if sha raw <> digest then
            invalidOp (name + "-private-custody")

        raw

    let verify
        requireNativeReadback
        (proposal: MigrationSandboxSeedJournalPlan option)
        (evidence: MigrationSandboxSeedIsolatedProvenanceEvidence)
        =
        if
            isNull (box read)
            || not (bounded (1024 * 1024) evidence.BindingBytes)
            || (requireNativeReadback
                && not (bounded (1024 * 1024) evidence.NativeCasReadbackBytes))
            || (not requireNativeReadback
                && not (isNull evidence.NativeCasReadbackBytes)
                && evidence.NativeCasReadbackBytes.Length <> 0)
            || (requireNativeReadback
                && ((not (isNull evidence.BootstrapAdmissionBytes)
                     && evidence.BootstrapAdmissionBytes.Length <> 0)
                    || (not (isNull evidence.BootstrapPrestateBytes)
                        && evidence.BootstrapPrestateBytes.Length <> 0)))
            || (not requireNativeReadback
                && (not (bounded (1024 * 1024) evidence.BootstrapAdmissionBytes)
                    || not (bounded (1024 * 1024) evidence.BootstrapPrestateBytes)))
            || evidence.WorkflowRunId <= 0L
            || evidence.WorkflowRunAttempt <= 0
            || not (hex 40 evidence.WorkflowSha)
            || not (hex 64 evidence.ApprovedArtifactSourceSha256)
        then
            false
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

            if
                str "schema" root <> "fsgg.github-substrate-v2.sandbox-seed-execution-binding/2"
                || str "status" root <> "bound-no-write-authority"
                || root.GetProperty("activation").GetBoolean()
                || str "authority" root
                   <> "unavailable-without-protected-host-install-and-native-readback"
                || str "schemaJoin" root <> "coordination-s1-provenance-interface-v1"
                || str "repository" source <> "FS-GG/.github"
                || str "workflowPath" source
                   <> ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                || str "workflowRef" source <> "refs/heads/main"
                || str "workflowSha" source <> evidence.WorkflowSha
                || str "providerWorkflowSha" source <> evidence.WorkflowSha
                || str "approvedArtifactSourceSha256" source
                   <> evidence.ApprovedArtifactSourceSha256
                || num "runId" source <> evidence.WorkflowRunId
                || num "runAttempt" source <> int64 evidence.WorkflowRunAttempt
                || not (hex 40 candidate)
                || str "runNonce" source <> nonce
                || num "repositoryId" sandbox <> 1353050537L
                || str "repositoryNodeId" sandbox <> "R_kgDOUKXpqQ"
                || str "projectNodeId" sandbox <> "PVT_kwDOEYAWY84BiESo"
                || num "appId" mint <> 4166418L
                || num "installationId" mint <> 143110413L
                || str "seedJournalRef" source <> refName
                || str "ref" (field "profile" journal) <> refName
            then
                false
            else
                let runRaw =
                    read.ReadRunAttempt(evidence.WorkflowRunId, evidence.WorkflowRunAttempt)
                    |> required (1024 * 1024)

                use runDocument = parse runRaw
                let run = runDocument.RootElement

                if
                    num "id" run <> evidence.WorkflowRunId
                    || num "run_attempt" run <> int64 evidence.WorkflowRunAttempt
                    || str "head_sha" run <> evidence.WorkflowSha
                    || str "head_branch" run <> "main"
                    || str "event" run <> "workflow_dispatch"
                    || str "path" run
                       <> ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                    || str "full_name" (field "repository" run) <> "FS-GG/.github"
                then
                    false
                else
                    let workflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                    let builderPath = "scripts/gs2-09-7-seed-execution-binding.py"

                    let workflow =
                        read.ReadGitBlob(evidence.WorkflowSha, workflowPath) |> required (1024 * 1024)

                    let builder =
                        read.ReadGitBlob(evidence.WorkflowSha, builderPath) |> required (1024 * 1024)

                    let checkout = field "protectedCheckout" source

                    if
                        str "checkoutHead" checkout <> evidence.WorkflowSha
                        || str "path" (field "workflow" checkout) <> workflowPath
                        || str "sha256" (field "workflow" checkout) <> sha workflow
                        || str "path" (field "builder" checkout) <> builderPath
                        || str "sha256" (field "builder" checkout) <> sha builder
                        || str "builderPath" source <> builderPath
                        || str "builderSha256" source <> sha builder
                    then
                        false
                    else
                        let proofRaw = retained "mint-proof" (str "proofSha256" mint) (1024 * 1024)
                        use proofDocument = parse proofRaw
                        let proof = proofDocument.RootElement
                        let actor = field "actor" proof
                        let selected = field "repository" proof
                        let grants = field "permissions" proof

                        let expectedGrants =
                            [
                                "administration", "write"
                                "contents", "write"
                                "issues", "write"
                                "metadata", "read"
                                "organization_projects", "write"
                                "pull_requests", "write"
                            ]

                        let actualGrantNames = grants.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                        if
                            str "schema" proof <> "fsgg.github-substrate-v2.sandbox-mint-grants/1"
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
                            || str "tokenSha256" proof <> str "tokenSha256" mint
                        then
                            false
                        else
                            let mintResponse =
                                privateEphemeral "mint-response" (str "mintResponseSha256" proof) (1024 * 1024)

                            let viewerResponse =
                                privateEphemeral "viewer-response" (str "viewerResponseSha256" proof) (1024 * 1024)

                            use mintDocument = parse mintResponse
                            use viewerDocument = parse viewerResponse
                            let minted = mintDocument.RootElement
                            let viewerRoot = viewerDocument.RootElement
                            let viewer = field "viewer" (field "data" viewerRoot)
                            let mintGrants = field "permissions" minted
                            let mintedGrantNames = mintGrants.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                            let tokenExact =
                                match read.CurrentTokenSha256() with
                                | Ok digest -> hex 64 digest && digest = str "tokenSha256" proof
                                | _ -> false

                            if
                                not tokenExact
                                || str "repository_selection" minted <> "selected"
                                || sha (System.Text.Encoding.UTF8.GetBytes(str "token" minted))
                                   <> str "tokenSha256" proof
                                || str "expires_at" minted <> str "expiresAt" proof
                                || mintedGrantNames <> (expectedGrants |> List.map fst |> Set.ofList)
                                || not (
                                    expectedGrants |> List.forall (fun (name, value) -> str name mintGrants = value)
                                )
                                || str "login" viewer <> "fs-gg-cross-repo-dispatch[bot]"
                                || num "databaseId" viewer <> 297630107L
                            then
                                false
                            else
                                let repoRaw = read.ReadSandboxRepository() |> required (1024 * 1024)
                                let projectRaw = read.ReadSandboxProject() |> required (1024 * 1024)
                                use repoDocument = parse repoRaw
                                use projectDocument = parse projectRaw
                                let repo = repoDocument.RootElement
                                let project = projectDocument.RootElement

                                if
                                    num "id" repo <> 1353050537L
                                    || str "node_id" repo <> "R_kgDOUKXpqQ"
                                    || str "full_name" repo <> "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
                                    || not (repo.GetProperty("private").GetBoolean())
                                    || str "description" repo
                                       <> "fsgg-sandbox-gs2-04-9 disposable qualification target; never production"
                                    || str "organization" project <> "FS-GG"
                                    || num "number" project <> 2L
                                    || str "id" project <> "PVT_kwDOEYAWY84BiESo"
                                    || str "title" project <> "fsgg-sandbox-gs2-04-9"
                                    || not (project.GetProperty("private").GetBoolean())
                                    || project.GetProperty("closed").GetBoolean()
                                then
                                    false
                                else
                                    let planDigest = str "sha256" (field "seedPlan" artifacts)
                                    let corpusDigest = str "sha256" (field "corpus" artifacts)

                                    let retainedInputs =
                                        hex 64 planDigest
                                        && hex 64 corpusDigest
                                        && (retained "seed-plan" planDigest (64 * 1024 * 1024)).Length > 0
                                        && (retained "corpus" corpusDigest (64 * 1024 * 1024)).Length > 0
                                        && (retained
                                                "approved-source"
                                                evidence.ApprovedArtifactSourceSha256
                                                (64 * 1024 * 1024))
                                            .Length
                                            >
                                            0

                                    if not retainedInputs then
                                        false
                                    elif not requireNativeReadback then
                                        match proposal with
                                        | None -> false
                                        | Some proposal ->
                                            let retainedAdmission =
                                                read.ReadRetained "bootstrap-final-admission" |> required (1024 * 1024)

                                            let retainedPrestate =
                                                read.ReadRetained "bootstrap-prestate" |> required (1024 * 1024)

                                            if
                                                retainedAdmission <> evidence.BootstrapAdmissionBytes
                                                || retainedPrestate <> evidence.BootstrapPrestateBytes
                                            then
                                                false
                                            else
                                                use admissionDocument = parse evidence.BootstrapAdmissionBytes
                                                use prestateDocument = parse evidence.BootstrapPrestateBytes
                                                let admission = admissionDocument.RootElement
                                                let subject = field "subject" admission
                                                let prestate = prestateDocument.RootElement
                                                let prestateEvidenceDigest = str "prestateEvidenceSha256" subject

                                                let prestateEvidence =
                                                    retained
                                                        "bootstrap-prestate-evidence"
                                                        prestateEvidenceDigest
                                                        (64 * 1024 * 1024)

                                                use prestateEvidenceDocument =
                                                    parseBounded (64 * 1024 * 1024) prestateEvidence

                                                let prestateProof = prestateEvidenceDocument.RootElement

                                                let passes =
                                                    prestateProof.GetProperty("passes").EnumerateArray() |> Seq.toArray

                                                let requestValid (request: JsonElement) =
                                                    let names = request.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
                                                    let status = num "status" request
                                                    let url = str "url" request

                                                    names = set [ "method"; "url"; "status"; "link"; "bodyBase64" ]
                                                    && str "method" request = "GET"
                                                    && Uri.IsWellFormedUriString(url, UriKind.Absolute)
                                                    && (status = 200L || status = 404L)
                                                    && (Convert.FromBase64String(str "bodyBase64" request)).Length
                                                       <= 16 * 1024 * 1024

                                                let passValid (pass: JsonElement) =
                                                    let requests =
                                                        pass.GetProperty("requests").EnumerateArray() |> Seq.toArray

                                                    let nonceUrl = "/git/ref/heads/gs2-09-7/" + nonce + "/seed-journal"

                                                    requests.Length >= 5
                                                    && (requests |> Array.forall requestValid)
                                                    && (requests
                                                        |> Array.exists (fun item ->
                                                            str "url" item =
                                                                "https://api.github.com/repos/FS-GG/FS.GG.GitHub.Substrate.Sandbox/git/ref/heads/main"
                                                            && num "status" item = 200L))
                                                    && (requests
                                                        |> Array.exists (fun item ->
                                                            (str "url" item)
                                                                .EndsWith(nonceUrl, StringComparison.Ordinal)
                                                            && num "status" item = 404L))

                                                let evidenceValid =
                                                    str "schema" prestateProof =
                                                        "fsgg.gs2-09-7.sandbox-seed-prestate-evidence/1"
                                                    && str "runNonce" prestateProof = nonce
                                                    && str "refName" prestateProof = refName
                                                    && num "repositoryId" prestateProof = 1353050537L
                                                    && str "projectNodeId" prestateProof = "PVT_kwDOEYAWY84BiESo"
                                                    && prestateProof.GetProperty("expectedRefAbsent").GetBoolean()
                                                    && prestateProof.GetProperty("complete").GetBoolean()
                                                    && str "summarySha256" prestateProof =
                                                        sha evidence.BootstrapPrestateBytes
                                                    && str "snapshotSha256" prestateProof =
                                                        str "snapshotSha256" prestate
                                                    && passes.Length = 2
                                                    && passes[0].GetRawText() = passes[1].GetRawText()
                                                    && sha (
                                                        System.Text.Encoding.UTF8.GetBytes(
                                                            passes[0].GetProperty("snapshot").GetRawText()
                                                        )
                                                    )
                                                        =
                                                        str "snapshotSha256" prestateProof
                                                    && (passes |> Array.forall passValid)

                                                let freshPrestateEvidence =
                                                    read.ReadBootstrapPrestateEvidence() |> required (64 * 1024 * 1024)

                                                use freshPrestateDocument =
                                                    parseBounded (64 * 1024 * 1024) freshPrestateEvidence

                                                let freshPrestate = freshPrestateDocument.RootElement

                                                let freshPasses =
                                                    freshPrestate.GetProperty("passes").EnumerateArray() |> Seq.toArray

                                                let freshEvidenceValid =
                                                    let initialObservedAt =
                                                        DateTimeOffset.Parse(
                                                            str "observedAt" prestateProof,
                                                            Globalization.CultureInfo.InvariantCulture
                                                        )

                                                    let freshObservedAt =
                                                        DateTimeOffset.Parse(
                                                            str "observedAt" freshPrestate,
                                                            Globalization.CultureInfo.InvariantCulture
                                                        )

                                                    str "schema" freshPrestate =
                                                        "fsgg.gs2-09-7.sandbox-seed-prestate-evidence/1"
                                                    && str "runNonce" freshPrestate = nonce
                                                    && str "refName" freshPrestate = refName
                                                    && num "repositoryId" freshPrestate = 1353050537L
                                                    && str "projectNodeId" freshPrestate = "PVT_kwDOEYAWY84BiESo"
                                                    && freshPrestate.GetProperty("expectedRefAbsent").GetBoolean()
                                                    && freshPrestate.GetProperty("complete").GetBoolean()
                                                    && str "summarySha256" freshPrestate =
                                                        sha evidence.BootstrapPrestateBytes
                                                    && str "snapshotSha256" freshPrestate =
                                                        str "snapshotSha256" prestateProof
                                                    && freshObservedAt > initialObservedAt
                                                    && not (
                                                        freshPrestateEvidence
                                                            .AsSpan()
                                                            .SequenceEqual(prestateEvidence.AsSpan())
                                                    )
                                                    && freshPasses.Length = 2
                                                    && freshPasses[0].GetRawText() = freshPasses[1].GetRawText()
                                                    && sha (
                                                        System.Text.Encoding.UTF8.GetBytes(
                                                            freshPasses[0].GetProperty("snapshot").GetRawText()
                                                        )
                                                    )
                                                        =
                                                        str "snapshotSha256" freshPrestate
                                                    && (freshPasses |> Array.forall passValid)

                                                let proposed: MigrationSandboxSeedJournalSnapshot =
                                                    {
                                                        RefName = proposal.RefName
                                                        JournalGeneration = proposal.JournalGeneration
                                                        StateGeneration = proposal.StateGeneration
                                                        RunNonce = proposal.RunNonce
                                                        BindingSeal = proposal.BindingSeal
                                                        CommitOid = proposal.CommitOid
                                                        ParentOid = proposal.ExpectedParent
                                                        StateSha256 = proposal.StateSha256
                                                        StateBytes = proposal.StateBytes
                                                        BlobOid = proposal.BlobOid
                                                        TreeOid = proposal.TreeOid
                                                        TreeBytes = proposal.TreeBytes
                                                        CommitBytes = proposal.CommitBytes
                                                    }

                                                match MigrationSandboxSeedJournal.restore None proposed with
                                                | Error _ -> false
                                                | Ok restored ->
                                                    let expectedPrestate = restored.State.Binding.Prestate

                                                    let admissionMatches =
                                                        str "schema" admission =
                                                            "fsgg.gs2-09-7.seed-admission-request/1"
                                                        && str "phase" admission = "final"
                                                        && str "workflowRepository" subject = "FS-GG/.github"
                                                        && str "workflowPath" subject =
                                                            ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                                                        && str "environment" subject = "github-substrate-v2-sandbox"
                                                        && str "workflowSha" subject = evidence.WorkflowSha
                                                        && num "runId" subject = evidence.WorkflowRunId
                                                        && num "runAttempt" subject = int64 evidence.WorkflowRunAttempt
                                                        && str "candidateSha" subject = candidate
                                                        && str "runNonce" subject = nonce
                                                        && str "approvedArtifactSourceSha256" subject =
                                                            evidence.ApprovedArtifactSourceSha256
                                                        && str "sourceManifestSha256" subject =
                                                            evidence.ApprovedArtifactSourceSha256
                                                        && num "sandboxRepositoryId" subject = 1353050537L
                                                        && str "sandboxRepositoryNodeId" subject = "R_kgDOUKXpqQ"
                                                        && str "projectNodeId" subject = "PVT_kwDOEYAWY84BiESo"
                                                        && num "appId" subject = 4166418L
                                                        && num "installationId" subject = 143110413L
                                                        && str "seedPlanSha256" subject = planDigest
                                                        && str "s2DeclarationSha256" subject = sha evidence.BindingBytes
                                                        && str "mintProofSha256" subject = str "proofSha256" mint
                                                        && str "tokenSha256" subject = str "tokenSha256" mint
                                                        && str "refName" subject = refName
                                                        && str "blobOid" subject = proposal.BlobOid
                                                        && str "treeOid" subject = proposal.TreeOid
                                                        && str "commitOid" subject = proposal.CommitOid
                                                        && (field "expectedOldOid" subject).ValueKind =
                                                            JsonValueKind.Null
                                                        && str "operation" subject = "genesis-nonce-seed-journal"
                                                        && subject.GetProperty("expectedRefAbsent").GetBoolean()
                                                        && str "prestateSha256" subject =
                                                            sha evidence.BootstrapPrestateBytes
                                                        && str "prestateSnapshotSha256" subject =
                                                            expectedPrestate.SnapshotSha256
                                                        && hex 64 prestateEvidenceDigest
                                                        && prestateEvidence.Length > 0
                                                        && evidenceValid
                                                        && freshEvidenceValid
                                                        && prestate.GetProperty("complete").GetBoolean()
                                                        && num "repositoryId" prestate = expectedPrestate.RepositoryId
                                                        && str "projectNodeId" prestate = expectedPrestate.ProjectNodeId
                                                        && num "nonceIssueCount" prestate =
                                                            int64 expectedPrestate.NonceIssueCount
                                                        && num "nonceProjectItemCount" prestate =
                                                            int64 expectedPrestate.NonceProjectItemCount
                                                        && str "snapshotSha256" prestate =
                                                            expectedPrestate.SnapshotSha256

                                                    admissionMatches
                                    else
                                        let freshCas = read.ReadNativeCasReadback refName |> required (1024 * 1024)
                                        use retainedDocument = parse evidence.NativeCasReadbackBytes
                                        use freshDocument = parse freshCas
                                        let retainedCas = retainedDocument.RootElement
                                        let fresh = freshDocument.RootElement

                                        let casTuple (cas: JsonElement) =
                                            let repository = field "repository" cas
                                            let oldOid = cas.GetProperty("oldOid")
                                            let parentOid = cas.GetProperty("commitParentOid")
                                            let observedParent = cas.GetProperty("observedCommitParentOid")

                                            if
                                                str "schema" cas <> "fsgg.gs2-09-7.sandbox-nonce-ref-cas-readback/1"
                                                || str "status" cas <> "readback-complete"
                                                || str "outcome" cas <> "applied-or-already-applied"
                                                || not (cas.GetProperty("complete").GetBoolean())
                                                || num "repositoryId" cas <> 1353050537L
                                                || num "id" repository <> 1353050537L
                                                || str "nodeId" repository <> "R_kgDOUKXpqQ"
                                                || str "fullName" repository <> "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
                                                || str "refName" cas <> refName
                                                || num "runId" cas <> evidence.WorkflowRunId
                                                || num "runAttempt" cas <> int64 evidence.WorkflowRunAttempt
                                                || str "runNonce" cas <> nonce
                                                || str "candidateSha" cas <> candidate
                                                || str "workflowSha" cas <> evidence.WorkflowSha
                                                || str "s2DeclarationSha256" cas <> sha evidence.BindingBytes
                                                || str "seedPlanSha256" cas <> planDigest
                                                || oldOid.ValueKind <> JsonValueKind.Null
                                                || parentOid.ValueKind <> JsonValueKind.Null
                                                || observedParent.ValueKind <> JsonValueKind.Null
                                                || num "journalGeneration" cas <> 0L
                                                || num "stateGeneration" cas <> 0L
                                                || str "readbackSource" cas
                                                   <> "fresh-git-fetch-cat-file-terminal-ref-reread"
                                            then
                                                invalidOp "native-cas-shape"

                                            let commit = str "commitOid" cas
                                            let tree = str "treeOid" cas
                                            let blob = str "blobOid" cas
                                            let payload = str "payloadSha256" cas

                                            let observedAt =
                                                DateTimeOffset.Parse(
                                                    str "observedAt" cas,
                                                    Globalization.CultureInfo.InvariantCulture
                                                )

                                            if
                                                not (hex 40 commit && hex 40 tree && hex 40 blob && hex 64 payload)
                                                || str "newOid" cas <> commit
                                                || str "observedRefOid" cas <> commit
                                                || str "observedTreeOid" cas <> tree
                                                || str "observedBlobOid" cas <> blob
                                                || str "observedPayloadSha256" cas <> payload
                                            then
                                                invalidOp "native-cas-objects"

                                            (commit, tree, blob, payload, observedAt)

                                        let retainedTuple = casTuple retainedCas
                                        let freshTuple = casTuple fresh
                                        let _, _, _, _, retainedAt = retainedTuple
                                        let _, _, _, _, freshAt = freshTuple
                                        let retainedObjects = retainedTuple |> fun (c, t, b, p, _) -> (c, t, b, p)
                                        let freshObjects = freshTuple |> fun (c, t, b, p, _) -> (c, t, b, p)

                                        retainedObjects = freshObjects
                                        && freshAt > retainedAt
                                        && not (
                                            freshCas.AsSpan().SequenceEqual(evidence.NativeCasReadbackBytes.AsSpan())
                                        )

    interface IMigrationSandboxSeedIsolatedProvenanceVerifier with
        member _.VerifyBootstrapExact(evidence, proposal) =
            try
                verify false (Some proposal) evidence
            with _ ->
                false

        member _.VerifyExact evidence =
            try
                verify true None evidence
            with _ ->
                false
