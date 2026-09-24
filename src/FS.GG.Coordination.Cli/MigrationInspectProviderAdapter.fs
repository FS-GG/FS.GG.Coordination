namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub
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
        | ("project-items" | "project-fields" | "project-values"), GraphQL value ->
            let document =
                match authority with
                | "project-fields" -> MigrationGitHubRead.projectFieldsQuery options.Project.ProjectNumber
                | "project-values" -> MigrationGitHubRead.projectValuesQuery options.Project.ProjectNumber
                | _ -> MigrationGitHubRead.projectItemsQuery options.Project.ProjectNumber
            let ownerKey = if authority = "project-values" then "organization" else "owner"
            value.Uri = options.Project.GraphQLUri
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && value.Document = document
            && Map.tryFind ownerKey value.Variables = Some options.Project.Organization
            && (value.Variables |> Map.toList
                |> List.forall (fun (key, v) -> key = ownerKey || (key = "after" && not (String.IsNullOrWhiteSpace v))))
        | "hierarchy-and-dependencies", GraphQL value ->
            let exactInitial =
                value.Document = MigrationGitHubRead.nativeRelationsQuery
                && value.Variables.Count = 1
                && (Map.tryFind "id" value.Variables |> Option.exists (String.IsNullOrWhiteSpace >> not))
            let exactContinuation =
                [ "subIssues"; "blockedBy"; "blocking" ]
                |> List.exists (fun connection ->
                    value.Document = MigrationGitHubRead.relationContinuationQuery connection)
                && value.Variables.Count = 2
                && (Map.tryFind "id" value.Variables |> Option.exists (String.IsNullOrWhiteSpace >> not))
                && (Map.tryFind "after" value.Variables |> Option.exists (String.IsNullOrWhiteSpace >> not))
            value.Uri = options.Repository.GraphQLUri
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && (exactInitial || exactContinuation)
        | _ -> false

    /// A provider-call fence that refuses a write-shaped request before the inner transport sees it.
    let guardReadTransport options authority (inner: IMigrationGitHubReadTransport) =
        CapturingTransport(inner, allowedRequest options authority) :> IMigrationGitHubReadTransport

    let internal allowedRelationIssueRequest options issueIds request =
        allowedRequest options "hierarchy-and-dependencies" request
        && (match request with
            | GraphQL value ->
                Map.tryFind "id" value.Variables |> Option.exists (fun id -> Set.contains id issueIds)
            | _ -> false)

    /// Fences relation reads to the exact issue census before dispatch.
    let guardRelationTransport options issueNodeIds (inner: IMigrationGitHubReadTransport) =
        CapturingTransport(inner, allowedRelationIssueRequest options (Set.ofList issueNodeIds))
        :> IMigrationGitHubReadTransport

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
            if not validRepositoryCall
               || (captures |> List.exists (fst >> allowedRequest options "issues-open-and-relevant-closed" >> not))
               || captures.Length <> population.PageCount + 1
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
            elif captures |> List.exists (fst >> allowedRequest options "project-items" >> not) then
                Error "project-request-scope"
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

    let private parseProjectConnection (options: MigrationInspectProviderOptions) (connectionName: string) (body: string) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement
            let mutable errors = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("errors", &errors) then failwith "graphql-errors"
            let project = root.GetProperty("data").GetProperty("organization").GetProperty("projectV2")
            if project.GetProperty("id").GetString() <> options.Project.ExpectedProjectNodeId
               || project.GetProperty("number").GetInt32() <> options.Project.ProjectNumber then
                failwith "project-identity"
            let connection = project.GetProperty(connectionName)
            let total = connection.GetProperty("totalCount").GetInt32()
            if total < 0 then failwith "negative-total"
            let pageInfo = connection.GetProperty("pageInfo")
            let next =
                if pageInfo.GetProperty("hasNextPage").GetBoolean() then
                    let cursor = pageInfo.GetProperty("endCursor").GetString()
                    if String.IsNullOrWhiteSpace cursor then failwith "missing-cursor"
                    Some cursor
                else None
            let nodes = connection.GetProperty("nodes").EnumerateArray() |> Seq.map _.GetRawText() |> Seq.toList
            Ok(total, next, nodes)
        with _ -> Error "project-raw-page-or-scope"

    let private bindProjectConnection options authority connectionName projectId pageCount totalCount
                                      (typed: 'a list) (key: 'a -> string)
                                      (parseNode: string -> Result<'a * GitHubDiscoverySubject, string>)
                                      (captures: (GitHubRequest * TransportOutcome) list) =
        if not (projectBinding options) || projectId <> options.Project.ExpectedProjectNodeId
           || pageCount < 1 || captures.Length <> pageCount then Error "project-capture-shape"
        elif captures |> List.exists (fst >> allowedRequest options authority >> not) then
            Error "project-request-scope"
        else
            let calls = captures |> List.choose (function GraphQL request, outcome -> Some(request, outcome) | _ -> None)
            if calls.Length <> pageCount then Error "project-capture-shape"
            else
                let bodies = calls |> List.map (snd >> responseBody)
                match bodies |> List.tryPick (function Error reason -> Some reason | _ -> None) with
                | Some reason -> Error reason
                | None ->
                    let raw = bodies |> List.choose (function Ok body -> Some body | _ -> None)
                    let parsed = raw |> List.map (parseProjectConnection options connectionName)
                    match parsed |> List.tryPick (function Error reason -> Some reason | _ -> None) with
                    | Some reason -> Error reason
                    | None ->
                        let pages = parsed |> List.choose (function Ok page -> Some page | _ -> None)
                        let linked =
                            calls |> List.mapi (fun index (request, _) ->
                                let previous = if index = 0 then None else let _, next, _ = pages.[index - 1] in next
                                Map.tryFind "after" request.Variables = previous)
                            |> List.forall id
                        let terminal =
                            pages |> List.mapi (fun index (_, next, _) ->
                                if index + 1 < pages.Length then next.IsSome else next.IsNone)
                            |> List.forall id
                        let sameTotal = pages |> List.forall (fun (total, _, _) -> total = totalCount)
                        if not linked || not terminal then Error "project-page-chain"
                        elif not sameTotal || (pages |> List.sumBy (fun (_, _, nodes) -> nodes.Length)) <> totalCount then
                            Error "project-total"
                        else
                            let perPage =
                                pages |> List.map (fun (_, _, nodes) -> nodes |> List.map parseNode)
                            let all = perPage |> List.collect id
                            match all |> List.tryPick (function Error reason -> Some reason | _ -> None) with
                            | Some reason -> Error reason
                            | None ->
                                let rows = all |> List.choose (function Ok row -> Some row | _ -> None)
                                let rawTyped = rows |> List.map fst |> List.sortBy key
                                let keys = rawTyped |> List.map key
                                if (keys |> Set.ofList |> Set.count) <> keys.Length then
                                    Error "project-duplicate-identity"
                                elif rawTyped <> typed then Error "project-raw-typed-mismatch"
                                else
                                    let evidence =
                                        List.zip3 calls raw perPage
                                        |> List.mapi (fun index ((request, _), body, parsedNodes) ->
                                            { RequestedUri=request.Uri.AbsoluteUri
                                              RequestIdentitySha256=graphQLIdentity request
                                              RawBody=body; PayloadSha256=sha body
                                              NextRequestIdentitySha256=
                                                if index + 1 < calls.Length then Some(graphQLIdentity (fst calls.[index + 1]))
                                                else None
                                              Subjects=parsedNodes |> List.choose (function Ok (_, item) -> Some item | _ -> None) })
                                    let subjects = evidence |> List.collect _.Subjects |> List.sortBy _.Identity
                                    Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                                         ScopeVerified=true; SubjectsParsedFromRaw=true
                                         Read={ Authority=authority; ObservedAt=DateTimeOffset.UtcNow
                                                PageCount=evidence.Length; ItemCount=subjects.Length
                                                Terminal=true; NextCursor=None
                                                HighWaterMark=digestParts (evidence |> List.map _.PayloadSha256)
                                                Subjects=subjects }
                                         Pages=evidence }

    let private parseFieldNode (options: MigrationInspectProviderOptions) (raw: string) =
        try
            use document = JsonDocument.Parse raw
            let item = document.RootElement
            let id = item.GetProperty("id").GetString()
            let name = item.GetProperty("name").GetString()
            let dataType = item.GetProperty("dataType").GetString()
            if List.exists String.IsNullOrWhiteSpace [ id; name; dataType ] then failwith "field-identity"
            let readOptions (nameProperty: string) (values: JsonElement) : MigrationProjectFieldOption list =
                values.EnumerateArray()
                |> Seq.map (fun value ->
                    ({ Id=value.GetProperty("id").GetString(); Name=value.GetProperty(nameProperty).GetString() }
                     : MigrationProjectFieldOption))
                |> Seq.toList
            let kind, optionsList =
                match item.GetProperty("__typename").GetString() with
                | "ProjectV2Field" -> MigrationProjectFieldKind.BuiltIn, []
                | "ProjectV2SingleSelectField" ->
                    MigrationProjectFieldKind.SingleSelect, readOptions "name" (item.GetProperty("options"))
                | "ProjectV2MultiSelectField" ->
                    MigrationProjectFieldKind.MultiSelect, readOptions "name" (item.GetProperty("multiSelectOptions"))
                | "ProjectV2IterationField" ->
                    let configuration = item.GetProperty("configuration")
                    MigrationProjectFieldKind.Iteration,
                    (readOptions "title" (configuration.GetProperty("iterations"))
                     @ readOptions "title" (configuration.GetProperty("completedIterations")))
                | _ -> failwith "unsupported-field-kind"
            let allowedType =
                match kind with
                | MigrationProjectFieldKind.SingleSelect -> dataType = "SINGLE_SELECT"
                | MigrationProjectFieldKind.MultiSelect -> dataType = "MULTI_SELECT"
                | MigrationProjectFieldKind.Iteration -> dataType = "ITERATION"
                | MigrationProjectFieldKind.BuiltIn ->
                    Set.contains dataType
                        (Set.ofList [ "ASSIGNEES"; "LINKED_PULL_REQUESTS"; "REVIEWERS"; "LABELS"
                                      "MILESTONE"; "REPOSITORY"; "TITLE"; "TEXT"; "NUMBER"; "DATE"
                                      "TRACKS"; "TRACKED_BY"; "ISSUE_TYPE"; "PARENT_ISSUE"
                                      "SUB_ISSUES_PROGRESS"; "CREATED"; "UPDATED"; "CLOSED" ])
            if not allowedType || (optionsList |> List.exists (fun value ->
                String.IsNullOrWhiteSpace value.Id || String.IsNullOrWhiteSpace value.Name))
               || (optionsList |> List.map _.Id |> Set.ofList |> Set.count) <> optionsList.Length
               || (optionsList |> List.map _.Name |> Set.ofList |> Set.count) <> optionsList.Length then
                failwith "field-shape"
            let field =
                { FieldNodeId=id; Name=name; DataType=dataType; Kind=kind
                  Options=optionsList; PayloadJson=raw; PayloadSha256=sha raw }
            Ok(field, subject $"project:{options.Project.ExpectedProjectNodeId}:field:{id}" (sha raw) raw)
        with _ -> Error "project-field-shape"

    let bindProjectFields options (population: MigrationProjectFieldPopulation) captures =
        if not population.Terminal then Error "project-cohort"
        else bindProjectConnection options "project-fields" "fields" population.ProjectNodeId
                 population.PageCount population.TotalCount population.Fields _.FieldNodeId
                 (parseFieldNode options) captures

    let private parseValueNode (options: MigrationInspectProviderOptions) (raw: string) =
        try
            use document = JsonDocument.Parse raw
            let item = document.RootElement
            let itemId = item.GetProperty("id").GetString()
            let updated = DateTimeOffset.Parse(item.GetProperty("updatedAt").GetString())
            if String.IsNullOrWhiteSpace itemId then failwith "item-identity"
            let connection = item.GetProperty("fieldValues")
            let total = connection.GetProperty("totalCount").GetInt32()
            let pageInfo = connection.GetProperty("pageInfo")
            if total < 0 || pageInfo.GetProperty("hasNextPage").GetBoolean() then
                failwith "nested-values-incomplete"
            let checkNested (name: string) (value: JsonElement) =
                let nested = value.GetProperty(name)
                let count = nested.GetProperty("totalCount").GetInt32()
                let nodes = nested.GetProperty("nodes").EnumerateArray() |> Seq.toList
                if count < 0 || count <> nodes.Length
                   || nested.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean() then
                    failwith "nested-value-incomplete"
                let ids = nodes |> List.map (fun node -> node.GetProperty("id").GetString())
                if ids |> List.exists String.IsNullOrWhiteSpace
                   || (ids |> Set.ofList |> Set.count) <> ids.Length then failwith "nested-value-ids"
            let values =
                connection.GetProperty("nodes").EnumerateArray()
                |> Seq.map (fun value ->
                    let fieldId = value.GetProperty("field").GetProperty("id").GetString()
                    let kind = value.GetProperty("__typename").GetString()
                    if String.IsNullOrWhiteSpace fieldId then failwith "field-identity"
                    match kind with
                    | "ProjectV2ItemFieldLabelValue" -> checkNested "labels" value
                    | "ProjectV2ItemFieldPullRequestValue" -> checkNested "pullRequests" value
                    | "ProjectV2ItemFieldReviewerValue" -> checkNested "reviewers" value
                    | "ProjectV2ItemFieldUserValue" -> checkNested "users" value
                    | "ProjectV2ItemFieldRepositoryValue" -> value.GetProperty("repository") |> ignore
                    | "ProjectV2ItemFieldMilestoneValue" -> value.GetProperty("milestone") |> ignore
                    | "ProjectV2ItemFieldMultiSelectValue" ->
                        let options = value.GetProperty("options").EnumerateArray() |> Seq.toList
                        let entries =
                            options |> List.map (fun option ->
                                option.GetProperty("id").GetString(), option.GetProperty("name").GetString())
                        let ids = entries |> List.map fst
                        let names = entries |> List.map snd
                        if entries |> List.exists (fun (id, name) ->
                            String.IsNullOrWhiteSpace id || String.IsNullOrWhiteSpace name)
                           || (ids |> Set.ofList |> Set.count) <> ids.Length
                           || (names |> Set.ofList |> Set.count) <> names.Length then
                            failwith "multi-select-options"
                    | "ProjectV2ItemFieldIterationValue" ->
                        for name in [ "iterationId"; "startDate"; "duration" ] do
                            value.GetProperty(name) |> ignore
                    | "ProjectV2ItemFieldNumberValue" -> value.GetProperty("number") |> ignore
                    | "ProjectV2ItemFieldDateValue" -> value.GetProperty("date") |> ignore
                    | "ProjectV2ItemFieldTextValue" -> value.GetProperty("text") |> ignore
                    | "ProjectV2ItemFieldSingleSelectValue" -> value.GetProperty("optionId") |> ignore
                    | _ -> failwith "unsupported-value-kind"
                    let mutable node = Unchecked.defaultof<JsonElement>
                    let nodeId =
                        if value.TryGetProperty("id", &node) && node.ValueKind = JsonValueKind.String
                           && not (String.IsNullOrWhiteSpace(node.GetString())) then Some(node.GetString())
                        else None
                    let payload = value.GetRawText()
                    { FieldNodeId=fieldId; ValueKind=kind; ValueNodeId=nodeId
                      PayloadJson=payload; PayloadSha256=sha payload })
                |> Seq.toList
            if values.Length <> total
               || (values |> List.map _.FieldNodeId |> Set.ofList |> Set.count) <> values.Length then
                failwith "value-count-or-duplicate"
            let typed =
                { ItemNodeId=itemId; UpdatedAt=updated; FieldValueCount=total
                  FieldValues=values |> List.sortBy _.FieldNodeId }
            Ok(typed, subject $"project:{options.Project.ExpectedProjectNodeId}:value-item:{itemId}"
                              (updated.ToUniversalTime().ToString("O")) raw)
        with _ -> Error "project-value-shape"

    let bindProjectValues options (population: MigrationProjectValuePopulation) captures =
        if not population.Terminal then Error "project-cohort"
        else bindProjectConnection options "project-values" "items" population.ProjectNodeId
                 population.PageCount population.TotalCount population.Items _.ItemNodeId
                 (parseValueNode options) captures

    let internal combineProjectItemsAndValues (items: MigrationProjectItemPopulation)
                                     (values: MigrationProjectValuePopulation)
                                     (membership: GitHubMigrationInspectAuthority)
                                     (valuePages: GitHubMigrationInspectAuthority) =
        let membershipKeys = items.Items |> List.map (fun item -> item.ItemNodeId, item.UpdatedAt)
        let valueKeys = values.Items |> List.map (fun item -> item.ItemNodeId, item.UpdatedAt)
        if membershipKeys <> valueKeys || membership.CohortSha256 <> valuePages.CohortSha256
           || membership.Pages.IsEmpty || valuePages.Pages.IsEmpty then
            Error "project-item-value-drift"
        else
            let linkedMembership =
                membership.Pages |> List.mapi (fun index page ->
                    if index + 1 = membership.Pages.Length then
                        { page with NextRequestIdentitySha256=Some valuePages.Pages.Head.RequestIdentitySha256 }
                    else page)
            let pages = linkedMembership @ valuePages.Pages
            let subjects = (membership.Read.Subjects @ valuePages.Read.Subjects) |> List.sortBy _.Identity
            Ok { membership with
                   Read={ membership.Read with
                            ObservedAt=max membership.Read.ObservedAt valuePages.Read.ObservedAt
                            PageCount=pages.Length; ItemCount=subjects.Length
                            HighWaterMark=digestParts (pages |> List.map _.PayloadSha256)
                            Subjects=subjects }
                   Pages=pages }

    let bindNativeRelations (options: MigrationInspectProviderOptions)
                                     (issues: MigrationIssuePopulation)
                                     (issueProof: GitHubMigrationInspectAuthority)
                                     (population: MigrationRelationPopulation)
                                     (captures: (GitHubRequest * TransportOutcome) list) =
        let repositoryId = options.Repository.ExpectedRepositoryId
        let validIssueProof =
            if issues.Pages.Length <> issues.PageCount || issueProof.Pages.Length <> issues.Pages.Length
               || issueProof.Read.PageCount <> issueProof.Pages.Length
               || issueProof.Read.ItemCount <> issueProof.Read.Subjects.Length
               || not issueProof.Read.Terminal || issueProof.Read.NextCursor.IsSome
               || not issueProof.ScopeVerified || not issueProof.SubjectsParsedFromRaw
               || issueProof.Read.Subjects <> (issueProof.Pages |> List.collect _.Subjects |> List.sortBy _.Identity)
               || issueProof.Read.HighWaterMark <> digestParts (issueProof.Pages |> List.map _.PayloadSha256)
               || not issues.Terminal then false
            else
                let pageFacts =
                    List.zip issues.Pages issueProof.Pages
                    |> List.forall (fun (census, proof) ->
                        proof.RequestedUri = census.RequestedUri
                        && proof.RequestIdentitySha256 = sha census.RequestedUri
                        && proof.PayloadSha256 = census.PayloadSha256
                        && proof.PayloadSha256 = sha proof.RawBody
                        && proof.NextRequestIdentitySha256 = (census.NextUri |> Option.map sha))
                let parsed = parseIssuePages repositoryId (issueProof.Pages |> List.map _.RawBody)
                match parsed with
                | Error _ -> false
                | Ok pages ->
                    let rawRecords =
                        pages |> List.collect (fst >> List.map fst)
                        |> List.sortBy (fun (number, _, _, _, _, _) -> number)
                    let typedRecords =
                        issues.Issues |> List.map (fun item ->
                            item.Number, item.DatabaseId, item.NodeId, item.State, item.UpdatedAt, item.PayloadJson)
                    let subjectFacts =
                        List.zip pages issueProof.Pages
                        |> List.forall (fun ((rows, _), proof) -> proof.Subjects = (rows |> List.map snd))
                    pageFacts && subjectFacts && rawRecords = typedRecords
                    && (pages |> List.sumBy snd) = issues.PullRequestCount
        if options.Cohort.Repositories.Length <> 1
           || options.Cohort.Repositories.Head.Id <> repositoryId
           || not population.CompleteForRepository || population.RepositoryId <> repositoryId
           || population.IssueCount <> issues.Issues.Length || population.Issues.Length <> issues.Issues.Length
           || population.ExternalEdgeCount <> 0
           || issueProof.Read.Authority <> "issues-open-and-relevant-closed"
           || issueProof.CohortSha256 <> GitHubMigrationInspect.cohortSha256 options.Cohort
           || not validIssueProof then
            Error "relation-cohort-or-population"
        elif captures |> List.exists (fst >> allowedRequest options "hierarchy-and-dependencies" >> not) then
            Error "relation-request-scope"
        else
            try
                let issueIds = issues.Issues |> List.map (fun (issue: MigrationIssueRecord) -> issue.NodeId) |> Set.ofList
                let calls = captures |> List.toArray
                let mutable index = 0
                let mutable stage = "request"
                let require condition = if not condition then failwith stage
                let take document variables =
                    require (index < calls.Length)
                    let request, outcome = calls.[index]
                    index <- index + 1
                    match request with
                    | GraphQL value ->
                        require (value.Uri = options.Repository.GraphQLUri
                                 && value.Document = document && value.Variables = variables)
                        let body =
                            match responseBody outcome with
                            | Ok value -> value | Error _ -> failwith "relation-response"
                        value, body
                    | _ -> failwith "relation-request"
                let endpoint (value: JsonElement) =
                    stage <- "endpoint"
                    let id = value.GetProperty("id").GetString()
                    let repo = value.GetProperty("repository").GetProperty("databaseId").GetInt64()
                    require (not (String.IsNullOrWhiteSpace id)
                             && repo = repositoryId && Set.contains id issueIds)
                    { NodeId=id; RepositoryId=repo }
                let parseResponse (source: MigrationIssueRecord) (body: string) =
                    stage <- "response"
                    use document = JsonDocument.Parse body
                    let root = document.RootElement
                    let mutable errors = Unchecked.defaultof<JsonElement>
                    require (not (root.TryGetProperty("errors", &errors)))
                    let item = root.GetProperty("data").GetProperty("node")
                    let current = endpoint item
                    let number = item.GetProperty("number").GetInt32()
                    let updated = DateTimeOffset.Parse(item.GetProperty("updatedAt").GetString())
                    require (current.NodeId = source.NodeId && number = source.Number
                             && updated = source.UpdatedAt)
                    item.Clone()
                let connection (name: string) (item: JsonElement) =
                    stage <- $"connection:{name}"
                    let value = item.GetProperty(name)
                    let total = value.GetProperty("totalCount").GetInt32()
                    require (total >= 0)
                    let nodes = value.GetProperty("nodes").EnumerateArray() |> Seq.map endpoint |> Seq.toList
                    let pageInfo = value.GetProperty("pageInfo")
                    let next =
                        if pageInfo.GetProperty("hasNextPage").GetBoolean() then
                            let cursor = pageInfo.GetProperty("endCursor").GetString()
                            require (not (String.IsNullOrWhiteSpace cursor) && not nodes.IsEmpty)
                            Some cursor
                        else None
                    require (nodes.Length <= total)
                    total, nodes, next
                let evidence = ResizeArray<GitHubMigrationInspectPage>()
                let edges = ResizeArray<MigrationRelationEdge>()
                for source, record in List.zip issues.Issues population.Issues do
                    stage <- $"issue:{source.NodeId}"
                    require (record.IssueNodeId = source.NodeId && record.UpdatedAt = source.UpdatedAt)
                    let initialRequest, initialBody =
                        take MigrationGitHubRead.nativeRelationsQuery (Map.ofList [ "id", source.NodeId ])
                    let initial = parseResponse source initialBody
                    let initialRaw = initial.GetRawText()
                    require (record.PayloadJson = initialRaw && record.PayloadSha256 = sha initialRaw)
                    let current = { NodeId=source.NodeId; RepositoryId=repositoryId }
                    let parent = initial.GetProperty("parent")
                    if parent.ValueKind <> JsonValueKind.Null then
                        edges.Add { Kind=MigrationRelationKind.ParentChild
                                    Source=endpoint parent; Target=current }
                    evidence.Add
                        { RequestedUri=initialRequest.Uri.AbsoluteUri
                          RequestIdentitySha256=graphQLIdentity initialRequest
                          RawBody=initialBody; PayloadSha256=sha initialBody
                          NextRequestIdentitySha256=None
                          Subjects=[ subject $"repository:{repositoryId}:relation:{source.NodeId}"
                                             (source.UpdatedAt.ToUniversalTime().ToString("O")) initialRaw ] }
                    let mutable continuation = 0
                    for name in [ "subIssues"; "blockedBy"; "blocking" ] do
                        let total, firstNodes, firstNext = connection name initial
                        let mutable nodes = firstNodes
                        let mutable next = firstNext
                        let mutable cursors = Set.empty<string>
                        while next.IsSome do
                            let cursor = next.Value
                            require (not (Set.contains cursor cursors) && continuation < 1000)
                            cursors <- Set.add cursor cursors
                            let request, body =
                                take (MigrationGitHubRead.relationContinuationQuery name)
                                     (Map.ofList [ "id", source.NodeId; "after", cursor ])
                            let item = parseResponse source body
                            let raw = item.GetRawText()
                            require (continuation < record.ContinuationPages.Length)
                            let retained = record.ContinuationPages.[continuation]
                            continuation <- continuation + 1
                            require (retained.Connection = name && retained.RequestedCursor = cursor
                                     && retained.PayloadJson = raw && retained.PayloadSha256 = sha raw)
                            let count, added, cursorNext = connection name item
                            require (count = total)
                            nodes <- nodes @ added
                            next <- cursorNext
                            evidence.Add
                                { RequestedUri=request.Uri.AbsoluteUri
                                  RequestIdentitySha256=graphQLIdentity request
                                  RawBody=body; PayloadSha256=sha body
                                  NextRequestIdentitySha256=None; Subjects=[] }
                        require (nodes.Length = total
                                 && (nodes |> List.map _.NodeId |> Set.ofList |> Set.count) = nodes.Length)
                        for other in nodes do
                            edges.Add
                                (match name with
                                 | "subIssues" ->
                                     { Kind=MigrationRelationKind.ParentChild; Source=current; Target=other }
                                 | "blockedBy" ->
                                     { Kind=MigrationRelationKind.Blocks; Source=other; Target=current }
                                 | _ ->
                                     { Kind=MigrationRelationKind.Blocks; Source=current; Target=other })
                    require (continuation = record.ContinuationPages.Length)
                require (index = calls.Length)
                stage <- "reciprocity"
                let grouped = edges |> Seq.toList |> List.countBy id
                require (grouped |> List.forall (fun (_, count) -> count = 2))
                let typed = grouped |> List.map fst
                            |> List.sortBy (fun edge ->
                                edge.Kind, edge.Source.RepositoryId, edge.Source.NodeId,
                                edge.Target.RepositoryId, edge.Target.NodeId)
                require (typed = population.Edges)
                let relationPages = evidence |> Seq.toList
                let pages = issueProof.Pages @ relationPages
                let linked =
                    pages |> List.mapi (fun position page ->
                        let nextIdentity =
                            if position + 1 < pages.Length then Some pages.[position + 1].RequestIdentitySha256
                            else None
                        { page with NextRequestIdentitySha256=nextIdentity })
                let subjects = linked |> List.collect _.Subjects |> List.sortBy _.Identity
                Ok { CohortSha256=issueProof.CohortSha256
                     ScopeVerified=true; SubjectsParsedFromRaw=true
                     Read={ Authority="hierarchy-and-dependencies"; ObservedAt=DateTimeOffset.UtcNow
                            PageCount=linked.Length; ItemCount=subjects.Length
                            Terminal=true; NextCursor=None
                            HighWaterMark=digestParts (linked |> List.map _.PayloadSha256)
                            Subjects=subjects }
                     Pages=linked }
            with failure -> Error $"relation-raw-or-scope:{failure.Message}"

type MigrationInspectProviderAdapter(options: MigrationInspectProviderOptions,
                                     transport: IMigrationGitHubReadTransport) =
    let fieldProofs = System.Collections.Generic.Dictionary<int * string, string>()
    let issueProofs = System.Collections.Generic.Dictionary<int * string, string>()
    let cohortDigest = GitHubMigrationInspect.cohortSha256 options.Cohort
    let fieldFingerprint (proof: GitHubMigrationInspectAuthority) =
        proof.Pages
        |> List.collect (fun page ->
            [ page.RequestIdentitySha256; page.PayloadSha256
              defaultArg page.NextRequestIdentitySha256 "terminal" ])
        |> List.map (fun part -> $"{Encoding.UTF8.GetByteCount part}:{part}")
        |> String.concat ""
        |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    interface IGitHubMigrationInspectSource with
        member _.ReadAuthority(passOrdinal, authority) =
            if passOrdinal <> 1 && passOrdinal <> 2 then Error "invalid-pass"
            elif authority = "issues-open-and-relevant-closed" then
                lock issueProofs (fun () -> issueProofs.Remove((passOrdinal, cohortDigest)) |> ignore)
                let capture = CapturingTransport(transport, MigrationInspectProviderAdapter.allowedRequest options authority)
                MigrationGitHubRead.readIssues options.Repository capture
                |> Result.mapError (fun failure -> $"issue-read:{failure}")
                |> Result.bind (fun population ->
                    MigrationInspectProviderAdapter.bindIssues options population capture.Calls)
                |> Result.map (fun proof ->
                    lock issueProofs (fun () ->
                        issueProofs.[(passOrdinal, cohortDigest)] <- fieldFingerprint proof)
                    proof)
            elif authority = "hierarchy-and-dependencies" then
                if options.Cohort.Repositories.Length <> 1
                   || options.Cohort.Repositories.Head.Id <> options.Repository.ExpectedRepositoryId then
                    Error "relation-single-repository-cohort-required"
                else
                    let expectedIssueProof =
                        lock issueProofs (fun () ->
                            match issueProofs.TryGetValue((passOrdinal, cohortDigest)) with
                            | true, proof -> Some proof | _ -> None)
                    match expectedIssueProof with
                    | None -> Error "relation-issue-proof-required"
                    | Some fingerprint ->
                        let issueCapture =
                            CapturingTransport(transport,
                                MigrationInspectProviderAdapter.allowedRequest options "issues-open-and-relevant-closed")
                        MigrationGitHubRead.readIssues options.Repository issueCapture
                        |> Result.mapError (fun failure -> $"relation-issue-read:{failure}")
                        |> Result.bind (fun issues ->
                            MigrationInspectProviderAdapter.bindIssues options issues issueCapture.Calls
                            |> Result.bind (fun issueProof ->
                                if fieldFingerprint issueProof <> fingerprint then
                                    Error "relation-issue-proof-drift"
                                else
                                    let relationCapture =
                                        CapturingTransport(transport,
                                            MigrationInspectProviderAdapter.allowedRelationIssueRequest options
                                                (issues.Issues |> List.map _.NodeId |> Set.ofList))
                                    MigrationGitHubRead.readNativeRelations options.Repository issues relationCapture
                                    |> Result.mapError (fun failure -> $"relation-read:{failure}")
                                    |> Result.bind (fun population ->
                                        MigrationInspectProviderAdapter.bindNativeRelations
                                            options issues issueProof population relationCapture.Calls)))
            elif authority = "project-items" then
                lock fieldProofs (fun () -> fieldProofs.Remove((passOrdinal, cohortDigest)) |> ignore)
                let membershipCapture = CapturingTransport(transport, MigrationInspectProviderAdapter.allowedRequest options authority)
                MigrationGitHubRead.readProjectItems options.Project membershipCapture
                |> Result.mapError (fun failure -> $"project-read:{failure}")
                |> Result.bind (fun items ->
                    MigrationInspectProviderAdapter.bindProjectItems options items membershipCapture.Calls
                    |> Result.bind (fun membership ->
                        let valueCapture = CapturingTransport(transport, MigrationInspectProviderAdapter.allowedRequest options "project-values")
                        MigrationGitHubRead.readProjectValues options.Project valueCapture
                        |> Result.mapError (fun failure -> $"project-value-read:{failure}")
                        |> Result.bind (fun values ->
                            MigrationInspectProviderAdapter.bindProjectValues options values valueCapture.Calls
                            |> Result.bind (fun valueProof ->
                                let fieldCapture = CapturingTransport(transport, MigrationInspectProviderAdapter.allowedRequest options "project-fields")
                                MigrationGitHubRead.readProjectFields options.Project fieldCapture
                                |> Result.mapError (fun failure -> $"project-field-read:{failure}")
                                |> Result.bind (fun fields ->
                                    MigrationInspectProviderAdapter.bindProjectFields options fields fieldCapture.Calls
                                    |> Result.bind (fun fieldProof ->
                                        MigrationGitHubRead.reconcileProject items fields values
                                        |> Result.mapError (fun failure -> $"project-reconcile:{failure}")
                                        |> Result.bind (fun _ ->
                                            MigrationInspectProviderAdapter.combineProjectItemsAndValues items values membership valueProof
                                            |> Result.map (fun combined ->
                                                lock fieldProofs (fun () ->
                                                    fieldProofs.[(passOrdinal, cohortDigest)] <- fieldFingerprint fieldProof)
                                                combined))))))))
            elif authority = "project-fields" then
                let expected =
                    lock fieldProofs (fun () ->
                        match fieldProofs.TryGetValue((passOrdinal, cohortDigest)) with
                        | true, proof -> Some proof | _ -> None)
                match expected with
                | None -> Error "project-item-field-proof-required"
                | Some fingerprint ->
                    let capture = CapturingTransport(transport, MigrationInspectProviderAdapter.allowedRequest options authority)
                    MigrationGitHubRead.readProjectFields options.Project capture
                    |> Result.mapError (fun failure -> $"project-field-read:{failure}")
                    |> Result.bind (fun population ->
                        MigrationInspectProviderAdapter.bindProjectFields options population capture.Calls)
                    |> Result.bind (fun proof ->
                        if fieldFingerprint proof = fingerprint then Ok proof
                        else Error "project-field-proof-drift")
            else Error $"authority-adapter-unavailable:{authority}"
