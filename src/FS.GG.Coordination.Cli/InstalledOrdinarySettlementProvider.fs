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

    let private sourceRepository = "FS-GG/.github"
    let private sourceRepositoryId = 1269292704L
    let private apiBase = Uri "https://api.github.com/"
    let private userAgent = "fsgg-coordination/0.1.2"
    let private policyPath = "policy/v2-ci-ordinary-settlement.json"
    let private rehearsalPolicyPath = "policy/v2-ci-ordinary-settlement-rehearsal.json"
    let private anchorPath = "policy/v2-ci-ordinary-settlement-anchor.json"
    let private rehearsalAnchorPath = "policy/v2-ci-ordinary-settlement-rehearsal-anchor.json"
    let private observerPath = "tools/v2-ci-ordinary-observe.py"
    let private requiredSettlementChecks = Set [ "contract-coherence / coherence"; "routine-eligibility" ]

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

    let validateReceiptFacts expectedEnvironment expectedPolicyId (receiptBytes: byte array) (policyBytes: byte array) =
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
                |> Seq.filter (fun value -> requiredSettlementChecks.Contains(value.GetProperty("name").GetString()))
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
               || expectedChecks <> requiredSettlementChecks || actualChecks <> expectedChecks
               || actualGateChecks <> expectedGateChecks
               || (checksElement.EnumerateArray() |> Seq.forall checkValid |> not)
               || (gateChecksElement.EnumerateArray() |> Seq.forall checkValid |> not)
               || checks.Length <> 2 then Error "preflight-receipt-binding"
            else Ok(source, head, tree, nodeId, baseSha, pr, policyDigest, checks)
        with _ -> Error "preflight-receipt-json"

    type private Provider
        (profile: OrdinarySettlementAuthorityProfile, receiptVariable, selectedPolicyPath, selectedAnchorPath, observerAction, rehearsal,
         appIdVariable, appPrivateKeyVariable, authorizerPrivateKeyVariable) =
        interface IOrdinarySettlementCommandProvider with
            member _.LoadOneAttempt() =
                let result =
                    result {
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
                            validateReceiptFacts profile.Environment profile.PolicyId receiptBytes policyBytes
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
                            { CredentialKind = "github-actions-repository-token"; RepositoryId = sourceRepositoryId
                              Permissions = Map [ "actions", "read"; "checks", "read"; "contents", "read"; "pull_requests", "read" ] }
                        let observation =
                            { Repository = sourceRepository; RepositoryId = sourceRepositoryId; PullRequestNumber = pr
                              PullRequestNodeId = nodeId; BaseRef = "main"; BaseSha = baseSha; HeadSha = head
                              PolicyRevision = policyDigest; Checks = checks; Epoch = epoch; EpochGeneration = epochGeneration
                              EpochCommit = epochCommit; JournalGeneration = 0L; JournalHead = String.replicate 40 "0"
                              SourceComplete = true; ChecksComplete = true; Authorized = true; Supported = true }
                        let association =
                            { Number = pr; NodeId = nodeId; Repository = sourceRepository; BaseRef = "main"
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
                "FSGG_V2_PREFLIGHT_RECEIPT", policyPath, anchorPath, "verify", false,
                "V2_ORDINARY_APP_ID", "V2_ORDINARY_APP_PRIVATE_KEY", "V2_ORDINARY_AUTHORIZER_PRIVATE_KEY")
            :> IOrdinarySettlementCommandProvider)

    let tryCreateRehearsal () =
        Some(
            Provider(
                OrdinarySettlementAuthorityProfiles.rehearsal,
                "FSGG_V2_REHEARSAL_PREFLIGHT_RECEIPT", rehearsalPolicyPath, rehearsalAnchorPath, "verify-rehearsal", true,
                "V2_ORDINARY_REHEARSAL_APP_ID", "V2_ORDINARY_REHEARSAL_APP_PRIVATE_KEY",
                "V2_ORDINARY_REHEARSAL_AUTHORIZER_PRIVATE_KEY")
            :> IOrdinarySettlementCommandProvider)
