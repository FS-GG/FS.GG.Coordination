namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type MigrationGitHubReadOptions =
    { ApiBase: Uri
      GraphQLUri: Uri
      Token: string
      UserAgent: string
      Owner: string
      Repository: string
      ExpectedRepositoryId: int64 }

type MigrationIssueRecord =
    { Number: int
      DatabaseId: int64
      NodeId: string
      State: string
      UpdatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssuePopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      Issues: MigrationIssueRecord list
      PullRequestCount: int }

type MigrationRepositoryCoreSettings =
    { RepositoryId: int64
      NodeId: string
      FullName: string
      DefaultBranch: string
      Visibility: string
      Archived: bool
      Disabled: bool
      HasIssues: bool
      AllowSquashMerge: bool
      AllowMergeCommit: bool
      AllowRebaseMerge: bool
      DeleteBranchOnMerge: bool
      PayloadJson: string
      PayloadSha256: string }

type MigrationPullRequestRecord =
    { Number: int
      DatabaseId: int64
      NodeId: string
      State: string
      UpdatedAt: DateTimeOffset
      HeadSha: string
      BaseSha: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationRestPageEvidence =
    { RequestedUri: string
      PayloadSha256: string
      NextUri: string option }

type MigrationPullRequestPopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      Pages: MigrationRestPageEvidence list
      PullRequests: MigrationPullRequestRecord list }

type MigrationIssueCommentRecord =
    { DatabaseId: int64
      NodeId: string
      SubjectNumber: int
      ActorLogin: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      Body: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssueCommentPopulation =
    { RepositoryId: int64
      SubjectNumber: int
      SubjectNodeId: string
      PageCount: int
      Terminal: bool
      Pages: MigrationRestPageEvidence list
      Comments: MigrationIssueCommentRecord list }

type MigrationIssueEventRecord =
    { DatabaseId: int64
      NodeId: string
      SubjectNumber: int
      EventKind: string
      ActorLogin: string option
      CreatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssueEventPopulation =
    { RepositoryId: int64
      SubjectNumber: int
      SubjectNodeId: string
      PageCount: int
      Terminal: bool
      Pages: MigrationRestPageEvidence list
      Events: MigrationIssueEventRecord list }

type MigrationPullRequestReviewRecord =
    { DatabaseId: int64
      NodeId: string
      PullRequestNumber: int
      State: string
      ActorLogin: string option
      CommitSha: string option
      SubmittedAt: DateTimeOffset option
      PayloadJson: string
      PayloadSha256: string }

type MigrationPullRequestReviewPopulation =
    { RepositoryId: int64
      PullRequestNumber: int
      PullRequestNodeId: string
      PageCount: int
      Terminal: bool
      Pages: MigrationRestPageEvidence list
      Reviews: MigrationPullRequestReviewRecord list }

type MigrationPullRequestReviewCommentRecord =
    { DatabaseId: int64
      NodeId: string
      PullRequestNumber: int
      ReviewId: int64 option
      Path: string
      Body: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string }

type MigrationPullRequestReviewCommentPopulation =
    { RepositoryId: int64
      PullRequestNumber: int
      PullRequestNodeId: string
      PageCount: int
      Terminal: bool
      Pages: MigrationRestPageEvidence list
      Comments: MigrationPullRequestReviewCommentRecord list }

type MigrationIssueTypeRecord =
    { NodeId: string
      Name: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssueTypePopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      IssueTypes: MigrationIssueTypeRecord list }

[<RequireQualifiedAccess>]
type MigrationRelationKind = ParentChild | Blocks

type MigrationRelationEndpoint =
    { NodeId: string
      RepositoryId: int64 }

type MigrationRelationEdge =
    { Kind: MigrationRelationKind
      Source: MigrationRelationEndpoint
      Target: MigrationRelationEndpoint }

type MigrationRelationContinuationPage =
    { Connection: string
      RequestedCursor: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssueRelationRecord =
    { IssueNodeId: string
      UpdatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string
      ContinuationPages: MigrationRelationContinuationPage list }

type MigrationRelationPopulation =
    { RepositoryId: int64
      IssueCount: int
      CompleteForRepository: bool
      ExternalEdgeCount: int
      Edges: MigrationRelationEdge list
      Issues: MigrationIssueRelationRecord list }

type MigrationProjectReadOptions =
    { GraphQLUri: Uri
      Token: string
      UserAgent: string
      Organization: string
      ProjectNumber: int
      ExpectedProjectNodeId: string }

[<RequireQualifiedAccess>]
type MigrationProjectContent =
    | Issue of nodeId:string * repositoryId:int64 * number:int
    | PullRequest of nodeId:string * repositoryId:int64 * number:int
    | DraftIssue of nodeId:string

type MigrationProjectItemRecord =
    { ItemNodeId: string
      Archived: bool
      UpdatedAt: DateTimeOffset
      Content: MigrationProjectContent
      PayloadJson: string
      PayloadSha256: string }

type MigrationProjectItemPopulation =
    { ProjectNodeId: string
      PageCount: int
      Terminal: bool
      TotalCount: int
      Items: MigrationProjectItemRecord list }

[<RequireQualifiedAccess>]
type MigrationProjectFieldKind = BuiltIn | SingleSelect | MultiSelect | Iteration

type MigrationProjectFieldOption = { Id: string; Name: string }

type MigrationProjectFieldRecord =
    { FieldNodeId: string
      Name: string
      DataType: string
      Kind: MigrationProjectFieldKind
      Options: MigrationProjectFieldOption list
      PayloadJson: string
      PayloadSha256: string }

type MigrationProjectFieldPopulation =
    { ProjectNodeId: string
      PageCount: int
      Terminal: bool
      TotalCount: int
      Fields: MigrationProjectFieldRecord list }

type MigrationProjectFieldValueRecord =
    { FieldNodeId: string
      ValueKind: string
      ValueNodeId: string option
      PayloadJson: string
      PayloadSha256: string }

type MigrationProjectItemValueRecord =
    { ItemNodeId: string
      UpdatedAt: DateTimeOffset
      FieldValueCount: int
      FieldValues: MigrationProjectFieldValueRecord list }

type MigrationProjectValuePopulation =
    { ProjectNodeId: string
      PageCount: int
      Terminal: bool
      TotalCount: int
      Items: MigrationProjectItemValueRecord list }

type MigrationProjectSnapshot =
    { ProjectNodeId: string
      ItemCount: int
      FieldCount: int
      FieldValueCount: int
      NormalizedSha256: string
      Items: MigrationProjectItemPopulation
      Fields: MigrationProjectFieldPopulation
      Values: MigrationProjectValuePopulation }

[<RequireQualifiedAccess>]
type MigrationReadFailure =
    | InvalidOptions
    | TransportUnavailable
    | HttpRefused of status:int
    | GraphQLErrors
    | MalformedResponse of reason:string
    | IdentityDrift
    | PaginationRefused of reason:string
    | DuplicateIdentity of identity:string
    | PopulationDrift
    | SnapshotMismatch of reason:string

type IMigrationGitHubReadTransport =
    abstract Send: GitHubRequest -> TransportOutcome

type HttpMigrationGitHubReadTransport(client: HttpClient) =
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            let readOnly =
                match request with
                | Rest value -> value.Method = Get
                | GraphQL value -> value.Document.TrimStart().StartsWith("query", StringComparison.Ordinal)
            match Transport.validateRequest request with
            | Error _ -> NetworkFailure
            | Ok () when not readOnly -> NetworkFailure
            | Ok () ->
                try
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
                        | GraphQL value ->
                            let payload = JsonObject()
                            payload.Add("query", value.Document)
                            let variables = JsonObject()
                            for KeyValue(name, item) in value.Variables do
                                variables.Add(name, item)
                            payload.Add("variables", variables)
                            HttpMethod.Post, value.Uri, value.Headers, Some(payload.ToJsonString())

                    use message = new HttpRequestMessage(methodValue, uri)
                    for KeyValue(name, value) in headers do
                        message.Headers.TryAddWithoutValidation(name, value) |> ignore
                    match body with
                    | Some value -> message.Content <- new StringContent(value, Encoding.UTF8, "application/json")
                    | None -> ()
                    use response = client.Send message
                    let headerValues =
                        Seq.append response.Headers response.Content.Headers
                        |> Seq.map (fun item -> item.Key.ToLowerInvariant(), String.concat "," item.Value)
                        |> Map.ofSeq
                    let tryInt name =
                        Map.tryFind name headerValues
                        |> Option.bind (fun value -> match Int32.TryParse value with true, parsed -> Some parsed | _ -> None)
                    let tryDate name =
                        Map.tryFind name headerValues
                        |> Option.bind (fun value -> match Int64.TryParse value with true, parsed -> Some(DateTimeOffset.FromUnixTimeSeconds parsed) | _ -> None)
                    Response
                        { StatusCode = int response.StatusCode
                          Headers = headerValues
                          Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                          ETag = Map.tryFind "etag" headerValues
                          RateBudget =
                            { Limit = tryInt "x-ratelimit-limit"
                              Remaining = tryInt "x-ratelimit-remaining"
                              ResetAt = tryDate "x-ratelimit-reset"
                              Cost = Some 1 } }
                with
                | :? OperationCanceledException -> TimedOut
                | :? HttpRequestException -> NetworkFailure

[<RequireQualifiedAccess>]
module MigrationGitHubRead =
    let private text (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private valid (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && not (isNull options.GraphQLUri) && options.GraphQLUri.IsAbsoluteUri
        && options.GraphQLUri.Scheme = Uri.UriSchemeHttps
        && text options.UserAgent && text options.Owner && text options.Repository
        && options.ExpectedRepositoryId > 0L

    let private headers token userAgent =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", userAgent
          if text token then "authorization", $"Bearer {token}" ]
        |> Map.ofList

    let private response (transport: IMigrationGitHubReadTransport) (request: GitHubRequest) =
        match transport.Send request with
        | Response value when value.StatusCode >= 200 && value.StatusCode < 300 -> Ok value
        | Response value -> Error(MigrationReadFailure.HttpRefused value.StatusCode)
        | NetworkFailure | TimedOut -> Error MigrationReadFailure.TransportUnavailable

    let private parse (body: string) =
        try Ok(JsonDocument.Parse body)
        with :? JsonException -> Error(MigrationReadFailure.MalformedResponse "invalid-json")

    let private property (name: string) (value: JsonElement) =
        let mutable result = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &result) then Ok result
        else Error(MigrationReadFailure.MalformedResponse $"missing:{name}")

    let private requiredString name value =
        property name value
        |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.String && text (item.GetString()) then Ok(item.GetString())
            else Error(MigrationReadFailure.MalformedResponse $"invalid:{name}"))

    let private requiredInt64 name value =
        property name value
        |> Result.bind (fun item ->
            let mutable parsed = 0L
            if item.ValueKind = JsonValueKind.Number && item.TryGetInt64(&parsed) && parsed > 0L then Ok parsed
            else Error(MigrationReadFailure.MalformedResponse $"invalid:{name}"))

    let private requiredInt name value =
        property name value
        |> Result.bind (fun item ->
            let mutable parsed = 0
            if item.ValueKind = JsonValueKind.Number && item.TryGetInt32(&parsed) && parsed > 0 then Ok parsed
            else Error(MigrationReadFailure.MalformedResponse $"invalid:{name}"))

    let private requiredBool name value =
        property name value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.True -> Ok true
            | JsonValueKind.False -> Ok false
            | _ -> Error(MigrationReadFailure.MalformedResponse $"invalid:{name}"))

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private collectUnique (key: 'a -> string) (values: 'a list) =
        let seen = HashSet<string>(StringComparer.Ordinal)
        values
        |> List.tryPick (fun value -> let id = key value in if seen.Add id then None else Some id)
        |> function
            | Some value -> Error(MigrationReadFailure.DuplicateIdentity value)
            | None -> Ok values

    let private readRepository (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        let path = $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"
        let uri = Uri(options.ApiBase, path)
        response transport (Rest { Method=Get; Uri=uri; Headers=headers options.Token options.UserAgent; Body=None
                                   ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
        |> Result.bind (fun result -> parse result.Body)
        |> Result.bind (fun document ->
            use document = document
            match requiredInt64 "id" document.RootElement, requiredString "full_name" document.RootElement with
            | Ok id, Ok fullName when id = options.ExpectedRepositoryId
                                      && fullName = $"{options.Owner}/{options.Repository}" -> Ok id
            | Ok _, Ok _ -> Error MigrationReadFailure.IdentityDrift
            | Error error, _ | _, Error error -> Error error)

    let readRepositoryCoreSettings (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        else
            let path = $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"
            let request =
                Rest { Method=Get; Uri=Uri(options.ApiBase, path)
                       Headers=headers options.Token options.UserAgent; Body=None
                       ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
            response transport request
            |> Result.bind (fun result -> parse result.Body |> Result.map (fun document -> result.Body, document))
            |> Result.bind (fun (payload, document) ->
                use document = document
                let root = document.RootElement
                match requiredInt64 "id" root, requiredString "node_id" root,
                      requiredString "full_name" root, requiredString "default_branch" root,
                      requiredString "visibility" root with
                | Ok id, Ok _, Ok fullName, Ok _, Ok _ when
                    id <> options.ExpectedRepositoryId
                    || fullName <> $"{options.Owner}/{options.Repository}" ->
                    Error MigrationReadFailure.IdentityDrift
                | Ok _, Ok _, Ok _, Ok _, Ok visibility when
                    not (Set.contains visibility (set [ "public"; "private"; "internal" ])) ->
                    Error(MigrationReadFailure.MalformedResponse "invalid:visibility")
                | Ok id, Ok nodeId, Ok fullName, Ok defaultBranch, Ok visibility when
                    id = options.ExpectedRepositoryId
                    && fullName = $"{options.Owner}/{options.Repository}" ->
                    let flags =
                        [ "archived"; "disabled"; "has_issues"; "allow_squash_merge"
                          "allow_merge_commit"; "allow_rebase_merge"; "delete_branch_on_merge" ]
                        |> List.map (fun name -> requiredBool name root)
                    match flags |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                    | Some failure -> Error failure
                    | None ->
                        let values = flags |> List.choose (function Ok value -> Some value | Error _ -> None)
                        match values with
                        | [ archived; disabled; hasIssues; allowSquash; allowMerge; allowRebase; deleteBranch ] ->
                            Ok { RepositoryId=id; NodeId=nodeId; FullName=fullName
                                 DefaultBranch=defaultBranch; Visibility=visibility
                                 Archived=archived; Disabled=disabled; HasIssues=hasIssues
                                 AllowSquashMerge=allowSquash; AllowMergeCommit=allowMerge
                                 AllowRebaseMerge=allowRebase; DeleteBranchOnMerge=deleteBranch
                                 PayloadJson=payload; PayloadSha256=sha payload }
                        | _ -> Error(MigrationReadFailure.MalformedResponse "invalid:repository-settings")
                | Ok _, Ok _, Ok _, Ok _, Ok _ -> Error MigrationReadFailure.IdentityDrift
                | Error failure, _, _, _, _ | _, Error failure, _, _, _
                | _, _, Error failure, _, _ | _, _, _, Error failure, _
                | _, _, _, _, Error failure -> Error failure)

    let private parseIssue (value: JsonElement) =
        match requiredInt "number" value, requiredInt64 "id" value, requiredString "node_id" value,
              requiredString "state" value, requiredString "updated_at" value with
        | Ok number, Ok databaseId, Ok nodeId, Ok state, Ok updated ->
            let mutable timestamp = DateTimeOffset.MinValue
            if not (DateTimeOffset.TryParse(updated, &timestamp)) then
                Error(MigrationReadFailure.MalformedResponse "invalid:updated_at")
            else
                let payload = value.GetRawText()
                Ok { Number=number; DatabaseId=databaseId; NodeId=nodeId; State=state
                     UpdatedAt=timestamp; PayloadJson=payload; PayloadSha256=sha payload }
        | Error error, _, _, _, _ | _, Error error, _, _, _ | _, _, Error error, _, _
        | _, _, _, Error error, _ | _, _, _, _, Error error -> Error error

    let private exactIssuePageQuery pageIndex (uri: Uri) =
        let raw = uri.Query.TrimStart('?')
        let entries =
            raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun part -> part.Split('=', 2))
        let expected =
            if pageIndex = 0 then Map.ofList [ "state", "all"; "per_page", "100" ]
            else Map.ofList [ "state", "all"; "per_page", "100"; "page", string (pageIndex + 1) ]
        let decoded =
            entries
            |> Array.choose (fun parts ->
                if parts.Length = 2 then
                    Some(Uri.UnescapeDataString parts.[0], Uri.UnescapeDataString parts.[1])
                else None)
        let keys = decoded |> Array.map fst |> Set.ofArray
        decoded.Length = entries.Length && keys.Count = entries.Length
        && (expected |> Map.forall (fun name value ->
            decoded |> Array.exists (fun (observedName, observedValue) ->
                observedName = name && observedValue = value)))
        && (decoded |> Array.forall (fun (name, value) ->
            Map.containsKey name expected
            || (pageIndex > 0 && name = "after" && text value)))

    let readIssues (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        else
            readRepository options transport
            |> Result.bind (fun repositoryId ->
                let path = $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}/issues?state=all&per_page=100"
                let start = Uri(options.ApiBase, path)
                let allowedPath = start.AbsolutePath
                let rec pages (seen: Set<string>) (count: int) (issues: MigrationIssueRecord list)
                              (pullRequests: int) (current: Uri) =
                    if count >= 1000 || Set.contains current.AbsoluteUri seen then
                        Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                    elif current.Scheme <> start.Scheme || current.Authority <> start.Authority
                         || (current.AbsolutePath <> allowedPath
                             && current.AbsolutePath <> $"/repositories/{repositoryId}/issues")
                         || not (exactIssuePageQuery count current) then
                        Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope")
                    else
                        response transport (Rest { Method=Get; Uri=current; Headers=headers options.Token options.UserAgent; Body=None
                                                   ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
                        |> Result.bind (fun result ->
                            parse result.Body
                            |> Result.bind (fun document ->
                                use document = document
                                if document.RootElement.ValueKind <> JsonValueKind.Array then
                                    Error(MigrationReadFailure.MalformedResponse "issues-not-array")
                                else
                                    let items = document.RootElement.EnumerateArray() |> Seq.toList
                                    let classified =
                                        items
                                        |> List.map (fun item ->
                                            if item.ValueKind <> JsonValueKind.Object then
                                                Error(MigrationReadFailure.MalformedResponse "invalid:issue-item")
                                            else
                                                let mutable marker = Unchecked.defaultof<JsonElement>
                                                if item.TryGetProperty("pull_request", &marker) then
                                                    if marker.ValueKind = JsonValueKind.Object then Ok None
                                                    else Error(MigrationReadFailure.MalformedResponse "invalid:pull-request-marker")
                                                else parseIssue item |> Result.map Some)
                                    let firstError = classified |> List.tryPick (function Error error -> Some error | _ -> None)
                                    match firstError with
                                    | Some error -> Error error
                                    | None ->
                                        let records = classified |> List.choose (function Ok(Some value) -> Some value | _ -> None)
                                        let link = Map.tryFind "link" result.Headers |> Option.defaultValue ""
                                        match Transport.tryNextLink link with
                                        | Error failure -> Error(MigrationReadFailure.PaginationRefused $"{failure}")
                                        | Ok next ->
                                            let accumulated = issues @ records
                                            let prCount = pullRequests + items.Length - records.Length
                                            match next with
                                            | Some uri -> pages (Set.add current.AbsoluteUri seen) (count + 1) accumulated prCount uri
                                            | None ->
                                                collectUnique (fun (issue: MigrationIssueRecord) -> issue.NodeId) accumulated
                                                |> Result.bind (collectUnique (fun issue -> string issue.DatabaseId))
                                                |> Result.bind (collectUnique (fun issue -> string issue.Number))
                                                |> Result.map (fun complete ->
                                                    { RepositoryId=repositoryId; PageCount=count + 1; Terminal=true
                                                      Issues=List.sortBy _.Number complete; PullRequestCount=prCount })))
                pages Set.empty 0 [] 0 start)

    let private parsePullRequest repositoryId (value: JsonElement) =
        let baseRepositoryId = property "base" value |> Result.bind (property "repo")
                               |> Result.bind (requiredInt64 "id")
        let headSha = property "head" value |> Result.bind (requiredString "sha")
        let baseSha = property "base" value |> Result.bind (requiredString "sha")
        match requiredInt "number" value, requiredInt64 "id" value,
              requiredString "node_id" value, requiredString "state" value,
              requiredString "updated_at" value, baseRepositoryId, headSha, baseSha with
        | Ok number, Ok databaseId, Ok nodeId, Ok state, Ok updated, Ok baseRepository, Ok head, Ok baseRevision ->
            let mutable timestamp = DateTimeOffset.MinValue
            let revision (value: string) =
                value.Length = 40 && (value |> Seq.forall Uri.IsHexDigit)
            if baseRepository <> repositoryId then Error MigrationReadFailure.IdentityDrift
            elif state <> "open" && state <> "closed" then
                Error(MigrationReadFailure.MalformedResponse "invalid:pull-request-state")
            elif not (revision head && revision baseRevision)
                 || not (DateTimeOffset.TryParse(updated, &timestamp)) then
                Error(MigrationReadFailure.MalformedResponse "invalid:pull-request-revision")
            else
                let payload = value.GetRawText()
                Ok { Number=number; DatabaseId=databaseId; NodeId=nodeId; State=state
                     UpdatedAt=timestamp; HeadSha=head; BaseSha=baseRevision
                     PayloadJson=payload; PayloadSha256=sha payload }
        | Error failure, _, _, _, _, _, _, _ | _, Error failure, _, _, _, _, _, _
        | _, _, Error failure, _, _, _, _, _ | _, _, _, Error failure, _, _, _, _
        | _, _, _, _, Error failure, _, _, _ | _, _, _, _, _, Error failure, _, _
        | _, _, _, _, _, _, Error failure, _ | _, _, _, _, _, _, _, Error failure -> Error failure

    let readPullRequests (options: MigrationGitHubReadOptions) (issues: MigrationIssuePopulation)
                         (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        elif not issues.Terminal || issues.PageCount < 1 || issues.RepositoryId <> options.ExpectedRepositoryId
             || issues.PullRequestCount < 0 then
            Error(MigrationReadFailure.SnapshotMismatch "issue-census")
        else
            readRepository options transport
            |> Result.bind (fun repositoryId ->
                let path = $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}/pulls?state=all&per_page=100"
                let start = Uri(options.ApiBase, path)
                let rec pages (seen: Set<string>) (count: int) (records: MigrationPullRequestRecord list)
                              (evidence: MigrationRestPageEvidence list) (current: Uri) =
                    if count >= 1000 || Set.contains current.AbsoluteUri seen then
                        Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                    elif current.Scheme <> start.Scheme || current.Authority <> start.Authority
                         || (current.AbsolutePath <> start.AbsolutePath
                             && current.AbsolutePath <> $"/repositories/{repositoryId}/pulls")
                         || not (exactIssuePageQuery count current) then
                        Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope")
                    else
                        response transport (Rest { Method=Get; Uri=current; Headers=headers options.Token options.UserAgent
                                                   Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
                        |> Result.bind (fun result -> parse result.Body |> Result.map (fun document -> result, document))
                        |> Result.bind (fun (result, document) ->
                            use document = document
                            if document.RootElement.ValueKind <> JsonValueKind.Array then
                                Error(MigrationReadFailure.MalformedResponse "pull-requests-not-array")
                            else
                                let parsed =
                                    document.RootElement.EnumerateArray()
                                    |> Seq.map (parsePullRequest repositoryId) |> Seq.toList
                                match parsed |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                                | Some failure -> Error failure
                                | None ->
                                    let values = parsed |> List.choose (function Ok value -> Some value | Error _ -> None)
                                    let link = Map.tryFind "link" result.Headers |> Option.defaultValue ""
                                    match Transport.tryNextLink link with
                                    | Error failure -> Error(MigrationReadFailure.PaginationRefused $"{failure}")
                                    | Ok next ->
                                        let page =
                                            { RequestedUri=current.AbsoluteUri; PayloadSha256=sha result.Body
                                              NextUri=next |> Option.map _.AbsoluteUri }
                                        let all = records @ values
                                        let allPages = evidence @ [ page ]
                                        match next with
                                        | Some uri -> pages (Set.add current.AbsoluteUri seen) (count + 1) all allPages uri
                                        | None when all.Length <> issues.PullRequestCount ->
                                            Error(MigrationReadFailure.SnapshotMismatch "pull-request-count")
                                        | None ->
                                            collectUnique (fun (value: MigrationPullRequestRecord) -> value.NodeId) all
                                            |> Result.bind (collectUnique (fun value -> string value.DatabaseId))
                                            |> Result.bind (collectUnique (fun value -> string value.Number))
                                            |> Result.map (fun complete ->
                                                { RepositoryId=repositoryId; PageCount=count + 1; Terminal=true
                                                  Pages=allPages; PullRequests=List.sortBy _.Number complete }))
                pages Set.empty 0 [] [] start)

    let private exactCommentPageQuery pageIndex (uri: Uri) =
        let raw = uri.Query.TrimStart('?')
        let entries =
            raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun part -> part.Split('=', 2))
        let expected =
            if pageIndex = 0 then Map.ofList [ "per_page", "100" ]
            else Map.ofList [ "per_page", "100"; "page", string (pageIndex + 1) ]
        let decoded =
            entries
            |> Array.choose (fun parts ->
                if parts.Length = 2 then
                    Some(Uri.UnescapeDataString parts.[0], Uri.UnescapeDataString parts.[1])
                else None)
        let keys = decoded |> Array.map fst |> Set.ofArray
        decoded.Length = entries.Length && keys.Count = entries.Length
        && (expected |> Map.forall (fun name value ->
            decoded |> Array.exists (fun (observedName, observedValue) ->
                observedName = name && observedValue = value)))
        && (decoded |> Array.forall (fun (name, value) ->
            Map.containsKey name expected
            || (pageIndex > 0 && name = "after" && text value)))

    let private parseIssueComment expectedIssueUrl issueNumber (value: JsonElement) =
        let actor = property "user" value |> Result.bind (requiredString "login")
        match requiredInt64 "id" value, requiredString "node_id" value,
              requiredString "issue_url" value, requiredString "body" value,
              requiredString "created_at" value, requiredString "updated_at" value, actor with
        | Ok id, Ok nodeId, Ok issueUrl, Ok body, Ok created, Ok updated, Ok login ->
            let mutable createdAt = DateTimeOffset.MinValue
            let mutable updatedAt = DateTimeOffset.MinValue
            if issueUrl <> expectedIssueUrl then Error MigrationReadFailure.IdentityDrift
            elif not (DateTimeOffset.TryParse(created, &createdAt))
                 || not (DateTimeOffset.TryParse(updated, &updatedAt))
                 || updatedAt < createdAt then
                Error(MigrationReadFailure.MalformedResponse "invalid:comment-revision")
            else
                let payload = value.GetRawText()
                Ok { DatabaseId=id; NodeId=nodeId; SubjectNumber=issueNumber
                     ActorLogin=login; CreatedAt=createdAt; UpdatedAt=updatedAt
                     Body=body; PayloadJson=payload; PayloadSha256=sha payload }
        | Error failure, _, _, _, _, _, _ | _, Error failure, _, _, _, _, _
        | _, _, Error failure, _, _, _, _ | _, _, _, Error failure, _, _, _
        | _, _, _, _, Error failure, _, _ | _, _, _, _, _, Error failure, _
        | _, _, _, _, _, _, Error failure -> Error failure

    let private readSubjectComments (options: MigrationGitHubReadOptions) (subjectNumber: int)
                                    (subjectNodeId: string) (transport: IMigrationGitHubReadTransport) =
        readRepository options transport
        |> Result.bind (fun repositoryId ->
            let basePath =
                $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}/issues/{subjectNumber}"
            let expectedIssueUrl = Uri(options.ApiBase, basePath).AbsoluteUri
            let start = Uri(options.ApiBase, basePath + "/comments?per_page=100")
            let rec pages (seen: Set<string>) (count: int) (records: MigrationIssueCommentRecord list)
                          (evidence: MigrationRestPageEvidence list) (current: Uri) =
                if count >= 1000 || Set.contains current.AbsoluteUri seen then
                    Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                elif current.Scheme <> start.Scheme || current.Authority <> start.Authority
                     || current.AbsolutePath <> start.AbsolutePath
                     || not (exactCommentPageQuery count current) then
                    Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope")
                else
                    response transport (Rest { Method=Get; Uri=current; Headers=headers options.Token options.UserAgent
                                               Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
                    |> Result.bind (fun result -> parse result.Body |> Result.map (fun document -> result, document))
                    |> Result.bind (fun (result, document) ->
                        use document = document
                        if document.RootElement.ValueKind <> JsonValueKind.Array then
                            Error(MigrationReadFailure.MalformedResponse "comments-not-array")
                        else
                            let parsed =
                                document.RootElement.EnumerateArray()
                                |> Seq.map (parseIssueComment expectedIssueUrl subjectNumber) |> Seq.toList
                            match parsed |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                            | Some failure -> Error failure
                            | None ->
                                let values = parsed |> List.choose (function Ok value -> Some value | Error _ -> None)
                                let link = Map.tryFind "link" result.Headers |> Option.defaultValue ""
                                match Transport.tryNextLink link with
                                | Error failure -> Error(MigrationReadFailure.PaginationRefused $"{failure}")
                                | Ok next ->
                                    let page =
                                        { RequestedUri=current.AbsoluteUri; PayloadSha256=sha result.Body
                                          NextUri=next |> Option.map _.AbsoluteUri }
                                    let all = records @ values
                                    let allPages = evidence @ [ page ]
                                    match next with
                                    | Some uri -> pages (Set.add current.AbsoluteUri seen) (count + 1) all allPages uri
                                    | None ->
                                        collectUnique (fun (value: MigrationIssueCommentRecord) -> value.NodeId) all
                                        |> Result.bind (collectUnique (fun value -> string value.DatabaseId))
                                        |> Result.map (fun complete ->
                                            { RepositoryId=repositoryId; SubjectNumber=subjectNumber
                                              SubjectNodeId=subjectNodeId; PageCount=count + 1
                                              Terminal=true; Pages=allPages
                                              Comments=List.sortBy _.DatabaseId complete }))
            pages Set.empty 0 [] [] start)

    let readIssueComments (options: MigrationGitHubReadOptions) (issues: MigrationIssuePopulation)
                          (issueNumber: int) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        elif not issues.Terminal || issues.PageCount < 1 || issues.RepositoryId <> options.ExpectedRepositoryId then
            Error(MigrationReadFailure.SnapshotMismatch "issue-census")
        else
            match issues.Issues |> List.tryFind (fun issue -> issue.Number = issueNumber) with
            | None -> Error(MigrationReadFailure.SnapshotMismatch "uncensused-issue")
            | Some issue -> readSubjectComments options issueNumber issue.NodeId transport

    let readPullRequestComments (options: MigrationGitHubReadOptions) (pullRequests: MigrationPullRequestPopulation)
                                (pullRequestNumber: int) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        elif not pullRequests.Terminal || pullRequests.PageCount < 1
             || pullRequests.RepositoryId <> options.ExpectedRepositoryId then
            Error(MigrationReadFailure.SnapshotMismatch "pull-request-census")
        else
            match pullRequests.PullRequests |> List.tryFind (fun pullRequest -> pullRequest.Number = pullRequestNumber) with
            | None -> Error(MigrationReadFailure.SnapshotMismatch "uncensused-pull-request")
            | Some pullRequest -> readSubjectComments options pullRequestNumber pullRequest.NodeId transport

    let private parsePullRequestReview expectedUrl pullRequestNumber (value: JsonElement) =
        let optionalString name =
            property name value
            |> Result.bind (fun item ->
                if item.ValueKind = JsonValueKind.Null then Ok None
                elif item.ValueKind = JsonValueKind.String && text (item.GetString()) then Ok(Some(item.GetString()))
                else Error(MigrationReadFailure.MalformedResponse $"invalid:{name}"))
        let actor =
            property "user" value
            |> Result.bind (fun item ->
                if item.ValueKind = JsonValueKind.Null then Ok None
                else requiredString "login" item |> Result.map Some)
        match requiredInt64 "id" value, requiredString "node_id" value,
              requiredString "pull_request_url" value, requiredString "state" value,
              optionalString "commit_id", optionalString "submitted_at", actor with
        | Ok id, Ok nodeId, Ok url, Ok state, Ok commitSha, Ok submitted, Ok login ->
            let states = set [ "APPROVED"; "CHANGES_REQUESTED"; "COMMENTED"; "DISMISSED"; "PENDING" ]
            let validSha (sha: string) = sha.Length = 40 && (sha |> Seq.forall Uri.IsHexDigit)
            let mutable timestamp = DateTimeOffset.MinValue
            if url <> expectedUrl then Error MigrationReadFailure.IdentityDrift
            elif not (Set.contains state states) then
                Error(MigrationReadFailure.MalformedResponse "invalid:review-state")
            elif commitSha |> Option.exists (validSha >> not) then
                Error(MigrationReadFailure.MalformedResponse "invalid:review-commit")
            elif submitted |> Option.exists (fun value -> not (DateTimeOffset.TryParse(value, &timestamp))) then
                Error(MigrationReadFailure.MalformedResponse "invalid:review-revision")
            else
                let payload = value.GetRawText()
                Ok { DatabaseId=id; NodeId=nodeId; PullRequestNumber=pullRequestNumber
                     State=state; ActorLogin=login; CommitSha=commitSha
                     SubmittedAt=submitted |> Option.map (fun _ -> timestamp)
                     PayloadJson=payload; PayloadSha256=sha payload }
        | Error failure, _, _, _, _, _, _ | _, Error failure, _, _, _, _, _
        | _, _, Error failure, _, _, _, _ | _, _, _, Error failure, _, _, _
        | _, _, _, _, Error failure, _, _ | _, _, _, _, _, Error failure, _
        | _, _, _, _, _, _, Error failure -> Error failure

    let readPullRequestReviews (options: MigrationGitHubReadOptions) (pullRequests: MigrationPullRequestPopulation)
                               (pullRequestNumber: int) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        elif not pullRequests.Terminal || pullRequests.PageCount < 1
             || pullRequests.RepositoryId <> options.ExpectedRepositoryId then
            Error(MigrationReadFailure.SnapshotMismatch "pull-request-census")
        else
            match pullRequests.PullRequests |> List.tryFind (fun pullRequest -> pullRequest.Number = pullRequestNumber) with
            | None -> Error(MigrationReadFailure.SnapshotMismatch "uncensused-pull-request")
            | Some pullRequest ->
                readRepository options transport
                |> Result.bind (fun repositoryId ->
                    let basePath =
                        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}/pulls/{pullRequestNumber}"
                    let expectedUrl = Uri(options.ApiBase, basePath).AbsoluteUri
                    let start = Uri(options.ApiBase, basePath + "/reviews?per_page=100")
                    let rec pages (seen: Set<string>) (count: int) (records: MigrationPullRequestReviewRecord list)
                                  (evidence: MigrationRestPageEvidence list) (current: Uri) =
                        if count >= 1000 || Set.contains current.AbsoluteUri seen then
                            Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                        elif current.Scheme <> start.Scheme || current.Authority <> start.Authority
                             || current.AbsolutePath <> start.AbsolutePath
                             || not (exactCommentPageQuery count current) then
                            Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope")
                        else
                            response transport (Rest { Method=Get; Uri=current; Headers=headers options.Token options.UserAgent
                                                       Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
                            |> Result.bind (fun result -> parse result.Body |> Result.map (fun document -> result, document))
                            |> Result.bind (fun (result, document) ->
                                use document = document
                                if document.RootElement.ValueKind <> JsonValueKind.Array then
                                    Error(MigrationReadFailure.MalformedResponse "reviews-not-array")
                                else
                                    let parsed =
                                        document.RootElement.EnumerateArray()
                                        |> Seq.map (parsePullRequestReview expectedUrl pullRequestNumber) |> Seq.toList
                                    match parsed |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                                    | Some failure -> Error failure
                                    | None ->
                                        let values = parsed |> List.choose (function Ok value -> Some value | Error _ -> None)
                                        let link = Map.tryFind "link" result.Headers |> Option.defaultValue ""
                                        match Transport.tryNextLink link with
                                        | Error failure -> Error(MigrationReadFailure.PaginationRefused $"{failure}")
                                        | Ok next ->
                                            let page =
                                                { RequestedUri=current.AbsoluteUri; PayloadSha256=sha result.Body
                                                  NextUri=next |> Option.map _.AbsoluteUri }
                                            let all = records @ values
                                            let allPages = evidence @ [ page ]
                                            match next with
                                            | Some uri -> pages (Set.add current.AbsoluteUri seen) (count + 1) all allPages uri
                                            | None ->
                                                collectUnique (fun (value: MigrationPullRequestReviewRecord) -> value.NodeId) all
                                                |> Result.bind (collectUnique (fun value -> string value.DatabaseId))
                                                |> Result.map (fun complete ->
                                                    { RepositoryId=repositoryId; PullRequestNumber=pullRequestNumber
                                                      PullRequestNodeId=pullRequest.NodeId; PageCount=count + 1
                                                      Terminal=true; Pages=allPages
                                                      Reviews=List.sortBy _.DatabaseId complete }))
                    pages Set.empty 0 [] [] start)

    let private parsePullRequestReviewComment expectedUrl pullRequestNumber (value: JsonElement) =
        let reviewId =
            property "pull_request_review_id" value
            |> Result.bind (fun item ->
                if item.ValueKind = JsonValueKind.Null then Ok None
                else
                    let mutable parsed = 0L
                    if item.ValueKind = JsonValueKind.Number && item.TryGetInt64(&parsed) && parsed > 0L then
                        Ok(Some parsed)
                    else Error(MigrationReadFailure.MalformedResponse "invalid:pull_request_review_id"))
        match requiredInt64 "id" value, requiredString "node_id" value,
              requiredString "pull_request_url" value, requiredString "path" value,
              requiredString "body" value, requiredString "created_at" value,
              requiredString "updated_at" value, reviewId with
        | Ok id, Ok nodeId, Ok url, Ok path, Ok body, Ok created, Ok updated, Ok parentReview ->
            let mutable createdAt = DateTimeOffset.MinValue
            let mutable updatedAt = DateTimeOffset.MinValue
            if url <> expectedUrl then Error MigrationReadFailure.IdentityDrift
            elif not (DateTimeOffset.TryParse(created, &createdAt))
                 || not (DateTimeOffset.TryParse(updated, &updatedAt))
                 || updatedAt < createdAt then
                Error(MigrationReadFailure.MalformedResponse "invalid:review-comment-revision")
            else
                let payload = value.GetRawText()
                Ok { DatabaseId=id; NodeId=nodeId; PullRequestNumber=pullRequestNumber
                     ReviewId=parentReview; Path=path; Body=body
                     CreatedAt=createdAt; UpdatedAt=updatedAt
                     PayloadJson=payload; PayloadSha256=sha payload }
        | Error failure, _, _, _, _, _, _, _ | _, Error failure, _, _, _, _, _, _
        | _, _, Error failure, _, _, _, _, _ | _, _, _, Error failure, _, _, _, _
        | _, _, _, _, Error failure, _, _, _ | _, _, _, _, _, Error failure, _, _
        | _, _, _, _, _, _, Error failure, _ | _, _, _, _, _, _, _, Error failure -> Error failure

    let readPullRequestReviewComments (options: MigrationGitHubReadOptions)
                                      (pullRequests: MigrationPullRequestPopulation)
                                      (pullRequestNumber: int) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        elif not pullRequests.Terminal || pullRequests.PageCount < 1
             || pullRequests.RepositoryId <> options.ExpectedRepositoryId then
            Error(MigrationReadFailure.SnapshotMismatch "pull-request-census")
        else
            match pullRequests.PullRequests |> List.tryFind (fun pullRequest -> pullRequest.Number = pullRequestNumber) with
            | None -> Error(MigrationReadFailure.SnapshotMismatch "uncensused-pull-request")
            | Some pullRequest ->
                readRepository options transport
                |> Result.bind (fun repositoryId ->
                    let basePath =
                        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}/pulls/{pullRequestNumber}"
                    let expectedUrl = Uri(options.ApiBase, basePath).AbsoluteUri
                    let start = Uri(options.ApiBase, basePath + "/comments?per_page=100")
                    let rec pages (seen: Set<string>) (count: int)
                                  (records: MigrationPullRequestReviewCommentRecord list)
                                  (evidence: MigrationRestPageEvidence list) (current: Uri) =
                        if count >= 1000 || Set.contains current.AbsoluteUri seen then
                            Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                        elif current.Scheme <> start.Scheme || current.Authority <> start.Authority
                             || current.AbsolutePath <> start.AbsolutePath
                             || not (exactCommentPageQuery count current) then
                            Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope")
                        else
                            response transport (Rest { Method=Get; Uri=current; Headers=headers options.Token options.UserAgent
                                                       Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
                            |> Result.bind (fun result -> parse result.Body |> Result.map (fun document -> result, document))
                            |> Result.bind (fun (result, document) ->
                                use document = document
                                if document.RootElement.ValueKind <> JsonValueKind.Array then
                                    Error(MigrationReadFailure.MalformedResponse "review-comments-not-array")
                                else
                                    let parsed =
                                        document.RootElement.EnumerateArray()
                                        |> Seq.map (parsePullRequestReviewComment expectedUrl pullRequestNumber)
                                        |> Seq.toList
                                    match parsed |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                                    | Some failure -> Error failure
                                    | None ->
                                        let values = parsed |> List.choose (function Ok value -> Some value | Error _ -> None)
                                        let link = Map.tryFind "link" result.Headers |> Option.defaultValue ""
                                        match Transport.tryNextLink link with
                                        | Error failure -> Error(MigrationReadFailure.PaginationRefused $"{failure}")
                                        | Ok next ->
                                            let page =
                                                { RequestedUri=current.AbsoluteUri; PayloadSha256=sha result.Body
                                                  NextUri=next |> Option.map _.AbsoluteUri }
                                            let all = records @ values
                                            let allPages = evidence @ [ page ]
                                            match next with
                                            | Some uri -> pages (Set.add current.AbsoluteUri seen) (count + 1) all allPages uri
                                            | None ->
                                                collectUnique (fun (value: MigrationPullRequestReviewCommentRecord) -> value.NodeId) all
                                                |> Result.bind (collectUnique (fun value -> string value.DatabaseId))
                                                |> Result.map (fun complete ->
                                                    { RepositoryId=repositoryId; PullRequestNumber=pullRequestNumber
                                                      PullRequestNodeId=pullRequest.NodeId; PageCount=count + 1
                                                      Terminal=true; Pages=allPages
                                                      Comments=List.sortBy _.DatabaseId complete }))
                    pages Set.empty 0 [] [] start)

    let private parseIssueEvent issueNumber (value: JsonElement) =
        let actor =
            property "actor" value
            |> Result.bind (fun item ->
                if item.ValueKind = JsonValueKind.Null then Ok None
                else requiredString "login" item |> Result.map Some)
        match requiredInt64 "id" value, requiredString "node_id" value,
              requiredString "event" value, requiredString "created_at" value, actor with
        | Ok id, Ok nodeId, Ok eventKind, Ok created, Ok login ->
            let mutable createdAt = DateTimeOffset.MinValue
            if not (DateTimeOffset.TryParse(created, &createdAt)) then
                Error(MigrationReadFailure.MalformedResponse "invalid:event-revision")
            else
                let payload = value.GetRawText()
                Ok { DatabaseId=id; NodeId=nodeId; SubjectNumber=issueNumber
                     EventKind=eventKind; ActorLogin=login; CreatedAt=createdAt
                     PayloadJson=payload; PayloadSha256=sha payload }
        | Error failure, _, _, _, _ | _, Error failure, _, _, _
        | _, _, Error failure, _, _ | _, _, _, Error failure, _
        | _, _, _, _, Error failure -> Error failure

    let readIssueEvents (options: MigrationGitHubReadOptions) (issues: MigrationIssuePopulation)
                        (issueNumber: int) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        elif not issues.Terminal || issues.PageCount < 1 || issues.RepositoryId <> options.ExpectedRepositoryId then
            Error(MigrationReadFailure.SnapshotMismatch "issue-census")
        else
            match issues.Issues |> List.tryFind (fun issue -> issue.Number = issueNumber) with
            | None -> Error(MigrationReadFailure.SnapshotMismatch "uncensused-issue")
            | Some issue ->
                readRepository options transport
                |> Result.bind (fun repositoryId ->
                    let basePath =
                        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}/issues/{issueNumber}"
                    let start = Uri(options.ApiBase, basePath + "/events?per_page=100")
                    let rec pages (seen: Set<string>) (count: int) (records: MigrationIssueEventRecord list)
                                  (evidence: MigrationRestPageEvidence list) (current: Uri) =
                        if count >= 1000 || Set.contains current.AbsoluteUri seen then
                            Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                        elif current.Scheme <> start.Scheme || current.Authority <> start.Authority
                             || current.AbsolutePath <> start.AbsolutePath
                             || not (exactCommentPageQuery count current) then
                            Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope")
                        else
                            response transport (Rest { Method=Get; Uri=current; Headers=headers options.Token options.UserAgent
                                                       Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
                            |> Result.bind (fun result -> parse result.Body |> Result.map (fun document -> result, document))
                            |> Result.bind (fun (result, document) ->
                                use document = document
                                if document.RootElement.ValueKind <> JsonValueKind.Array then
                                    Error(MigrationReadFailure.MalformedResponse "events-not-array")
                                else
                                    let parsed =
                                        document.RootElement.EnumerateArray()
                                        |> Seq.map (parseIssueEvent issueNumber) |> Seq.toList
                                    match parsed |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                                    | Some failure -> Error failure
                                    | None ->
                                        let values = parsed |> List.choose (function Ok value -> Some value | Error _ -> None)
                                        let link = Map.tryFind "link" result.Headers |> Option.defaultValue ""
                                        match Transport.tryNextLink link with
                                        | Error failure -> Error(MigrationReadFailure.PaginationRefused $"{failure}")
                                        | Ok next ->
                                            let page =
                                                { RequestedUri=current.AbsoluteUri; PayloadSha256=sha result.Body
                                                  NextUri=next |> Option.map _.AbsoluteUri }
                                            let all = records @ values
                                            let allPages = evidence @ [ page ]
                                            match next with
                                            | Some uri -> pages (Set.add current.AbsoluteUri seen) (count + 1) all allPages uri
                                            | None ->
                                                collectUnique (fun (value: MigrationIssueEventRecord) -> value.NodeId) all
                                                |> Result.bind (collectUnique (fun value -> string value.DatabaseId))
                                                |> Result.map (fun complete ->
                                                    { RepositoryId=repositoryId; SubjectNumber=issueNumber
                                                      SubjectNodeId=issue.NodeId; PageCount=count + 1
                                                      Terminal=true; Pages=allPages
                                                      Events=List.sortBy _.DatabaseId complete }))
                    pages Set.empty 0 [] [] start)

    let private issueTypeQuery =
        "query($owner:String!,$name:String!,$after:String) { repository(owner:$owner,name:$name) { databaseId issueTypes(first:100,after:$after) { nodes { id name } pageInfo { hasNextPage endCursor } } } }"

    let readIssueTypes (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (valid options) then Error MigrationReadFailure.InvalidOptions
        else
            let rec pages (seen: Set<string>) (count: int) (accumulated: MigrationIssueTypeRecord list)
                          (cursor: string option) =
                if count >= 1000 || (cursor |> Option.exists (fun value -> Set.contains value seen)) then
                    Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                else
                    let variables =
                        [ "owner", options.Owner; "name", options.Repository
                          match cursor with Some value -> "after", value | None -> () ]
                        |> Map.ofList
                    let request =
                        GraphQL { Uri=options.GraphQLUri; Document=issueTypeQuery; Variables=variables
                                  Headers=headers options.Token options.UserAgent; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
                    response transport request
                    |> Result.bind (fun result -> parse result.Body)
                    |> Result.bind (fun document ->
                        use document = document
                        let root = document.RootElement
                        let mutable errors = Unchecked.defaultof<JsonElement>
                        if root.TryGetProperty("errors", &errors) then
                            // GraphQL can return both useful data and an authorization error. It is never complete.
                            Error MigrationReadFailure.GraphQLErrors
                        else
                            match property "data" root |> Result.bind (property "repository") with
                            | Error error -> Error error
                            | Ok repository when repository.ValueKind = JsonValueKind.Null ->
                                Error MigrationReadFailure.IdentityDrift
                            | Ok repository ->
                                match requiredInt64 "databaseId" repository, property "issueTypes" repository with
                                | Ok repositoryId, Ok connection when repositoryId = options.ExpectedRepositoryId ->
                                    match property "nodes" connection, property "pageInfo" connection with
                                    | Ok nodes, Ok pageInfo when nodes.ValueKind = JsonValueKind.Array ->
                                        let records =
                                            nodes.EnumerateArray()
                                            |> Seq.map (fun node ->
                                                match requiredString "id" node, requiredString "name" node with
                                                | Ok id, Ok name ->
                                                    let payload = node.GetRawText()
                                                    Ok { NodeId=id; Name=name; PayloadJson=payload; PayloadSha256=sha payload }
                                                | Error error, _ | _, Error error -> Error error)
                                            |> Seq.toList
                                        match records |> List.tryPick (function Error error -> Some error | _ -> None) with
                                        | Some error -> Error error
                                        | None ->
                                            match property "hasNextPage" pageInfo, property "endCursor" pageInfo with
                                            | Ok hasNext, Ok endCursor when hasNext.ValueKind = JsonValueKind.True
                                                                    || hasNext.ValueKind = JsonValueKind.False ->
                                                let values = records |> List.choose (function Ok value -> Some value | _ -> None)
                                                let combined = accumulated @ values
                                                if hasNext.GetBoolean() then
                                                    if endCursor.ValueKind <> JsonValueKind.String || not (text (endCursor.GetString())) then
                                                        Error(MigrationReadFailure.PaginationRefused "missing-end-cursor")
                                                    else
                                                        pages (cursor |> Option.map (fun value -> Set.add value seen) |> Option.defaultValue seen)
                                                              (count + 1) combined (Some(endCursor.GetString()))
                                                else
                                                    collectUnique (fun (item: MigrationIssueTypeRecord) -> item.NodeId) combined
                                                    |> Result.map (fun complete ->
                                                        { RepositoryId=repositoryId; PageCount=count + 1; Terminal=true
                                                          IssueTypes=List.sortBy _.NodeId complete })
                                            | _ -> Error(MigrationReadFailure.MalformedResponse "invalid:pageInfo")
                                    | _ -> Error(MigrationReadFailure.MalformedResponse "invalid:issueTypes")
                                | Ok _, _ -> Error MigrationReadFailure.IdentityDrift
                                | Error error, _ | _, Error error -> Error error)
            pages Set.empty 0 [] None

    // Initial relation pages share one issue identity and revision; continuations
    // are fetched only for these three closed connection names.
    let private nativeRelationsQuery =
        "query($id:ID!) { node(id:$id) { ... on Issue { id number updatedAt repository { databaseId } parent { id repository { databaseId } } subIssues(first:100) { totalCount nodes { id repository { databaseId } } pageInfo { hasNextPage endCursor } } blockedBy(first:100) { totalCount nodes { id repository { databaseId } } pageInfo { hasNextPage endCursor } } blocking(first:100) { totalCount nodes { id repository { databaseId } } pageInfo { hasNextPage endCursor } } } } }"

    let private relationContinuationQuery connection =
        let field =
            match connection with
            | "subIssues" -> "subIssues"
            | "blockedBy" -> "blockedBy"
            | "blocking" -> "blocking"
            | _ -> invalidArg "connection" "relation connection is outside the closed reader"
        $"query($id:ID!,$after:String!) {{ node(id:$id) {{ ... on Issue {{ id number updatedAt repository {{ databaseId }} {field}(first:100,after:$after) {{ totalCount nodes {{ id repository {{ databaseId }} }} pageInfo {{ hasNextPage endCursor }} }} }} }} }}"

    type private RelationConnectionPage =
        { TotalCount: int
          Nodes: MigrationRelationEndpoint list
          NextCursor: string option }

    let private relationEndpoint (value: JsonElement) =
        match requiredString "id" value,
              property "repository" value |> Result.bind (requiredInt64 "databaseId") with
        | Ok id, Ok repositoryId -> Ok { NodeId=id; RepositoryId=repositoryId }
        | Error failure, _ | _, Error failure -> Error failure

    let private relationConnection name (issue: JsonElement) =
        property name issue
        |> Result.bind (fun connection ->
            match property "totalCount" connection, property "nodes" connection,
                  property "pageInfo" connection with
            | Ok total, Ok nodes, Ok pageInfo when nodes.ValueKind = JsonValueKind.Array ->
                let mutable count = 0
                if total.ValueKind <> JsonValueKind.Number || not (total.TryGetInt32(&count)) || count < 0 then
                    Error(MigrationReadFailure.MalformedResponse $"invalid:{name}:totalCount")
                else
                    match requiredBool "hasNextPage" pageInfo, property "endCursor" pageInfo with
                    | Ok hasNext, Ok endCursor ->
                        let parsed = nodes.EnumerateArray() |> Seq.map relationEndpoint |> Seq.toList
                        match parsed |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                        | Some failure -> Error failure
                        | None ->
                            let values = parsed |> List.choose (function Ok value -> Some value | _ -> None)
                            collectUnique (fun endpoint -> endpoint.NodeId) values
                            |> Result.bind (fun unique ->
                                if hasNext && (endCursor.ValueKind <> JsonValueKind.String
                                               || not (text (endCursor.GetString()))
                                               || values.IsEmpty) then
                                    Error(MigrationReadFailure.PaginationRefused $"{name}:missing-end-cursor-or-page")
                                elif not hasNext && values.Length > count then
                                    Error MigrationReadFailure.PopulationDrift
                                else
                                    Ok { TotalCount=count; Nodes=unique
                                         NextCursor=if hasNext then Some(endCursor.GetString()) else None })
                    | Error failure, _ | _, Error failure -> Error failure
            | Ok _, Ok _, Ok _ -> Error(MigrationReadFailure.MalformedResponse $"invalid:{name}:nodes")
            | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure)

    let readNativeRelations (options: MigrationGitHubReadOptions)
                            (issues: MigrationIssuePopulation)
                            (transport: IMigrationGitHubReadTransport) =
        if not (valid options) || issues.RepositoryId <> options.ExpectedRepositoryId
           || not issues.Terminal || issues.PageCount < 1 then
            Error MigrationReadFailure.InvalidOptions
        else
            let issueIds = issues.Issues |> List.map _.NodeId |> Set.ofList
            if issueIds.Count <> issues.Issues.Length
               || (issues.Issues |> List.exists (fun item ->
                   not (text item.NodeId) || item.DatabaseId <= 0L || item.Number <= 0
                   || sha item.PayloadJson <> item.PayloadSha256)) then
                Error(MigrationReadFailure.SnapshotMismatch "invalid-issue-census")
            else
                let readContinuationPage (source: MigrationIssueRecord) connection cursor =
                    let request =
                        GraphQL { Uri=options.GraphQLUri
                                  Document=relationContinuationQuery connection
                                  Variables=Map.ofList [ "id", source.NodeId; "after", cursor ]
                                  Headers=headers options.Token options.UserAgent
                                  ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
                    response transport request
                    |> Result.bind (fun result -> parse result.Body)
                    |> Result.bind (fun document ->
                        use document = document
                        let root = document.RootElement
                        let mutable errors = Unchecked.defaultof<JsonElement>
                        if root.TryGetProperty("errors", &errors) then Error MigrationReadFailure.GraphQLErrors
                        else
                            match property "data" root |> Result.bind (property "node") with
                            | Error failure -> Error failure
                            | Ok issue when issue.ValueKind <> JsonValueKind.Object ->
                                Error MigrationReadFailure.IdentityDrift
                            | Ok issue ->
                                match relationEndpoint issue, requiredInt "number" issue,
                                      requiredString "updatedAt" issue,
                                      relationConnection connection issue with
                                | Ok current, Ok number, Ok updated, Ok page
                                    when current.NodeId = source.NodeId
                                         && current.RepositoryId = options.ExpectedRepositoryId
                                         && number = source.Number ->
                                    let mutable timestamp = DateTimeOffset.MinValue
                                    if not (DateTimeOffset.TryParse(updated, &timestamp)) then
                                        Error(MigrationReadFailure.MalformedResponse "invalid:relation-updatedAt")
                                    elif timestamp <> source.UpdatedAt then
                                        Error MigrationReadFailure.PopulationDrift
                                    else
                                        let payload = issue.GetRawText()
                                        Ok (page,
                                            { Connection=connection; RequestedCursor=cursor
                                              PayloadJson=payload; PayloadSha256=sha payload })
                                | Ok _, Ok _, Ok _, Ok _ -> Error MigrationReadFailure.IdentityDrift
                                | Error failure, _, _, _ | _, Error failure, _, _
                                | _, _, Error failure, _ | _, _, _, Error failure -> Error failure)

                let followConnection (source: MigrationIssueRecord) name
                                     (initial: RelationConnectionPage) =
                    let mutable nodes = initial.Nodes
                    let mutable next = initial.NextCursor
                    let mutable seen = Set.empty<string>
                    let mutable pages: MigrationRelationContinuationPage list = []
                    let mutable failure: MigrationReadFailure option = None
                    while next.IsSome && failure.IsNone do
                        let cursor = next.Value
                        if pages.Length >= 1000 || Set.contains cursor seen then
                            failure <- Some(MigrationReadFailure.PaginationRefused $"{name}:cycle-or-page-limit")
                        else
                            seen <- Set.add cursor seen
                            match readContinuationPage source name cursor with
                            | Error reason -> failure <- Some reason
                            | Ok (page, evidence) when page.TotalCount <> initial.TotalCount ->
                                failure <- Some MigrationReadFailure.PopulationDrift
                            | Ok (page, evidence) ->
                                nodes <- nodes @ page.Nodes
                                pages <- evidence :: pages
                                next <- page.NextCursor
                                if nodes.Length > initial.TotalCount then
                                    failure <- Some MigrationReadFailure.PopulationDrift
                    match failure with
                    | Some reason -> Error reason
                    | None when nodes.Length <> initial.TotalCount -> Error MigrationReadFailure.PopulationDrift
                    | None ->
                        collectUnique (fun endpoint -> endpoint.NodeId) nodes
                        |> Result.map (fun complete -> complete, List.rev pages)

                let readOne (source: MigrationIssueRecord) =
                    let request =
                        GraphQL { Uri=options.GraphQLUri; Document=nativeRelationsQuery
                                  Variables=Map.ofList [ "id", source.NodeId ]
                                  Headers=headers options.Token options.UserAgent
                                  ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
                    response transport request
                    |> Result.bind (fun result -> parse result.Body)
                    |> Result.bind (fun document ->
                        use document = document
                        let root = document.RootElement
                        let mutable errors = Unchecked.defaultof<JsonElement>
                        if root.TryGetProperty("errors", &errors) then Error MigrationReadFailure.GraphQLErrors
                        else
                            match property "data" root |> Result.bind (property "node") with
                            | Error failure -> Error failure
                            | Ok issue when issue.ValueKind <> JsonValueKind.Object ->
                                Error MigrationReadFailure.IdentityDrift
                            | Ok issue ->
                                match relationEndpoint issue, requiredInt "number" issue,
                                      requiredString "updatedAt" issue,
                                      property "parent" issue,
                                      relationConnection "subIssues" issue,
                                      relationConnection "blockedBy" issue,
                                      relationConnection "blocking" issue with
                                | Ok current, Ok number, Ok updated, Ok parent,
                                  Ok children, Ok blockers, Ok blocked
                                    when current.NodeId = source.NodeId
                                         && current.RepositoryId = options.ExpectedRepositoryId
                                         && number = source.Number ->
                                    let mutable timestamp = DateTimeOffset.MinValue
                                    if not (DateTimeOffset.TryParse(updated, &timestamp)) then
                                        Error(MigrationReadFailure.MalformedResponse "invalid:relation-updatedAt")
                                    elif timestamp <> source.UpdatedAt then
                                        Error MigrationReadFailure.PopulationDrift
                                    else
                                        let parentResult =
                                            if parent.ValueKind = JsonValueKind.Null then Ok None
                                            else relationEndpoint parent |> Result.map Some
                                        parentResult
                                        |> Result.bind (fun parentValue ->
                                            followConnection source "subIssues" children
                                            |> Result.bind (fun (allChildren, childPages) ->
                                                followConnection source "blockedBy" blockers
                                                |> Result.bind (fun (allBlockers, blockerPages) ->
                                                    followConnection source "blocking" blocked
                                                    |> Result.bind (fun (allBlocked, blockedPages) ->
                                                        let edges =
                                                            [ match parentValue with
                                                              | Some endpoint ->
                                                                  { Kind=MigrationRelationKind.ParentChild; Source=endpoint; Target=current }
                                                              | None -> ()
                                                              for endpoint in allChildren do
                                                                  { Kind=MigrationRelationKind.ParentChild; Source=current; Target=endpoint }
                                                              for endpoint in allBlockers do
                                                                  { Kind=MigrationRelationKind.Blocks; Source=endpoint; Target=current }
                                                              for endpoint in allBlocked do
                                                                  { Kind=MigrationRelationKind.Blocks; Source=current; Target=endpoint } ]
                                                        if edges |> List.exists (fun edge -> edge.Source = edge.Target) then
                                                            Error(MigrationReadFailure.SnapshotMismatch "self-relation")
                                                        else
                                                            let payload = issue.GetRawText()
                                                            Ok ({ IssueNodeId=current.NodeId; UpdatedAt=timestamp
                                                                  PayloadJson=payload; PayloadSha256=sha payload
                                                                  ContinuationPages=childPages @ blockerPages @ blockedPages }, edges)))))
                                | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ ->
                                    Error MigrationReadFailure.IdentityDrift
                                | Error failure, _, _, _, _, _, _
                                | _, Error failure, _, _, _, _, _
                                | _, _, Error failure, _, _, _, _
                                | _, _, _, Error failure, _, _, _
                                | _, _, _, _, Error failure, _, _
                                | _, _, _, _, _, Error failure, _
                                | _, _, _, _, _, _, Error failure -> Error failure)
                let mutable records: MigrationIssueRelationRecord list = []
                let mutable edges: MigrationRelationEdge list = []
                let mutable failure: MigrationReadFailure option = None
                for source in issues.Issues do
                    if failure.IsNone then
                        match readOne source with
                        | Ok (record, observedEdges) ->
                            records <- record :: records
                            edges <- observedEdges @ edges
                        | Error reason -> failure <- Some reason
                match failure with
                | Some reason -> Error reason
                | None ->
                        let grouped = edges |> List.countBy id
                        let invalid =
                            grouped |> List.tryFind (fun (edge, count) ->
                                let endpoints = [edge.Source; edge.Target]
                                let local =
                                    endpoints |> List.filter (fun endpoint ->
                                        endpoint.RepositoryId = options.ExpectedRepositoryId)
                                (local |> List.exists (fun endpoint -> not (Set.contains endpoint.NodeId issueIds)))
                                || count <> (if local.Length = 2 then 2 else 1))
                        match invalid with
                        | Some _ -> Error(MigrationReadFailure.SnapshotMismatch "relation-reciprocity-or-census")
                        | None ->
                            let uniqueEdges = grouped |> List.map fst
                            let sorted =
                                uniqueEdges
                                |> List.sortBy (fun edge ->
                                    (edge.Kind, edge.Source.RepositoryId, edge.Source.NodeId,
                                     edge.Target.RepositoryId, edge.Target.NodeId))
                            let externalCount =
                                sorted |> List.filter (fun edge ->
                                    edge.Source.RepositoryId <> options.ExpectedRepositoryId
                                    || edge.Target.RepositoryId <> options.ExpectedRepositoryId) |> List.length
                            Ok { RepositoryId=options.ExpectedRepositoryId; IssueCount=records.Length
                                 CompleteForRepository=true; ExternalEdgeCount=externalCount
                                 Edges=sorted; Issues=List.rev records }

    let private projectQuery number =
        $"""query($owner:String!,$after:String) {{ organization(login:$owner) {{ projectV2(number:{number}) {{ id number items(first:100,after:$after) {{ totalCount nodes {{ id isArchived updatedAt content {{ __typename ... on Issue {{ id number repository {{ databaseId }} }} ... on PullRequest {{ id number repository {{ databaseId }} }} ... on DraftIssue {{ id }} }} }} pageInfo {{ hasNextPage endCursor }} }} }} }} }}"""

    let private nonNegativeInt name value =
        property name value
        |> Result.bind (fun item ->
            let mutable parsed = 0
            if item.ValueKind = JsonValueKind.Number && item.TryGetInt32(&parsed) && parsed >= 0 then Ok parsed
            else Error(MigrationReadFailure.MalformedResponse $"invalid:{name}"))

    let private projectContent (item: JsonElement) =
        match property "content" item with
        | Error failure -> Error failure
        | Ok content when content.ValueKind <> JsonValueKind.Object ->
            Error(MigrationReadFailure.MalformedResponse "invalid:project-content")
        | Ok content ->
            match requiredString "__typename" content, requiredString "id" content with
            | Ok "DraftIssue", Ok id -> Ok(MigrationProjectContent.DraftIssue id)
            | Ok kind, Ok id when kind = "Issue" || kind = "PullRequest" ->
                match requiredInt "number" content,
                      property "repository" content |> Result.bind (requiredInt64 "databaseId") with
                | Ok number, Ok repositoryId ->
                    if kind = "Issue" then Ok(MigrationProjectContent.Issue(id, repositoryId, number))
                    else Ok(MigrationProjectContent.PullRequest(id, repositoryId, number))
                | Error failure, _ | _, Error failure -> Error failure
            | Ok _, Ok _ -> Error(MigrationReadFailure.MalformedResponse "unsupported:project-content-kind")
            | Error failure, _ | _, Error failure -> Error failure

    let private projectItem (value: JsonElement) =
        match requiredString "id" value, requiredBool "isArchived" value,
              requiredString "updatedAt" value, projectContent value with
        | Ok id, Ok archived, Ok updated, Ok content ->
            let mutable timestamp = DateTimeOffset.MinValue
            if DateTimeOffset.TryParse(updated, &timestamp) then
                let payload = value.GetRawText()
                Ok { ItemNodeId=id; Archived=archived; UpdatedAt=timestamp
                     Content=content; PayloadJson=payload; PayloadSha256=sha payload }
            else Error(MigrationReadFailure.MalformedResponse "invalid:project-updatedAt")
        | Error failure, _, _, _ | _, Error failure, _, _
        | _, _, Error failure, _ | _, _, _, Error failure -> Error failure

    let private validProjectOptions (options: MigrationProjectReadOptions) =
        not (isNull options.GraphQLUri) && options.GraphQLUri.IsAbsoluteUri
        && options.GraphQLUri.Scheme = Uri.UriSchemeHttps
        && text options.UserAgent && text options.Organization && text options.ExpectedProjectNodeId
        && options.ProjectNumber > 0

    let readProjectItems (options: MigrationProjectReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validProjectOptions options) then
            Error MigrationReadFailure.InvalidOptions
        else
            let rec pages (seen: Set<string>) (count: int) (population: int option)
                          (accumulated: MigrationProjectItemRecord list) (cursor: string option) =
                if count >= 1000 || (cursor |> Option.exists (fun value -> Set.contains value seen)) then
                    Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                else
                    let variables =
                        [ "owner", options.Organization
                          match cursor with Some value -> "after", value | None -> () ]
                        |> Map.ofList
                    let request =
                        GraphQL { Uri=options.GraphQLUri; Document=projectQuery options.ProjectNumber
                                  Variables=variables; Headers=headers options.Token options.UserAgent
                                  ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
                    response transport request
                    |> Result.bind (fun result -> parse result.Body)
                    |> Result.bind (fun document ->
                        use document = document
                        let root = document.RootElement
                        let mutable errors = Unchecked.defaultof<JsonElement>
                        if root.TryGetProperty("errors", &errors) then Error MigrationReadFailure.GraphQLErrors
                        else
                            match property "data" root |> Result.bind (property "organization")
                                  |> Result.bind (property "projectV2") with
                            | Error failure -> Error failure
                            | Ok project when project.ValueKind = JsonValueKind.Null ->
                                Error MigrationReadFailure.IdentityDrift
                            | Ok project ->
                                match requiredString "id" project, requiredInt "number" project,
                                      property "items" project with
                                | Ok id, Ok number, Ok connection when
                                    id = options.ExpectedProjectNodeId && number = options.ProjectNumber ->
                                    match nonNegativeInt "totalCount" connection,
                                          property "nodes" connection, property "pageInfo" connection with
                                    | Ok total, Ok nodes, Ok pageInfo when nodes.ValueKind = JsonValueKind.Array ->
                                        if population |> Option.exists ((<>) total) then
                                            Error MigrationReadFailure.PopulationDrift
                                        else
                                            let parsed = nodes.EnumerateArray() |> Seq.map projectItem |> Seq.toList
                                            match parsed |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                                            | Some failure -> Error failure
                                            | None ->
                                                let values = parsed |> List.choose (function Ok value -> Some value | _ -> None)
                                                let combined = accumulated @ values
                                                match requiredBool "hasNextPage" pageInfo,
                                                      property "endCursor" pageInfo with
                                                | Ok true, Ok endCursor when endCursor.ValueKind = JsonValueKind.String
                                                                          && text (endCursor.GetString()) ->
                                                    pages (cursor |> Option.map (fun value -> Set.add value seen)
                                                                  |> Option.defaultValue seen)
                                                          (count + 1) (Some total) combined (Some(endCursor.GetString()))
                                                | Ok false, Ok _ when combined.Length = total ->
                                                    collectUnique (fun (value: MigrationProjectItemRecord) -> value.ItemNodeId) combined
                                                    |> Result.map (fun complete ->
                                                        let result: MigrationProjectItemPopulation =
                                                            { ProjectNodeId=id; PageCount=count + 1; Terminal=true
                                                              TotalCount=total
                                                              Items=List.sortBy (fun (item: MigrationProjectItemRecord) -> item.ItemNodeId) complete }
                                                        result)
                                                | Ok false, Ok _ -> Error MigrationReadFailure.PopulationDrift
                                                | Ok true, _ -> Error(MigrationReadFailure.PaginationRefused "missing-end-cursor")
                                                | Error failure, _ | _, Error failure -> Error failure
                                    | _ -> Error(MigrationReadFailure.MalformedResponse "invalid:project-items")
                                | Ok _, Ok _, _ -> Error MigrationReadFailure.IdentityDrift
                                | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure)
            pages Set.empty 0 None [] None

    let private projectFieldQuery number =
        $"""query($owner:String!,$after:String) {{ organization(login:$owner) {{ projectV2(number:{number}) {{ id number fields(first:100,after:$after) {{ totalCount nodes {{ __typename ... on ProjectV2FieldCommon {{ id name dataType }} ... on ProjectV2SingleSelectField {{ options {{ id name }} }} ... on ProjectV2MultiSelectField {{ multiSelectOptions {{ id name }} }} ... on ProjectV2IterationField {{ configuration {{ iterations {{ id title }} completedIterations {{ id title }} }} }} }} pageInfo {{ hasNextPage endCursor }} }} }} }} }}"""

    let private fieldOption nameProperty (value: JsonElement) =
        match requiredString "id" value, requiredString nameProperty value with
        | Ok id, Ok name -> Ok { Id=id; Name=name }
        | Error failure, _ | _, Error failure -> Error failure

    let private fieldOptions (nameProperty: string) (arrayValue: JsonElement) =
        if arrayValue.ValueKind <> JsonValueKind.Array then
            Error(MigrationReadFailure.MalformedResponse "invalid:field-options")
        else
            let parsed = arrayValue.EnumerateArray() |> Seq.map (fieldOption nameProperty) |> Seq.toList
            match parsed |> List.tryPick (function Error failure -> Some failure | _ -> None) with
            | Some failure -> Error failure
            | None ->
                let options = parsed |> List.choose (function Ok value -> Some value | _ -> None)
                collectUnique (fun option -> option.Id) options
                |> Result.bind (collectUnique (fun option -> option.Name))

    let private projectField (value: JsonElement) =
        let optionSource =
            match requiredString "__typename" value with
            | Ok "ProjectV2Field" -> Ok(MigrationProjectFieldKind.BuiltIn, [])
            | Ok "ProjectV2SingleSelectField" ->
                property "options" value
                |> Result.bind (fieldOptions "name")
                |> Result.map (fun values -> MigrationProjectFieldKind.SingleSelect, values)
            | Ok "ProjectV2MultiSelectField" ->
                property "multiSelectOptions" value
                |> Result.bind (fieldOptions "name")
                |> Result.map (fun values -> MigrationProjectFieldKind.MultiSelect, values)
            | Ok "ProjectV2IterationField" ->
                property "configuration" value
                |> Result.bind (fun configuration ->
                    match property "iterations" configuration, property "completedIterations" configuration with
                    | Ok current, Ok completed ->
                        match fieldOptions "title" current, fieldOptions "title" completed with
                        | Ok currentOptions, Ok completedOptions ->
                            collectUnique (fun option -> option.Id) (currentOptions @ completedOptions)
                            |> Result.map (fun values -> MigrationProjectFieldKind.Iteration, values)
                        | Error failure, _ | _, Error failure -> Error failure
                    | Error failure, _ | _, Error failure -> Error failure)
            | Ok _ -> Error(MigrationReadFailure.MalformedResponse "unsupported:project-field-kind")
            | Error failure -> Error failure
        match requiredString "id" value, requiredString "name" value,
              requiredString "dataType" value, optionSource with
        | Ok id, Ok name, Ok dataType, Ok(kind, options) when
            (match kind, dataType with
             | MigrationProjectFieldKind.SingleSelect, "SINGLE_SELECT"
             | MigrationProjectFieldKind.MultiSelect, "MULTI_SELECT"
             | MigrationProjectFieldKind.Iteration, "ITERATION" -> true
             | MigrationProjectFieldKind.BuiltIn, value ->
                 Set.contains value
                     (Set.ofList [ "ASSIGNEES"; "LINKED_PULL_REQUESTS"; "REVIEWERS"; "LABELS"
                                   "MILESTONE"; "REPOSITORY"; "TITLE"; "TEXT"; "NUMBER"; "DATE"
                                   "TRACKS"; "TRACKED_BY"; "ISSUE_TYPE"; "PARENT_ISSUE"
                                   "SUB_ISSUES_PROGRESS"; "CREATED"; "UPDATED"; "CLOSED" ])
             | _ -> false) ->
            let payload = value.GetRawText()
            Ok { FieldNodeId=id; Name=name; DataType=dataType; Kind=kind
                 Options=options; PayloadJson=payload; PayloadSha256=sha payload }
        | Ok _, Ok _, Ok _, Ok _ ->
            Error(MigrationReadFailure.MalformedResponse "unsupported:project-field-data-type")
        | Error failure, _, _, _ | _, Error failure, _, _
        | _, _, Error failure, _ | _, _, _, Error failure -> Error failure

    let readProjectFields (options: MigrationProjectReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validProjectOptions options) then Error MigrationReadFailure.InvalidOptions
        else
            let rec pages (seen: Set<string>) (count: int) (population: int option)
                          (accumulated: MigrationProjectFieldRecord list) (cursor: string option) =
                if count >= 1000 || (cursor |> Option.exists (fun value -> Set.contains value seen)) then
                    Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                else
                    let variables =
                        [ "owner", options.Organization
                          match cursor with Some value -> "after", value | None -> () ]
                        |> Map.ofList
                    let request =
                        GraphQL { Uri=options.GraphQLUri; Document=projectFieldQuery options.ProjectNumber
                                  Variables=variables; Headers=headers options.Token options.UserAgent
                                  ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
                    response transport request
                    |> Result.bind (fun result -> parse result.Body)
                    |> Result.bind (fun document ->
                        use document = document
                        let root = document.RootElement
                        let mutable errors = Unchecked.defaultof<JsonElement>
                        if root.TryGetProperty("errors", &errors) then Error MigrationReadFailure.GraphQLErrors
                        else
                            match property "data" root |> Result.bind (property "organization")
                                  |> Result.bind (property "projectV2") with
                            | Error failure -> Error failure
                            | Ok project when project.ValueKind = JsonValueKind.Null ->
                                Error MigrationReadFailure.IdentityDrift
                            | Ok project ->
                                match requiredString "id" project, requiredInt "number" project,
                                      property "fields" project with
                                | Ok id, Ok number, Ok connection when
                                    id = options.ExpectedProjectNodeId && number = options.ProjectNumber ->
                                    match nonNegativeInt "totalCount" connection,
                                          property "nodes" connection, property "pageInfo" connection with
                                    | Ok total, Ok nodes, Ok pageInfo when nodes.ValueKind = JsonValueKind.Array ->
                                        if population |> Option.exists ((<>) total) then
                                            Error MigrationReadFailure.PopulationDrift
                                        else
                                            let parsed = nodes.EnumerateArray() |> Seq.map projectField |> Seq.toList
                                            match parsed |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                                            | Some failure -> Error failure
                                            | None ->
                                                let values = parsed |> List.choose (function Ok value -> Some value | _ -> None)
                                                let combined = accumulated @ values
                                                match requiredBool "hasNextPage" pageInfo,
                                                      property "endCursor" pageInfo with
                                                | Ok true, Ok endCursor when endCursor.ValueKind = JsonValueKind.String
                                                                          && text (endCursor.GetString()) ->
                                                    pages (cursor |> Option.map (fun value -> Set.add value seen)
                                                                  |> Option.defaultValue seen)
                                                          (count + 1) (Some total) combined (Some(endCursor.GetString()))
                                                | Ok false, Ok _ when combined.Length = total ->
                                                    collectUnique (fun (field: MigrationProjectFieldRecord) -> field.FieldNodeId) combined
                                                    |> Result.bind (collectUnique (fun field -> field.Name))
                                                    |> Result.map (fun complete ->
                                                        { ProjectNodeId=id; PageCount=count + 1; Terminal=true
                                                          TotalCount=total; Fields=List.sortBy _.FieldNodeId complete })
                                                | Ok false, Ok _ -> Error MigrationReadFailure.PopulationDrift
                                                | Ok true, _ -> Error(MigrationReadFailure.PaginationRefused "missing-end-cursor")
                                                | Error failure, _ | _, Error failure -> Error failure
                                    | _ -> Error(MigrationReadFailure.MalformedResponse "invalid:project-fields")
                                | Ok _, Ok _, _ -> Error MigrationReadFailure.IdentityDrift
                                | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure)
            pages Set.empty 0 None [] None

    let private projectValueQueryTemplate =
        let name = "FS.GG.Coordination.GitHub.MigrationProjectValues.graphql"
        use stream = typeof<MigrationProjectReadOptions>.Assembly.GetManifestResourceStream name
        if isNull stream then invalidOp $"missing-embedded-query:{name}"
        use reader = new StreamReader(stream, Encoding.UTF8)
        reader.ReadToEnd()

    let private nestedIds (name: string) (value: JsonElement) =
        match property name value with
        | Error failure -> Error failure
        | Ok connection ->
            match nonNegativeInt "totalCount" connection,
                  property "nodes" connection, property "pageInfo" connection with
            | Ok total, Ok nodes, Ok pageInfo when nodes.ValueKind = JsonValueKind.Array ->
                match requiredBool "hasNextPage" pageInfo, property "endCursor" pageInfo with
                | Ok false, Ok _ when nodes.GetArrayLength() = total ->
                    let parsed =
                        nodes.EnumerateArray()
                        |> Seq.map (requiredString "id")
                        |> Seq.toList
                    match parsed |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                    | Some failure -> Error failure
                    | None ->
                        let ids = parsed |> List.choose (function Ok id -> Some id | _ -> None)
                        collectUnique id ids |> Result.map ignore
                | Ok true, _ -> Error(MigrationReadFailure.PaginationRefused $"nested:{name}")
                | Ok false, Ok _ -> Error MigrationReadFailure.PopulationDrift
                | Error failure, _ | _, Error failure -> Error failure
            | _ -> Error(MigrationReadFailure.MalformedResponse $"invalid:nested:{name}")

    let private requiredValueProperty name value =
        property name value |> Result.map ignore

    let private projectFieldValue (value: JsonElement) =
        let fieldId =
            property "field" value |> Result.bind (requiredString "id")
        let kind = requiredString "__typename" value
        let valueShape =
            match kind with
            | Ok "ProjectV2ItemFieldLabelValue" -> nestedIds "labels" value
            | Ok "ProjectV2ItemFieldPullRequestValue" -> nestedIds "pullRequests" value
            | Ok "ProjectV2ItemFieldReviewerValue" -> nestedIds "reviewers" value
            | Ok "ProjectV2ItemFieldUserValue" -> nestedIds "users" value
            | Ok "ProjectV2ItemFieldRepositoryValue" -> requiredValueProperty "repository" value
            | Ok "ProjectV2ItemFieldMilestoneValue" -> requiredValueProperty "milestone" value
            | Ok "ProjectV2ItemFieldMultiSelectValue" ->
                property "options" value |> Result.bind (fieldOptions "name") |> Result.map ignore
            | Ok "ProjectV2ItemFieldIterationValue" ->
                match requiredValueProperty "iterationId" value,
                      requiredValueProperty "startDate" value, requiredValueProperty "duration" value with
                | Ok _, Ok _, Ok _ -> Ok()
                | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure
            | Ok "ProjectV2ItemFieldNumberValue" -> requiredValueProperty "number" value
            | Ok "ProjectV2ItemFieldDateValue" -> requiredValueProperty "date" value
            | Ok "ProjectV2ItemFieldTextValue" -> requiredValueProperty "text" value
            | Ok "ProjectV2ItemFieldSingleSelectValue" -> requiredValueProperty "optionId" value
            | Ok "ProjectV2ItemIssueFieldValue" ->
                Error(MigrationReadFailure.MalformedResponse "unsupported:issue-field-value")
            | Ok _ -> Error(MigrationReadFailure.MalformedResponse "unsupported:project-field-value-kind")
            | Error failure -> Error failure
        match fieldId, kind, valueShape with
        | Ok id, Ok valueKind, Ok _ ->
            let mutable valueNodeId = Unchecked.defaultof<JsonElement>
            let nodeId =
                if value.TryGetProperty("id", &valueNodeId)
                   && valueNodeId.ValueKind = JsonValueKind.String
                   && text (valueNodeId.GetString()) then Some(valueNodeId.GetString())
                else None
            let payload = value.GetRawText()
            Ok { FieldNodeId=id; ValueKind=valueKind; ValueNodeId=nodeId
                 PayloadJson=payload; PayloadSha256=sha payload }
        | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure

    let private projectItemValues (value: JsonElement) =
        match requiredString "id" value, requiredString "updatedAt" value,
              property "fieldValues" value with
        | Ok itemId, Ok updated, Ok connection ->
            let mutable timestamp = DateTimeOffset.MinValue
            if not (DateTimeOffset.TryParse(updated, &timestamp)) then
                Error(MigrationReadFailure.MalformedResponse "invalid:project-item-updatedAt")
            else
            match nonNegativeInt "totalCount" connection,
                  property "nodes" connection, property "pageInfo" connection with
            | Ok total, Ok nodes, Ok pageInfo when nodes.ValueKind = JsonValueKind.Array ->
                match requiredBool "hasNextPage" pageInfo, property "endCursor" pageInfo with
                | Ok false, Ok _ when nodes.GetArrayLength() = total ->
                    let parsed = nodes.EnumerateArray() |> Seq.map projectFieldValue |> Seq.toList
                    match parsed |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                    | Some failure -> Error failure
                    | None ->
                        let values = parsed |> List.choose (function Ok value -> Some value | _ -> None)
                        collectUnique (fun (entry: MigrationProjectFieldValueRecord) -> entry.FieldNodeId) values
                        |> Result.map (fun complete ->
                            { ItemNodeId=itemId; UpdatedAt=timestamp; FieldValueCount=total
                              FieldValues=List.sortBy _.FieldNodeId complete })
                | Ok true, _ -> Error(MigrationReadFailure.PaginationRefused "nested:fieldValues")
                | Ok false, Ok _ -> Error MigrationReadFailure.PopulationDrift
                | Error failure, _ | _, Error failure -> Error failure
            | _ -> Error(MigrationReadFailure.MalformedResponse "invalid:fieldValues")
        | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure

    let readProjectValues (options: MigrationProjectReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validProjectOptions options) then Error MigrationReadFailure.InvalidOptions
        else
            let query = projectValueQueryTemplate.Replace("__PROJECT_NUMBER__", string options.ProjectNumber)
            let rec pages (seen: Set<string>) (count: int) (population: int option)
                          (accumulated: MigrationProjectItemValueRecord list) (cursor: string option) =
                if count >= 1000 || (cursor |> Option.exists (fun value -> Set.contains value seen)) then
                    Error(MigrationReadFailure.PaginationRefused "cycle-or-page-limit")
                else
                    let variables =
                        [ "organization", options.Organization
                          match cursor with Some value -> "after", value | None -> () ]
                        |> Map.ofList
                    let request =
                        GraphQL { Uri=options.GraphQLUri; Document=query; Variables=variables
                                  Headers=headers options.Token options.UserAgent
                                  ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
                    response transport request
                    |> Result.bind (fun result -> parse result.Body)
                    |> Result.bind (fun document ->
                        use document = document
                        let root = document.RootElement
                        let mutable errors = Unchecked.defaultof<JsonElement>
                        if root.TryGetProperty("errors", &errors) then Error MigrationReadFailure.GraphQLErrors
                        else
                            match property "data" root |> Result.bind (property "organization")
                                  |> Result.bind (property "projectV2") with
                            | Error failure -> Error failure
                            | Ok project when project.ValueKind = JsonValueKind.Null ->
                                Error MigrationReadFailure.IdentityDrift
                            | Ok project ->
                                match requiredString "id" project, requiredInt "number" project,
                                      property "items" project with
                                | Ok id, Ok number, Ok connection when
                                    id = options.ExpectedProjectNodeId && number = options.ProjectNumber ->
                                    match nonNegativeInt "totalCount" connection,
                                          property "nodes" connection, property "pageInfo" connection with
                                    | Ok total, Ok nodes, Ok pageInfo when nodes.ValueKind = JsonValueKind.Array ->
                                        if population |> Option.exists ((<>) total) then
                                            Error MigrationReadFailure.PopulationDrift
                                        else
                                            let parsed = nodes.EnumerateArray() |> Seq.map projectItemValues |> Seq.toList
                                            match parsed |> List.tryPick (function Error failure -> Some failure | _ -> None) with
                                            | Some failure -> Error failure
                                            | None ->
                                                let values = parsed |> List.choose (function Ok value -> Some value | _ -> None)
                                                let combined = accumulated @ values
                                                match requiredBool "hasNextPage" pageInfo,
                                                      property "endCursor" pageInfo with
                                                | Ok true, Ok endCursor when endCursor.ValueKind = JsonValueKind.String
                                                                          && text (endCursor.GetString()) ->
                                                    pages (cursor |> Option.map (fun value -> Set.add value seen)
                                                                  |> Option.defaultValue seen)
                                                          (count + 1) (Some total) combined (Some(endCursor.GetString()))
                                                | Ok false, Ok _ when combined.Length = total ->
                                                    collectUnique (fun (item: MigrationProjectItemValueRecord) -> item.ItemNodeId) combined
                                                    |> Result.map (fun complete ->
                                                        let result: MigrationProjectValuePopulation =
                                                            { ProjectNodeId=id; PageCount=count + 1; Terminal=true
                                                              TotalCount=total
                                                              Items=List.sortBy (fun (item: MigrationProjectItemValueRecord) -> item.ItemNodeId) complete }
                                                        result)
                                                | Ok false, Ok _ -> Error MigrationReadFailure.PopulationDrift
                                                | Ok true, _ -> Error(MigrationReadFailure.PaginationRefused "missing-end-cursor")
                                                | Error failure, _ | _, Error failure -> Error failure
                                    | _ -> Error(MigrationReadFailure.MalformedResponse "invalid:project-values")
                                | Ok _, Ok _, _ -> Error MigrationReadFailure.IdentityDrift
                                | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure)
            pages Set.empty 0 None [] None

    let reconcileProject (items: MigrationProjectItemPopulation)
                         (fields: MigrationProjectFieldPopulation)
                         (values: MigrationProjectValuePopulation) =
        let mismatch reason = Error(MigrationReadFailure.SnapshotMismatch reason)
        let identitySet identities = identities |> Set.ofList
        let itemIds = items.Items |> List.map _.ItemNodeId
        let valueIds = values.Items |> List.map _.ItemNodeId
        let fieldIds = fields.Fields |> List.map _.FieldNodeId
        let validPayload raw digest = sha raw = digest
        if not (text items.ProjectNodeId)
           || items.ProjectNodeId <> fields.ProjectNodeId
           || items.ProjectNodeId <> values.ProjectNodeId then
            mismatch "project-identity"
        elif not (items.Terminal && fields.Terminal && values.Terminal)
             || items.PageCount < 1 || fields.PageCount < 1 || values.PageCount < 1 then
            mismatch "nonterminal-population"
        elif items.TotalCount <> items.Items.Length || values.TotalCount <> values.Items.Length
             || fields.TotalCount <> fields.Fields.Length || items.TotalCount <> values.TotalCount then
            mismatch "population-count"
        elif (identitySet itemIds).Count <> itemIds.Length
             || (identitySet valueIds).Count <> valueIds.Length
             || (identitySet fieldIds).Count <> fieldIds.Length then
            mismatch "duplicate-identity"
        elif identitySet itemIds <> identitySet valueIds then
            mismatch "item-population"
        elif items.Items |> List.exists (fun item -> not (validPayload item.PayloadJson item.PayloadSha256))
             || fields.Fields |> List.exists (fun field -> not (validPayload field.PayloadJson field.PayloadSha256))
             || values.Items |> List.exists (fun item ->
                 item.FieldValueCount <> item.FieldValues.Length
                 || item.FieldValues |> List.exists (fun field -> not (validPayload field.PayloadJson field.PayloadSha256))) then
            mismatch "payload-digest"
        else
            let itemById = items.Items |> List.map (fun item -> item.ItemNodeId, item) |> Map.ofList
            let declared = identitySet fieldIds
            let changedRevision =
                values.Items
                |> List.exists (fun item -> item.UpdatedAt <> itemById.[item.ItemNodeId].UpdatedAt)
            let unknownField =
                values.Items
                |> List.exists (fun item ->
                    let ids = item.FieldValues |> List.map _.FieldNodeId
                    ids.Length <> (identitySet ids).Count
                    || ids |> List.exists (fun id -> not (Set.contains id declared)))
            if changedRevision then mismatch "item-revision"
            elif unknownField then mismatch "undeclared-or-duplicate-field"
            else
                let framed (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
                let fieldValueCount = values.Items |> List.sumBy _.FieldValueCount
                let parts =
                    [ items.ProjectNodeId; string items.TotalCount; string fields.TotalCount
                      string fieldValueCount; string items.PageCount; string fields.PageCount
                      string values.PageCount ]
                    @ (items.Items |> List.sortBy _.ItemNodeId |> List.collect (fun item ->
                        [ item.ItemNodeId; item.UpdatedAt.ToUniversalTime().ToString("O"); item.PayloadSha256 ]))
                    @ (fields.Fields |> List.sortBy _.FieldNodeId |> List.collect (fun field ->
                        [ field.FieldNodeId; field.DataType; field.PayloadSha256 ]))
                    @ (values.Items |> List.sortBy _.ItemNodeId |> List.collect (fun item ->
                        [ item.ItemNodeId; item.UpdatedAt.ToUniversalTime().ToString("O") ]
                        @ (item.FieldValues |> List.sortBy _.FieldNodeId |> List.collect (fun field ->
                            [ field.FieldNodeId; field.ValueKind; field.PayloadSha256 ]))))
                let digest = parts |> List.map framed |> String.concat "" |> sha
                Ok { ProjectNodeId=items.ProjectNodeId; ItemCount=items.TotalCount
                     FieldCount=fields.TotalCount; FieldValueCount=fieldValueCount
                     NormalizedSha256=digest; Items=items; Fields=fields; Values=values }
