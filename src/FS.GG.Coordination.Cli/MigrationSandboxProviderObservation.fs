namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub

type MigrationSandboxProviderOptions =
    { ApiBase: Uri
      GraphQLUri: Uri
      Token: string
      UserAgent: string }

type MigrationSandboxProviderProof =
    { RequestIdentity: string
      RawSha256: string
      NextIdentity: string option }

type MigrationSandboxScopeIdentityEvidence =
    { TokenSha256: string
      ActorLogin: string
      ActorDatabaseId: int64
      RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RepositoryPrivate: bool
      RepositoryDescription: string
      ProjectOrganization: string
      ProjectNumber: int
      ProjectNodeId: string
      ProjectTitle: string
      ProjectPrivate: bool
      ProjectClosed: bool
      TokenRepositorySelectionComplete: bool
      Proofs: MigrationSandboxProviderProof list }

type MigrationSandboxFixtureEvidence =
    { TokenSha256: string
      Observation: MigrationSandboxFixtureObservation
      RepositoryIdentity: MigrationSandboxProviderProof
      IssuePages: MigrationSandboxProviderProof list
      ProjectPages: MigrationSandboxProviderProof list
      ExactIssue: MigrationSandboxProviderProof }

[<RequireQualifiedAccess>]
type MigrationSandboxProviderFailure =
    | InvalidOptions
    | InvalidNonce
    | TransportRefused
    | HttpRefused of int
    | GraphQLErrors
    | MalformedResponse
    | ForeignIdentity
    | PaginationRefused
    | PopulationDrift
    | MissingRevision
    | GrantUnavailable

exception private Refused of MigrationSandboxProviderFailure

[<AutoOpen>]
module private SandboxProviderHelpers =
    let repoId = 1353050537L
    let repoNodeId = "R_kgDOUKXpqQ"
    let repoName = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
    let projectNodeId = "PVT_kwDOEYAWY84BiESo"
    let projectPurpose = "fsgg-sandbox-gs2-04-9"
    let repoDescription = "fsgg-sandbox-gs2-04-9 disposable qualification target; never production"
    let repoPath = "repos/FS-GG/FS.GG.GitHub.Substrate.Sandbox"
    let projectQuery =
        "query($owner:String!){organization(login:$owner){login projectV2(number:2){id number title closed public}}}"
    let viewerQuery = "query{viewer{login databaseId}}"

    let refuse failure = raise (Refused failure)
    let rec uniqueJson (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            let members = element.EnumerateObject() |> Seq.toList
            if members.Length <> (members |> List.map _.Name |> Set.ofList |> Set.count) then
                refuse MigrationSandboxProviderFailure.MalformedResponse
            for memberValue in members do uniqueJson memberValue.Value
        | JsonValueKind.Array ->
            for item in element.EnumerateArray() do uniqueJson item
        | _ -> ()
    let parse (body: string) (action: JsonElement -> 'a) : Result<'a, MigrationSandboxProviderFailure> =
        try
            use document = JsonDocument.Parse body
            uniqueJson document.RootElement
            action document.RootElement |> Ok
        with
        | Refused failure -> Error failure
        | :? JsonException | :? InvalidOperationException | :? KeyNotFoundException
        | :? FormatException | :? OverflowException -> Error MigrationSandboxProviderFailure.MalformedResponse

    let prop (name: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then refuse MigrationSandboxProviderFailure.MalformedResponse
        let mutable found = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &found) then found
        else refuse MigrationSandboxProviderFailure.MalformedResponse
    let str name value =
        let item = prop name value
        if item.ValueKind <> JsonValueKind.String then refuse MigrationSandboxProviderFailure.MalformedResponse
        let result = item.GetString()
        if String.IsNullOrWhiteSpace result then refuse MigrationSandboxProviderFailure.MalformedResponse
        result
    let number name value =
        let item = prop name value
        let mutable result = 0L
        if item.ValueKind <> JsonValueKind.Number || not (item.TryGetInt64(&result)) || result <= 0L then
            refuse MigrationSandboxProviderFailure.MalformedResponse
        result
    let boolean name value =
        let item = prop name value
        if item.ValueKind <> JsonValueKind.True && item.ValueKind <> JsonValueKind.False then
            refuse MigrationSandboxProviderFailure.MalformedResponse
        item.GetBoolean()
    let array name value =
        let item = prop name value
        if item.ValueKind <> JsonValueKind.Array then refuse MigrationSandboxProviderFailure.MalformedResponse
        item.EnumerateArray() |> Seq.toList
    let nullableString name value =
        let item = prop name value
        match item.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(item.GetString())
        | _ -> refuse MigrationSandboxProviderFailure.MalformedResponse
    let sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()
    let digest values =
        values |> List.map (fun (v: string) -> $"{Encoding.UTF8.GetByteCount v}:{v}")
        |> String.concat "" |> sha
    let proof identity body next =
        { RequestIdentity=identity; RawSha256=sha body; NextIdentity=next }
    let header name (headers: Map<string,string>) =
        headers |> Map.toList |> List.tryPick (fun (key, value) ->
            if String.Equals(key, name, StringComparison.OrdinalIgnoreCase) then Some value else None)
    let response outcome =
        match outcome with
        | Response reply when reply.StatusCode = 200 -> Ok reply
        | Response reply -> Error(MigrationSandboxProviderFailure.HttpRefused reply.StatusCode)
        | _ -> Error MigrationSandboxProviderFailure.TransportRefused
    let graphRoot body action =
        parse body (fun root ->
            let mutable errors = Unchecked.defaultof<JsonElement>
            if root.ValueKind <> JsonValueKind.Object then refuse MigrationSandboxProviderFailure.MalformedResponse
            if root.TryGetProperty("errors", &errors) then refuse MigrationSandboxProviderFailure.GraphQLErrors
            action (prop "data" root))

    let mapReadFailure = function
        | MigrationReadFailure.HttpRefused status -> MigrationSandboxProviderFailure.HttpRefused status
        | MigrationReadFailure.GraphQLErrors -> MigrationSandboxProviderFailure.GraphQLErrors
        | MigrationReadFailure.IdentityDrift -> MigrationSandboxProviderFailure.ForeignIdentity
        | MigrationReadFailure.PaginationRefused _ -> MigrationSandboxProviderFailure.PaginationRefused
        | MigrationReadFailure.PopulationDrift -> MigrationSandboxProviderFailure.PopulationDrift
        | MigrationReadFailure.TransportUnavailable -> MigrationSandboxProviderFailure.TransportRefused
        | _ -> MigrationSandboxProviderFailure.MalformedResponse

type private Capture(inner: IMigrationGitHubReadTransport, allowed: GitHubRequest -> bool) =
    let calls = ResizeArray<GitHubRequest * TransportOutcome>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            let outcome = if allowed request then inner.Send request else NetworkFailure
            calls.Add(request, outcome)
            outcome

type MigrationSandboxProviderObservation(options: MigrationSandboxProviderOptions,
                                         transport: IMigrationGitHubReadTransport) =
    let validOptions =
        not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.AbsoluteUri = "https://api.github.com/"
        && not (isNull options.GraphQLUri) && options.GraphQLUri.IsAbsoluteUri
        && options.GraphQLUri.AbsoluteUri = "https://api.github.com/graphql"
        && not (String.IsNullOrWhiteSpace options.Token)
        && not (String.IsNullOrWhiteSpace options.UserAgent)

    let headers =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", options.UserAgent
          "authorization", $"Bearer {options.Token}" ] |> Map.ofList

    let rest uri =
        let request = Rest { Method=Get; Uri=uri; Headers=headers; Body=None
                             ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        transport.Send request |> response

    let graph (document: string) (variables: Map<string,string>) =
        let request = GraphQL { Uri=options.GraphQLUri; Document=document; Variables=variables;
                               Headers=headers; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        transport.Send request |> response

    let repositoryUri = Uri("https://api.github.com/" + repoPath)
    let issueUri = Uri("https://api.github.com/" + repoPath + "/issues/1")
    let issueStart = Uri("https://api.github.com/" + repoPath + "/issues?state=all&per_page=100")
    let installationUri = Uri("https://api.github.com/installation/repositories?per_page=100")
    let issueOptions =
        { ApiBase=options.ApiBase; GraphQLUri=options.GraphQLUri; Token=options.Token
          UserAgent=options.UserAgent; Owner="FS-GG"; Repository="FS.GG.GitHub.Substrate.Sandbox"
          ExpectedRepositoryId=repoId }
    let projectOptions =
        { GraphQLUri=options.GraphQLUri; Token=options.Token; UserAgent=options.UserAgent
          Organization="FS-GG"; ProjectNumber=2; ExpectedProjectNodeId=projectNodeId }

    let exactRepo body =
        parse body (fun root ->
            let id = number "id" root
            let node = str "node_id" root
            let name = str "full_name" root
            let privateValue = boolean "private" root
            let description = str "description" root
            if id <> repoId || node <> repoNodeId || name <> repoName
               || not privateValue || description <> repoDescription then
                refuse MigrationSandboxProviderFailure.ForeignIdentity
            id, node, name, privateValue, description)

    let validIssuePageRequest index (uri: Uri) =
        let parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun entry -> entry.Split('=', 2))
        let values = parts |> Array.filter (fun entry -> entry.Length = 2) |> Array.map (fun entry -> entry.[0], entry.[1])
        let map = values |> Map.ofArray
        uri.Scheme = Uri.UriSchemeHttps && uri.Authority = options.ApiBase.Authority
        && (uri.AbsolutePath = issueStart.AbsolutePath
            || (index > 0 && uri.AbsolutePath = $"/repositories/{repoId}/issues"))
        && parts.Length = values.Length && map.Count = values.Length
        && Map.tryFind "state" map = Some "all" && Map.tryFind "per_page" map = Some "100"
        && (map |> Map.forall (fun key value ->
            key = "state" || key = "per_page"
            || (index > 0 && (key = "page" || key = "after") && not (String.IsNullOrWhiteSpace value))))
        && (index > 0 || uri.AbsoluteUri = issueStart.AbsoluteUri)

    let issueRequestAllowed request =
        match request with
        | Rest value when value.Method = Get && value.Body.IsNone ->
            value.Uri = repositoryUri || validIssuePageRequest 1 value.Uri
        | _ -> false

    let projectRequestAllowed request =
        match request with
        | GraphQL value ->
            value.Uri = options.GraphQLUri
            && value.Document = MigrationGitHubRead.projectItemsQuery 2
            && Map.tryFind "owner" value.Variables = Some "FS-GG"
            && (value.Variables |> Map.forall (fun key value ->
                key = "owner" || (key = "after" && not (String.IsNullOrWhiteSpace value))))
        | _ -> false

    member _.ReadScopeIdentity() =
        if not validOptions then Error MigrationSandboxProviderFailure.InvalidOptions
        else
            graph viewerQuery Map.empty
            |> Result.bind (fun viewer ->
                graphRoot viewer.Body (fun data ->
                    let actor = prop "viewer" data
                    let login = str "login" actor
                    let id = number "databaseId" actor
                    if login <> "fs-gg-cross-repo-dispatch[bot]" || id <> 297630107L then
                        refuse MigrationSandboxProviderFailure.ForeignIdentity
                    login, id)
                |> Result.map (fun identity ->
                    identity, proof ($"graphql:viewer:{sha viewerQuery}") viewer.Body None))
            |> Result.bind (fun ((login, actorId), viewerProof) ->
                rest repositoryUri
                |> Result.bind (fun repository ->
                    exactRepo repository.Body
                    |> Result.map (fun repo -> login, actorId, viewerProof, repo,
                                                proof repositoryUri.AbsoluteUri repository.Body None)))
            |> Result.bind (fun (login, actorId, viewerProof, repo, repositoryProof) ->
                graph projectQuery (Map.ofList [ "owner", "FS-GG" ])
                |> Result.bind (fun project ->
                    graphRoot project.Body (fun data ->
                        let org = prop "organization" data
                        let orgLogin = str "login" org
                        let value = prop "projectV2" org
                        let id = str "id" value
                        let number = number "number" value
                        let title = str "title" value
                        let publicValue = boolean "public" value
                        let closed = boolean "closed" value
                        if orgLogin <> "FS-GG" || id <> projectNodeId || number <> 2L
                           || title <> projectPurpose || publicValue || closed then
                            refuse MigrationSandboxProviderFailure.ForeignIdentity
                        orgLogin, int number, id, title, publicValue, closed)
                    |> Result.map (fun p ->
                        login, actorId, viewerProof, repo, repositoryProof, p,
                        proof ($"graphql:project-2:{sha projectQuery}:owner=FS-GG") project.Body None)))
            |> Result.bind (fun (login, actorId, viewerProof, repo, repositoryProof,
                                 (org, projectNumber, projectId, title, publicValue, closed), projectProof) ->
                rest installationUri
                |> Result.bind (fun installation ->
                    let next = header "link" installation.Headers |> Option.defaultValue ""
                    match Transport.tryNextLink next with
                    | Error _ -> Error MigrationSandboxProviderFailure.PaginationRefused
                    | Ok(Some _) -> Error MigrationSandboxProviderFailure.PaginationRefused
                    | Ok None ->
                        parse installation.Body (fun root ->
                            let total = number "total_count" root
                            let repositories = array "repositories" root
                            if total <> 1L || repositories.Length <> 1 then
                                refuse MigrationSandboxProviderFailure.ForeignIdentity
                            let row = repositories.Head
                            if number "id" row <> repoId || str "node_id" row <> repoNodeId
                               || str "full_name" row <> repoName then
                                refuse MigrationSandboxProviderFailure.ForeignIdentity
                            let (id, node, name, privateRepo, description) = repo
                            { TokenSha256=sha options.Token
                              ActorLogin=login; ActorDatabaseId=actorId; RepositoryId=id
                              RepositoryNodeId=node; RepositoryFullName=name
                              RepositoryPrivate=privateRepo; RepositoryDescription=description
                              ProjectOrganization=org; ProjectNumber=projectNumber; ProjectNodeId=projectId
                              ProjectTitle=title; ProjectPrivate=not publicValue; ProjectClosed=closed
                              TokenRepositorySelectionComplete=true
                              Proofs=[ viewerProof; repositoryProof; projectProof
                                       proof installationUri.AbsoluteUri installation.Body None ] })))

    member this.ObserveScope() : Result<MigrationSandboxScopeObservation, MigrationSandboxProviderFailure> =
        this.ReadScopeIdentity()
        |> Result.bind (fun _ -> Error MigrationSandboxProviderFailure.GrantUnavailable)

    member _.ReadFixture(nonce) =
        let validNonce =
            if String.IsNullOrWhiteSpace nonce || nonce.Length > 160 then false
            else
                match nonce.Split('-') with
                | [| run; attempt; candidate |] ->
                    let mutable runId = 0L
                    let mutable attemptId = 0
                    Int64.TryParse(run, &runId) && runId > 0L
                    && Int32.TryParse(attempt, &attemptId) && attemptId > 0
                    && candidate.Length = 40
                    && (candidate |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                    && nonce = $"{runId}-{attemptId}-{candidate}"
                | _ -> false
        if not validOptions then Error MigrationSandboxProviderFailure.InvalidOptions
        elif not validNonce then
            Error MigrationSandboxProviderFailure.InvalidNonce
        else
            let issueCapture = Capture(transport, issueRequestAllowed)
            MigrationGitHubRead.readIssues issueOptions issueCapture
            |> Result.mapError mapReadFailure
            |> Result.bind (fun issues ->
                if not issues.Terminal || issues.RepositoryId <> repoId then
                    Error MigrationSandboxProviderFailure.PopulationDrift
                else
                    let calls = issueCapture.Calls
                    match calls with
                    | (Rest repositoryRequest, Response repositoryReply) :: pageCalls
                        when repositoryRequest.Uri = repositoryUri && repositoryRequest.Method = Get
                             && repositoryRequest.Body.IsNone && pageCalls.Length = issues.PageCount ->
                        exactRepo repositoryReply.Body
                        |> Result.bind (fun _ ->
                            let pageProofs =
                                pageCalls |> List.mapi (fun index (request, outcome) ->
                                    match request, response outcome with
                                    | Rest value, Ok reply when validIssuePageRequest index value.Uri ->
                                        match Transport.tryNextLink (header "link" reply.Headers |> Option.defaultValue "") with
                                        | Ok next ->
                                            let nextUri = next |> Option.map _.AbsoluteUri
                                            if issues.Pages.[index].RequestedUri <> value.Uri.AbsoluteUri
                                               || issues.Pages.[index].PayloadSha256 <> sha reply.Body
                                               || issues.Pages.[index].NextUri <> nextUri then
                                                Error MigrationSandboxProviderFailure.PopulationDrift
                                            else Ok(proof value.Uri.AbsoluteUri reply.Body nextUri, reply.Body)
                                        | Error _ -> Error MigrationSandboxProviderFailure.PaginationRefused
                                    | _ -> Error MigrationSandboxProviderFailure.TransportRefused)
                            match pageProofs |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                            | Some failure -> Error failure
                            | None ->
                                let pages = pageProofs |> List.choose (function Ok value -> Some value | _ -> None)
                                Ok(issues, proof repositoryUri.AbsoluteUri repositoryReply.Body None, pages))
                    | _ -> Error MigrationSandboxProviderFailure.PopulationDrift)
            |> Result.bind (fun (issues, repoProof, issuePages) ->
                try
                    let rawRows =
                        issuePages |> List.collect (fun (_, raw) ->
                            match parse raw (fun root ->
                                if root.ValueKind <> JsonValueKind.Array then
                                    refuse MigrationSandboxProviderFailure.MalformedResponse
                                root.EnumerateArray()
                                |> Seq.map (fun item ->
                                    let id = number "id" item
                                    let node = str "node_id" item
                                    let issueNumber = number "number" item
                                    let mutable marker = Unchecked.defaultof<JsonElement>
                                    let pr = item.TryGetProperty("pull_request", &marker)
                                    if pr && marker.ValueKind <> JsonValueKind.Object then
                                        refuse MigrationSandboxProviderFailure.MalformedResponse
                                    id, node, issueNumber, pr, item.GetRawText())
                                |> Seq.toList) with
                            | Ok values -> values
                            | Error failure -> refuse failure)
                    let distinctIds = rawRows |> List.map (fun (id, _, _, _, _) -> id) |> Set.ofList
                    if distinctIds.Count <> rawRows.Length then refuse MigrationSandboxProviderFailure.PopulationDrift
                    let rawIssues = rawRows |> List.filter (fun (_, _, _, pr, _) -> not pr)
                    if rawRows.Length - rawIssues.Length <> issues.PullRequestCount
                       || rawIssues.Length <> issues.Issues.Length then
                        refuse MigrationSandboxProviderFailure.PopulationDrift
                    for record in issues.Issues do
                        if not (rawIssues |> List.exists (fun (id, node, n, _, raw) ->
                            id = record.DatabaseId && node = record.NodeId
                            && n = int64 record.Number && sha raw = record.PayloadSha256)) then
                            refuse MigrationSandboxProviderFailure.PopulationDrift
                    let fixtureRows = rawIssues |> List.filter (fun (_, _, n, _, _) -> n = 1L)
                    if fixtureRows.Length <> 1 then refuse MigrationSandboxProviderFailure.PopulationDrift
                    let unowned =
                        rawRows |> List.filter (fun (_, _, n, pr, _) -> pr || n <> 1L)
                        |> List.sortBy (fun (id, _, _, _, _) -> id)
                        |> List.map (fun (id, _, _, _, raw) -> $"{id}:{sha raw}") |> digest
                    let nonceMarker = $"[fsgg:gs2-09-7:{nonce}]"
                    let nonceNodes =
                        rawIssues |> List.choose (fun (_, node, _, _, raw) ->
                            match parse raw (fun item -> str "title" item, nullableString "body" item) with
                            | Error failure -> refuse failure
                            | Ok(title, body) when title.Contains(nonceMarker, StringComparison.Ordinal)
                                                   || (body |> Option.exists (fun value ->
                                                       value.Contains(nonceMarker, StringComparison.Ordinal))) -> Some node
                            | Ok _ -> None) |> List.sort
                    Ok(fixtureRows.Head, unowned, nonceNodes, repoProof, issuePages |> List.map fst)
                with Refused failure -> Error failure)
            |> Result.bind (fun ((fixtureId, fixtureNode, _, _, fixtureRaw), unownedIssues,
                                 nonceNodes, repoProof, issueProofs) ->
                rest issueUri
                |> Result.bind (fun exact ->
                    match exact.ETag with
                    | None -> Error MigrationSandboxProviderFailure.MissingRevision
                    | Some revision when String.IsNullOrWhiteSpace revision ->
                        Error MigrationSandboxProviderFailure.MissingRevision
                    | Some revision ->
                        let parseSnapshot raw =
                            parse raw (fun root ->
                                let id = number "id" root
                                let node = str "node_id" root
                                let n = number "number" root
                                let title = str "title" root
                                let body = nullableString "body" root
                                let state = str "state" root
                                let labels = array "labels" root |> List.map (str "name") |> List.sort
                                if labels.Length <> (labels |> Set.ofList |> Set.count) then
                                    refuse MigrationSandboxProviderFailure.PopulationDrift
                                let updated = str "updated_at" root
                                if id <> fixtureId || node <> fixtureNode || n <> 1L
                                   || (state <> "open" && state <> "closed") then
                                    refuse MigrationSandboxProviderFailure.ForeignIdentity
                                title, body, state, labels, updated)
                        match parseSnapshot fixtureRaw, parseSnapshot exact.Body with
                        | Ok listValue, Ok exactValue when listValue = exactValue ->
                            let title, body, state, labels, _ = exactValue
                            let issue =
                                { RepositoryId=repoId; Number=1; DatabaseId=fixtureId; NodeId=fixtureNode
                                  Title=title; Body=body; State=state; Labels=labels; Revision=revision }
                            Ok(issue, unownedIssues, nonceNodes, repoProof, issueProofs,
                               proof issueUri.AbsoluteUri exact.Body None)
                        | Ok _, Ok _ -> Error MigrationSandboxProviderFailure.PopulationDrift
                        | Error failure, _ | _, Error failure -> Error failure))
            |> Result.bind (fun (issue, unownedIssues, nonceNodes, repoProof, issueProofs, exactProof) ->
                let projectCapture = Capture(transport, projectRequestAllowed)
                MigrationGitHubRead.readProjectItems projectOptions projectCapture
                |> Result.mapError mapReadFailure
                |> Result.bind (fun items ->
                    if not items.Terminal || items.ProjectNodeId <> projectNodeId
                       || items.PageCount <> projectCapture.Calls.Length then
                        Error MigrationSandboxProviderFailure.PopulationDrift
                    else
                        let mutable previousCursor = None
                        let pageProofs =
                            projectCapture.Calls |> List.map (fun (request, outcome) ->
                                match request, response outcome with
                                | GraphQL value, Ok reply when projectRequestAllowed request ->
                                    let observedCursor = Map.tryFind "after" value.Variables
                                    if observedCursor <> previousCursor then
                                        Error MigrationSandboxProviderFailure.PaginationRefused
                                    else
                                        graphRoot reply.Body (fun data ->
                                            let project = prop "projectV2" (prop "organization" data)
                                            if str "id" project <> projectNodeId || number "number" project <> 2L then
                                                refuse MigrationSandboxProviderFailure.ForeignIdentity
                                            let connection = prop "items" project
                                            let nodes = array "nodes" connection
                                            let page = prop "pageInfo" connection
                                            let next = if boolean "hasNextPage" page then
                                                           Some(str "endCursor" page)
                                                       else
                                                           match prop "endCursor" page with
                                                           | item when item.ValueKind = JsonValueKind.Null -> None
                                                           | item when item.ValueKind = JsonValueKind.String -> None
                                                           | _ -> refuse MigrationSandboxProviderFailure.MalformedResponse
                                            previousCursor <- next
                                            let rawItems = nodes |> List.map (fun item -> str "id" item, sha (item.GetRawText()))
                                            let position = observedCursor |> Option.defaultValue "<first>"
                                            let document = sha (MigrationGitHubRead.projectItemsQuery 2)
                                            proof ($"graphql:project-items:{document}:owner=FS-GG:after={position}")
                                                  reply.Body next, rawItems)
                                | _ -> Error MigrationSandboxProviderFailure.TransportRefused)
                        match pageProofs |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                        | Some failure -> Error failure
                        | None ->
                            let pages = pageProofs |> List.choose (function Ok value -> Some value | _ -> None)
                            if previousCursor.IsSome then Error MigrationSandboxProviderFailure.PaginationRefused
                            else
                                let rawItems = pages |> List.collect snd
                                let typed = items.Items |> List.map (fun item -> item.ItemNodeId, item.PayloadSha256)
                                if rawItems.Length <> items.TotalCount
                                   || (List.sort rawItems <> List.sort typed) then
                                    Error MigrationSandboxProviderFailure.PopulationDrift
                                else
                                    let conflictingItem =
                                        items.Items |> List.exists (fun item ->
                                            match item.Content with
                                            | MigrationProjectContent.Issue(id, repository, number) ->
                                                (repository = repoId && number = 1 && id <> issue.NodeId)
                                                || (id = issue.NodeId && (repository <> repoId || number <> 1))
                                            | _ -> false)
                                    if conflictingItem then
                                        Error MigrationSandboxProviderFailure.PopulationDrift
                                    else
                                        let memberships =
                                            items.Items |> List.choose (fun item ->
                                                match item.Content with
                                                | MigrationProjectContent.Issue(id, repository, number)
                                                    when id = issue.NodeId && repository = repoId && number = 1 ->
                                                    Some item.ItemNodeId
                                                | _ -> None) |> List.sort
                                        let unownedProject =
                                            items.Items |> List.filter (fun item -> not (List.contains item.ItemNodeId memberships))
                                            |> List.map (fun item -> $"{item.ItemNodeId}:{item.PayloadSha256}") |> digest
                                        let observation =
                                            { Complete=true; ProjectNodeId=projectNodeId; Issue=issue
                                              ProjectItemIdsForIssue=memberships; NonceIssueNodeIds=nonceNodes
                                              UnownedIssuesSha256=unownedIssues
                                              UnownedProjectItemsSha256=unownedProject }
                                        Ok { TokenSha256=sha options.Token; Observation=observation
                                             RepositoryIdentity=repoProof
                                             IssuePages=issueProofs; ProjectPages=pages |> List.map fst
                                             ExactIssue=exactProof }))

    member this.ObserveFixture(nonce) =
        this.ReadFixture(nonce) |> Result.map _.Observation
