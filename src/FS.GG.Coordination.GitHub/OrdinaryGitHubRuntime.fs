namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

type IOrdinaryGitHubTransport =
    abstract Send: GitHubRequest -> TransportOutcome

type OrdinaryGitHubOptions =
    {
        ApiBase: Uri
        Token: string
        UserAgent: string
        Repository: string
        PullRequestNumber: int
        PolicyRef: string
        EpochRepository: string
        EpochRef: string
        EpochPath: string
        JournalRepository: string
    }

type HttpOrdinaryGitHubTransport(client: HttpClient) =
    let responseHeaders (response: HttpResponseMessage) =
        Seq.append response.Headers response.Content.Headers
        |> Seq.map (fun item -> item.Key.ToLowerInvariant(), String.concat "," item.Value)
        |> Map.ofSeq

    interface IOrdinaryGitHubTransport with
        member _.Send request =
            match Transport.validateRequest request with
            | Error _ -> Response { StatusCode = 400; Headers = Map.empty; Body = "invalid-request"; ETag = None; RateBudget = { Limit = None; Remaining = None; ResetAt = None; Cost = None } }
            | Ok () ->
                let methodValue, uri, headers, body =
                    match request with
                    | Rest value ->
                        let methodValue =
                            match value.Method with
                            | Get -> HttpMethod.Get
                            | Post -> HttpMethod.Post
                            | Put -> HttpMethod.Put
                            | Patch -> HttpMethod.Patch
                            | Delete -> HttpMethod.Delete
                        methodValue, value.Uri, value.Headers, value.Body
                    | GraphQL _ -> invalidOp "ordinary delivery uses the GitHub REST API"

                try
                    use message = new HttpRequestMessage(methodValue, uri)
                    for KeyValue(name, value) in headers do
                        message.Headers.TryAddWithoutValidation(name, value) |> ignore
                    match body with
                    | Some value -> message.Content <- new StringContent(value, Encoding.UTF8, "application/json")
                    | None -> ()
                    use response = client.Send(message)
                    let headers = responseHeaders response
                    let tryInt name =
                        Map.tryFind name headers
                        |> Option.bind (fun value -> match Int32.TryParse value with true, parsed -> Some parsed | _ -> None)
                    let tryDate name =
                        Map.tryFind name headers
                        |> Option.bind (fun value -> match Int64.TryParse value with true, parsed -> Some(DateTimeOffset.FromUnixTimeSeconds parsed) | _ -> None)
                    Response
                        {
                            StatusCode = int response.StatusCode
                            Headers = headers
                            Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            ETag = Map.tryFind "etag" headers
                            RateBudget =
                                {
                                    Limit = tryInt "x-ratelimit-limit"
                                    Remaining = tryInt "x-ratelimit-remaining"
                                    ResetAt = tryDate "x-ratelimit-reset"
                                    Cost = Some 1
                                }
                        }
                with
                | :? TaskCanceledException -> TimedOut
                | :? HttpRequestException -> NetworkFailure

[<RequireQualifiedAccess>]
module OrdinaryGitHubRuntime =
    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()
        member _.Delay(generator) = generator
        member _.Run(generator) = generator()
        member _.Combine(value, continuation) = Result.bind (fun () -> continuation()) value
        member _.TryFinally(generator, compensation) =
            try generator() finally compensation()
        member this.Using(resource: #IDisposable, binder) =
            this.TryFinally((fun () -> binder resource), fun () -> if not (isNull (box resource)) then resource.Dispose())

    let private result = ResultBuilder()

    let private shaLength value =
        not (String.IsNullOrWhiteSpace value)
        && (value.Length = 40 || value.Length = 64)
        && value |> Seq.forall Uri.IsHexDigit

    let private stageText =
        function
        | OrdinaryIntentPersisted -> "intent-persisted"
        | OrdinaryEffectPending -> "effect-pending"
        | OrdinarySettled -> "settled"

    let private parseStage =
        function
        | "intent-persisted" -> Some OrdinaryIntentPersisted
        | "effect-pending" -> Some OrdinaryEffectPending
        | "settled" -> Some OrdinarySettled
        | _ -> None

    type private JournalRecord = { Authority: OrdinaryJournalAuthority; BlobSha: string }

    let private encodeSegment (value: string) = Uri.EscapeDataString value

    let private repoPath (repository: string) =
        repository.Split('/') |> Array.map encodeSegment |> String.concat "/"

    let private combine (baseUri: Uri) (path: string) = Uri(baseUri, path.TrimStart('/'))

    let private jsonDocument (body: string) =
        try Ok(JsonDocument.Parse body) with error -> Error $"malformed-json:{error.Message}"

    let private decodeBase64 (value: string) =
        try Ok(Convert.FromBase64String(value.Replace("\n", "")))
        with error -> Error $"malformed-base64:{error.Message}"

    let private requiredString (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
            let text = value.GetString()
            if String.IsNullOrWhiteSpace text then Error $"missing:{name}" else Ok text
        else Error $"missing:{name}"

    let private canonicalState (authority: OrdinaryJournalAuthority) =
        let root = JsonObject()
        root.Add("generation", authority.Generation)
        match authority.MergeCommit with Some value -> root.Add("mergeCommit", value) | None -> root.Add("mergeCommit", null)
        root.Add("operationId", authority.OperationId)
        root.Add("planDigest", authority.PlanDigest)
        root.Add("schema", "fsgg.coordination.ordinary-delivery-journal/1")
        root.Add("stage", stageText authority.Stage)
        root.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson
        |> Result.defaultWith invalidOp

    type Runtime(options: OrdinaryGitHubOptions, transport: IOrdinaryGitHubTransport) =
        let headers =
            [
                "accept", "application/vnd.github+json"
                "x-github-api-version", ApiVersion.value ApiVersion.required
                "user-agent", options.UserAgent
                if not (String.IsNullOrWhiteSpace options.Token) then "authorization", $"Bearer {options.Token}"
            ] |> Map.ofList

        let mutable subject: (int64 * string) option = None

        let request methodValue uri body idempotency =
            let value =
                Rest
                    {
                        Method = methodValue
                        Uri = uri
                        Headers = headers
                        Body = body
                        ApiVersion = ApiVersion.required
                        Idempotency = idempotency
                    }
            transport.Send value

        let get uri =
            let rec run attempt =
                let value = request Get uri None ReplaySafe
                match value, Transport.decideRetry 2 attempt (Rest { Method = Get; Uri = uri; Headers = headers; Body = None; ApiVersion = ApiVersion.required; Idempotency = ReplaySafe }) value with
                | Response response, _ -> Ok response
                | _, RetryAfter _ -> run (attempt + 1)
                | NetworkFailure, _ -> Error "network-failure"
                | TimedOut, _ -> Error "timeout"
            run 1

        let getJson uri =
            get uri
            |> Result.bind (fun response ->
                if response.StatusCode >= 200 && response.StatusCode < 300 then
                    jsonDocument response.Body |> Result.map (fun document -> response, document)
                else Error $"github-read:{response.StatusCode}")

        let getRef (repository: string) (refName: string) =
            let normalized =
                let trimmed = refName.TrimStart('/')
                if trimmed.StartsWith("refs/", StringComparison.Ordinal) then trimmed.Substring("refs/".Length) else trimmed
            let relative = $"repos/{repoPath repository}/git/ref/{normalized}"
            getJson (combine options.ApiBase relative)
            |> Result.bind (fun (_, document) ->
                use document = document
                requiredString "sha" (document.RootElement.GetProperty("object")))

        let journalAddress () =
            match subject with
            | Some(repositoryId, nodeId) ->
                ShardedJournalAdapter.address Operation $"ordinary:{repositoryId}:{nodeId}"
                |> Result.mapError (sprintf "%A")
            | None -> Error "subject-not-observed"

        let journalPath (address: AggregateAddress) = $"ordinary/{address.Digest}.json"

        let readJournalCore () =
            journalAddress ()
            |> Result.bind (fun address ->
                getRef options.JournalRepository address.Ref
                |> Result.bind (fun refSha ->
                    let relative =
                        $"repos/{repoPath options.JournalRepository}/contents/{journalPath address}?ref={encodeSegment refSha}"
                    get (combine options.ApiBase relative)
                    |> Result.bind (fun response ->
                        if response.StatusCode = 404 then Ok(refSha, None)
                        elif response.StatusCode < 200 || response.StatusCode >= 300 then Error $"journal-read:{response.StatusCode}"
                        else
                            jsonDocument response.Body
                            |> Result.bind (fun document ->
                                use document = document
                                let root = document.RootElement
                                result {
                                    let! blobSha = requiredString "sha" root
                                    let! encoded = requiredString "content" root
                                    let! bytes = decodeBase64 encoded
                                    use state = JsonDocument.Parse bytes
                                    let value = state.RootElement
                                    let! schema = requiredString "schema" value
                                    let! operationId = requiredString "operationId" value
                                    let! digest = requiredString "planDigest" value
                                    let! stageValue = requiredString "stage" value
                                    if schema <> "fsgg.coordination.ordinary-delivery-journal/1" then
                                        return! Error "journal-schema"
                                    let stage = parseStage stageValue
                                    if stage.IsNone then return! Error "journal-stage"
                                    let mutable merge = Unchecked.defaultof<JsonElement>
                                    let mergeCommit =
                                        if value.TryGetProperty("mergeCommit", &merge) && merge.ValueKind = JsonValueKind.String then Some(merge.GetString()) else None
                                    return
                                        refSha,
                                        Some
                                            {
                                                BlobSha = blobSha
                                                Authority =
                                                    {
                                                        OperationId = operationId
                                                        PlanDigest = digest
                                                        Generation = value.GetProperty("generation").GetInt64()
                                                        Stage = stage.Value
                                                        MergeCommit = mergeCommit
                                                    }
                                            }
                                }))))

        let readJournal () =
            try readJournalCore ()
            with error -> Error $"malformed-journal:{error.Message}"

        let writeJournal (_expected: int64) (existing: JournalRecord option) (authority: OrdinaryJournalAuthority) =
            journalAddress ()
            |> Result.bind (fun address ->
                let body = JsonObject()
                body.Add("branch", address.Ref.Substring("refs/heads/".Length))
                body.Add("content", Convert.ToBase64String(canonicalState authority))
                body.Add("message", $"ordinary delivery {authority.OperationId} generation {authority.Generation}")
                match existing with Some value -> body.Add("sha", value.BlobSha) | None -> ()
                let uri = combine options.ApiBase $"repos/{repoPath options.JournalRepository}/contents/{journalPath address}"
                match request Put uri (Some(body.ToJsonString())) (ReplayWithKey authority.OperationId) with
                | NetworkFailure
                | TimedOut -> Ok CasUnknown
                | Response response when response.StatusCode = 409 || response.StatusCode = 422 -> Ok CasConflict
                | Response response when response.StatusCode >= 200 && response.StatusCode < 300 -> Ok(CasAccepted authority)
                | Response response -> Error $"journal-write:{response.StatusCode}")
            |> Result.defaultValue CasUnknown

        let observeCore () =
            result {
                let repositoryUri = combine options.ApiBase $"repos/{repoPath options.Repository}"
                let! _, repositoryDocument = getJson repositoryUri
                use repositoryDocument = repositoryDocument
                let repositoryRoot = repositoryDocument.RootElement
                let! fullName = requiredString "full_name" repositoryRoot
                let repositoryId = repositoryRoot.GetProperty("id").GetInt64()
                let mutable permissions = Unchecked.defaultof<JsonElement>
                let userPush =
                    repositoryRoot.TryGetProperty("permissions", &permissions)
                    && permissions.TryGetProperty("push", &permissions)
                    && permissions.GetBoolean()

                // GitHub reports user collaboration permissions as false for installation
                // tokens. For that credential kind, prove the exact repository is in the
                // token's installation selection instead of treating `permissions.push`
                // as an App permission. The operation grant separately binds the minted
                // token's write permissions; mutation responses still fail closed.
                let rec installationContains (page: int) (seen: Set<string>) (uri: Uri) =
                    if page >= 10 || Set.contains uri.AbsoluteUri seen then Error "installation-pagination-incomplete"
                    else
                        getJson uri
                        |> Result.bind (fun (response, document) ->
                            use document = document
                            let root = document.RootElement
                            let repositories = root.GetProperty("repositories").EnumerateArray() |> Seq.toList
                            let found =
                                repositories
                                |> List.exists (fun item ->
                                    item.GetProperty("id").GetInt64() = repositoryId
                                    && item.GetProperty("full_name").GetString().Equals(fullName, StringComparison.OrdinalIgnoreCase))
                            match Map.tryFind "link" response.Headers with
                            | Some link ->
                                match Transport.tryNextLink link with
                                | Error _ -> Error "installation-pagination-incomplete"
                                | Ok(Some next) ->
                                    installationContains (page + 1) (Set.add uri.AbsoluteUri seen) next
                                    |> Result.map (fun later -> found || later)
                                | Ok None -> Ok found
                            | None ->
                                let total = root.GetProperty("total_count").GetInt32()
                                if total > repositories.Length then Error "installation-pagination-incomplete"
                                else Ok found)
                let! authorized =
                    if userPush then Ok true
                    else installationContains 0 Set.empty (combine options.ApiBase "installation/repositories?per_page=100")

                let pullUri = combine options.ApiBase $"repos/{repoPath options.Repository}/pulls/{options.PullRequestNumber}"
                let! _, pullDocument = getJson pullUri
                use pullDocument = pullDocument
                let pull = pullDocument.RootElement
                let! nodeId = requiredString "node_id" pull
                let baseValue = pull.GetProperty("base")
                let headValue = pull.GetProperty("head")
                let! baseRef = requiredString "ref" baseValue
                let! baseSha = requiredString "sha" baseValue
                let! headSha = requiredString "sha" headValue
                subject <- Some(repositoryId, nodeId)

                let! policyRevision = getRef options.Repository options.PolicyRef
                let! epochCommit = getRef options.EpochRepository options.EpochRef
                let epochUri =
                    combine options.ApiBase $"repos/{repoPath options.EpochRepository}/contents/{options.EpochPath.TrimStart('/')}?ref={encodeSegment epochCommit}"
                let! _, epochDocument = getJson epochUri
                use epochDocument = epochDocument
                let! epochEncoded = requiredString "content" epochDocument.RootElement
                let! epochBytes = decodeBase64 epochEncoded
                use epochState = JsonDocument.Parse epochBytes
                let epochRoot = epochState.RootElement
                let! epoch = requiredString "phase" epochRoot
                let epochGeneration = epochRoot.GetProperty("generation").GetInt64()
                let complete = epochRoot.GetProperty("complete").GetBoolean()

                let protectionUri =
                    combine options.ApiBase $"repos/{repoPath options.Repository}/branches/{encodeSegment baseRef}/protection/required_status_checks"
                let! _, protectionDocument = getJson protectionUri
                use protectionDocument = protectionDocument
                let requiredChecks =
                    protectionDocument.RootElement.GetProperty("checks").EnumerateArray()
                    |> Seq.map (fun value -> value.GetProperty("context").GetString(), value.GetProperty("app_id").GetInt64())
                    |> Seq.toList

                let start = combine options.ApiBase $"repos/{repoPath options.Repository}/commits/{headSha}/check-runs?per_page=100"
                let rec collect (pages: int) (seen: Set<string>) (uri: Uri) =
                    if Set.contains uri.AbsoluteUri seen || pages >= 10 then Error "check-pagination-incomplete"
                    else
                        getJson uri
                        |> Result.bind (fun (response, document) ->
                            use document = document
                            let root = document.RootElement
                            let values = root.GetProperty("check_runs").EnumerateArray() |> Seq.map _.Clone() |> Seq.toList
                            let mutable total = Unchecked.defaultof<JsonElement>
                            let expectedTotal =
                                if root.TryGetProperty("total_count", &total) && total.ValueKind = JsonValueKind.Number then total.GetInt32()
                                else values.Length
                            match Map.tryFind "link" response.Headers with
                            | None when values.Length < expectedTotal -> Error "check-pagination-incomplete"
                            | None -> Ok values
                            | Some link ->
                                match Transport.tryNextLink link with
                                | Error _ -> Error "check-pagination-incomplete"
                                | Ok None -> Ok values
                                | Ok(Some next) -> collect (pages + 1) (Set.add uri.AbsoluteUri seen) next |> Result.map (List.append values))
                let! checkRuns = collect 0 Set.empty start
                let checks =
                    requiredChecks
                    |> List.map (fun (identity, appId) ->
                        let found =
                            checkRuns
                            |> List.filter (fun run ->
                                run.GetProperty("name").GetString() = identity
                                && run.GetProperty("app").GetProperty("id").GetInt64() = appId)
                            |> List.sortByDescending (fun run -> run.GetProperty("id").GetInt64())
                            |> List.tryHead
                        let conclusion =
                            match found with
                            | None -> CheckUnknown
                            | Some value when value.GetProperty("status").GetString() <> "completed" -> CheckPending
                            | Some value ->
                                match value.GetProperty("conclusion").GetString() with
                                | "success" -> CheckPassed
                                | null -> CheckPending
                                | _ -> CheckFailed
                        { Identity = identity; AppId = appId; Conclusion = conclusion })

                let! journalRef, journal = readJournal ()
                return
                    {
                        Repository = fullName
                        RepositoryId = repositoryId
                        PullRequestNumber = options.PullRequestNumber
                        PullRequestNodeId = nodeId
                        BaseRef = baseRef
                        BaseSha = baseSha
                        HeadSha = headSha
                        PolicyRevision = policyRevision
                        Checks = checks
                        Epoch = epoch
                        EpochGeneration = epochGeneration
                        EpochCommit = epochCommit
                        JournalGeneration = journal |> Option.map (fun value -> value.Authority.Generation) |> Option.defaultValue 0L
                        JournalHead = journalRef
                        SourceComplete = true
                        ChecksComplete = not (List.isEmpty requiredChecks)
                        Authorized = authorized
                        Supported = complete && fullName.Equals(options.Repository, StringComparison.OrdinalIgnoreCase)
                    }
            }

        let observe () =
            try observeCore ()
            with error -> Error $"malformed-provider-observation:{error.Message}"

        interface IOrdinaryDeliveryRuntime with
            member _.Observe() = observe ()

            member _.ObserveJournal operationId =
                readJournal ()
                |> Result.map (fun (_, value) ->
                    value
                    |> Option.map _.Authority
                    |> Option.filter (fun authority -> authority.OperationId = operationId))

            member _.PersistIntent(expected, digest, operationId) =
                match readJournal () with
                | Error _ -> CasUnknown
                | Ok(_, current) when (current |> Option.map (fun value -> value.Authority.Generation) |> Option.defaultValue 0L) <> expected -> CasConflict
                | Ok(_, current) ->
                    let authority =
                        { OperationId = operationId; PlanDigest = digest; Generation = expected + 1L; Stage = OrdinaryIntentPersisted; MergeCommit = None }
                    writeJournal expected current authority

            member _.MarkPending(expected, operationId) =
                match readJournal () with
                | Ok(_, Some current) when current.Authority.Generation = expected && current.Authority.OperationId = operationId ->
                    writeJournal expected (Some current) { current.Authority with Generation = expected + 1L; Stage = OrdinaryEffectPending }
                | Error _ -> CasUnknown
                | _ -> CasConflict

            member _.ObserveEffect _ =
                let uri = combine options.ApiBase $"repos/{repoPath options.Repository}/pulls/{options.PullRequestNumber}"
                getJson uri
                |> Result.bind (fun (_, document) ->
                    use document = document
                    let root = document.RootElement
                    if root.GetProperty("merged").GetBoolean() then
                        requiredString "merge_commit_sha" root |> Result.map EffectApplied
                    else Ok EffectProvenAbsent)

            member _.DispatchMerge(operationId, repositoryId, pullRequestNumber, expectedHead) =
                match subject with
                | Some(observedRepositoryId, _) when observedRepositoryId = repositoryId && pullRequestNumber = options.PullRequestNumber ->
                    let body = JsonObject()
                    body.Add("commit_title", $"ordinary source delivery {operationId.Substring(0, min 32 operationId.Length)}")
                    body.Add("merge_method", "squash")
                    body.Add("sha", expectedHead)
                    let uri = combine options.ApiBase $"repos/{repoPath options.Repository}/pulls/{pullRequestNumber}/merge"
                    match request Put uri (Some(body.ToJsonString())) NeverReplay with
                    | NetworkFailure
                    | TimedOut -> DispatchOutcomeUnknown
                    | Response response when response.StatusCode >= 200 && response.StatusCode < 300 ->
                        match jsonDocument response.Body with
                        | Ok document ->
                            use document = document
                            if document.RootElement.GetProperty("merged").GetBoolean() then
                                match requiredString "sha" document.RootElement with Ok value -> DispatchApplied value | Error reason -> DispatchRefused reason
                            else DispatchRefused "merge-not-applied"
                        | Error reason -> DispatchRefused reason
                    | Response response -> DispatchRefused $"github-merge:{response.StatusCode}"
                | _ -> DispatchRefused "cross-subject-dispatch"

            member _.PersistSettlement(expected, operationId, mergeCommit) =
                match readJournal () with
                | Ok(_, Some current) when current.Authority.Generation = expected && current.Authority.OperationId = operationId && shaLength mergeCommit ->
                    writeJournal expected (Some current) { current.Authority with Generation = expected + 1L; Stage = OrdinarySettled; MergeCommit = Some mergeCommit }
                | Error _ -> CasUnknown
                | _ -> CasConflict
