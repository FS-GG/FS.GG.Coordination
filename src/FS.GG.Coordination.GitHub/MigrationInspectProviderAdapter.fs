namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts

type MigrationInspectProviderOptions =
    { Cohort: GitHubMigrationCopyCohort
      Repository: MigrationGitHubReadOptions
      Project: MigrationProjectReadOptions }

type private CapturingTransport(inner: IMigrationGitHubReadTransport, allow: GitHubRequest -> bool) =
    let calls = ResizeArray<GitHubRequest * TransportOutcome>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            let outcome = if allow request then inner.Send request else NetworkFailure
            calls.Add(request, outcome)
            outcome

[<RequireQualifiedAccess>]
module MigrationInspectProviderAdapter =
    let internal allowedRequest (options: MigrationInspectProviderOptions) authority request =
        match authority, request with
        | "issues-open-and-relevant-closed", Rest value ->
            let repository =
                Uri(options.Repository.ApiBase,
                    $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}")
            let issuesPath = repository.AbsolutePath + "/issues"
            value.Method = Get && value.Body.IsNone
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && value.Uri.Authority = repository.Authority
            && (value.Uri.AbsoluteUri = repository.AbsoluteUri
                || value.Uri.AbsolutePath = issuesPath
                || value.Uri.AbsolutePath = $"/repositories/{options.Repository.ExpectedRepositoryId}/issues")
        | "project-items", GraphQL value ->
            value.Uri = options.Project.GraphQLUri
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && value.Document = MigrationGitHubRead.projectItemsQuery options.Project.ProjectNumber
            && Map.tryFind "owner" value.Variables = Some options.Project.Organization
            && (value.Variables |> Map.toList
                |> List.forall (fun (key, v) -> key = "owner" || (key = "after" && not (String.IsNullOrWhiteSpace v))))
        | _ -> false

    /// A provider-call fence that refuses a write-shaped request before the inner transport sees it.
    let guardReadTransport options authority (inner: IMigrationGitHubReadTransport) =
        CapturingTransport(inner, allowedRequest options authority) :> IMigrationGitHubReadTransport

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digestParts values = values |> List.map frame |> String.concat "" |> sha

    let private responseBody = function
        | Response reply when reply.StatusCode = 200 -> Ok reply.Body
        | _ -> Error "provider-response"

    let private repositoryBinding options =
        let expected = $"{options.Repository.Owner}/{options.Repository.Repository}"
        options.Cohort.Isolated
        && (options.Cohort.Repositories
            |> List.exists (fun repository ->
                repository.Id = options.Repository.ExpectedRepositoryId
                && repository.FullName = expected))

    let private projectBinding options =
        options.Cohort.Isolated
        && options.Cohort.ProjectOrganization = options.Project.Organization
        && options.Cohort.ProjectNumber = options.Project.ProjectNumber
        && options.Cohort.ProjectNodeId = options.Project.ExpectedProjectNodeId

    let private subject identity revision payload =
        { Identity=identity; Revision=revision; PayloadSha256=sha payload }

    let private issuePageScope (options: MigrationInspectProviderOptions) index (uri: Uri) =
        let start =
            Uri(options.Repository.ApiBase,
                $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}/issues?state=all&per_page=100")
        let allowedPath = $"/repositories/{options.Repository.ExpectedRepositoryId}/issues"
        let parameters =
            uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun part -> part.Split('=', 2))
        let keys = parameters |> Array.map (fun parts -> parts.[0]) |> Array.toList
        let allowed = if index = 0 then Set.ofList [ "state"; "per_page" ]
                      else Set.ofList [ "state"; "per_page"; "page"; "after" ]
        uri.Scheme = Uri.UriSchemeHttps && uri.Authority = start.Authority
        && (if index = 0 then uri.AbsoluteUri = start.AbsoluteUri
            else uri.AbsolutePath = start.AbsolutePath || uri.AbsolutePath = allowedPath)
        && (parameters |> Array.forall (fun parts -> parts.Length = 2))
        && keys.Length = (keys |> Set.ofList |> Set.count)
        && (keys |> List.forall (fun key -> Set.contains key allowed))
        && (parameters |> Array.exists (fun parts -> parts.[0] = "state" && parts.[1] = "all"))
        && (parameters |> Array.exists (fun parts -> parts.[0] = "per_page" && parts.[1] = "100"))

    let private parseIssuePages repositoryId (raw: string list) =
        try
            raw
            |> List.map (fun body ->
                use document = JsonDocument.Parse body
                if document.RootElement.ValueKind <> JsonValueKind.Array then failwith "issue-page-array"
                let items = document.RootElement.EnumerateArray() |> Seq.toList
                let isPullRequest (item: JsonElement) =
                    let mutable marker = Unchecked.defaultof<JsonElement>
                    if item.TryGetProperty("pull_request", &marker) then
                        if marker.ValueKind <> JsonValueKind.Object then failwith "pr-marker"
                        true
                    else false
                let pullRequestCount = items |> List.filter isPullRequest |> List.length
                let issues =
                    items |> List.choose (fun item ->
                      if isPullRequest item then None else
                        let number = item.GetProperty("number").GetInt32()
                        let databaseId = item.GetProperty("id").GetInt64()
                        let nodeId = item.GetProperty("node_id").GetString()
                        let state = item.GetProperty("state").GetString()
                        let updated = DateTimeOffset.Parse(item.GetProperty("updated_at").GetString())
                        let payload = item.GetRawText()
                        let record = number, databaseId, nodeId, state, updated, payload
                        Some(record,
                             subject $"repository:{repositoryId}:issue:{number}"
                                 (updated.ToUniversalTime().ToString("O")) payload))
                issues, pullRequestCount)
            |> Ok
        with _ -> Error "raw-issue-parse"

    let bindIssues (options: MigrationInspectProviderOptions) (population: MigrationIssuePopulation)
                   (captures: (GitHubRequest * TransportOutcome) list) =
        if not (repositoryBinding options) || population.RepositoryId <> options.Repository.ExpectedRepositoryId
           || not population.Terminal then Error "issue-cohort"
        else
            let repositoryUri =
                Uri(options.Repository.ApiBase,
                    $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}")
            let validRepositoryCall =
                match captures with
                | (Rest request, outcome) :: _ when request.Method = Get && request.Uri = repositoryUri ->
                    match responseBody outcome with
                    | Error _ -> false
                    | Ok body ->
                        try
                            use document = JsonDocument.Parse body
                            document.RootElement.GetProperty("id").GetInt64() = options.Repository.ExpectedRepositoryId
                            && document.RootElement.GetProperty("full_name").GetString()
                               = $"{options.Repository.Owner}/{options.Repository.Repository}"
                        with _ -> false
                | _ -> false
            let calls =
                captures |> List.skip (min 1 captures.Length) |> List.choose (function
                    | Rest request, outcome when request.Method = Get -> Some(request, outcome)
                    | _ -> None)
            if not validRepositoryCall || captures.Length <> population.PageCount + 1
               || calls.Length <> population.PageCount then
                Error "issue-capture-shape"
            elif calls.Length <> population.Pages.Length then Error "issue-page-count"
            elif calls |> List.mapi (fun index (request, _) ->
                    request.Method = Get && issuePageScope options index request.Uri
                    && request.Uri.AbsoluteUri = population.Pages.[index].RequestedUri)
                 |> List.contains false then Error "issue-request-scope"
            else
                let bodies = calls |> List.map (snd >> responseBody)
                match bodies |> List.tryPick (function Error reason -> Some reason | _ -> None) with
                | Some reason -> Error reason
                | None ->
                    let raw = bodies |> List.choose (function Ok body -> Some body | _ -> None)
                    if List.zip population.Pages raw
                       |> List.exists (fun (page, body) -> page.PayloadSha256 <> sha body) then
                        Error "issue-raw-page"
                    elif population.Pages
                         |> List.mapi (fun index page ->
                             page.NextUri = (if index + 1 < population.Pages.Length
                                             then Some population.Pages.[index + 1].RequestedUri else None))
                         |> List.contains false then Error "issue-page-chain"
                    else
                        match parseIssuePages population.RepositoryId raw with
                        | Error reason -> Error reason
                        | Ok parsed ->
                            let rawRecords = parsed |> List.collect (fst >> List.map fst)
                                                    |> List.sortBy (fun (number, _, _, _, _, _) -> number)
                            let rawPullRequestCount = parsed |> List.sumBy snd
                            let typedRecords =
                                population.Issues
                                |> List.map (fun item ->
                                    item.Number, item.DatabaseId, item.NodeId, item.State,
                                    item.UpdatedAt, item.PayloadJson)
                            if rawPullRequestCount <> population.PullRequestCount then Error "issue-pr-count"
                            elif rawRecords <> typedRecords then Error "issue-raw-typed-mismatch"
                            else
                                let pages =
                                    List.zip3 population.Pages raw parsed
                                    |> List.map (fun (proof, body, (rows, _)) ->
                                        { RequestedUri=proof.RequestedUri
                                          RequestIdentitySha256=sha proof.RequestedUri
                                          RawBody=body; PayloadSha256=sha body
                                          NextRequestIdentitySha256=proof.NextUri |> Option.map sha
                                          Subjects=rows |> List.map snd })
                                let subjects = pages |> List.collect _.Subjects |> List.sortBy _.Identity
                                Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                                     ScopeVerified=true; SubjectsParsedFromRaw=true
                                     Read={ Authority="issues-open-and-relevant-closed"
                                            ObservedAt=DateTimeOffset.UtcNow
                                            PageCount=pages.Length; ItemCount=subjects.Length
                                            Terminal=true; NextCursor=None
                                            HighWaterMark=digestParts (pages |> List.map _.PayloadSha256)
                                            Subjects=subjects }
                                     Pages=pages }

    let private graphQLIdentity (request: GraphQLRequest) =
        [ request.Uri.AbsoluteUri; request.Document
          yield! request.Variables |> Map.toList |> List.collect (fun (key, value) -> [ key; value ]) ]
        |> digestParts

    let private parseProjectPage (options: MigrationInspectProviderOptions) (body: string) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement
            let mutable errors = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("errors", &errors) then failwith "partial-graphql-errors"
            let project = root.GetProperty("data").GetProperty("organization").GetProperty("projectV2")
            let id = project.GetProperty("id").GetString()
            let number = project.GetProperty("number").GetInt32()
            if id <> options.Project.ExpectedProjectNodeId || number <> options.Project.ProjectNumber then
                failwith "project-identity"
            let connection = project.GetProperty("items")
            let total = connection.GetProperty("totalCount").GetInt32()
            let pageInfo = connection.GetProperty("pageInfo")
            let hasNext = pageInfo.GetProperty("hasNextPage").GetBoolean()
            let cursor = pageInfo.GetProperty("endCursor")
            let next = if hasNext then Some(cursor.GetString()) else None
            let rows =
                connection.GetProperty("nodes").EnumerateArray()
                |> Seq.map (fun item ->
                    let itemId = item.GetProperty("id").GetString()
                    let archived = item.GetProperty("isArchived").GetBoolean()
                    let updated = DateTimeOffset.Parse(item.GetProperty("updatedAt").GetString())
                    let content = item.GetProperty("content")
                    let kind = content.GetProperty("__typename").GetString()
                    let contentId = content.GetProperty("id").GetString()
                    let parsedContent =
                        if kind = "DraftIssue" then MigrationProjectContent.DraftIssue contentId
                        else
                            let repositoryId = content.GetProperty("repository").GetProperty("databaseId").GetInt64()
                            if not (options.Cohort.Repositories |> List.exists (fun repository -> repository.Id = repositoryId)) then
                                failwith "foreign-project-content"
                            let number = content.GetProperty("number").GetInt32()
                            if kind = "Issue" then MigrationProjectContent.Issue(contentId, repositoryId, number)
                            elif kind = "PullRequest" then MigrationProjectContent.PullRequest(contentId, repositoryId, number)
                            else failwith "project-content-kind"
                    let payload = item.GetRawText()
                    (itemId, archived, updated, parsedContent, payload),
                    subject $"project:{id}:item:{itemId}" (updated.ToUniversalTime().ToString("O")) payload)
                |> Seq.toList
            Ok(total, next, rows)
        with _ -> Error "raw-project-parse-or-scope"

    let bindProjectItems (options: MigrationInspectProviderOptions) (population: MigrationProjectItemPopulation)
                         (captures: (GitHubRequest * TransportOutcome) list) =
        if not (projectBinding options) || population.ProjectNodeId <> options.Project.ExpectedProjectNodeId
           || not population.Terminal then Error "project-cohort"
        else
            let calls =
                captures |> List.choose (function
                    | GraphQL request, outcome -> Some(request, outcome)
                    | _ -> None)
            if captures.Length <> population.PageCount || calls.Length <> population.PageCount then
                Error "project-page-count"
            elif calls |> List.exists (fun (request, _) ->
                    request.Uri <> options.Project.GraphQLUri
                    || request.Document <> MigrationGitHubRead.projectItemsQuery options.Project.ProjectNumber
                    || Map.tryFind "owner" request.Variables <> Some options.Project.Organization
                    || (request.Variables |> Map.toList |> List.exists (fun (key, _) -> key <> "owner" && key <> "after"))) then
                Error "project-request-scope"
            else
                let bodies = calls |> List.map (snd >> responseBody)
                match bodies |> List.tryPick (function Error reason -> Some reason | _ -> None) with
                | Some reason -> Error reason
                | None ->
                    let raw = bodies |> List.choose (function Ok body -> Some body | _ -> None)
                    let parsed = raw |> List.map (parseProjectPage options)
                    match parsed |> List.tryPick (function Error reason -> Some reason | _ -> None) with
                    | Some reason -> Error reason
                    | None ->
                        let pages = parsed |> List.choose (function Ok page -> Some page | _ -> None)
                        let correctCursors =
                            calls |> List.mapi (fun index (request, _) ->
                                let expected = if index = 0 then None else
                                                   let _, cursor, _ = pages.[index - 1] in cursor
                                Map.tryFind "after" request.Variables = expected)
                            |> List.forall id
                        let terminal = pages |> List.mapi (fun index (_, cursor, _) ->
                            if index + 1 < pages.Length then cursor.IsSome else cursor.IsNone) |> List.forall id
                        let sameTotals = pages |> List.forall (fun (total, _, _) -> total = population.TotalCount)
                        let rows = pages |> List.collect (fun (_, _, values) -> values)
                        let rawRecords = rows |> List.map fst |> List.sortBy (fun (id, _, _, _, _) -> id)
                        let typedRecords =
                            population.Items |> List.map (fun item ->
                                item.ItemNodeId, item.Archived, item.UpdatedAt, item.Content, item.PayloadJson)
                        if not correctCursors || not terminal then Error "project-page-chain"
                        elif not sameTotals || rows.Length <> population.TotalCount then Error "project-total"
                        elif rawRecords <> typedRecords then Error "project-raw-typed-mismatch"
                        else
                            let evidence =
                                List.zip3 calls raw pages
                                |> List.mapi (fun index ((request, _), body, (_, _, values)) ->
                                    { RequestedUri=request.Uri.AbsoluteUri
                                      RequestIdentitySha256=graphQLIdentity request
                                      RawBody=body; PayloadSha256=sha body
                                      NextRequestIdentitySha256=
                                        if index + 1 < calls.Length then
                                            Some(graphQLIdentity (fst calls.[index + 1])) else None
                                      Subjects=values |> List.map snd })
                            let subjects = evidence |> List.collect _.Subjects |> List.sortBy _.Identity
                            Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                                 ScopeVerified=true; SubjectsParsedFromRaw=true
                                 Read={ Authority="project-items"; ObservedAt=DateTimeOffset.UtcNow
                                        PageCount=evidence.Length; ItemCount=subjects.Length
                                        Terminal=true; NextCursor=None
                                        HighWaterMark=digestParts (evidence |> List.map _.PayloadSha256)
                                        Subjects=subjects }
                                 Pages=evidence }

type MigrationInspectProviderAdapter(options: MigrationInspectProviderOptions,
                                     transport: IMigrationGitHubReadTransport) =
    interface IGitHubMigrationInspectSource with
        member _.ReadAuthority(passOrdinal, authority) =
            if passOrdinal <> 1 && passOrdinal <> 2 then Error "invalid-pass"
            elif authority = "issues-open-and-relevant-closed" then
                let capture = CapturingTransport(transport, MigrationInspectProviderAdapter.allowedRequest options authority)
                MigrationGitHubRead.readIssues options.Repository capture
                |> Result.mapError (fun failure -> $"issue-read:{failure}")
                |> Result.bind (fun population ->
                    MigrationInspectProviderAdapter.bindIssues options population capture.Calls)
            elif authority = "project-items" then
                let capture = CapturingTransport(transport, MigrationInspectProviderAdapter.allowedRequest options authority)
                MigrationGitHubRead.readProjectItems options.Project capture
                |> Result.mapError (fun failure -> $"project-read:{failure}")
                |> Result.bind (fun population ->
                    MigrationInspectProviderAdapter.bindProjectItems options population capture.Calls)
            else Error $"authority-adapter-unavailable:{authority}"
