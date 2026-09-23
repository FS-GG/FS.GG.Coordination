namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
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
      PayloadSha256: string }

type MigrationIssuePopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      Issues: MigrationIssueRecord list
      PullRequestCount: int }

type MigrationIssueTypeRecord = { NodeId: string; Name: string }

type MigrationIssueTypePopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      IssueTypes: MigrationIssueTypeRecord list }

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

    let private headers (options: MigrationGitHubReadOptions) =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", options.UserAgent
          if text options.Token then "authorization", $"Bearer {options.Token}" ]
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
        response transport (Rest { Method=Get; Uri=uri; Headers=headers options; Body=None
                                   ApiVersion=ApiVersion.required; Idempotency=ReplaySafe })
        |> Result.bind (fun result -> parse result.Body)
        |> Result.bind (fun document ->
            use document = document
            match requiredInt64 "id" document.RootElement, requiredString "full_name" document.RootElement with
            | Ok id, Ok fullName when id = options.ExpectedRepositoryId
                                      && fullName = $"{options.Owner}/{options.Repository}" -> Ok id
            | Ok _, Ok _ -> Error MigrationReadFailure.IdentityDrift
            | Error error, _ | _, Error error -> Error error)

    let private parseIssue (value: JsonElement) =
        match requiredInt "number" value, requiredInt64 "id" value, requiredString "node_id" value,
              requiredString "state" value, requiredString "updated_at" value with
        | Ok number, Ok databaseId, Ok nodeId, Ok state, Ok updated ->
            let mutable timestamp = DateTimeOffset.MinValue
            if not (DateTimeOffset.TryParse(updated, &timestamp)) then
                Error(MigrationReadFailure.MalformedResponse "invalid:updated_at")
            else
                Ok { Number=number; DatabaseId=databaseId; NodeId=nodeId; State=state
                     UpdatedAt=timestamp; PayloadSha256=sha (value.GetRawText()) }
        | Error error, _, _, _, _ | _, Error error, _, _, _ | _, _, Error error, _, _
        | _, _, _, Error error, _ | _, _, _, _, Error error -> Error error

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
                         || current.AbsolutePath <> allowedPath
                         || not (current.Query.Contains("state=all") && current.Query.Contains("per_page=100")) then
                        Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope")
                    else
                        response transport (Rest { Method=Get; Uri=current; Headers=headers options; Body=None
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
                                  Headers=headers options; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
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
                                                | Ok id, Ok name -> Ok { NodeId=id; Name=name }
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
