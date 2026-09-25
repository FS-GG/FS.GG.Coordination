namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type MigrationOrganizationEnabledRepositories =
    | All
    | None
    | Selected

[<RequireQualifiedAccess>]
type MigrationOrganizationAllowedActions =
    | All
    | LocalOnly
    | Selected

type MigrationOrganizationActionsEvidence =
    { RequestedUri: string
      RawBody: string
      RawSha256: string
      NextUri: string option }

type MigrationOrganizationSelectedActions =
    { GitHubOwnedAllowed: bool
      VerifiedAllowed: bool
      PatternsAllowed: string list }

type MigrationOrganizationSelectedRepository =
    { Id: int64
      NodeId: string
      Name: string
      FullName: string }

type MigrationOrganizationActionsPolicySnapshot =
    { OrganizationId: int64
      OrganizationNodeId: string
      OrganizationLogin: string
      IdentityEvidence: MigrationOrganizationActionsEvidence
      PolicyEvidence: MigrationOrganizationActionsEvidence
      EnabledRepositories: MigrationOrganizationEnabledRepositories
      AllowedActions: MigrationOrganizationAllowedActions
      ShaPinningRequired: bool
      SelectedActionsUrl: string option
      SelectedActions: MigrationOrganizationSelectedActions option
      SelectedActionsEvidence: MigrationOrganizationActionsEvidence option
      SelectedRepositories: MigrationOrganizationSelectedRepository list option
      SelectedRepositoryPages: MigrationOrganizationActionsEvidence list }

type MigrationOrganizationActionsPolicyTwoPass =
    { First: MigrationOrganizationActionsPolicySnapshot
      Second: MigrationOrganizationActionsPolicySnapshot }

[<RequireQualifiedAccess>]
module MigrationOrganizationActionsPolicyRead =
    let private text (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private equalName left right =
        String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let private uniqueMembers (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then Error "invalid:object"
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length = (names |> Set.ofList |> Set.count) then Ok()
            else Error "duplicate:object-member"

    let private parse (body: string) (f: JsonElement -> Result<'a, string>) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement
            uniqueMembers root |> Result.bind (fun () -> f root)
        with :? JsonException -> Error "invalid:json"

    let private property (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) then Ok value
        else Error $"missing:{name}"

    let private requiredString name root =
        property name root
        |> Result.bind (fun value ->
            if value.ValueKind = JsonValueKind.String && text (value.GetString()) then
                Ok(value.GetString())
            else Error $"invalid:{name}")

    let private requiredBool name root =
        property name root
        |> Result.bind (fun value ->
            match value.ValueKind with
            | JsonValueKind.True -> Ok true
            | JsonValueKind.False -> Ok false
            | _ -> Error $"invalid:{name}")

    let private requiredInt64 name root =
        property name root
        |> Result.bind (fun value ->
            let mutable parsed = 0L
            if value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&parsed)
               && parsed > 0L then Ok parsed
            else Error $"invalid:{name}")

    let private requiredCount root =
        property "total_count" root
        |> Result.bind (fun value ->
            let mutable parsed = 0
            if value.ValueKind = JsonValueKind.Number && value.TryGetInt32(&parsed)
               && parsed >= 0 then Ok parsed
            else Error "invalid:total_count")

    let private headers (options: MigrationGitHubReadOptions) =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", options.UserAgent
          if text options.Token then "authorization", $"Bearer {options.Token}" ]
        |> Map.ofList

    let private get (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport)
                    paged (uri: Uri) =
        let request =
            Rest { Method=Get; Uri=uri; Headers=headers options; Body=None
                   ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        match transport.Send request with
        | NetworkFailure | TimedOut -> Error "unavailable:organization-actions-transport"
        | Response response when response.StatusCode <> 200 ->
            Error $"unknown:organization-actions-http-{response.StatusCode}"
        | Response response when isNull response.Body ->
            Error "invalid:organization-actions-body"
        | Response response ->
            let links =
                response.Headers
                |> Map.toList
                |> List.filter (fun (key, _) -> equalName key "link")
            match links with
            | [] ->
                Ok { RequestedUri=uri.AbsoluteUri; RawBody=response.Body
                     RawSha256=sha response.Body; NextUri=None }
            | [ _, link ] when paged ->
                match Transport.tryNextLink link with
                | Error failure -> Error $"invalid:organization-actions-link:{failure}"
                | Ok next ->
                    Ok { RequestedUri=uri.AbsoluteUri; RawBody=response.Body
                         RawSha256=sha response.Body; NextUri=next |> Option.map _.AbsoluteUri }
            | _ -> Error "invalid:organization-actions-link"

    let private selectedUrl (options: MigrationGitHubReadOptions) (expectedId: int64)
                            (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if not (root.TryGetProperty("selected_actions_url", &value))
           || value.ValueKind = JsonValueKind.Null then Ok None
        elif value.ValueKind <> JsonValueKind.String then Error "invalid:selected-actions-url"
        else
            let mutable uri = Unchecked.defaultof<Uri>
            let raw = value.GetString()
            let named =
                Uri(options.ApiBase,
                    $"orgs/{Uri.EscapeDataString options.Owner}/actions/permissions/selected-actions")
            let numeric =
                Uri(options.ApiBase,
                    $"organizations/{expectedId}/actions/permissions/selected-actions")
            if not (Uri.TryCreate(raw, UriKind.Absolute, &uri))
               || uri.Scheme <> Uri.UriSchemeHttps
               || (uri.AbsoluteUri <> named.AbsoluteUri && uri.AbsoluteUri <> numeric.AbsoluteUri) then
                Error "foreign:selected-actions-url"
            else Ok(Some uri)

    let private parseSelectedActions (evidence: MigrationOrganizationActionsEvidence) =
        parse evidence.RawBody (fun root ->
            match requiredBool "github_owned_allowed" root,
                  requiredBool "verified_allowed" root, property "patterns_allowed" root with
            | Ok githubOwned, Ok verified, Ok patterns when patterns.ValueKind = JsonValueKind.Array ->
                let elements = patterns.EnumerateArray() |> Seq.toList
                let names =
                    elements
                    |> List.choose (fun value ->
                        if value.ValueKind = JsonValueKind.String && text (value.GetString()) then
                            Some(value.GetString()) else None)
                if names.Length <> elements.Length || (names |> Set.ofList |> Set.count) <> names.Length then
                    Error "invalid:selected-actions-patterns"
                else
                    Ok { GitHubOwnedAllowed=githubOwned; VerifiedAllowed=verified
                         PatternsAllowed=names }
            | Ok _, Ok _, Ok _ -> Error "invalid:selected-actions-patterns"
            | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure)

    let private parseRepository expectedId expectedNode orgLogin (value: JsonElement) =
        uniqueMembers value
        |> Result.bind (fun () ->
            match requiredInt64 "id" value, requiredString "node_id" value,
                  requiredString "name" value, requiredString "full_name" value,
                  property "owner" value with
            | Ok id, Ok node, Ok name, Ok fullName, Ok owner ->
                uniqueMembers owner
                |> Result.bind (fun () ->
                    match requiredInt64 "id" owner, requiredString "node_id" owner,
                          requiredString "login" owner with
                    | Ok ownerId, Ok ownerNode, Ok ownerLogin when
                        ownerId = expectedId && ownerNode = expectedNode
                        && equalName ownerLogin orgLogin
                        && not (name.Contains('/'))
                        && equalName fullName $"{orgLogin}/{name}" ->
                        Ok { Id=id; NodeId=node; Name=name; FullName=fullName }
                    | Ok _, Ok _, Ok _ -> Error "foreign:selected-repository-owner"
                    | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure)
            | Error failure, _, _, _, _ | _, Error failure, _, _, _
            | _, _, Error failure, _, _ | _, _, _, Error failure, _
            | _, _, _, _, Error failure -> Error failure)

    let private parseRepositoryPage expectedId expectedNode orgLogin evidence =
        parse evidence.RawBody (fun root ->
            match requiredCount root, property "repositories" root with
            | Ok count, Ok repositories when repositories.ValueKind = JsonValueKind.Array ->
                let values = repositories.EnumerateArray() |> Seq.toList
                if values.Length > 100 then Error "invalid:selected-repository-page-size"
                else
                    values
                    |> List.fold (fun result value ->
                        result
                        |> Result.bind (fun previous ->
                            parseRepository expectedId expectedNode orgLogin value
                            |> Result.map (fun item -> item :: previous))) (Ok [])
                    |> Result.map (fun items -> count, List.rev items)
            | Ok _, Ok _ -> Error "invalid:selected-repositories"
            | Error failure, _ | _, Error failure -> Error failure)

    let private nextPage (firstUri: Uri) pageNumber (raw: string) =
        let mutable uri = Unchecked.defaultof<Uri>
        if not (Uri.TryCreate(raw, UriKind.Absolute, &uri))
           || uri.Scheme <> Uri.UriSchemeHttps
           || uri.GetLeftPart(UriPartial.Path) <> firstUri.GetLeftPart(UriPartial.Path)
           || uri.Fragment <> "" then Error "foreign:selected-repository-page"
        else
            let parts =
                uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun part -> part.Split('=', 2))
            if parts.Length <> 2 || parts |> Array.exists (fun part -> part.Length <> 2) then
                Error "invalid:selected-repository-page-query"
            else
                let query = parts |> Array.map (fun part -> part.[0], part.[1]) |> Map.ofArray
                if query.Count <> 2 || Map.tryFind "per_page" query <> Some "100"
                   || Map.tryFind "page" query <> Some(string pageNumber) then
                    Error "invalid:selected-repository-page-query"
                else Ok uri

    let private readSelectedRepositories (options: MigrationGitHubReadOptions)
                                         (transport: IMigrationGitHubReadTransport)
                                         expectedId expectedNode orgLogin =
        let firstUri =
            Uri(options.ApiBase,
                $"orgs/{Uri.EscapeDataString options.Owner}/actions/permissions/repositories?per_page=100&page=1")
        let rec readPage (pageNumber: int) (expectedTotal: int option) (seen: Set<string>)
                         (records: MigrationOrganizationSelectedRepository list)
                         (pages: MigrationOrganizationActionsEvidence list) (uri: Uri) =
            if pageNumber > 10000 || Set.contains uri.AbsoluteUri seen then
                Error "invalid:selected-repository-pagination"
            else
                get options transport true uri
                |> Result.bind (fun evidence ->
                    parseRepositoryPage expectedId expectedNode orgLogin evidence
                    |> Result.bind (fun (count, items) ->
                        if expectedTotal.IsSome && expectedTotal <> Some count then
                            Error "changed:selected-repository-count"
                        else
                            let all = records @ items
                            let pageEvidence = pages @ [ evidence ]
                            if all.Length > count then Error "invalid:selected-repository-count"
                            else
                                match evidence.NextUri with
                                | None ->
                                    if all.Length <> count then Error "missing:selected-repository-page"
                                    elif (all |> List.map _.Id |> Set.ofList |> Set.count) <> all.Length
                                         || (all |> List.map _.NodeId |> Set.ofList |> Set.count) <> all.Length
                                         || (all |> List.map (fun item -> item.FullName.ToLowerInvariant())
                                             |> Set.ofList |> Set.count) <> all.Length then
                                        Error "duplicate:selected-repository"
                                    else Ok(all, pageEvidence)
                                | Some raw ->
                                    if all.Length >= count || items.IsEmpty then
                                        Error "invalid:selected-repository-next"
                                    else
                                        nextPage firstUri (pageNumber + 1) raw
                                        |> Result.bind (fun next ->
                                            readPage (pageNumber + 1) (Some count)
                                                (Set.add uri.AbsoluteUri seen) all pageEvidence next)))
        readPage 1 None Set.empty [] [] firstUri

    let read expectedOrganizationId (options: MigrationGitHubReadOptions)
             (transport: IMigrationGitHubReadTransport) =
        let validOptions =
            expectedOrganizationId > 0L
            && not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri
            && options.ApiBase.Scheme = Uri.UriSchemeHttps
            && not (isNull options.GraphQLUri) && options.GraphQLUri.IsAbsoluteUri
            && options.GraphQLUri.Scheme = Uri.UriSchemeHttps
            && text options.Owner && text options.UserAgent
        if not validOptions then Error "invalid:organization-actions-options"
        else
            let orgPath = $"orgs/{Uri.EscapeDataString options.Owner}"
            let identityUri = Uri(options.ApiBase, orgPath)
            let policyUri = Uri(options.ApiBase, $"{orgPath}/actions/permissions")
            let readPass () =
                get options transport false identityUri
                |> Result.bind (fun identity ->
                    parse identity.RawBody (fun root ->
                        match requiredInt64 "id" root, requiredString "node_id" root,
                              requiredString "login" root, requiredString "type" root,
                              requiredString "url" root with
                        | Ok id, Ok node, Ok login, Ok "Organization", Ok url when
                            id = expectedOrganizationId && equalName login options.Owner
                            && equalName url identityUri.AbsoluteUri -> Ok(node, login)
                        | Ok _, Ok _, Ok _, Ok _, Ok _ -> Error "foreign:organization-identity"
                        | Error failure, _, _, _, _ | _, Error failure, _, _, _
                        | _, _, Error failure, _, _ | _, _, _, Error failure, _
                        | _, _, _, _, Error failure -> Error failure)
                    |> Result.bind (fun (node, login) ->
                        get options transport false policyUri
                        |> Result.bind (fun policy ->
                            parse policy.RawBody (fun root ->
                                match requiredString "enabled_repositories" root,
                                      requiredString "allowed_actions" root,
                                      requiredBool "sha_pinning_required" root,
                                      selectedUrl options expectedOrganizationId root with
                                | Ok enabled, Ok allowed, Ok pinning, Ok selected ->
                                    let enabled =
                                        match enabled with
                                        | "all" -> Some MigrationOrganizationEnabledRepositories.All
                                        | "none" -> Some MigrationOrganizationEnabledRepositories.None
                                        | "selected" -> Some MigrationOrganizationEnabledRepositories.Selected
                                        | _ -> None
                                    let allowed =
                                        match allowed with
                                        | "all" -> Some MigrationOrganizationAllowedActions.All
                                        | "local_only" -> Some MigrationOrganizationAllowedActions.LocalOnly
                                        | "selected" -> Some MigrationOrganizationAllowedActions.Selected
                                        | _ -> None
                                    match enabled, allowed with
                                    | Some enabled, Some allowed when
                                        (allowed = MigrationOrganizationAllowedActions.Selected) = selected.IsSome ->
                                        Ok(enabled, allowed, pinning, selected)
                                    | Some _, Some _ -> Error "invalid:selected-actions-boundary"
                                    | _ -> Error "invalid:organization-actions-policy"
                                | Error failure, _, _, _ | _, Error failure, _, _
                                | _, _, Error failure, _ | _, _, _, Error failure -> Error failure)
                            |> Result.bind (fun (enabled, allowed, pinning, selected) ->
                                let actions =
                                    match selected with
                                    | None -> Ok(None, None)
                                    | Some uri ->
                                        get options transport false uri
                                        |> Result.bind (fun evidence ->
                                            parseSelectedActions evidence
                                            |> Result.map (fun value -> Some value, Some evidence))
                                actions
                                |> Result.bind (fun (selectedActions, selectedEvidence) ->
                                    let repositories =
                                        if enabled = MigrationOrganizationEnabledRepositories.Selected then
                                            readSelectedRepositories options transport expectedOrganizationId node login
                                            |> Result.map (fun (items, pages) -> Some items, pages)
                                        else Ok(None, [])
                                    repositories
                                    |> Result.map (fun (selectedRepositories, pages) ->
                                        { OrganizationId=expectedOrganizationId; OrganizationNodeId=node
                                          OrganizationLogin=login; IdentityEvidence=identity
                                          PolicyEvidence=policy; EnabledRepositories=enabled
                                          AllowedActions=allowed; ShaPinningRequired=pinning
                                          SelectedActionsUrl=selected |> Option.map _.AbsoluteUri
                                          SelectedActions=selectedActions
                                          SelectedActionsEvidence=selectedEvidence
                                          SelectedRepositories=selectedRepositories
                                          SelectedRepositoryPages=pages }))))))
            readPass ()
            |> Result.bind (fun first ->
                readPass ()
                |> Result.bind (fun second ->
                    if first <> second then Error "changed:organization-actions-policy"
                    else Ok { First=first; Second=second }))
