#nowarn "3391"

namespace FS.GG.Coordination.Cli

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module InstalledOrdinarySettlementProvider =
    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()
        member _.Combine(value, binder) = Result.bind binder value
        member _.Delay(generator) = generator
        member _.Run(generator) = generator()
        member _.Using(resource: #IDisposable, binder) =
            try binder resource finally if not (obj.ReferenceEquals(resource, null)) then resource.Dispose()
    let private result = ResultBuilder()

    type SourceProfile =
        {
            Name: string
            Repository: string
            RepositoryId: int64
            RequiredSettlementChecks: Set<string>
            RequiredGateChecks: Set<string>
        }

    let private dotGitHubSourceProfile =
        { Name = "dotgithub-v1"
          Repository = "FS-GG/.github"
          RepositoryId = 1269292704L
          RequiredSettlementChecks = Set [ "contract-coherence / coherence"; "routine-eligibility" ]
          RequiredGateChecks =
            Set
                [ "contract-coherence / coherence"; "projection"; "roster-closure"; "drift"; "claim-generation"
                  "Lint every shell file in the repo (pinned shellcheck)"; "claim-fence"; "architecture-map reconcile" ] }

    let private audioSourceProfile =
        { Name = "audio-v1"
          Repository = "FS-GG/FS.GG.Audio"
          RepositoryId = 1292226968L
          RequiredSettlementChecks =
            Set [ "Build + test (locked restore, net10.0, headless)"; "routine-eligibility" ]
          RequiredGateChecks =
            Set
                [ "Build + test (locked restore, net10.0, headless)"; "lock-ranges / lock-ranges"
                  "kit / coordination-kit"; "materialize / receiver-validate" ] }

    let private renderingSourceProfile =
        { Name = "rendering-v1"
          Repository = "FS-GG/FS.GG.Rendering"
          RepositoryId = 1269292235L
          RequiredSettlementChecks = Set [ "Deterministic gate"; "routine-eligibility" ]
          RequiredGateChecks =
            Set
                [ "Deterministic gate"; "API compatibility gate (breaking-change → SemVer major)"
                  "kit / coordination-kit"; "skill-view-check"; "materialize / receiver-validate" ] }

    let private netSourceProfile =
        { Name = "net-v1"
          Repository = "FS-GG/FS.GG.Net"
          RepositoryId = 1305845505L
          RequiredSettlementChecks = Set [ "Build + test (locked restore)"; "contract-coherence / coherence" ]
          RequiredGateChecks =
            Set
                [ "Build + test (locked restore)"; "kit / coordination-kit"
                  "contract-coherence / coherence"; "materialize / receiver-validate" ] }

    let private governanceSourceProfile =
        { Name = "governance-v1"
          Repository = "FS-GG/FS.GG.Governance"
          RepositoryId = 1273065119L
          RequiredSettlementChecks = Set [ "Deterministic gate (locked restore + build)"; "contract-coherence / coherence" ]
          RequiredGateChecks =
            Set
                [ "Deterministic gate (locked restore + build)"
                  "Full test suite (dotnet fsi build.fsx test)"
                  "Full test suite — Release (dotnet fsi build.fsx test -c Release)"
                  "Build-config drift check (shared-build-config)"
                  "Reference gate set — pack guard (byte-identity + gated + versioned)"
                  "contract-coherence / coherence"
                  "kit / coordination-kit"
                  "skill-view-check"
                  "materialize / receiver-validate" ] }

    let private gameSourceProfile =
        { Name = "game-v1"
          Repository = "FS-GG/FS.GG.Game"
          RepositoryId = 1290990429L
          RequiredSettlementChecks =
            Set [ "Deterministic gate (locked restore + build) (ubuntu-latest)"
                  "Full test suite (dotnet test, headless) (ubuntu-latest)" ]
          RequiredGateChecks =
            Set
                [ "Surface baseline drift (readiness/surface-baselines)"
                  "Build-config drift check (shared-build-config)"
                  "Lock-range coherence (project refs track declared versions) / lock-ranges"
                  "Skill-manifest drift (template/skill-manifest)"
                  "Dangling skill refs (template/product-skills)"
                  "Skill-refs gate tests (scripts/check-skill-refs.sh)"
                  "Skill-refs sweep tests (.github/workflows/skill-refs-sweep.yml)"
                  "Test-harness selftest (scripts/lib/test-harness.sh)"
                  "Shell lint (actionlint + shellcheck over every run: block, and over the repo's own scripts)"
                  "Markdown fsharp blocks typecheck (skills + TestSpecs)"
                  "Scaffold drift (_scaffold.fs == published template geometry)"
                  "kit / coordination-kit"
                  "Deterministic gate (locked restore + build) (ubuntu-latest)"
                  "Deterministic gate (locked restore + build) (windows-latest)"
                  "Full test suite (dotnet test, headless) (ubuntu-latest)"
                  "Full test suite (dotnet test, headless) (windows-latest)"
                  "Determinism & property invariants (constraint face) (ubuntu-latest)"
                  "Determinism & property invariants (constraint face) (windows-latest)"
                  "materialize / receiver-validate" ] }

    let private sddSourceProfile =
        { Name = "sdd-v1"
          Repository = "FS-GG/FS.GG.SDD"
          RepositoryId = 1274272672L
          RequiredSettlementChecks =
            Set [ "Deterministic gate (locked restore + build + test)"; "Shared-build-config drift check" ]
          RequiredGateChecks =
            Set
                [ "Deterministic gate (locked restore + build + test)"
                  "Shared-build-config drift check"
                  "API compatibility gate (breaking-change → SemVer major)"
                  "kit / coordination-kit"
                  "skill-view-check"
                  "materialize / receiver-validate" ] }

    let private templatesSourceProfile =
        { Name = "templates-v1"
          Repository = "FS-GG/FS.GG.Templates"
          RepositoryId = 1281961814L
          RequiredSettlementChecks = Set [ "composition"; "kit / coordination-kit" ]
          RequiredGateChecks =
            Set [ "composition"; "kit / coordination-kit"; "materialize / receiver-validate" ] }

    let selectSourceProfile value =
        match value with
        | value when String.IsNullOrWhiteSpace value -> Ok dotGitHubSourceProfile
        | "dotgithub-v1" -> Ok dotGitHubSourceProfile
        | "audio-v1" -> Ok audioSourceProfile
        | "rendering-v1" -> Ok renderingSourceProfile
        | "net-v1" -> Ok netSourceProfile
        | "governance-v1" -> Ok governanceSourceProfile
        | "game-v1" -> Ok gameSourceProfile
        | "sdd-v1" -> Ok sddSourceProfile
        | "templates-v1" -> Ok templatesSourceProfile
        | _ -> Error "unsupported-source-profile"

    let private apiBase = Uri "https://api.github.com/"
    let private userAgent = "fsgg-coordination/0.1.2"
    let private policyPath = "policy/v2-ci-ordinary-settlement.json"
    let private rehearsalPolicyPath = "policy/v2-ci-ordinary-settlement-rehearsal.json"
    let private anchorPath = "policy/v2-ci-ordinary-settlement-anchor.json"
    let private rehearsalAnchorPath = "policy/v2-ci-ordinary-settlement-rehearsal-anchor.json"
    let private observerPath = "tools/v2-ci-ordinary-observe.py"

    let private requiredEnvironment name =
        match Environment.GetEnvironmentVariable name with
        | value when String.IsNullOrWhiteSpace value -> Error $"missing-environment:{name}"
        | value -> Ok value

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private base64Url (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private runObserver workspace action receiptPath =
        try
            let start = ProcessStartInfo("python3")
            start.ArgumentList.Add(Path.Combine(workspace, observerPath))
            start.ArgumentList.Add action
            start.ArgumentList.Add receiptPath
            start.UseShellExecute <- false
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.WorkingDirectory <- workspace
            for name in
                [ "V2_ORDINARY_AUTHORIZER_PRIVATE_KEY"; "V2_ORDINARY_APP_ID"; "V2_ORDINARY_APP_PRIVATE_KEY"
                  "V2_ORDINARY_REHEARSAL_AUTHORIZER_PRIVATE_KEY"; "V2_ORDINARY_REHEARSAL_APP_ID"; "V2_ORDINARY_REHEARSAL_APP_PRIVATE_KEY" ] do
                start.Environment.Remove name |> ignore
            use child = Process.Start start
            let stdout = child.StandardOutput.ReadToEndAsync()
            let stderr = child.StandardError.ReadToEndAsync()
            child.WaitForExit()
            stdout.GetAwaiter().GetResult() |> ignore
            stderr.GetAwaiter().GetResult() |> ignore
            if child.ExitCode = 0 then Ok()
            else Error "current-source-observer-refused"
        with _ -> Error "current-source-observer-unavailable"

    let private send (transport: IOrdinaryGitHubTransport) (token: string) methodValue (path: string) body idempotency =
        transport.Send(
            Rest
                { Method = methodValue; Uri = Uri(apiBase, path)
                  Headers =
                    Map [ "Accept", "application/vnd.github+json"; "Authorization", "Bearer " + token
                          "User-Agent", userAgent; "X-GitHub-Api-Version", ApiVersion.value ApiVersion.required ]
                  Body = body; ApiVersion = ApiVersion.required; Idempotency = idempotency })

    let private objectResponse expected =
        function
        | Response value when Set.contains value.StatusCode expected ->
            try
                use document = JsonDocument.Parse value.Body
                if document.RootElement.ValueKind = JsonValueKind.Object then Ok(document.RootElement.Clone())
                else Error "github-response-shape"
            with _ -> Error "github-response-json"
        | Response value -> Error $"github-http-{value.StatusCode}"
        | NetworkFailure -> Error "github-network"
        | TimedOut -> Error "github-timeout"

    let private mintInstallationToken transport appId installationId repositoryId privateKey =
        try
            use rsa = RSA.Create()
            rsa.ImportFromPem privateKey
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            let header = Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}") |> base64Url
            let payload = Encoding.UTF8.GetBytes($"{{\"exp\":{now + 540L},\"iat\":{now - 60L},\"iss\":\"{appId}\"}}") |> base64Url
            let unsigned = header + "." + payload
            let signature = rsa.SignData(Encoding.ASCII.GetBytes unsigned, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) |> base64Url
            let jwt = unsigned + "." + signature
            let body = JsonObject()
            let repositories = JsonArray()
            repositories.Add repositoryId
            body.Add("repository_ids", repositories)
            let permissions = JsonObject()
            permissions.Add("contents", "write")
            body.Add("permissions", permissions)
            send transport jwt Post $"app/installations/{installationId}/access_tokens" (Some(body.ToJsonString())) NeverReplay
            |> objectResponse (Set [ 201 ])
            |> Result.bind (fun root ->
                let token = root.GetProperty("token").GetString()
                let actualPermissions =
                    root.GetProperty("permissions").EnumerateObject()
                    |> Seq.map (fun item -> item.Name, item.Value.GetString())
                    |> Map.ofSeq
                if String.IsNullOrWhiteSpace token
                   || actualPermissions <> Map [ "contents", "write"; "metadata", "read" ] then
                    Error "installation-token-scope"
                else Ok token)
        with _ -> Error "installation-token-mint"

    let private decodedContent (root: JsonElement) =
        try
            if root.GetProperty("encoding").GetString() <> "base64" then Error "authority-content-encoding"
            else root.GetProperty("content").GetString().Replace("\n", "") |> Convert.FromBase64String |> Ok
        with _ -> Error "authority-content-base64"

    type private RehearsalFault = RehearsalNoFault | RehearsalLostRefReply | RehearsalRefConflict

    let validateRehearsalFaultSetting enabled value =
        match enabled, value with
        | false, value when String.IsNullOrWhiteSpace value -> Ok()
        | false, _ -> Error "production-refuses-rehearsal-fault"
        | true, value when String.IsNullOrWhiteSpace value || value = "none" -> Ok()
        | true, ("lost-ref-reply" | "ref-conflict") -> Ok()
        | true, _ -> Error "unsupported-rehearsal-fault"

    let private rehearsalFault enabled =
        let value = Environment.GetEnvironmentVariable "FSGG_V2_REHEARSAL_FAULT"
        validateRehearsalFaultSetting enabled value
        |> Result.map (fun () ->
            match value with
            | "lost-ref-reply" -> RehearsalLostRefReply
            | "ref-conflict" -> RehearsalRefConflict
            | _ -> RehearsalNoFault)

    type private RehearsalFaultTransport(inner: IOrdinaryGitHubTransport, fault: RehearsalFault) =
        interface IOrdinaryGitHubTransport with
            member _.Send request =
                let isRefMutation =
                    match request with
                    | Rest value ->
                        (value.Method = Patch && value.Uri.AbsolutePath.Contains("/git/refs/", StringComparison.Ordinal))
                        || (value.Method = Post && value.Uri.AbsolutePath.EndsWith("/git/refs", StringComparison.Ordinal))
                    | _ -> false
                match fault, isRefMutation with
                | RehearsalRefConflict, true ->
                    Response
                        { StatusCode = 422; Headers = Map.empty; Body = "{}"; ETag = None
                          RateBudget = { Limit = None; Remaining = None; ResetAt = None; Cost = Some 1 } }
                | RehearsalLostRefReply, true ->
                    match inner.Send request with
                    | Response value when value.StatusCode = 200 || value.StatusCode = 201 -> NetworkFailure
                    | outcome -> outcome
                | _ -> inner.Send request

    let validateEpochDocuments (profile: OrdinarySettlementAuthorityProfile) revision (headBytes: byte array) (eventBytes: byte array) =
        let epochAddress =
            ShardedJournalAdapter.address Cutover profile.EpochAggregateId
            |> Result.defaultWith (fun _ -> invalidOp "invalid pinned epoch aggregate")
        try
            use head = JsonDocument.Parse headBytes
            use event = JsonDocument.Parse eventBytes
            let phase = event.RootElement.GetProperty("phase").GetString()
            let generation = head.RootElement.GetProperty("generation").GetInt64()
            if event.RootElement.GetProperty("schema").GetString() <> "fsgg.github-substrate.epoch-event/1"
               || event.RootElement.GetProperty("fleetId").GetString() <> profile.EpochFleetId
               || head.RootElement.GetProperty("aggregateId").GetString() <> epochAddress.CanonicalId
               || head.RootElement.GetProperty("aggregateDigest").GetString() <> epochAddress.Digest
               || head.RootElement.GetProperty("journalKind").GetString() <> "cutover"
               || head.RootElement.GetProperty("shard").GetString() <> epochAddress.Shard
               || head.RootElement.GetProperty("eventDigest").GetString() <> sha256 eventBytes
               || phase <> "OpenV2" || generation < 1L then Error "epoch-not-open-v2"
            else Ok(phase, generation, revision)
        with _ -> Error "epoch-document"

    let private authorityEpoch (profile: OrdinarySettlementAuthorityProfile) (transport: IOrdinaryGitHubTransport) (token: string) =
        let repository = profile.Repository.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/"
        let epochRef = profile.EpochRef.Substring("refs/".Length)
        let readRef () =
            send transport token Get $"repos/{repository}/git/ref/{epochRef}" None ReplaySafe
            |> objectResponse (Set [ 200 ])
            |> Result.bind (fun root ->
                let sha = root.GetProperty("object").GetProperty("sha").GetString()
                if String.IsNullOrWhiteSpace sha then Error "epoch-ref" else Ok sha)
        let readFile (revision: string) (path: string) =
            send transport token Get $"repos/{repository}/contents/{path}?ref={Uri.EscapeDataString revision}" None ReplaySafe
            |> objectResponse (Set [ 200 ])
            |> Result.bind decodedContent
        match readRef () with
        | Error reason -> Error reason
        | Ok before ->
            match readFile before "head.json", readFile before "event.json", readRef () with
            | Ok headBytes, Ok eventBytes, Ok after when before = after ->
                validateEpochDocuments profile before headBytes eventBytes
            | _, _, Ok _ -> Error "epoch-read"
            | Error reason, _, _
            | _, Error reason, _
            | _, _, Error reason -> Error reason

    let private selectedPolicySourceValid required (profile: SourceProfile) (root: JsonElement) =
        let mutable selected = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty("selectedSource", &selected) then
            try
                selected.ValueKind = JsonValueKind.Object
                && selected.GetProperty("profile").GetString() = profile.Name
                && selected.GetProperty("repository").GetString() = profile.Repository
                && selected.GetProperty("repositoryId").GetInt64() = profile.RepositoryId
            with _ -> false
        else not required

    let private selectedReceiptSourceValid required (profile: SourceProfile) (root: JsonElement) =
        let mutable name = Unchecked.defaultof<JsonElement>
        let mutable repository = Unchecked.defaultof<JsonElement>
        let mutable repositoryId = Unchecked.defaultof<JsonElement>
        let hasName = root.TryGetProperty("sourceProfile", &name)
        let hasRepository = root.TryGetProperty("sourceRepository", &repository)
        let hasRepositoryId = root.TryGetProperty("sourceRepositoryId", &repositoryId)
        if hasName && hasRepository && hasRepositoryId then
            try
                name.GetString() = profile.Name
                && repository.GetString() = profile.Repository
                && repositoryId.GetInt64() = profile.RepositoryId
            with _ -> false
        elif hasName || hasRepository || hasRepositoryId then false
        else not required

    let validateReceiptFactsForSourceProfile
        (profile: SourceProfile)
        expectedEnvironment
        expectedPolicyId
        (receiptBytes: byte array)
        (policyBytes: byte array)
        =
        try
            use receipt = JsonDocument.Parse receiptBytes
            use policy = JsonDocument.Parse policyBytes
            let root = receipt.RootElement
            let source = root.GetProperty("sourceSha").GetString()
            let head = root.GetProperty("qualificationSha").GetString()
            let tree = root.GetProperty("qualifiedTreeSha").GetString()
            let nodeId = root.GetProperty("pullRequestNodeId").GetString()
            let baseSha = root.GetProperty("pullRequestBaseSha").GetString()
            let pr = root.GetProperty("pullRequest").GetInt32()
            let policyDigest = root.GetProperty("policySha256").GetString()
            let checksElement = root.GetProperty("requiredChecks")
            let gateChecksElement = root.GetProperty("requiredGateChecks")
            let qualification = policy.RootElement.GetProperty("qualification")
            let expectedChecks = qualification.GetProperty("requiredChecks").EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
            let expectedGateChecks = qualification.GetProperty("requiredGateChecks").EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
            let checkValid (value: JsonElement) =
                value.GetProperty("appId").GetInt64() = 15368L
                && value.GetProperty("sourceSha").GetString() = head
                && value.GetProperty("conclusion").GetString() = "success"
            let actualChecks = checksElement.EnumerateArray() |> Seq.map (fun value -> value.GetProperty("name").GetString()) |> Set.ofSeq
            let actualGateChecks = gateChecksElement.EnumerateArray() |> Seq.map (fun value -> value.GetProperty("name").GetString()) |> Set.ofSeq
            let checks =
                checksElement.EnumerateArray()
                |> Seq.filter (fun value -> profile.RequiredSettlementChecks.Contains(value.GetProperty("name").GetString()))
                |> Seq.map (fun value ->
                    { Identity = value.GetProperty("name").GetString()
                      AppId = value.GetProperty("appId").GetInt64()
                      Conclusion = if value.GetProperty("conclusion").GetString() = "success" then CheckPassed else CheckFailed })
                |> Seq.toList
            if root.GetProperty("schema").GetString() <> "fsgg.github.v2-ci-secret-free-predecessor-receipt/1"
               || root.GetProperty("status").GetString() <> "qualified"
               || root.GetProperty("policyId").GetString() <> expectedPolicyId
               || policy.RootElement.GetProperty("policyId").GetString() <> expectedPolicyId
               || root.GetProperty("policyId").GetString() <> policy.RootElement.GetProperty("policyId").GetString()
               || root.GetProperty("mergeCommitSha").GetString() <> source
               || root.GetProperty("workflowRevision").GetString() <> source
               || root.GetProperty("environment").GetString() <> expectedEnvironment
               || root.GetProperty("operationClass").GetString() <> "ordinary-post-merge-delivery-settlement"
               || root.GetProperty("credentialAccess").GetBoolean() <> false
               || root.GetProperty("activation").GetBoolean() <> true
               || policyDigest <> sha256 policyBytes
               || policy.RootElement.GetProperty("credentialJob").GetProperty("installed").GetBoolean() <> true
               || not (selectedReceiptSourceValid (profile.Name <> dotGitHubSourceProfile.Name) profile receipt.RootElement)
               || not (selectedPolicySourceValid (profile.Name <> dotGitHubSourceProfile.Name) profile policy.RootElement)
               || expectedChecks <> profile.RequiredSettlementChecks || actualChecks <> expectedChecks
               || expectedGateChecks <> profile.RequiredGateChecks || actualGateChecks <> expectedGateChecks
               || (checksElement.EnumerateArray() |> Seq.forall checkValid |> not)
               || (gateChecksElement.EnumerateArray() |> Seq.forall checkValid |> not)
               || checks.Length <> profile.RequiredSettlementChecks.Count then Error "preflight-receipt-binding"
            else Ok(source, head, tree, nodeId, baseSha, pr, policyDigest, checks)
        with _ -> Error "preflight-receipt-json"

    let validateReceiptFacts expectedEnvironment expectedPolicyId receiptBytes policyBytes =
        validateReceiptFactsForSourceProfile
            dotGitHubSourceProfile expectedEnvironment expectedPolicyId receiptBytes policyBytes

    type private Provider
        (profile: OrdinarySettlementAuthorityProfile, sourceProfile: Result<SourceProfile, string>, receiptVariable,
         selectedPolicyPath, selectedAnchorPath, observerAction, rehearsal,
         appIdVariable, appPrivateKeyVariable, authorizerPrivateKeyVariable) =
        interface IOrdinarySettlementCommandProvider with
            member _.LoadOneAttempt() =
                let result =
                    result {
                        let! sourceProfile = sourceProfile
                        let! receiptPath = requiredEnvironment receiptVariable
                        let! workspace = requiredEnvironment "GITHUB_WORKSPACE"
                        let workspace = Path.GetFullPath workspace
                        let receiptPath = Path.GetFullPath receiptPath
                        let! _ = runObserver workspace observerAction receiptPath
                        let! fault = rehearsalFault rehearsal
                        let! appIdText = requiredEnvironment appIdVariable
                        let! appPrivateKey = requiredEnvironment appPrivateKeyVariable
                        let! authorizerPrivateKey = requiredEnvironment authorizerPrivateKeyVariable
                        let mutable appId = 0L
                        if not (Int64.TryParse(appIdText, &appId)) || appId <= 0L then return! Error "app-id"
                        let policyBytes = File.ReadAllBytes(Path.Combine(workspace, selectedPolicyPath))
                        let anchorBytes = File.ReadAllBytes(Path.Combine(workspace, selectedAnchorPath))
                        let receiptBytes = File.ReadAllBytes receiptPath
                        let! anchor = OrdinarySettlementPublicAnchor.parse profile (ReadOnlyMemory anchorBytes)
                        if anchor.Trust.AppId <> appId then return! Error "app-id-anchor"
                        let! source, head, tree, nodeId, baseSha, pr, policyDigest, checks =
                            validateReceiptFactsForSourceProfile
                                sourceProfile profile.Environment profile.PolicyId receiptBytes policyBytes
                        let sourceToken = Environment.GetEnvironmentVariable "GH_TOKEN"
                        if String.IsNullOrWhiteSpace sourceToken then return! Error "missing-environment:GH_TOKEN"
                        let handler = new HttpClientHandler(AllowAutoRedirect = false)
                        let client = new HttpClient(handler, true, Timeout = TimeSpan.FromSeconds 30.0)
                        let github = HttpOrdinaryGitHubTransport client :> IOrdinaryGitHubTransport
                        let! authorityToken = mintInstallationToken github appId anchor.Trust.InstallationId profile.RepositoryId appPrivateKey
                        let! epoch, epochGeneration, epochCommit = authorityEpoch profile github authorityToken
                        // Fence queued runs again after credential minting and the current epoch read,
                        // immediately before plan construction and signing. The child still receives no secrets.
                        let! _ = runObserver workspace observerAction receiptPath
                        let binding =
                            { AppId = appId; InstallationId = anchor.Trust.InstallationId
                              RepositoryIds = [ profile.RepositoryId ]; Permissions = anchor.Trust.Permissions }
                        let readBinding =
                            { CredentialKind = "github-actions-repository-token"; RepositoryId = sourceProfile.RepositoryId
                              Permissions = Map [ "actions", "read"; "checks", "read"; "contents", "read"; "pull_requests", "read" ] }
                        let observation =
                            { Repository = sourceProfile.Repository; RepositoryId = sourceProfile.RepositoryId; PullRequestNumber = pr
                              PullRequestNodeId = nodeId; BaseRef = "main"; BaseSha = baseSha; HeadSha = head
                              PolicyRevision = policyDigest; Checks = checks; Epoch = epoch; EpochGeneration = epochGeneration
                              EpochCommit = epochCommit; JournalGeneration = 0L; JournalHead = String.replicate 40 "0"
                              SourceComplete = true; ChecksComplete = true; Authorized = true; Supported = true }
                        let association =
                            { Number = pr; NodeId = nodeId; Repository = sourceProfile.Repository; BaseRef = "main"
                              HeadCommit = head; MergeCommit = source; Merged = true }
                        let! plan, _ =
                            OrdinaryPostMergeSettlement.prepare
                                ("ordinary-push:" + source.ToLowerInvariant()) profile.RepositoryId tree source source
                                "ordinary-post-merge-delivery-settlement" profile.Environment observation [ association ] readBinding binding
                            |> Result.mapError (sprintf "plan:%A")
                        use authorizer = RSA.Create()
                        authorizer.ImportFromPem authorizerPrivateKey
                        let publicPem = authorizer.ExportSubjectPublicKeyInfoPem()
                        if sha256 (authorizer.ExportSubjectPublicKeyInfo()) <> anchor.Trust.PublicKeySpkiSha256 then
                            return! Error "authorizer-spki"
                        let intent = OrdinaryPostMergeSettlement.canonicalIntent plan binding
                        let authorization =
                            { KeyId = anchor.Trust.KeyId; PublicKeyPem = publicPem; IntentSha256 = sha256 intent
                              Signature = authorizer.SignData(intent, HashAlgorithmName.SHA256, RSASignaturePadding.Pss) }
                        let effectTransport = RehearsalFaultTransport(github, fault) :> IOrdinaryGitHubTransport
                        let options = OrdinarySettlementPublicAnchor.transportOptions apiBase authorityToken userAgent profile anchor epochCommit epochGeneration
                        let runtime = OrdinarySettlementGitAuthority.Runtime(appId, OrdinarySettlementGitHubAuthority.Transport(options, effectTransport))
                        return { Plan = plan; Credential = binding; Anchor = anchor.Trust; Authorization = authorization
                                 Runtime = runtime :> IOrdinaryPostMergeSettlementRuntime }
                    }
                try result with error -> Error("installed-provider:" + error.GetType().Name)

    let tryCreate () =
        Some(
            Provider(
                OrdinarySettlementAuthorityProfiles.production,
                selectSourceProfile (Environment.GetEnvironmentVariable "FSGG_V2_SOURCE_PROFILE"),
                "FSGG_V2_PREFLIGHT_RECEIPT", policyPath, anchorPath, "verify", false,
                "V2_ORDINARY_APP_ID", "V2_ORDINARY_APP_PRIVATE_KEY", "V2_ORDINARY_AUTHORIZER_PRIVATE_KEY")
            :> IOrdinarySettlementCommandProvider)

    let tryCreateRehearsal () =
        Some(
            Provider(
                OrdinarySettlementAuthorityProfiles.rehearsal,
                Ok dotGitHubSourceProfile,
                "FSGG_V2_REHEARSAL_PREFLIGHT_RECEIPT", rehearsalPolicyPath, rehearsalAnchorPath, "verify-rehearsal", true,
                "V2_ORDINARY_REHEARSAL_APP_ID", "V2_ORDINARY_REHEARSAL_APP_PRIVATE_KEY",
                "V2_ORDINARY_REHEARSAL_AUTHORIZER_PRIVATE_KEY")
            :> IOrdinarySettlementCommandProvider)
