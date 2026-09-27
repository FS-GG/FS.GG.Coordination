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
    let bindReviewDeliveryRecords options native journals =
        MigrationReviewDeliveryInspectBinder.bind options.Cohort options.Repository native journals
        |> Result.bind (fun capture ->
            MigrationReviewDeliveryInspectBinder.authority options.Cohort 1 capture
            |> Result.bind (fun first ->
                MigrationReviewDeliveryInspectBinder.authority options.Cohort 2 capture
                |> Result.map (fun second -> first, second)))

    let internal allowedRequest (options: MigrationInspectProviderOptions) authority request =
        match authority, request with
        | "repository-settings/core", Rest value ->
            let repository =
                Uri(options.Repository.ApiBase,
                    $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}")
            value.Method = Get && value.Body.IsNone
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && value.Uri = repository
        | "repository-settings/actions", Rest value ->
            let repositoryPath =
                $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}"
            let allowed =
                [ Uri(options.Repository.ApiBase, repositoryPath)
                  Uri(options.Repository.ApiBase, $"{repositoryPath}/actions/permissions")
                  Uri(options.Repository.ApiBase, $"{repositoryPath}/actions/permissions/selected-actions")
                  Uri(options.Repository.ApiBase,
                      $"repositories/{options.Repository.ExpectedRepositoryId}/actions/permissions/selected-actions") ]
            value.Method = Get && value.Body.IsNone
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && List.contains value.Uri allowed
        | "repository-settings/custom-properties", Rest value ->
            let repositoryPath =
                $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}"
            let allowed =
                [ Uri(options.Repository.ApiBase, repositoryPath)
                  Uri(options.Repository.ApiBase,
                      $"orgs/{Uri.EscapeDataString options.Repository.Owner}/properties/schema")
                  Uri(options.Repository.ApiBase, $"{repositoryPath}/properties/values") ]
            value.Method = Get && value.Body.IsNone
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && List.contains value.Uri allowed
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

    let private capturedNextUri = function
        | Response reply when reply.StatusCode = 200 ->
            let link =
                reply.Headers
                |> Map.toSeq
                |> Seq.choose (fun (name, value) ->
                    if String.Equals(name, "link", StringComparison.OrdinalIgnoreCase) then Some value
                    else None)
                |> String.concat ","
            match Transport.tryNextLink link with
            | Ok next -> Some(next |> Option.map _.AbsoluteUri)
            | Error _ -> None
        | _ -> None

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

    let rec private requireUniqueMembers (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let properties = value.EnumerateObject() |> Seq.toList
            let names = properties |> List.map _.Name
            if names.Length <> (names |> Set.ofList |> Set.count) then
                failwith "duplicate-json-member"
            properties |> List.iter (fun property -> requireUniqueMembers property.Value)
        | JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.iter requireUniqueMembers
        | _ -> ()

    let private parseIssuePages repositoryId (raw: string list) =
        try
            raw
            |> List.map (fun body ->
                use document = JsonDocument.Parse body
                if document.RootElement.ValueKind <> JsonValueKind.Array then failwith "issue-page-array"
                requireUniqueMembers document.RootElement
                let items = document.RootElement.EnumerateArray() |> Seq.toList
                let isPullRequest (item: JsonElement) =
                    let mutable marker = Unchecked.defaultof<JsonElement>
                    if item.TryGetProperty("pull_request", &marker) then
                        if marker.ValueKind <> JsonValueKind.Object then failwith "pr-marker"
                        true
                    else false
                let pullRequestNumbers =
                    items |> List.filter isPullRequest
                    |> List.map (fun item -> item.GetProperty("number").GetInt32())
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
                issues, pullRequestNumbers)
            |> Ok
        with _ -> Error "raw-issue-parse"

    let bindRepositoryCoreSettings
        (options: MigrationInspectProviderOptions)
        (settings: MigrationRepositoryCoreSettings)
        (captures: (GitHubRequest * TransportOutcome) list) =
        if not (repositoryBinding options)
           || settings.RepositoryId <> options.Repository.ExpectedRepositoryId
           || settings.FullName <> $"{options.Repository.Owner}/{options.Repository.Repository}" then
            Error "repository-core-cohort"
        elif captures.Length <> 1
             || (captures |> List.exists (fst >> allowedRequest options "repository-settings/core" >> not)) then
            Error "repository-core-capture-shape"
        else
            match captures.Head with
            | Rest request, outcome ->
                match responseBody outcome with
                | Error reason -> Error reason
                | Ok body ->
                    try
                        use document = JsonDocument.Parse body
                        let root = document.RootElement
                        requireUniqueMembers root
                        let parsed =
                            root.GetProperty("id").GetInt64(),
                            root.GetProperty("node_id").GetString(),
                            root.GetProperty("full_name").GetString(),
                            root.GetProperty("default_branch").GetString(),
                            root.GetProperty("visibility").GetString(),
                            root.GetProperty("archived").GetBoolean(),
                            root.GetProperty("disabled").GetBoolean(),
                            root.GetProperty("has_issues").GetBoolean(),
                            root.GetProperty("allow_squash_merge").GetBoolean(),
                            root.GetProperty("allow_merge_commit").GetBoolean(),
                            root.GetProperty("allow_rebase_merge").GetBoolean(),
                            root.GetProperty("delete_branch_on_merge").GetBoolean()
                        let typed =
                            settings.RepositoryId, settings.NodeId, settings.FullName,
                            settings.DefaultBranch, settings.Visibility, settings.Archived,
                            settings.Disabled, settings.HasIssues, settings.AllowSquashMerge,
                            settings.AllowMergeCommit, settings.AllowRebaseMerge,
                            settings.DeleteBranchOnMerge
                        let validStrings =
                            not (String.IsNullOrWhiteSpace settings.NodeId)
                            && not (String.IsNullOrWhiteSpace settings.DefaultBranch)
                            && Set.contains settings.Visibility (set [ "public"; "private"; "internal" ])
                        if parsed <> typed || not validStrings
                           || settings.PayloadJson <> body || settings.PayloadSha256 <> sha body then
                            Error "repository-core-raw-typed-mismatch"
                        elif capturedNextUri outcome <> Some None then
                            Error "repository-core-unexpected-continuation"
                        else
                            let revision = sha body
                            let observed = subject $"repository:{settings.RepositoryId}:settings:core" revision body
                            let page =
                                { RequestedUri=request.Uri.AbsoluteUri
                                  RequestIdentitySha256=sha request.Uri.AbsoluteUri
                                  RawBody=body; PayloadSha256=revision
                                  NextRequestIdentitySha256=None; Subjects=[ observed ] }
                            Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                                 ScopeVerified=true; SubjectsParsedFromRaw=true
                                 Read={ Authority="repository-settings/core"
                                        ObservedAt=DateTimeOffset.UtcNow
                                        PageCount=1; ItemCount=1; Terminal=true; NextCursor=None
                                        HighWaterMark=digestParts [ revision ]; Subjects=[ observed ] }
                                 Pages=[ page ] }
                    with _ -> Error "repository-core-raw-parse"
            | _ -> Error "repository-core-capture-shape"

    let bindRepositoryActionsPolicy
        (options: MigrationInspectProviderOptions)
        (settings: MigrationRepositoryActionsPolicy)
        (captures: (GitHubRequest * TransportOutcome) list) =
        let repositoryPath =
            $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}"
        let identityUri = Uri(options.Repository.ApiBase, repositoryPath).AbsoluteUri
        let policyUri = Uri(options.Repository.ApiBase, $"{repositoryPath}/actions/permissions").AbsoluteUri
        let expectedUris =
            [ yield identityUri
              yield policyUri
              match settings.SelectedActionsUri with
              | Some uri -> yield uri
              | None -> () ]
        if not (repositoryBinding options)
           || settings.RepositoryId <> options.Repository.ExpectedRepositoryId
           || settings.RepositoryFullName <> $"{options.Repository.Owner}/{options.Repository.Repository}"
           || settings.IdentityUri <> identityUri || settings.PolicyUri <> policyUri then
            Error "repository-actions-cohort"
        elif captures.Length <> expectedUris.Length
             || (captures |> List.exists (fst >> allowedRequest options "repository-settings/actions" >> not)) then
            Error "repository-actions-capture-shape"
        else
            try
                let captured =
                    captures
                    |> List.map (fun (request, outcome) ->
                        match request, responseBody outcome with
                        | Rest value, Ok body when capturedNextUri outcome = Some None ->
                            value.Uri.AbsoluteUri, body
                        | Rest _, Ok _ -> failwith "unexpected-continuation"
                        | _, Error _ -> failwith "provider-response"
                        | _ -> failwith "request-kind")
                if (captured |> List.map fst) <> expectedUris then failwith "request-sequence"
                let identityBody = captured.[0] |> snd
                let policyBody = captured.[1] |> snd
                use identityDocument = JsonDocument.Parse identityBody
                use policyDocument = JsonDocument.Parse policyBody
                requireUniqueMembers identityDocument.RootElement
                requireUniqueMembers policyDocument.RootElement
                let identity =
                    identityDocument.RootElement.GetProperty("id").GetInt64(),
                    identityDocument.RootElement.GetProperty("full_name").GetString()
                let policy = policyDocument.RootElement
                let selectedProperty = policy.GetProperty("selected_actions_url")
                let selectedUri =
                    match selectedProperty.ValueKind with
                    | JsonValueKind.Null -> None
                    | JsonValueKind.String -> Some(selectedProperty.GetString())
                    | _ -> failwith "selected-actions-url"
                let parsedPolicy =
                    policy.GetProperty("enabled").GetBoolean(),
                    policy.GetProperty("allowed_actions").GetString(),
                    policy.GetProperty("sha_pinning_required").GetBoolean(),
                    selectedUri
                let typedPolicy =
                    settings.Enabled, settings.AllowedActions, settings.ShaPinningRequired,
                    settings.SelectedActionsUri
                let validPolicy =
                    Set.contains settings.AllowedActions (set [ "all"; "local_only"; "selected" ])
                    && ((settings.AllowedActions = "selected") = settings.SelectedActionsUri.IsSome)
                if identity <> (settings.RepositoryId, settings.RepositoryFullName)
                   || parsedPolicy <> typedPolicy || not validPolicy
                   || settings.IdentityPayloadJson <> identityBody
                   || settings.IdentityPayloadSha256 <> sha identityBody
                   || settings.PolicyPayloadJson <> policyBody
                   || settings.PolicyPayloadSha256 <> sha policyBody then
                    failwith "raw-typed-mismatch"
                match settings.SelectedActionsUri, settings.SelectedActionsPayloadJson,
                      settings.SelectedActionsPayloadSha256, settings.GitHubOwnedAllowed,
                      settings.VerifiedAllowed, settings.PatternsAllowed with
                | None, None, None, None, None, None when captured.Length = 2 -> ()
                | Some _, Some selectedBody, Some selectedHash, Some githubOwned,
                  Some verified, Some patterns when captured.Length = 3 ->
                    if selectedBody <> (captured.[2] |> snd) || selectedHash <> sha selectedBody then
                        failwith "selected-raw"
                    use selectedDocument = JsonDocument.Parse selectedBody
                    let selected = selectedDocument.RootElement
                    requireUniqueMembers selected
                    let entries = selected.GetProperty("patterns_allowed").EnumerateArray() |> Seq.toList
                    let parsedPatterns = entries |> List.map _.GetString()
                    let parsedSelected =
                        selected.GetProperty("github_owned_allowed").GetBoolean(),
                        selected.GetProperty("verified_allowed").GetBoolean(), parsedPatterns
                    if parsedSelected <> (githubOwned, verified, patterns)
                       || patterns |> List.exists String.IsNullOrWhiteSpace
                       || (patterns |> Set.ofList |> Set.count) <> patterns.Length then
                        failwith "selected-raw-typed-mismatch"
                | _ -> failwith "selected-shape"
                let names =
                    [ $"repository:{settings.RepositoryId}:settings:actions:identity"
                      $"repository:{settings.RepositoryId}:settings:actions:policy"
                      if captured.Length = 3 then
                          $"repository:{settings.RepositoryId}:settings:actions:selected" ]
                let pages =
                    List.zip3 expectedUris (captured |> List.map snd) names
                    |> List.mapi (fun index (uri, body, name) ->
                        let digest = sha body
                        { RequestedUri=uri; RequestIdentitySha256=sha uri
                          RawBody=body; PayloadSha256=digest
                          NextRequestIdentitySha256=
                              if index + 1 < expectedUris.Length then Some(sha expectedUris.[index + 1]) else None
                          Subjects=[ subject name digest body ] })
                let subjects = pages |> List.collect _.Subjects
                Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                     ScopeVerified=true; SubjectsParsedFromRaw=true
                     Read={ Authority="repository-settings/actions"
                            ObservedAt=DateTimeOffset.UtcNow
                            PageCount=pages.Length; ItemCount=subjects.Length
                            Terminal=true; NextCursor=None
                            HighWaterMark=digestParts (pages |> List.map _.PayloadSha256)
                            Subjects=subjects }
                     Pages=pages }
            with failure -> Error $"repository-actions-raw-or-scope:{failure.Message}"

    let bindRepositoryCustomProperties
        (options: MigrationInspectProviderOptions)
        (settings: MigrationCustomProperties)
        (captures: (GitHubRequest * TransportOutcome) list) =
        let repositoryPath =
            $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}"
        let expectedUris =
            [ Uri(options.Repository.ApiBase, repositoryPath).AbsoluteUri
              Uri(options.Repository.ApiBase,
                  $"orgs/{Uri.EscapeDataString options.Repository.Owner}/properties/schema").AbsoluteUri
              Uri(options.Repository.ApiBase, $"{repositoryPath}/properties/values").AbsoluteUri ]
        if not (repositoryBinding options)
           || settings.RepositoryId <> options.Repository.ExpectedRepositoryId
           || settings.RepositoryFullName <> $"{options.Repository.Owner}/{options.Repository.Repository}"
           || [ settings.IdentityUri; settings.SchemaUri; settings.ValuesUri ] <> expectedUris then
            Error "repository-custom-properties-cohort"
        elif captures.Length <> 3
             || (captures
                 |> List.exists (fst >> allowedRequest options "repository-settings/custom-properties" >> not)) then
            Error "repository-custom-properties-capture-shape"
        else
            try
                let nonblank value = not (String.IsNullOrWhiteSpace value)
                let requiredString (name: string) (element: JsonElement) =
                    let value = element.GetProperty(name)
                    if value.ValueKind <> JsonValueKind.String || not (nonblank (value.GetString())) then
                        failwith $"string:{name}"
                    value.GetString()
                let optionalProperty (name: string) (element: JsonElement) =
                    let mutable value = Unchecked.defaultof<JsonElement>
                    if element.TryGetProperty(name, &value) then Some value else None
                let stringList (value: JsonElement) =
                    if value.ValueKind <> JsonValueKind.Array then failwith "string-list"
                    let values = value.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                    if values |> List.exists (nonblank >> not)
                       || (values |> Set.ofList |> Set.count) <> values.Length then failwith "string-list"
                    values
                let propertyData valueType (value: JsonElement) =
                    match valueType, value.ValueKind with
                    | ("string" | "single_select"), JsonValueKind.String ->
                        let parsed = value.GetString()
                        if isNull parsed then failwith "property-text"
                        PropertyText parsed
                    | "url", JsonValueKind.String ->
                        let parsed = value.GetString()
                        let mutable uri = Unchecked.defaultof<Uri>
                        if not (Uri.TryCreate(parsed, UriKind.Absolute, &uri))
                           || (uri.Scheme <> Uri.UriSchemeHttps && uri.Scheme <> Uri.UriSchemeHttp) then
                            failwith "property-url"
                        PropertyText parsed
                    | "multi_select", JsonValueKind.Array -> PropertyChoices(stringList value)
                    | "true_false", JsonValueKind.True -> PropertyFlag true
                    | "true_false", JsonValueKind.False -> PropertyFlag false
                    | _ -> failwith "property-value"
                let parseDefinition (element: JsonElement) =
                    requireUniqueMembers element
                    let name = requiredString "property_name" element
                    let sourceType = requiredString "source_type" element
                    let valueType = requiredString "value_type" element
                    if sourceType <> "organization"
                       || not (Set.contains valueType
                                   (set [ "string"; "single_select"; "multi_select"; "true_false"; "url" ])) then
                        failwith "property-definition"
                    let required = element.GetProperty("required").GetBoolean()
                    let requireExplicit =
                        optionalProperty "require_explicit_values" element
                        |> Option.map _.GetBoolean()
                    let editableBy =
                        optionalProperty "values_editable_by" element
                        |> Option.map (fun value ->
                            if value.ValueKind = JsonValueKind.Null then None
                            else
                                let parsed = requiredString "values_editable_by" element
                                if not (Set.contains parsed (set [ "org_actors"; "org_and_repo_actors" ])) then
                                    failwith "values-editable-by"
                                Some parsed)
                    let defaultValue =
                        optionalProperty "default_value" element
                        |> Option.map (fun value ->
                            if value.ValueKind = JsonValueKind.Null then None
                            else Some(propertyData valueType value))
                    let allowedValues =
                        match optionalProperty "allowed_values" element with
                        | None when valueType = "single_select" || valueType = "multi_select" ->
                            failwith "allowed-values-required"
                        | None -> None
                        | Some value when value.ValueKind = JsonValueKind.Null
                                          && valueType <> "single_select" && valueType <> "multi_select" -> None
                        | Some value when valueType = "single_select" || valueType = "multi_select" ->
                            Some(stringList value)
                        | Some _ -> failwith "allowed-values-shape"
                    let permitted = function
                        | PropertyText value, Some allowed -> List.contains value allowed
                        | PropertyChoices values, Some allowed ->
                            values |> List.forall (fun value -> List.contains value allowed)
                        | _, None -> true
                        | _ -> false
                    if defaultValue |> Option.bind id |> Option.exists (fun value -> not (permitted (value, allowedValues))) then
                        failwith "default-value"
                    let raw = element.GetRawText()
                    { Name=name; SourceType=sourceType; ValueType=valueType; Required=required
                      RequireExplicitValues=requireExplicit; ValuesEditableBy=editableBy
                      DefaultValue=defaultValue; AllowedValues=allowedValues
                      PayloadJson=raw; PayloadSha256=sha raw }
                let parseValue definitions (element: JsonElement) =
                    requireUniqueMembers element
                    let name = requiredString "property_name" element
                    let definition =
                        definitions |> List.tryFind (fun (value: MigrationCustomPropertyDefinition) -> value.Name = name)
                        |> Option.defaultWith (fun () -> failwith "unknown-property")
                    let value = propertyData definition.ValueType (element.GetProperty("value"))
                    match value, definition.AllowedValues with
                    | PropertyText choice, Some allowed when not (List.contains choice allowed) ->
                        failwith "property-choice"
                    | PropertyChoices choices, Some allowed when
                        choices |> List.exists (fun choice -> not (List.contains choice allowed)) ->
                        failwith "property-choice"
                    | _ -> ()
                    let raw = element.GetRawText()
                    { Name=name; Value=value; PayloadJson=raw; PayloadSha256=sha raw }
                let captured =
                    captures
                    |> List.map (fun (request, outcome) ->
                        match request, responseBody outcome with
                        | Rest value, Ok body when capturedNextUri outcome = Some None ->
                            value.Uri.AbsoluteUri, body
                        | Rest _, Ok _ -> failwith "unexpected-continuation"
                        | _, Error _ -> failwith "provider-response"
                        | _ -> failwith "request-kind")
                if (captured |> List.map fst) <> expectedUris then failwith "request-sequence"
                let identityBody = captured.[0] |> snd
                let schemaBody = captured.[1] |> snd
                let valuesBody = captured.[2] |> snd
                use identityDocument = JsonDocument.Parse identityBody
                requireUniqueMembers identityDocument.RootElement
                let identity =
                    identityDocument.RootElement.GetProperty("id").GetInt64(),
                    requiredString "full_name" identityDocument.RootElement
                use schemaDocument = JsonDocument.Parse schemaBody
                use valuesDocument = JsonDocument.Parse valuesBody
                if schemaDocument.RootElement.ValueKind <> JsonValueKind.Array
                   || valuesDocument.RootElement.ValueKind <> JsonValueKind.Array then
                    failwith "array-shape"
                let definitions =
                    schemaDocument.RootElement.EnumerateArray() |> Seq.map parseDefinition |> Seq.toList
                if definitions.Length <> (definitions |> List.map _.Name |> Set.ofList |> Set.count) then
                    failwith "duplicate-definition"
                let values =
                    valuesDocument.RootElement.EnumerateArray()
                    |> Seq.map (parseValue definitions) |> Seq.toList
                if values.Length <> (values |> List.map _.Name |> Set.ofList |> Set.count) then
                    failwith "duplicate-value"
                let valueNames = values |> List.map _.Name |> Set.ofList
                if definitions
                   |> List.exists (fun definition ->
                       (definition.Required || definition.RequireExplicitValues = Some true)
                       && not (Set.contains definition.Name valueNames)) then
                    failwith "required-value"
                if identity <> (settings.RepositoryId, settings.RepositoryFullName)
                   || definitions <> settings.Definitions || values <> settings.Values
                   || settings.IdentityPayloadJson <> identityBody
                   || settings.IdentityPayloadSha256 <> sha identityBody
                   || settings.SchemaPayloadJson <> schemaBody
                   || settings.SchemaPayloadSha256 <> sha schemaBody
                   || settings.ValuesPayloadJson <> valuesBody
                   || settings.ValuesPayloadSha256 <> sha valuesBody then
                    failwith "raw-typed-mismatch"
                let identitySubject =
                    subject $"repository:{settings.RepositoryId}:settings:custom-properties:identity"
                        (sha identityBody) identityBody
                let definitionSubjects =
                    definitions
                    |> List.map (fun definition ->
                        subject
                            $"repository:{settings.RepositoryId}:settings:custom-property-definition:{definition.Name}"
                            definition.PayloadSha256 definition.PayloadJson)
                let valueSubjects =
                    values
                    |> List.map (fun value ->
                        subject
                            $"repository:{settings.RepositoryId}:settings:custom-property-value:{value.Name}"
                            value.PayloadSha256 value.PayloadJson)
                let bodies = [ identityBody; schemaBody; valuesBody ]
                let pageSubjects = [ [ identitySubject ]; definitionSubjects; valueSubjects ]
                let pages =
                    List.zip3 expectedUris bodies pageSubjects
                    |> List.mapi (fun index (uri, body, subjects) ->
                        { RequestedUri=uri; RequestIdentitySha256=sha uri
                          RawBody=body; PayloadSha256=sha body
                          NextRequestIdentitySha256=
                              if index + 1 < expectedUris.Length then Some(sha expectedUris.[index + 1]) else None
                          Subjects=subjects })
                let subjects = pages |> List.collect _.Subjects
                Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                     ScopeVerified=true; SubjectsParsedFromRaw=true
                     Read={ Authority="repository-settings/custom-properties"
                            ObservedAt=DateTimeOffset.UtcNow
                            PageCount=pages.Length; ItemCount=subjects.Length
                            Terminal=true; NextCursor=None
                            HighWaterMark=digestParts (pages |> List.map _.PayloadSha256)
                            Subjects=subjects }
                     Pages=pages }
            with failure -> Error $"repository-custom-properties-raw-or-scope:{failure.Message}"

    let bindDeclaredReceiverIdentities
        (options: MigrationInspectProviderOptions)
        (first: MigrationReceiverSnapshot list)
        (second: MigrationReceiverSnapshot list) =
        if not (GitHubMigrationInspect.validCohort options.Cohort) then
            Error "receiver-declared-cohort"
        else
            try
                let hex40 (value: string) =
                    not (isNull value) && value.Length = 40
                    && (value |> Seq.forall (fun c -> Char.IsAsciiHexDigitLower c))
                let stringProperty (name: string) (root: JsonElement) =
                    let value = root.GetProperty(name)
                    if value.ValueKind <> JsonValueKind.String || String.IsNullOrWhiteSpace(value.GetString()) then
                        failwith $"string:{name}"
                    value.GetString()
                let shaProperty (name: string) (root: JsonElement) =
                    let value = stringProperty name root
                    if not (hex40 value) then failwith $"sha:{name}"
                    value
                let parse (body: string) =
                    let document = JsonDocument.Parse body
                    requireUniqueMembers document.RootElement
                    document
                let parseTree (body: string) (expectedTree: string) =
                    use document = parse body
                    let root = document.RootElement
                    if shaProperty "sha" root <> expectedTree
                       || root.GetProperty("truncated").GetBoolean()
                       || root.GetProperty("tree").ValueKind <> JsonValueKind.Array then
                        failwith "tree-root"
                    let entries =
                        root.GetProperty("tree").EnumerateArray()
                        |> Seq.map (fun entry ->
                            let path = stringProperty "path" entry
                            let mode = stringProperty "mode" entry
                            let kind = stringProperty "type" entry
                            let entrySha = shaProperty "sha" entry
                            let safePath =
                                not (path.StartsWith('/')) && not (path.Contains("..", StringComparison.Ordinal))
                                && not (path.Contains('\\'))
                                && (path.Split('/') |> Array.forall (fun part ->
                                    not (String.IsNullOrWhiteSpace part) && part <> "."))
                            if not safePath || not (Set.contains kind (set [ "blob"; "tree" ]))
                               || (kind = "blob" && not (Set.contains mode (set [ "100644"; "100755"; "120000" ])))
                               || (kind = "tree" && mode <> "040000") then
                                failwith "tree-entry"
                            let mutable size = Unchecked.defaultof<JsonElement>
                            let hasSize = entry.TryGetProperty("size", &size)
                            let entrySize =
                                if kind = "blob" then
                                    let mutable parsed = 0L
                                    if not hasSize || size.ValueKind <> JsonValueKind.Number
                                       || not (size.TryGetInt64(&parsed)) || parsed < 0L then
                                        failwith "tree-size"
                                    Some parsed
                                else
                                    if hasSize && size.ValueKind <> JsonValueKind.Null then failwith "tree-size"
                                    None
                            { EntryPath=path; EntryMode=mode; EntryKind=kind
                              EntrySha=entrySha; EntrySize=entrySize })
                        |> Seq.toList
                    if entries.Length <> (entries |> List.map _.EntryPath |> Set.ofList |> Set.count) then
                        failwith "tree-duplicate-path"
                    entries |> List.sortBy _.EntryPath
                let repositories =
                    options.Cohort.Repositories
                    |> List.map (fun (repository: GitHubMigrationCopyRepository) ->
                        repository.Id, repository)
                    |> Map.ofList
                let declarations: GitHubMigrationCopyReceiver list =
                    options.Cohort.Receivers |> List.sortBy _.Receiver
                let expectedNames = declarations |> List.map _.Receiver
                let validateSnapshot (passOrdinal: int)
                                     (declaration: GitHubMigrationCopyReceiver)
                                     (snapshot: MigrationReceiverSnapshot) =
                    let repository =
                        Map.tryFind declaration.RepositoryId repositories
                        |> Option.defaultWith (fun () -> failwith "receiver-repository")
                    if snapshot.ReceiverName <> declaration.Receiver
                       || snapshot.RepositoryId <> repository.Id
                       || snapshot.RepositoryNodeId <> repository.NodeId
                       || snapshot.RepositoryFullName <> repository.FullName
                       || snapshot.RefName <> declaration.RefName
                       || snapshot.CommitSha <> declaration.ExpectedHead
                       || not (hex40 snapshot.CommitSha) || not (hex40 snapshot.TreeSha) then
                        failwith "receiver-binding"
                    let parts = repository.FullName.Split('/')
                    if parts.Length <> 2 then failwith "repository-name"
                    let repositoryPath =
                        $"repos/{Uri.EscapeDataString parts.[0]}/{Uri.EscapeDataString parts.[1]}"
                    let identityUri = Uri(options.Repository.ApiBase, repositoryPath).AbsoluteUri
                    let refUri =
                        Uri(options.Repository.ApiBase,
                            $"{repositoryPath}/git/ref/{declaration.RefName.Substring(5)}").AbsoluteUri
                    let commitUri =
                        Uri(options.Repository.ApiBase,
                            $"{repositoryPath}/git/commits/{declaration.ExpectedHead}").AbsoluteUri
                    let treeUri =
                        Uri(options.Repository.ApiBase,
                            $"{repositoryPath}/git/trees/{snapshot.TreeSha}?recursive=1").AbsoluteUri
                    let evidence =
                        [ "identity", identityUri, snapshot.IdentityEvidence
                          "initial-ref", refUri, snapshot.InitialRefEvidence
                          "commit", commitUri, snapshot.CommitEvidence
                          "tree", treeUri, snapshot.TreeEvidence
                          "terminal-ref", refUri, snapshot.TerminalRefEvidence ]
                    for _, expectedUri, item in evidence do
                        if item.RequestUri <> expectedUri || item.RawSha256 <> sha item.RawBody then
                            failwith "receiver-evidence"
                    use identityDocument = parse snapshot.IdentityEvidence.RawBody
                    let identity = identityDocument.RootElement
                    if identity.GetProperty("id").GetInt64() <> repository.Id
                       || stringProperty "node_id" identity <> repository.NodeId
                       || stringProperty "full_name" identity <> repository.FullName then
                        failwith "receiver-identity"
                    let parseRef (body: string) =
                        use document = parse body
                        let root = document.RootElement
                        let target = root.GetProperty("object")
                        if stringProperty "ref" root <> declaration.RefName
                           || stringProperty "type" target <> "commit"
                           || shaProperty "sha" target <> declaration.ExpectedHead then
                            failwith "receiver-ref"
                    parseRef snapshot.InitialRefEvidence.RawBody
                    parseRef snapshot.TerminalRefEvidence.RawBody
                    use commitDocument = parse snapshot.CommitEvidence.RawBody
                    let commit = commitDocument.RootElement
                    if shaProperty "sha" commit <> declaration.ExpectedHead
                       || shaProperty "sha" (commit.GetProperty("tree")) <> snapshot.TreeSha then
                        failwith "receiver-commit"
                    let entries = parseTree snapshot.TreeEvidence.RawBody snapshot.TreeSha
                    if entries <> snapshot.TreeEntries then failwith "receiver-tree-typed"
                    let expectedSnapshot =
                        [ yield snapshot.ReceiverName
                          yield string snapshot.RepositoryId
                          yield snapshot.RepositoryNodeId
                          yield snapshot.RepositoryFullName
                          yield snapshot.RefName
                          yield snapshot.CommitSha
                          yield snapshot.TreeSha
                          for _, _, item in evidence do
                              yield item.RequestUri
                              yield item.RawSha256
                          for item in entries do
                              yield item.EntryPath
                              yield item.EntryMode
                              yield item.EntryKind
                              yield item.EntrySha
                              yield item.EntrySize |> Option.map string |> Option.defaultValue "" ]
                        |> digestParts
                    if snapshot.SnapshotSha256 <> expectedSnapshot then failwith "receiver-snapshot-digest"
                    evidence
                    |> List.map (fun (kind, uri, item) ->
                        let observed =
                            subject
                                $"receiver:{snapshot.ReceiverName}:pass:{passOrdinal}:{kind}"
                                item.RawSha256 item.RawBody
                        uri, item.RawBody, observed)
                let validatePass (passOrdinal: int) (snapshots: MigrationReceiverSnapshot list) =
                    if snapshots |> List.map _.ReceiverName <> expectedNames then
                        failwith "receiver-population"
                    List.map2 (validateSnapshot passOrdinal) declarations snapshots |> List.collect id
                let firstRows = validatePass 1 first
                let secondRows = validatePass 2 second
                if first <> second then failwith "receiver-two-pass-drift"
                let rows = firstRows @ secondRows
                let pages =
                    rows
                    |> List.mapi (fun index (uri, body, observed) ->
                        let next =
                            if index + 1 < rows.Length then
                                let nextUri, _, _ = rows.[index + 1]
                                Some(sha nextUri)
                            else None
                        { RequestedUri=uri; RequestIdentitySha256=sha uri
                          RawBody=body; PayloadSha256=sha body
                          NextRequestIdentitySha256=next; Subjects=[ observed ] })
                let subjects = pages |> List.collect _.Subjects
                Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                     ScopeVerified=true; SubjectsParsedFromRaw=true
                     Read={ Authority="receiver-identities/declared"
                            ObservedAt=DateTimeOffset.UtcNow
                            PageCount=pages.Length; ItemCount=subjects.Length
                            Terminal=true; NextCursor=None
                            HighWaterMark=digestParts (pages |> List.map _.PayloadSha256)
                            Subjects=subjects }
                     Pages=pages }
            with failure -> Error $"receiver-declared-raw-or-scope:{failure.Message}"

    let bindDeclaredWorkflowPins
        (options: MigrationInspectProviderOptions)
        (pinsByReceiver: Map<string, MigrationReceiverPinDeclaration list>)
        (first: MigrationReceiverPinSnapshot list)
        (second: MigrationReceiverPinSnapshot list) =
        let receivers = options.Cohort.Receivers |> List.sortBy _.Receiver
        let receiverNames = receivers |> List.map _.Receiver
        let declaredNames = pinsByReceiver |> Map.toList |> List.map fst |> List.sort
        if not (GitHubMigrationInspect.validCohort options.Cohort)
           || declaredNames <> (receiverNames |> List.sort)
           || pinsByReceiver |> Map.exists (fun _ pins -> isNull (box pins) || pins.IsEmpty) then
            Error "workflow-pins-declaration"
        else
            match bindDeclaredReceiverIdentities options
                      (first |> List.map _.Receiver) (second |> List.map _.Receiver) with
            | Error reason -> Error $"workflow-pins-receiver:{reason}"
            | Ok _ ->
                try
                    let bytesSha (value: byte array) =
                        value |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
                    let isWorkflow (path: string) =
                        path.StartsWith(".github/workflows/", StringComparison.Ordinal)
                    let packageNames =
                        set [ "global.json"; "Directory.Packages.props"; "packages.lock.json"
                              "package.json"; "package-lock.json"; "pnpm-lock.yaml"
                              "yarn.lock"; "nuget.config" ]
                    let isPackage (path: string) = path.Split('/') |> Array.last |> packageNames.Contains
                    let validDeclaration (pin: MigrationReceiverPinDeclaration) =
                        not (String.IsNullOrWhiteSpace pin.EntryPath)
                        && (pin.PinKind = "workflow" && isWorkflow pin.EntryPath
                            || pin.PinKind = "package" && isPackage pin.EntryPath
                               && not (isWorkflow pin.EntryPath))
                    let stringProperty (name: string) (root: JsonElement) =
                        let value = root.GetProperty(name)
                        if value.ValueKind <> JsonValueKind.String then failwith $"string:{name}"
                        value.GetString()
                    let validateBlob (receiver: MigrationReceiverSnapshot)
                                     (declaration: MigrationReceiverPinDeclaration)
                                     (pin: MigrationReceiverPinBlob) =
                        let entry =
                            receiver.TreeEntries
                            |> List.tryFind (fun item -> item.EntryPath = declaration.EntryPath)
                            |> Option.defaultWith (fun () -> failwith "pin-tree-entry")
                        if pin.EntryPath <> declaration.EntryPath || pin.PinKind <> declaration.PinKind
                           || entry.EntryKind <> "blob" || entry.EntryMode <> pin.EntryMode
                           || entry.EntrySha <> pin.EntrySha || entry.EntrySize <> Some pin.EntrySize
                           || not (Set.contains pin.EntryMode (set [ "100644"; "100755" ])) then
                            failwith "pin-binding"
                        let parts = receiver.RepositoryFullName.Split('/')
                        if parts.Length <> 2 then failwith "pin-repository"
                        let uri =
                            Uri(options.Repository.ApiBase,
                                $"repos/{Uri.EscapeDataString parts.[0]}/{Uri.EscapeDataString parts.[1]}/git/blobs/{pin.EntrySha}")
                                .AbsoluteUri
                        if pin.RequestUri <> uri || pin.RequestSha256 <> sha $"GET\n{uri}"
                           || pin.RawSha256 <> sha pin.RawBody || pin.BytesSha256 <> bytesSha pin.Bytes
                           || int64 pin.Bytes.LongLength <> pin.EntrySize then
                            failwith "pin-evidence"
                        use document = JsonDocument.Parse pin.RawBody
                        let root = document.RootElement
                        requireUniqueMembers root
                        let encoded = stringProperty "content" root
                        let rawBytes =
                            let compact = encoded.Replace("\r", "").Replace("\n", "")
                            if compact |> Seq.exists (fun c ->
                                not (Char.IsAsciiLetterOrDigit c || c = '+' || c = '/' || c = '=')) then
                                failwith "pin-base64"
                            Convert.FromBase64String compact
                        if stringProperty "sha" root <> pin.EntrySha
                           || stringProperty "encoding" root <> "base64"
                           || stringProperty "url" root <> uri
                           || root.GetProperty("size").GetInt64() <> pin.EntrySize
                           || rawBytes <> pin.Bytes then
                            failwith "pin-raw-typed"
                        let gitBytes =
                            Array.append
                                (Encoding.ASCII.GetBytes($"blob {rawBytes.LongLength}\u0000")) rawBytes
                        let gitSha =
                            gitBytes |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
                        if gitSha <> pin.EntrySha then failwith "pin-git-object"
                    let validatePass (snapshots: MigrationReceiverPinSnapshot list) =
                        if snapshots |> List.map (fun item -> item.Receiver.ReceiverName) <> receiverNames then
                            failwith "pin-receiver-population"
                        snapshots
                        |> List.map (fun snapshot ->
                            let receiverName = snapshot.Receiver.ReceiverName
                            let declarations = pinsByReceiver.[receiverName] |> List.sortBy _.EntryPath
                            if declarations |> List.exists (validDeclaration >> not)
                               || declarations.Length
                                  <> (declarations |> List.map _.EntryPath |> Set.ofList |> Set.count)
                               || snapshot.Pins |> List.map _.EntryPath
                                  <> (declarations |> List.map _.EntryPath) then
                                failwith "pin-population"
                            let relevantPaths =
                                snapshot.Receiver.TreeEntries
                                |> List.filter (fun entry -> isWorkflow entry.EntryPath || isPackage entry.EntryPath)
                                |> List.map _.EntryPath |> Set.ofList
                            if relevantPaths <> (declarations |> List.map _.EntryPath |> Set.ofList) then
                                failwith "pin-tree-census"
                            List.iter2 (validateBlob snapshot.Receiver) declarations snapshot.Pins
                            let terminal = snapshot.TerminalRefEvidence
                            if terminal.RequestUri <> snapshot.Receiver.TerminalRefEvidence.RequestUri
                               || terminal.RawBody <> snapshot.Receiver.TerminalRefEvidence.RawBody
                               || terminal.RawSha256 <> sha terminal.RawBody then
                                failwith "pin-terminal-ref"
                            let expectedDigest =
                                [ yield snapshot.Receiver.SnapshotSha256
                                  for pin in snapshot.Pins do
                                      yield pin.EntryPath
                                      yield pin.PinKind
                                      yield pin.EntryMode
                                      yield pin.EntrySha
                                      yield string pin.EntrySize
                                      yield pin.RequestUri
                                      yield pin.RequestSha256
                                      yield pin.RawSha256
                                      yield pin.BytesSha256
                                  yield terminal.RequestUri
                                  yield terminal.RawSha256 ]
                                |> digestParts
                            if snapshot.PinSnapshotSha256 <> expectedDigest then
                                failwith "pin-snapshot-digest"
                            snapshot)
                    let firstValidated = validatePass first
                    let secondValidated = validatePass second
                    if firstValidated <> secondValidated then failwith "pin-two-pass-drift"
                    let rows =
                        firstValidated
                        |> List.collect (fun snapshot ->
                            [ for pin in snapshot.Pins do
                                let observed =
                                    subject
                                        $"receiver:{snapshot.Receiver.ReceiverName}:workflow-pin:{pin.EntryPath}"
                                        pin.BytesSha256 pin.RawBody
                                yield pin.RequestUri, pin.RawBody, observed
                              let terminal = snapshot.TerminalRefEvidence
                              let terminalSubject =
                                  subject
                                      $"receiver:{snapshot.Receiver.ReceiverName}:workflow-pin:terminal-ref"
                                      terminal.RawSha256 terminal.RawBody
                              yield terminal.RequestUri, terminal.RawBody, terminalSubject ])
                    let pages =
                        rows
                        |> List.mapi (fun index (uri, body, observed) ->
                            let next =
                                if index + 1 < rows.Length then
                                    let nextUri, _, _ = rows.[index + 1]
                                    Some(sha nextUri)
                                else None
                            { RequestedUri=uri; RequestIdentitySha256=sha uri
                              RawBody=body; PayloadSha256=sha body
                              NextRequestIdentitySha256=next; Subjects=[ observed ] })
                    let subjects = pages |> List.collect _.Subjects |> List.sortBy _.Identity
                    Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                         ScopeVerified=true; SubjectsParsedFromRaw=true
                         Read={ Authority="workflow-pins/declared"
                                ObservedAt=DateTimeOffset.UtcNow
                                PageCount=pages.Length; ItemCount=subjects.Length
                                Terminal=true; NextCursor=None
                                HighWaterMark=digestParts (pages |> List.map _.PayloadSha256)
                                Subjects=subjects }
                         Pages=pages }
                with failure -> Error $"workflow-pins-raw-or-scope:{failure.Message}"

    let private allowedNativeActivityRequest (options: MigrationInspectProviderOptions) request =
        match request with
        | GraphQL _ -> false
        | Rest value ->
            let repository =
                Uri(options.Repository.ApiBase,
                    $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}")
            let relative =
                if value.Uri.AbsolutePath.StartsWith(repository.AbsolutePath, StringComparison.Ordinal) then
                    value.Uri.AbsolutePath.Substring(repository.AbsolutePath.Length)
                else "foreign"
            let segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            let positive (candidate: string) =
                let mutable number = 0
                Int32.TryParse(candidate, &number) && number > 0
            let activityPath =
                match segments with
                | [||] -> true
                | [| "issues" |] | [| "pulls" |] -> true
                | [| "issues"; number; ("comments" | "events") |] -> positive number
                | [| "pulls"; number; ("reviews" | "comments") |] -> positive number
                | _ -> false
            value.Method = Get && value.Body.IsNone
            && value.Uri.Scheme = Uri.UriSchemeHttps
            && value.Uri.Authority = repository.Authority
            && activityPath

    let bindNativeActivity
        (options: MigrationInspectProviderOptions)
        (first: MigrationNativeActivityCapture)
        (firstCaptures: (GitHubRequest * TransportOutcome) list)
        (second: MigrationNativeActivityCapture)
        (secondCaptures: (GitHubRequest * TransportOutcome) list) =
        if not (repositoryBinding options) then Error "native-activity-cohort"
        else
            try
                let identityUri =
                    Uri(options.Repository.ApiBase,
                        $"repos/{Uri.EscapeDataString options.Repository.Owner}/{Uri.EscapeDataString options.Repository.Repository}")
                        .AbsoluteUri
                let pageUris (pages: MigrationRestPageEvidence list) = pages |> List.map _.RequestedUri
                let streamPages (input: MigrationNativeActivityInput) =
                    [ yield! input.IssueComments |> List.collect (fun value -> value.Pages)
                      yield! input.IssueEvents |> List.collect (fun value -> value.Pages)
                      yield! input.PullRequestComments |> List.collect (fun value -> value.Pages)
                      yield! input.PullRequestReviews |> List.collect (fun value -> value.Pages)
                      yield! input.PullRequestInlineComments |> List.collect (fun value -> value.Pages) ]
                let expectedUriCounts (input: MigrationNativeActivityInput) =
                    let streamReadCount =
                        input.IssueComments.Length + input.IssueEvents.Length
                        + input.PullRequestComments.Length + input.PullRequestReviews.Length
                        + input.PullRequestInlineComments.Length
                    [ for _ in 1 .. 4 + streamReadCount do yield identityUri
                      for uri in pageUris input.Issues.Pages do yield uri; yield uri
                      for uri in pageUris input.PullRequests.Pages do yield uri; yield uri
                      yield! streamPages input |> pageUris ]
                    |> List.countBy id |> Map.ofList
                let rawCalls captures =
                    captures
                    |> List.map (fun (request, outcome) ->
                        if not (allowedNativeActivityRequest options request) then failwith "request-scope"
                        match request, responseBody outcome with
                        | Rest value, Ok body -> value.Uri.AbsoluteUri, body, outcome
                        | _ -> failwith "provider-response")
                let payloadSubject identity payload =
                    payload, subject identity (sha payload) payload
                let typedRows (input: MigrationNativeActivityInput) =
                    [ yield! input.Issues.Issues
                              |> List.map (fun item ->
                                  payloadSubject $"native:issue:{item.NodeId}" item.PayloadJson)
                      yield! input.PullRequests.PullRequests
                              |> List.map (fun item ->
                                  payloadSubject $"native:pull-request:{item.NodeId}" item.PayloadJson)
                      yield! input.IssueComments |> List.collect (fun stream ->
                          stream.Comments |> List.map (fun item ->
                              payloadSubject $"native:issue-comment:{item.NodeId}" item.PayloadJson))
                      yield! input.IssueEvents |> List.collect (fun stream ->
                          stream.Events |> List.map (fun item ->
                              payloadSubject $"native:issue-event:{item.NodeId}" item.PayloadJson))
                      yield! input.PullRequestComments |> List.collect (fun stream ->
                          stream.Comments |> List.map (fun item ->
                              payloadSubject $"native:pull-comment:{item.NodeId}" item.PayloadJson))
                      yield! input.PullRequestReviews |> List.collect (fun stream ->
                          stream.Reviews |> List.map (fun item ->
                              payloadSubject $"native:review:{item.NodeId}" item.PayloadJson))
                      yield! input.PullRequestInlineComments |> List.collect (fun stream ->
                          stream.Comments |> List.map (fun item ->
                              payloadSubject $"native:inline-comment:{item.NodeId}" item.PayloadJson)) ]
                let validatePass (capture: MigrationNativeActivityCapture) captures =
                    let reconciled =
                        MigrationNativeActivity.reconcile options.Repository capture.Input
                        |> function
                           | Ok value -> value
                           | Error failure -> failwith $"typed-reconcile:{failure}"
                    if reconciled <> capture.Snapshot then failwith "snapshot-mismatch"
                    let calls = rawCalls captures
                    let actualCounts = calls |> List.map (fun (uri, _, _) -> uri) |> List.countBy id |> Map.ofList
                    if actualCounts <> expectedUriCounts capture.Input then failwith "request-population"
                    let byUri = calls |> List.groupBy (fun (uri, _, _) -> uri) |> Map.ofList
                    let identities = byUri.[identityUri]
                    let expectedIdentityCount =
                        4 + capture.Input.IssueComments.Length + capture.Input.IssueEvents.Length
                        + capture.Input.PullRequestComments.Length + capture.Input.PullRequestReviews.Length
                        + capture.Input.PullRequestInlineComments.Length
                    if identities.Length <> expectedIdentityCount
                       || (identities |> List.map (fun (_, body, _) -> body) |> List.distinct |> List.length) <> 1 then
                        failwith "identity-drift"
                    let identityBody = identities.Head |> fun (_, body, _) -> body
                    use identityDocument = JsonDocument.Parse identityBody
                    requireUniqueMembers identityDocument.RootElement
                    if identityDocument.RootElement.GetProperty("id").GetInt64()
                           <> options.Repository.ExpectedRepositoryId
                       || identityDocument.RootElement.GetProperty("full_name").GetString()
                           <> $"{options.Repository.Owner}/{options.Repository.Repository}" then
                        failwith "identity"
                    let rows = typedRows capture.Input
                    if rows.Length <> (rows |> List.map fst |> Set.ofList |> Set.count) then
                        failwith "typed-payload-duplicate"
                    let subjectByPayload = rows |> Map.ofList
                    let pageSubjects = System.Collections.Generic.Dictionary<string, GitHubDiscoverySubject list>()
                    let validatePages (pages: MigrationRestPageEvidence list)
                                      (expectedPayloads: string list) skipPullMarkers expectedCopies =
                        let rawItems = ResizeArray<string>()
                        for page in pages do
                            let occurrences = byUri.[page.RequestedUri]
                            if occurrences.Length <> expectedCopies then failwith "page-copy-count"
                            let bodies = occurrences |> List.map (fun (_, body, _) -> body) |> List.distinct
                            if bodies.Length <> 1 || sha bodies.Head <> page.PayloadSha256 then
                                failwith "raw-page-drift"
                            if occurrences
                               |> List.exists (fun (_, _, outcome) -> capturedNextUri outcome <> Some page.NextUri) then
                                failwith "page-chain"
                            use document = JsonDocument.Parse bodies.Head
                            if document.RootElement.ValueKind <> JsonValueKind.Array then failwith "page-array"
                            requireUniqueMembers document.RootElement
                            let retained =
                                document.RootElement.EnumerateArray()
                                |> Seq.choose (fun item ->
                                    let mutable marker = Unchecked.defaultof<JsonElement>
                                    if skipPullMarkers && item.TryGetProperty("pull_request", &marker) then None
                                    else Some(item.GetRawText()))
                                |> Seq.toList
                            retained |> List.iter rawItems.Add
                            let subjects =
                                retained |> List.map (fun payload ->
                                    Map.tryFind payload subjectByPayload
                                    |> Option.defaultWith (fun () -> failwith "raw-typed-item"))
                            pageSubjects.[page.RequestedUri] <- subjects
                        if (rawItems |> Seq.toList |> List.sort) <> List.sort expectedPayloads then
                            failwith "raw-typed-population"
                    validatePages capture.Input.Issues.Pages
                        (capture.Input.Issues.Issues |> List.map _.PayloadJson) true 2
                    validatePages capture.Input.PullRequests.Pages
                        (capture.Input.PullRequests.PullRequests |> List.map _.PayloadJson) false 2
                    for stream in capture.Input.IssueComments do
                        validatePages stream.Pages (stream.Comments |> List.map _.PayloadJson) false 1
                    for stream in capture.Input.IssueEvents do
                        validatePages stream.Pages (stream.Events |> List.map _.PayloadJson) false 1
                    for stream in capture.Input.PullRequestComments do
                        validatePages stream.Pages (stream.Comments |> List.map _.PayloadJson) false 1
                    for stream in capture.Input.PullRequestReviews do
                        validatePages stream.Pages (stream.Reviews |> List.map _.PayloadJson) false 1
                    for stream in capture.Input.PullRequestInlineComments do
                        validatePages stream.Pages (stream.Comments |> List.map _.PayloadJson) false 1
                    let orderedPages =
                        capture.Input.Issues.Pages @ capture.Input.PullRequests.Pages
                        @ streamPages capture.Input
                    orderedPages
                    |> List.map (fun page ->
                        let _, body, _ = byUri.[page.RequestedUri].Head
                        page.RequestedUri, body, pageSubjects.[page.RequestedUri])
                let firstPages = validatePass first firstCaptures
                let secondPages = validatePass second secondCaptures
                let normalizedCalls captures =
                    rawCalls captures
                    |> List.map (fun (uri, body, outcome) -> uri, body, capturedNextUri outcome)
                if first <> second || normalizedCalls firstCaptures <> normalizedCalls secondCaptures
                   || firstPages <> secondPages then
                    failwith "two-pass-drift"
                let pages =
                    firstPages
                    |> List.mapi (fun index (uri, body, subjects) ->
                        let next =
                            if index + 1 < firstPages.Length then
                                let nextUri, _, _ = firstPages.[index + 1]
                                Some(sha nextUri)
                            else None
                        { RequestedUri=uri; RequestIdentitySha256=sha uri
                          RawBody=body; PayloadSha256=sha body
                          NextRequestIdentitySha256=next; Subjects=subjects })
                let subjects = pages |> List.collect _.Subjects |> List.sortBy _.Identity
                Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 options.Cohort
                     ScopeVerified=true; SubjectsParsedFromRaw=true
                     Read={ Authority="claim-and-event-streams/native"
                            ObservedAt=DateTimeOffset.UtcNow
                            PageCount=pages.Length; ItemCount=subjects.Length
                            Terminal=true; NextCursor=None
                            HighWaterMark=digestParts (pages |> List.map _.PayloadSha256)
                            Subjects=subjects }
                     Pages=pages }
            with failure -> Error $"native-activity-raw-or-scope:{failure.Message}"

    let readNativeActivity options (transport: IMigrationGitHubReadTransport) =
        let capture () =
            let retained = CapturingTransport(transport, allowedNativeActivityRequest options)
            MigrationNativeActivity.capture options.Repository retained
            |> Result.mapError (fun failure -> $"native-activity-read:{failure}")
            |> Result.map (fun value -> value, retained.Calls)
        capture ()
        |> Result.bind (fun (first, firstCalls) ->
            capture ()
            |> Result.bind (fun (second, secondCalls) ->
                bindNativeActivity options first firstCalls second secondCalls))

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
                            requireUniqueMembers document.RootElement
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
                    elif List.zip population.Pages calls
                         |> List.exists (fun (page, (_, outcome)) ->
                             capturedNextUri outcome <> Some page.NextUri) then Error "issue-page-chain"
                    else
                        match parseIssuePages population.RepositoryId raw with
                        | Error reason -> Error reason
                        | Ok parsed ->
                            let rawRecords = parsed |> List.collect (fst >> List.map fst)
                                                    |> List.sortBy (fun (number, _, _, _, _, _) -> number)
                            let rawPullRequestNumbers = parsed |> List.collect snd |> List.sort
                            let typedRecords =
                                population.Issues
                                |> List.map (fun item ->
                                    item.Number, item.DatabaseId, item.NodeId, item.State,
                                    item.UpdatedAt, item.PayloadJson)
                            let issueNumbers = rawRecords |> List.map (fun (number, _, _, _, _, _) -> number)
                            let databaseIds = rawRecords |> List.map (fun (_, databaseId, _, _, _, _) -> databaseId)
                            let nodeIds = rawRecords |> List.map (fun (_, _, nodeId, _, _, _) -> nodeId)
                            let issueNumberSet = Set.ofList issueNumbers
                            let uniqueCensus =
                                issueNumbers.Length = issueNumberSet.Count
                                && databaseIds.Length = (databaseIds |> Set.ofList |> Set.count)
                                && nodeIds.Length = (nodeIds |> Set.ofList |> Set.count)
                                && rawPullRequestNumbers.Length = (rawPullRequestNumbers |> Set.ofList |> Set.count)
                                && (rawPullRequestNumbers |> List.forall (fun number ->
                                    not (Set.contains number issueNumberSet)))
                            if rawPullRequestNumbers.Length <> population.PullRequestCount then Error "issue-pr-count"
                            elif rawPullRequestNumbers <> population.PullRequestMarkerNumbers then Error "issue-pr-markers"
                            elif not uniqueCensus then Error "issue-duplicate-identity"
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
            requireUniqueMembers root
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
                        let itemIds = rows |> List.map (fun ((id, _, _, _, _), _) -> id)
                        let typedRecords =
                            population.Items |> List.map (fun item ->
                                item.ItemNodeId, item.Archived, item.UpdatedAt, item.Content, item.PayloadJson)
                        if not correctCursors || not terminal then Error "project-page-chain"
                        elif not sameTotals || rows.Length <> population.TotalCount then Error "project-total"
                        elif itemIds |> List.exists String.IsNullOrWhiteSpace
                             || itemIds.Length <> (itemIds |> Set.ofList |> Set.count) then
                            Error "project-duplicate-identity"
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
            requireUniqueMembers root
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
                    && (pages |> List.collect snd |> List.sort) = issues.PullRequestMarkerNumbers
                    && issues.PullRequestMarkerNumbers.Length = issues.PullRequestCount
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
                    requireUniqueMembers root
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
