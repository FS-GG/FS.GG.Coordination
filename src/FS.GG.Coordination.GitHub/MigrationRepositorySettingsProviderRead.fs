namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationRepositoryTagSetting =
    { Name: string; NodeId: string; TargetSha: string
      PayloadJson: string; PayloadSha256: string }

type MigrationRepositoryReleaseSetting =
    { DatabaseId: int64; NodeId: string; TagName: string; TargetCommitish: string
      Name: string option; Draft: bool; Prerelease: bool; Immutable: bool
      PayloadJson: string; PayloadSha256: string }

type MigrationRepositoryReleasesAndTagsRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Tags: MigrationRepositoryTagSetting list
      Releases: MigrationRepositoryReleaseSetting list }

type MigrationRepositoryCodeSecurityRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      ConfigurationId: int64
      ConfigurationTargetType: string
      ConfigurationName: string
      Enforcement: string
      ConfigurationUpdatedAt: string }

type MigrationRepositoryDependencyControlsRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      ConfigurationId: int64
      ConfigurationTargetType: string
      ConfigurationName: string
      Enforcement: string
      ConfigurationUpdatedAt: string
      DependencyGraph: bool
      DependencyGraphAutosubmitAction: bool
      DependencyGraphAutosubmitUsesLabeledRunners: bool
      DependabotAlerts: bool
      DependabotSecurityUpdates: bool
      DependabotSecurityUpdatesPaused: bool
      DependabotDelegatedAlertDismissal: bool }

type MigrationRepositoryPropertiesRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Visibility: string
      Archived: bool
      Disabled: bool
      HasIssues: bool
      HasProjects: bool
      HasWiki: bool
      HasPages: bool
      HasDiscussions: bool
      HasDownloads: bool
      HasPullRequests: bool
      PullRequestCreationPolicy: string
      IsTemplate: bool
      AllowForking: bool option
      WebCommitSignoffRequired: bool
      Description: string option
      Homepage: string option
      Topics: string list }

type MigrationRepositoryMergePolicyRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      AllowSquashMerge: bool
      AllowMergeCommit: bool
      AllowRebaseMerge: bool
      AllowAutoMerge: bool
      AllowUpdateBranch: bool
      DeleteBranchOnMerge: bool
      SquashMergeCommitTitle: string
      SquashMergeCommitMessage: string
      MergeCommitTitle: string
      MergeCommitMessage: string }

type MigrationRepositorySelectedActions =
    { GitHubOwnedAllowed: bool
      VerifiedAllowed: bool
      PatternsAllowed: string list }

type MigrationRepositoryActionsPolicyRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      OrganizationEnabledRepositories: string
      OrganizationAllowedActions: string
      OrganizationSelectedActions: MigrationRepositorySelectedActions option
      OrganizationShaPinningRequired: bool
      RepositoryEnabled: bool
      RepositoryAllowedActions: string
      RepositorySelectedActions: MigrationRepositorySelectedActions option
      ShaPinningRequired: bool
      OrganizationDefaultWorkflowPermissions: string
      OrganizationCanApprovePullRequestReviews: bool
      RepositoryDefaultWorkflowPermissions: string
      RepositoryCanApprovePullRequestReviews: bool
      OrganizationArtifactAndLogRetentionDays: int64
      OrganizationMaximumArtifactAndLogRetentionDays: int64
      RepositoryArtifactAndLogRetentionDays: int64
      RepositoryMaximumArtifactAndLogRetentionDays: int64
      OrganizationForkPullRequestApprovalPolicy: string
      RepositoryForkPullRequestApprovalPolicy: string
      ApplicableActionsPolicyCount: int64 }

type MigrationRepositoryEnvironmentsRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Environments: MigrationEnvironmentObservation list }

[<RequireQualifiedAccess>]
module MigrationRepositorySettingsProviderRead =
    let private hashText (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private refuse reason =
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable reason)

    let private prop (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &found) then Ok found
        else refuse $"missing:{name}"

    let private text (name: string) (value: JsonElement) =
        prop name value
        |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.String && validText (item.GetString()) then Ok(item.GetString())
            else refuse $"invalid:{name}")

    let private optionalText (name: string) (value: JsonElement) =
        prop name value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.Null -> Ok None
            | JsonValueKind.String when not (isNull (item.GetString())) -> Ok(Some(item.GetString()))
            | _ -> refuse $"invalid:{name}")

    let private positive (name: string) (value: JsonElement) =
        prop name value
        |> Result.bind (fun item ->
            let mutable number = 0L
            if item.ValueKind = JsonValueKind.Number && item.TryGetInt64(&number) && number > 0L then Ok number
            else refuse $"invalid:{name}")

    let private flag (name: string) (value: JsonElement) =
        prop name value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.True -> Ok true
            | JsonValueKind.False -> Ok false
            | _ -> refuse $"invalid:{name}")

    let private arrayRoot (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Array then Ok(value.EnumerateArray() |> Seq.toList)
        else refuse "invalid:array-root"

    let rec private uniqueMembers (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let seen = HashSet<string>(StringComparer.Ordinal)
            value.EnumerateObject()
            |> Seq.fold (fun state memberValue ->
                state
                |> Result.bind (fun () ->
                    if not (seen.Add memberValue.Name) then refuse $"duplicate-member:{memberValue.Name}"
                    else uniqueMembers memberValue.Value)) (Ok())
        | JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.fold (fun state item -> state |> Result.bind (fun () -> uniqueMembers item)) (Ok())
        | _ -> Ok()

    let private parse (body: string) =
        if isNull body then refuse "missing:response-body"
        else
            try
                use document = JsonDocument.Parse body
                let root = document.RootElement.Clone()
                uniqueMembers root |> Result.map (fun () -> root)
            with :? JsonException -> refuse "invalid-json"

    let private headers (options: MigrationGitHubReadOptions) =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", options.UserAgent
          if not (String.IsNullOrWhiteSpace options.Token) then
              "authorization", $"Bearer {options.Token}" ]
        |> Map.ofList

    let private responseHeader name (headers: Map<string, string>) =
        headers
        |> Map.toSeq
        |> Seq.tryPick (fun (key, value) ->
            if key.Equals(name, StringComparison.OrdinalIgnoreCase) then Some value else None)

    let private get
        (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport)
        (uri: Uri)
        : Result<ResponseEnvelope, MigrationRepositorySettingsSurfaceRefusal> =
        let request =
            Rest
                { Method=Get; Uri=uri; Headers=headers options; Body=None
                  ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        match transport.Send request with
        | Response response when response.StatusCode = 200 && not (isNull response.Body) -> Ok response
        | Response response when response.StatusCode = 401 || response.StatusCode = 403 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized $"http:{response.StatusCode}")
        | Response response when response.StatusCode = 404 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "http:404")
        | Response response ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable $"http:{response.StatusCode}")
        | NetworkFailure | TimedOut ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "transport-unavailable")

    let private repoPath (options: MigrationGitHubReadOptions) =
        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps && options.ApiBase.AbsolutePath = "/"
        && options.ExpectedRepositoryId > 0L
        && [ options.Owner; options.Repository; options.UserAgent ]
           |> List.forall (fun value ->
               validText value && value.IndexOfAny([| '/'; '?'; '#'; '\\' |]) < 0)

    let private sourceNodeId (root: JsonElement) =
        let mutable fork = Unchecked.defaultof<JsonElement>
        if not (root.TryGetProperty("fork", &fork)) then refuse "missing:fork"
        elif fork.ValueKind = JsonValueKind.False then Ok None
        elif fork.ValueKind <> JsonValueKind.True then refuse "invalid:fork"
        else
            prop "source" root
            |> Result.bind (fun source -> text "node_id" source)
            |> Result.map Some

    let private repositoryResponse
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (revision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        let uri = Uri(options.ApiBase, repoPath options)
        get options transport uri
        |> Result.bind (fun response ->
            if responseHeader "link" response.Headers |> Option.isSome then
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                    "repository-unexpected-continuation")
            else
                parse response.Body
                |> Result.bind (fun root ->
                    match positive "id" root, text "node_id" root, text "full_name" root,
                          text "default_branch" root, text "updated_at" root, sourceNodeId root with
                    | Ok id, Ok nodeId, Ok fullName, Ok defaultBranch, Ok updatedAt, Ok source when
                        id = options.ExpectedRepositoryId
                        && id = identity.DatabaseId
                        && nodeId = identity.NodeId
                        && fullName = $"{identity.Owner}/{identity.Name}"
                        && fullName = $"{options.Owner}/{options.Repository}"
                        && defaultBranch = identity.DefaultBranch
                        && source = identity.SourceRepositoryNodeId
                        && updatedAt = revision ->
                        Ok(response, root)
                    | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ ->
                        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "repository-identity-drift")
                    | Error failure, _, _, _, _, _ | _, Error failure, _, _, _, _
                    | _, _, Error failure, _, _, _ | _, _, _, Error failure, _, _
                    | _, _, _, _, Error failure, _ | _, _, _, _, _, Error failure -> Error failure))

    let private identityPage (options: MigrationGitHubReadOptions) (response: ResponseEnvelope) =
        { SettingsStream="repository-identity"
          SettingsRequestedUri=Uri(options.ApiBase, repoPath options).AbsoluteUri
          SettingsPayloadJson=response.Body
          SettingsPayloadSha256=hashText response.Body
          SettingsNextUri=None }

    let private repositoryEvidence options identity revision transport =
        repositoryResponse options identity revision transport
        |> Result.bind (fun (response, root) ->
            prop "permissions" root
            |> Result.bind (flag "push")
            |> Result.bind (fun canPush ->
                if canPush then Ok(identityPage options response)
                else
                    Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                        "draft-release-visibility-unproven")))

    let private validPageUri (options: MigrationGitHubReadOptions) (suffix: string) (uri: Uri) =
        let named = $"/{repoPath options}/{suffix}"
        let numeric = $"/repositories/{options.ExpectedRepositoryId}/{suffix}"
        let query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        let pageIsPositive =
            query
            |> Array.tryPick (fun item ->
                if item.StartsWith("page=", StringComparison.Ordinal) then
                    let mutable page = 0
                    if Int32.TryParse(item.Substring(5), &page) then Some(page > 0) else Some false
                else None)
            |> Option.defaultValue false
        uri.IsAbsoluteUri && uri.Scheme = options.ApiBase.Scheme && uri.Authority = options.ApiBase.Authority
        && (uri.AbsolutePath = named || uri.AbsolutePath = numeric)
        && query.Length = 2
        && (query |> Array.filter ((=) "per_page=100") |> Array.length) = 1
        && (query
            |> Array.filter (fun item -> item.StartsWith("page=", StringComparison.Ordinal))
            |> Array.length) = 1
        && pageIsPositive

    let private nextUri
        (options: MigrationGitHubReadOptions)
        (suffix: string)
        (response: ResponseEnvelope)
        =
        match responseHeader "link" response.Headers with
        | None -> Ok None
        | Some link ->
            match Transport.tryNextLink link with
            | Ok None -> Ok None
            | Ok(Some uri) when validPageUri options suffix uri -> Ok(Some uri)
            | Ok(Some _) ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial "pagination-escaped-scope")
            | Error _ -> Error(MigrationRepositorySettingsSurfaceRefusal.Partial "pagination-malformed")

    let private collect
        (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport)
        (suffix: string)
        (stream: string)
        (parseItem: JsonElement -> Result<'item, MigrationRepositorySettingsSurfaceRefusal>)
        : Result<MigrationRepositorySettingsPageEvidence list * 'item list,
                 MigrationRepositorySettingsSurfaceRefusal> =
        let start = Uri(options.ApiBase, $"{repoPath options}/{suffix}?per_page=100&page=1")
        let rec loop
            (seen: Set<string>)
            (uri: Uri)
            (pages: MigrationRepositorySettingsPageEvidence list)
            (items: 'item list)
            =
            if not (validPageUri options suffix uri) then
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial "pagination-request-out-of-scope")
            elif Set.contains uri.AbsoluteUri seen then
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial "pagination-repeated")
            else
                get options transport uri
                |> Result.bind (fun response ->
                    parse response.Body
                    |> Result.bind arrayRoot
                    |> Result.bind (fun entries ->
                        entries
                        |> List.fold (fun state entry ->
                            state |> Result.bind (fun values -> parseItem entry |> Result.map (fun value -> value::values)))
                            (Ok [])
                        |> Result.map List.rev)
                    |> Result.bind (fun parsed ->
                        nextUri options suffix response
                        |> Result.bind (fun next ->
                            let page =
                                { SettingsStream=stream; SettingsRequestedUri=uri.AbsoluteUri
                                  SettingsPayloadJson=response.Body; SettingsPayloadSha256=hashText response.Body
                                  SettingsNextUri=next |> Option.map _.AbsoluteUri }
                            match next with
                            | Some nextPage -> loop (Set.add uri.AbsoluteUri seen) nextPage (page::pages) (items@parsed)
                            | None -> Ok(List.rev (page::pages), items@parsed))))
        loop Set.empty start [] []

    let private tag (value: JsonElement) : Result<MigrationRepositoryTagSetting, MigrationRepositorySettingsSurfaceRefusal> =
        match text "name" value, text "node_id" value, prop "commit" value |> Result.bind (text "sha") with
        | Ok name, Ok nodeId, Ok target ->
            let payload = value.GetRawText()
            Ok { Name=name; NodeId=nodeId; TargetSha=target
                 PayloadJson=payload; PayloadSha256=hashText payload }
        | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure

    let private release (value: JsonElement) : Result<MigrationRepositoryReleaseSetting, MigrationRepositorySettingsSurfaceRefusal> =
        match positive "id" value, text "node_id" value, text "tag_name" value,
              text "target_commitish" value, optionalText "name" value,
              flag "draft" value, flag "prerelease" value, flag "immutable" value with
        | Ok id, Ok nodeId, Ok tagName, Ok target, Ok name, Ok draft, Ok prerelease, Ok immutable ->
            let payload = value.GetRawText()
            Ok { DatabaseId=id; NodeId=nodeId; TagName=tagName; TargetCommitish=target; Name=name
                 Draft=draft; Prerelease=prerelease; Immutable=immutable
                 PayloadJson=payload; PayloadSha256=hashText payload }
        | Error failure, _, _, _, _, _, _, _ | _, Error failure, _, _, _, _, _, _
        | _, _, Error failure, _, _, _, _, _ | _, _, _, Error failure, _, _, _, _
        | _, _, _, _, Error failure, _, _, _ | _, _, _, _, _, Error failure, _, _
        | _, _, _, _, _, _, Error failure, _ | _, _, _, _, _, _, _, Error failure -> Error failure

    let private unique (key: 'item -> 'key) (reason: string) (values: 'item list)
        : Result<'item list, MigrationRepositorySettingsSurfaceRefusal> when 'key: comparison =
        match values |> List.countBy key |> List.tryFind (fun (_, count) -> count > 1) with
        | Some _ -> refuse reason
        | None -> Ok values

    let private tagSettings (tag: MigrationRepositoryTagSetting) =
        let subject = $"tag:{tag.Name}"
        [ { Surface=ReleasesAndTags; Subject=subject; Name="node-id"; Value=SettingValue.Text tag.NodeId }
          { Surface=ReleasesAndTags; Subject=subject; Name="target-sha"; Value=SettingValue.Text tag.TargetSha } ]

    let private releaseSettings (release: MigrationRepositoryReleaseSetting) =
        let subject = $"release:{release.DatabaseId}"
        let core =
            [ { Surface=ReleasesAndTags; Subject=subject; Name="node-id"; Value=SettingValue.Text release.NodeId }
              { Surface=ReleasesAndTags; Subject=subject; Name="tag-name"; Value=SettingValue.Text release.TagName }
              { Surface=ReleasesAndTags; Subject=subject; Name="target-commitish"; Value=SettingValue.Text release.TargetCommitish }
              { Surface=ReleasesAndTags; Subject=subject; Name="draft"; Value=SettingValue.Boolean release.Draft }
              { Surface=ReleasesAndTags; Subject=subject; Name="prerelease"; Value=SettingValue.Boolean release.Prerelease }
              { Surface=ReleasesAndTags; Subject=subject; Name="immutable"; Value=SettingValue.Boolean release.Immutable } ]
        match release.Name with
        | Some name -> core @ [ { Surface=ReleasesAndTags; Subject=subject; Name="name"; Value=SettingValue.Text name } ]
        | None -> core

    let private explicitSecurityStatus name value =
        text name value
        |> Result.bind (fun status ->
            match status with
            | "enabled" | "disabled" -> Ok status
            | "not_set" ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial $"inherited-or-unset:{name}")
            | _ -> refuse $"unsupported:{name}:{status}")

    let private explicitSecurityFlag name value =
        explicitSecurityStatus name value
        |> Result.map ((=) "enabled")

    let private advancedSecurity value =
        text "advanced_security" value
        |> Result.bind (fun status ->
            match status with
            | "enabled" | "disabled" | "code_security" | "secret_protection" -> Ok status
            | _ -> refuse $"unsupported:advanced_security:{status}")

    let private exactMembers name allowed (value: JsonElement) =
        let names = value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
        if names = Set.ofList allowed then Ok value else refuse $"unsupported:{name}-shape"

    let private defaultSetupOptions value =
        prop "code_scanning_default_setup_options" value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.Null -> Ok "null"
            | JsonValueKind.Object ->
                exactMembers "code_scanning_default_setup_options" [ "runner_type"; "runner_label" ] item
                |> Result.bind (fun options ->
                    text "runner_type" options
                    |> Result.bind (fun runnerType ->
                        let label = prop "runner_label" options
                        match runnerType, label with
                        | "standard", Ok label when label.ValueKind = JsonValueKind.Null -> Ok(item.GetRawText())
                        | "labeled", Ok label when
                            label.ValueKind = JsonValueKind.String && validText (label.GetString()) ->
                            Ok(item.GetRawText())
                        | "not_set", _ ->
                            Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                                "inherited-or-unset:code_scanning_default_setup_options")
                        | _, Error failure -> Error failure
                        | _ -> refuse "invalid:code_scanning_default_setup_options"))
            | _ -> refuse "invalid:code_scanning_default_setup_options")

    let private codeScanningOptions value =
        prop "code_scanning_options" value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.Null -> Ok "null"
            | JsonValueKind.Object ->
                exactMembers "code_scanning_options" [ "allow_advanced" ] item
                |> Result.bind (fun options ->
                    prop "allow_advanced" options
                    |> Result.bind (fun allow ->
                        match allow.ValueKind with
                        | JsonValueKind.True | JsonValueKind.False | JsonValueKind.Null -> Ok(item.GetRawText())
                        | _ -> refuse "invalid:code_scanning_options"))
            | _ -> refuse "invalid:code_scanning_options")

    let private delegatedBypassOptions value =
        prop "secret_scanning_delegated_bypass_options" value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.Null -> Ok "null"
            | JsonValueKind.Object ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                    "secret-scanning-delegated-bypass-reviewers-not-modeled")
            | _ -> refuse "invalid:secret_scanning_delegated_bypass_options")

    let private advancedSecurityIsConsistent advanced statuses =
        let status name = statuses |> List.find (fst >> (=) name) |> snd
        match advanced, status "code_security", status "secret_protection" with
        | "enabled", "enabled", "enabled"
        | "disabled", "disabled", "disabled"
        | "code_security", "enabled", "disabled"
        | "secret_protection", "disabled", "enabled" -> true
        | _ -> false

    let private configurationUrl
        (options: MigrationGitHubReadOptions)
        (targetType: string)
        (configurationId: int64)
        (configuration: JsonElement)
        =
        text "url" configuration
        |> Result.bind (fun value ->
            let mutable uri = Unchecked.defaultof<Uri>
            let suffix = $"/code-security/configurations/{configurationId}"
            if not (Uri.TryCreate(value, UriKind.Absolute, &uri))
               || uri.Scheme <> options.ApiBase.Scheme
               || uri.Authority <> options.ApiBase.Authority
               || not (uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)) then
                refuse "configuration-url-drift"
            elif targetType = "organization"
                 && not (uri.AbsolutePath.StartsWith(
                     $"/orgs/{Uri.EscapeDataString options.Owner}/", StringComparison.Ordinal)) then
                refuse "configuration-owner-drift"
            elif targetType = "enterprise"
                 && not (uri.AbsolutePath.StartsWith("/enterprises/", StringComparison.Ordinal)) then
                refuse "configuration-owner-drift"
            else Ok value)

    let private codeSecurityRequest (options: MigrationGitHubReadOptions) =
        let uri = Uri(options.ApiBase, $"{repoPath options}/code-security-configuration")
        uri,
        Rest
            { Method=Get; Uri=uri; Headers=headers options; Body=None
              ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }

    let readCodeSecurity
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        if not (validOptions options) then refuse "invalid-options"
        elif identity.Owner <> options.Owner || identity.Name <> options.Repository then
            refuse "repository-identity-drift"
        elif not (validText repositoryRevision) then refuse "invalid-repository-revision"
        else
            repositoryResponse options identity repositoryRevision transport
            |> Result.bind (fun (repository, _) ->
                let uri, request = codeSecurityRequest options
                match transport.Send request with
                | NetworkFailure | TimedOut ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "transport-unavailable")
                | Response response when response.StatusCode = 401 || response.StatusCode = 403 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized $"http:{response.StatusCode}")
                | Response response when response.StatusCode = 404 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "http:404")
                | Response response when response.StatusCode = 204 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                        "no-attached-configuration-effective-settings-unproven")
                | Response response when response.StatusCode <> 200 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable $"http:{response.StatusCode}")
                | Response response when responseHeader "link" response.Headers |> Option.isSome ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Partial "unexpected-continuation")
                | Response response ->
                    parse response.Body
                    |> Result.bind (fun root ->
                        match text "status" root, prop "configuration" root with
                        | Ok "attached", Ok configuration ->
                            let fields =
                                [ "code_security"
                                  "code_scanning_default_setup"
                                  "code_scanning_delegated_alert_dismissal"
                                  "secret_protection"
                                  "secret_scanning"
                                  "secret_scanning_push_protection"
                                  "secret_scanning_delegated_bypass"
                                  "secret_scanning_validity_checks"
                                  "secret_scanning_non_provider_patterns"
                                  "secret_scanning_generic_secrets"
                                  "secret_scanning_delegated_alert_dismissal"
                                  "secret_scanning_extended_metadata"
                                  "private_vulnerability_reporting" ]
                            match positive "id" configuration, text "target_type" configuration,
                                  text "name" configuration, text "enforcement" configuration,
                                  text "updated_at" configuration, advancedSecurity configuration with
                            | Ok id, Ok targetType, Ok name, Ok enforcement, Ok updatedAt, Ok advanced when
                                (targetType = "organization" || targetType = "enterprise")
                                && (enforcement = "enforced" || enforcement = "unenforced"
                                    || enforcement = "enterprise_enforced") ->
                                configurationUrl options targetType id configuration
                                |> Result.bind (fun _ ->
                                    fields
                                    |> List.fold (fun state field ->
                                        state
                                        |> Result.bind (fun values ->
                                            explicitSecurityStatus field configuration
                                            |> Result.map (fun value -> (field, value)::values))) (Ok [])
                                    |> Result.map List.rev
                                    |> Result.bind (fun statuses ->
                                        if not (advancedSecurityIsConsistent advanced statuses) then
                                            refuse "contradictory:advanced-security"
                                        else
                                            let delegatedBypassStatus =
                                                statuses
                                                |> List.find (fst >> (=) "secret_scanning_delegated_bypass")
                                                |> snd
                                            let parsedOptions =
                                                match defaultSetupOptions configuration,
                                                      codeScanningOptions configuration,
                                                      delegatedBypassOptions configuration with
                                                | Ok defaults, Ok scanning, Ok "null" when delegatedBypassStatus = "enabled" ->
                                                    Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                                                        "enabled-delegated-bypass-reviewers-unproven")
                                                | Ok defaults, Ok scanning, Ok bypass ->
                                                    Ok
                                                        [ "code_scanning_default_setup_options", defaults
                                                          "code_scanning_options", scanning
                                                          "secret_scanning_delegated_bypass_options", bypass ]
                                                | Error failure, _, _ | _, Error failure, _ | _, _, Error failure ->
                                                    Error failure
                                            parsedOptions
                                            |> Result.map (fun optionsValues ->
                                                let subject = $"configuration:{id}"
                                                let metadata =
                                                    [ { Surface=CodeSecurity; Subject=subject; Name="configuration-id"
                                                        Value=SettingValue.Integer id }
                                                      { Surface=CodeSecurity; Subject=subject; Name="target-type"
                                                        Value=SettingValue.Text targetType }
                                                      { Surface=CodeSecurity; Subject=subject; Name="configuration-name"
                                                        Value=SettingValue.Text name }
                                                      { Surface=CodeSecurity; Subject=subject; Name="enforcement"
                                                        Value=SettingValue.Text enforcement }
                                                      { Surface=CodeSecurity; Subject=subject; Name="configuration-updated-at"
                                                        Value=SettingValue.Text updatedAt }
                                                      { Surface=CodeSecurity; Subject=subject; Name="advanced-security"
                                                        Value=SettingValue.Text advanced } ]
                                                let settings =
                                                    metadata
                                                    @ ([ statuses; optionsValues ]
                                                       |> List.concat
                                                       |> List.map (fun (field, value) ->
                                                           { Surface=CodeSecurity; Subject=subject; Name=field
                                                             Value=SettingValue.Text value }))
                                                let configurationPage =
                                                    { SettingsStream="code-security-configuration"
                                                      SettingsRequestedUri=uri.AbsoluteUri
                                                      SettingsPayloadJson=response.Body
                                                      SettingsPayloadSha256=hashText response.Body
                                                      SettingsNextUri=None }
                                                { SurfaceRead=
                                                    { RepositoryIdentity=identity; RepositoryRevision=repositoryRevision
                                                      Surface=CodeSecurity; Complete=true
                                                      Pages=[ identityPage options repository; configurationPage ]
                                                      Settings=settings }
                                                  ConfigurationId=id; ConfigurationTargetType=targetType
                                                  ConfigurationName=name; Enforcement=enforcement
                                                  ConfigurationUpdatedAt=updatedAt })))
                            | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ -> refuse "unsupported:configuration-provenance"
                            | Error failure, _, _, _, _, _ | _, Error failure, _, _, _, _
                            | _, _, Error failure, _, _, _ | _, _, _, Error failure, _, _
                            | _, _, _, _, Error failure, _ | _, _, _, _, _, Error failure -> Error failure
                        | Ok status, Ok _ -> refuse $"unsupported:attachment-status:{status}"
                        | Error failure, _ | _, Error failure -> Error failure))

    let private tryProp (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &found) then Some found
        else None

    let private optionalTrimmedText name value =
        prop name value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.Null -> Ok None
            | JsonValueKind.String when validText (item.GetString()) -> Ok(Some(item.GetString()))
            | _ -> refuse $"invalid:{name}")

    let private textList name value =
        prop name value
        |> Result.bind (fun item ->
            if item.ValueKind <> JsonValueKind.Array then refuse $"invalid:{name}"
            else
                item.EnumerateArray()
                |> Seq.fold (fun state entry ->
                    state
                    |> Result.bind (fun values ->
                        if entry.ValueKind = JsonValueKind.String && validText (entry.GetString()) then
                            Ok(entry.GetString() :: values)
                        else refuse $"invalid:{name}")) (Ok [])
                |> Result.map List.rev
                |> Result.bind (unique id $"duplicate:{name}"))

    let private enumText name allowed value =
        text name value
        |> Result.bind (fun actual ->
            if Set.contains actual (Set.ofList allowed) then Ok actual
            else refuse $"unsupported:{name}:{actual}")

    let private organizationPolicy
        (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport)
        =
        let uri = Uri(options.ApiBase, $"orgs/{Uri.EscapeDataString options.Owner}")
        get options transport uri
        |> Result.bind (fun response ->
            if responseHeader "link" response.Headers |> Option.isSome then
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                    "organization-policy-unexpected-continuation")
            else
                parse response.Body
                |> Result.bind (fun root ->
                    match text "login" root, text "updated_at" root,
                          flag "has_repository_projects" root,
                          flag "members_can_fork_private_repositories" root,
                          flag "web_commit_signoff_required" root with
                    | Ok login, Ok _, Ok projects, Ok privateForking, Ok signoff when
                        login = options.Owner ->
                        Ok(
                            projects,
                            privateForking,
                            signoff,
                            { SettingsStream="organization-repository-policy"
                              SettingsRequestedUri=uri.AbsoluteUri
                              SettingsPayloadJson=response.Body
                              SettingsPayloadSha256=hashText response.Body
                              SettingsNextUri=None })
                    | Ok _, Ok _, Ok _, Ok _, Ok _ -> refuse "organization-identity-drift"
                    | Error failure, _, _, _, _ | _, Error failure, _, _, _
                    | _, _, Error failure, _, _ | _, _, _, Error failure, _
                    | _, _, _, _, Error failure -> Error failure))

    let readRepository
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        if not (validOptions options) then refuse "invalid-options"
        elif identity.Owner <> options.Owner || identity.Name <> options.Repository then
            refuse "repository-identity-drift"
        elif not (validText repositoryRevision) then refuse "invalid-repository-revision"
        else
            repositoryResponse options identity repositoryRevision transport
            |> Result.bind (fun (response, root) ->
                let flags =
                    [ "private"; "archived"; "disabled"; "has_issues"; "has_projects"
                      "has_wiki"; "has_pages"; "has_discussions"; "has_downloads"
                      "has_pull_requests"; "is_template"; "web_commit_signoff_required" ]
                    |> List.fold (fun state name ->
                        state
                        |> Result.bind (fun values ->
                            flag name root |> Result.map (fun actual -> Map.add name actual values))) (Ok Map.empty)
                let ownerType = prop "owner" root |> Result.bind (text "type")
                match enumText "visibility" [ "public"; "private"; "internal" ] root,
                      enumText "pull_request_creation_policy" [ "all"; "collaborators_only" ] root,
                      optionalTrimmedText "description" root, optionalTrimmedText "homepage" root,
                      textList "topics" root, ownerType, flags with
                | Ok visibility, Ok pullPolicy, Ok description, Ok homepage, Ok topics,
                  Ok ownerKind, Ok values when ownerKind = "Organization" || ownerKind = "User" ->
                    let isPrivate = values["private"]
                    if isPrivate <> (visibility <> "public") then
                        refuse "contradictory:visibility"
                    else
                        let allowForking =
                            if visibility = "public" then
                                match tryProp "allow_forking" root with
                                | None -> Ok None
                                | Some item ->
                                    match item.ValueKind with
                                    | JsonValueKind.True -> Ok(Some true)
                                    | JsonValueKind.False -> Ok(Some false)
                                    | _ -> refuse "invalid:allow_forking"
                            else flag "allow_forking" root |> Result.map Some
                        allowForking
                        |> Result.bind (fun allowForking ->
                            let policy =
                                if ownerKind = "Organization" then
                                    organizationPolicy options transport
                                    |> Result.map (fun (projects, forking, signoff, page) ->
                                        Some(projects, forking, signoff), [ page ])
                                else Ok(None, [])
                            policy
                            |> Result.bind (fun (organization, organizationPages) ->
                                match organization with
                                | Some(false, _, _) ->
                                    Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                                        "inherited:organization-repository-projects-policy")
                                | Some(_, false, _) when visibility <> "public" ->
                                    Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                                        "inherited:organization-private-forking-policy")
                                | Some(_, _, true) ->
                                    Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                                        "inherited:organization-web-commit-signoff-policy")
                                | _ ->
                                    let subject = $"repository:{identity.DatabaseId}"
                                    let setting name value =
                                        { Surface=SettingsSurface.Repository; Subject=subject
                                          Name=name; Value=value }
                                    let coreSettings =
                                        [ setting "default-branch" (SettingValue.Text identity.DefaultBranch)
                                          setting "visibility" (SettingValue.Text visibility)
                                          setting "archived" (SettingValue.Boolean values["archived"])
                                          setting "disabled" (SettingValue.Boolean values["disabled"])
                                          setting "has-issues" (SettingValue.Boolean values["has_issues"])
                                          setting "has-projects" (SettingValue.Boolean values["has_projects"])
                                          setting "has-wiki" (SettingValue.Boolean values["has_wiki"])
                                          setting "has-pages" (SettingValue.Boolean values["has_pages"])
                                          setting "has-discussions" (SettingValue.Boolean values["has_discussions"])
                                          setting "has-downloads" (SettingValue.Boolean values["has_downloads"])
                                          setting "has-pull-requests" (SettingValue.Boolean values["has_pull_requests"])
                                          setting "pull-request-creation-policy" (SettingValue.Text pullPolicy)
                                          setting "is-template" (SettingValue.Boolean values["is_template"])
                                          setting "web-commit-signoff-required"
                                              (SettingValue.Boolean values["web_commit_signoff_required"])
                                          setting "description-present" (SettingValue.Boolean description.IsSome)
                                          setting "homepage-present" (SettingValue.Boolean homepage.IsSome)
                                          setting "topics" (SettingValue.TextList topics) ]
                                    let optionalSettings =
                                        [ description
                                          |> Option.map (SettingValue.Text >> setting "description")
                                          homepage
                                          |> Option.map (SettingValue.Text >> setting "homepage")
                                          allowForking
                                          |> Option.map (SettingValue.Boolean >> setting "allow-forking") ]
                                        |> List.choose id
                                    let settings = coreSettings @ optionalSettings
                                    Ok
                                        { SurfaceRead=
                                            { RepositoryIdentity=identity
                                              RepositoryRevision=repositoryRevision
                                              Surface=SettingsSurface.Repository
                                              Complete=true
                                              Pages=identityPage options response :: organizationPages
                                              Settings=settings }
                                          Visibility=visibility
                                          Archived=values["archived"]
                                          Disabled=values["disabled"]
                                          HasIssues=values["has_issues"]
                                          HasProjects=values["has_projects"]
                                          HasWiki=values["has_wiki"]
                                          HasPages=values["has_pages"]
                                          HasDiscussions=values["has_discussions"]
                                          HasDownloads=values["has_downloads"]
                                          HasPullRequests=values["has_pull_requests"]
                                          PullRequestCreationPolicy=pullPolicy
                                          IsTemplate=values["is_template"]
                                          AllowForking=allowForking
                                          WebCommitSignoffRequired=values["web_commit_signoff_required"]
                                          Description=description
                                          Homepage=homepage
                                          Topics=topics }))
                | Ok _, Ok _, Ok _, Ok _, Ok _, Ok ownerKind, Ok _ ->
                    refuse $"unsupported:owner-type:{ownerKind}"
                | Error failure, _, _, _, _, _, _ | _, Error failure, _, _, _, _, _
                | _, _, Error failure, _, _, _, _ | _, _, _, Error failure, _, _, _
                | _, _, _, _, Error failure, _, _ | _, _, _, _, _, Error failure, _
                | _, _, _, _, _, _, Error failure -> Error failure)

    let private optionalDeprecatedSquashTitle value expected =
        match tryProp "use_squash_pr_title_as_default" value with
        | None -> Ok()
        | Some item ->
            match item.ValueKind with
            | JsonValueKind.True when expected = "PR_TITLE" -> Ok()
            | JsonValueKind.False when expected = "COMMIT_OR_PR_TITLE" -> Ok()
            | JsonValueKind.True | JsonValueKind.False ->
                refuse "contradictory:use_squash_pr_title_as_default"
            | _ -> refuse "invalid:use_squash_pr_title_as_default"

    let readMergePolicy
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        if not (validOptions options) then refuse "invalid-options"
        elif identity.Owner <> options.Owner || identity.Name <> options.Repository then
            refuse "repository-identity-drift"
        elif not (validText repositoryRevision) then refuse "invalid-repository-revision"
        else
            repositoryResponse options identity repositoryRevision transport
            |> Result.bind (fun (response, root) ->
                let canViewMergeSettings = prop "permissions" root |> Result.bind (flag "push")
                match flag "allow_squash_merge" root, flag "allow_merge_commit" root,
                      flag "allow_rebase_merge" root, flag "allow_auto_merge" root,
                      flag "allow_update_branch" root, flag "delete_branch_on_merge" root,
                      enumText "squash_merge_commit_title" [ "PR_TITLE"; "COMMIT_OR_PR_TITLE" ] root,
                      enumText "squash_merge_commit_message" [ "PR_BODY"; "COMMIT_MESSAGES"; "BLANK" ] root,
                      enumText "merge_commit_title" [ "PR_TITLE"; "MERGE_MESSAGE" ] root,
                      enumText "merge_commit_message" [ "PR_TITLE"; "PR_BODY"; "BLANK" ] root,
                      canViewMergeSettings with
                | Ok true, Ok true, Ok allowRebase, Ok allowAuto, Ok allowUpdate, Ok deleteBranch,
                  Ok squashTitle, Ok squashMessage, Ok mergeTitle, Ok mergeMessage, Ok true ->
                    optionalDeprecatedSquashTitle root squashTitle
                    |> Result.map (fun () ->
                        let subject = $"repository:{identity.DatabaseId}"
                        let setting name value =
                            { Surface=MergePolicy; Subject=subject; Name=name; Value=value }
                        let settings =
                            [ setting "allow-squash-merge" (SettingValue.Boolean true)
                              setting "allow-merge-commit" (SettingValue.Boolean true)
                              setting "allow-rebase-merge" (SettingValue.Boolean allowRebase)
                              setting "allow-auto-merge" (SettingValue.Boolean allowAuto)
                              setting "allow-update-branch" (SettingValue.Boolean allowUpdate)
                              setting "delete-branch-on-merge" (SettingValue.Boolean deleteBranch)
                              setting "squash-merge-commit-title" (SettingValue.Text squashTitle)
                              setting "squash-merge-commit-message" (SettingValue.Text squashMessage)
                              setting "merge-commit-title" (SettingValue.Text mergeTitle)
                              setting "merge-commit-message" (SettingValue.Text mergeMessage) ]
                        { SurfaceRead=
                            { RepositoryIdentity=identity
                              RepositoryRevision=repositoryRevision
                              Surface=MergePolicy
                              Complete=true
                              Pages=[ identityPage options response ]
                              Settings=settings }
                          AllowSquashMerge=true
                          AllowMergeCommit=true
                          AllowRebaseMerge=allowRebase
                          AllowAutoMerge=allowAuto
                          AllowUpdateBranch=allowUpdate
                          DeleteBranchOnMerge=deleteBranch
                          SquashMergeCommitTitle=squashTitle
                          SquashMergeCommitMessage=squashMessage
                          MergeCommitTitle=mergeTitle
                          MergeCommitMessage=mergeMessage })
                | Ok false, _, _, _, _, _, _, _, _, _, _ ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                        "disabled-squash-merge-latent-options-unavailable")
                | _, Ok false, _, _, _, _, _, _, _, _, _ ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                        "disabled-merge-commit-latent-options-unavailable")
                | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok false ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized
                        "merge-settings-contents-write-unproven")
                | Error failure, _, _, _, _, _, _, _, _, _, _
                | _, Error failure, _, _, _, _, _, _, _, _, _
                | _, _, Error failure, _, _, _, _, _, _, _, _
                | _, _, _, Error failure, _, _, _, _, _, _, _
                | _, _, _, _, Error failure, _, _, _, _, _, _
                | _, _, _, _, _, Error failure, _, _, _, _, _
                | _, _, _, _, _, _, Error failure, _, _, _, _
                | _, _, _, _, _, _, _, Error failure, _, _, _
                | _, _, _, _, _, _, _, _, Error failure, _, _
                | _, _, _, _, _, _, _, _, _, Error failure, _
                | _, _, _, _, _, _, _, _, _, _, Error failure -> Error failure)

    let private dependencyOptions value =
        prop "dependency_graph_autosubmit_action_options" value
        |> Result.bind (fun item ->
            if item.ValueKind <> JsonValueKind.Object then
                refuse "invalid:dependency_graph_autosubmit_action_options"
            else
                exactMembers "dependency_graph_autosubmit_action_options" [ "labeled_runners" ] item
                |> Result.bind (flag "labeled_runners"))

    let private endpointRequest (options: MigrationGitHubReadOptions) suffix =
        let uri = Uri(options.ApiBase, $"{repoPath options}/{suffix}")
        uri,
        Rest
            { Method=Get; Uri=uri; Headers=headers options; Body=None
              ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }

    let private terminalPage stream (uri: Uri) (response: ResponseEnvelope) =
        let body = if isNull response.Body then "" else response.Body
        { SettingsStream=stream
          SettingsRequestedUri=uri.AbsoluteUri
          SettingsPayloadJson=body
          SettingsPayloadSha256=hashText body
          SettingsNextUri=None }

    // Contracts are from the GitHub REST Actions permissions and Actions policies
    // documentation. Each non-paginated endpoint must terminate in its response;
    // applicable policy listing is accepted only when its first terminal page proves zero.
    let private actionsGetJson options transport stream (uri: Uri) allowedMembers =
        get options transport uri
        |> Result.bind (fun response ->
            if responseHeader "link" response.Headers |> Option.isSome then
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                    $"{stream}-unexpected-continuation")
            else
                parse response.Body
                |> Result.bind (fun root ->
                    if root.ValueKind <> JsonValueKind.Object then refuse $"invalid:{stream}"
                    else exactMembers stream allowedMembers root
                         |> Result.map (fun _ -> root, terminalPage stream uri response)))

    let private actionsUri (options: MigrationGitHubReadOptions) scope suffix =
        match scope with
        | "organization" ->
            Uri(options.ApiBase, $"orgs/{Uri.EscapeDataString options.Owner}/actions/permissions{suffix}")
        | _ -> Uri(options.ApiBase, $"{repoPath options}/actions/permissions{suffix}")

    let private selectedActionsUrl options scope (root: JsonElement) allowed =
        prop "selected_actions_url" root
        |> Result.bind (fun item ->
            if allowed <> "selected" then
                if item.ValueKind = JsonValueKind.Null then Ok None
                else refuse $"invalid:{scope}-selected-actions-boundary"
            elif item.ValueKind <> JsonValueKind.String || not (validText (item.GetString())) then
                refuse $"invalid:{scope}-selected-actions-url"
            else
                let mutable actual = Unchecked.defaultof<Uri>
                let named = actionsUri options scope "/selected-actions"
                let numeric =
                    Uri(options.ApiBase,
                        $"repositories/{options.ExpectedRepositoryId}/actions/permissions/selected-actions")
                if not (Uri.TryCreate(item.GetString(), UriKind.Absolute, &actual))
                   || actual.Scheme <> Uri.UriSchemeHttps
                   || (actual.AbsoluteUri <> named.AbsoluteUri
                       && (scope <> "repository" || actual.AbsoluteUri <> numeric.AbsoluteUri)) then
                    refuse $"foreign:{scope}-selected-actions-url"
                else Ok(Some named))

    let private readSelectedActions options transport scope uri =
        actionsGetJson options transport $"{scope}-selected-actions" uri
            [ "github_owned_allowed"; "verified_allowed"; "patterns_allowed" ]
        |> Result.bind (fun (root, page) ->
            match flag "github_owned_allowed" root, flag "verified_allowed" root,
                  textList "patterns_allowed" root with
            | Ok githubOwned, Ok verified, Ok patterns ->
                Ok(
                    { GitHubOwnedAllowed=githubOwned
                      VerifiedAllowed=verified
                      PatternsAllowed=patterns },
                    page)
            | Error failure, _, _ | _, Error failure, _ | _, _, Error failure -> Error failure)

    let private readActionsCore options transport scope =
        let uri = actionsUri options scope ""
        let members =
            if scope = "organization" then
                [ "enabled_repositories"; "allowed_actions"; "selected_actions_url"
                  "sha_pinning_required" ]
            else [ "enabled"; "allowed_actions"; "selected_actions_url"; "sha_pinning_required" ]
        actionsGetJson options transport $"{scope}-actions-permissions" uri members
        |> Result.bind (fun (root, page) ->
            enumText "allowed_actions" [ "all"; "local_only"; "selected" ] root
            |> Result.bind (fun allowed ->
                (if scope = "organization" && allowed = "selected" then
                     Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                         "organization-selected-actions-identity-unmodeled")
                 else selectedActionsUrl options scope root allowed)
                |> Result.bind (fun selectedUri ->
                    let selection =
                        match selectedUri with
                        | Some selected ->
                            readSelectedActions options transport scope selected
                            |> Result.map (fun (value, selectedPage) -> Some value, [ selectedPage ])
                        | None -> Ok(None, [])
                    selection
                    |> Result.bind (fun (selected, selectedPages) ->
                        flag "sha_pinning_required" root
                        |> Result.bind (fun pinning ->
                            if scope = "organization" then
                                enumText "enabled_repositories" [ "all"; "none"; "selected" ] root
                                |> Result.bind (fun enabledRepositories ->
                                    if enabledRepositories = "selected" then
                                        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                                            "organization-selected-repository-scope-unmodeled")
                                    elif enabledRepositories <> "all" then
                                        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                                            "organization-actions-disabled")
                                    else Ok(enabledRepositories, true, allowed, selected, pinning,
                                            page :: selectedPages))
                            else
                                flag "enabled" root
                                |> Result.bind (fun enabled ->
                                    if enabled then Ok("repository", enabled, allowed, selected, pinning,
                                                       page :: selectedPages)
                                    else
                                        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                                            "repository-actions-disabled")))))))

    let private readWorkflowDefaults options transport scope =
        let uri = actionsUri options scope "/workflow"
        actionsGetJson options transport $"{scope}-workflow-permissions" uri
            [ "default_workflow_permissions"; "can_approve_pull_request_reviews" ]
        |> Result.bind (fun (root, page) ->
            match enumText "default_workflow_permissions" [ "read"; "write" ] root,
                  flag "can_approve_pull_request_reviews" root with
            | Ok permissions, Ok approval -> Ok(permissions, approval, page)
            | Error failure, _ | _, Error failure -> Error failure)

    let private readRetention options transport scope =
        let uri = actionsUri options scope "/artifact-and-log-retention"
        actionsGetJson options transport $"{scope}-artifact-and-log-retention" uri
            [ "days"; "maximum_allowed_days" ]
        |> Result.bind (fun (root, page) ->
            match positive "days" root, positive "maximum_allowed_days" root with
            | Ok days, Ok maximum when days <= maximum -> Ok(days, maximum, page)
            | Ok _, Ok _ -> refuse $"contradictory:{scope}-artifact-and-log-retention"
            | Error failure, _ | _, Error failure -> Error failure)

    let private readForkApproval options transport scope =
        let uri = actionsUri options scope "/fork-pr-contributor-approval"
        actionsGetJson options transport $"{scope}-fork-pr-contributor-approval" uri [ "approval_policy" ]
        |> Result.bind (fun (root, page) ->
            enumText "approval_policy"
                [ "first_time_contributors_new_to_github"; "first_time_contributors"
                  "all_external_contributors" ] root
            |> Result.map (fun policy -> policy, page))

    let private readApplicableActionsPolicies
        (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport)
        =
        let uri =
            Uri(options.ApiBase,
                $"{repoPath options}/actions/policies?per_page=100&has_parents=true")
        actionsGetJson options transport "applicable-actions-policies" uri [ "total_count"; "policies" ]
        |> Result.bind (fun (root, page) ->
            let mutable count = -1L
            let countResult =
                prop "total_count" root
                |> Result.bind (fun item ->
                    if item.ValueKind = JsonValueKind.Number && item.TryGetInt64(&count) && count >= 0L then Ok count
                    else refuse "invalid:total_count")
            match countResult, prop "policies" root with
            | Ok 0L, Ok policies when policies.ValueKind = JsonValueKind.Array
                                   && (policies.EnumerateArray() |> Seq.isEmpty) -> Ok(0L, page)
            | Ok 0L, Ok policies when policies.ValueKind = JsonValueKind.Array ->
                refuse "contradictory:applicable-actions-policies"
            | Ok count, Ok policies when count > 0L && policies.ValueKind = JsonValueKind.Array ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                    "applicable-actions-policies-detail-unmodeled")
            | Ok _, Ok _ -> refuse "invalid:policies"
            | Error failure, _ | _, Error failure -> Error failure)

    let readActionsPolicy
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        if not (validOptions options) then refuse "invalid-options"
        elif identity.Owner <> options.Owner || identity.Name <> options.Repository then
            refuse "repository-identity-drift"
        elif not (validText repositoryRevision) then refuse "invalid-repository-revision"
        else
            repositoryResponse options identity repositoryRevision transport
            |> Result.bind (fun (repository, repositoryRoot) ->
                match enumText "visibility" [ "public"; "private"; "internal" ] repositoryRoot,
                      prop "owner" repositoryRoot |> Result.bind (text "type") with
                | Ok "public", Ok "Organization" ->
                    readActionsCore options transport "organization"
                    |> Result.bind (fun (orgScope, _, orgAllowed, orgSelected, orgPinning, orgCorePages) ->
                        readActionsCore options transport "repository"
                        |> Result.bind (fun (_, repoEnabled, repoAllowed, repoSelected, pinning, repoCorePages) ->
                            readWorkflowDefaults options transport "organization"
                            |> Result.bind (fun (orgWorkflow, orgApprove, orgWorkflowPage) ->
                                readWorkflowDefaults options transport "repository"
                                |> Result.bind (fun (repoWorkflow, repoApprove, repoWorkflowPage) ->
                                    readRetention options transport "organization"
                                    |> Result.bind (fun (orgDays, orgMaximum, orgRetentionPage) ->
                                        readRetention options transport "repository"
                                        |> Result.bind (fun (repoDays, repoMaximum, repoRetentionPage) ->
                                            if repoMaximum > orgMaximum then
                                                refuse "contradictory:repository-retention-maximum"
                                            else
                                                readForkApproval options transport "organization"
                                                |> Result.bind (fun (orgForkApproval, orgForkPage) ->
                                                    readForkApproval options transport "repository"
                                                    |> Result.bind (fun (repoForkApproval, repoForkPage) ->
                                                        readApplicableActionsPolicies options transport
                                                        |> Result.map (fun (policyCount, policiesPage) ->
                                                            let setting subject name value =
                                                                { Surface=ActionsPolicy; Subject=subject; Name=name; Value=value }
                                                            let orgSubject = $"organization:{options.Owner}"
                                                            let repoSubject = $"repository:{identity.DatabaseId}"
                                                            let selectedSettings subject selected =
                                                                match selected with
                                                                | None -> []
                                                                | Some value ->
                                                                    [ setting subject "github-owned-actions-allowed" (SettingValue.Boolean value.GitHubOwnedAllowed)
                                                                      setting subject "verified-actions-allowed" (SettingValue.Boolean value.VerifiedAllowed)
                                                                      setting subject "action-patterns-allowed" (SettingValue.TextList value.PatternsAllowed) ]
                                                            let settings =
                                                                [ setting orgSubject "enabled-repositories" (SettingValue.Text orgScope)
                                                                  setting orgSubject "allowed-actions" (SettingValue.Text orgAllowed) ]
                                                                @ selectedSettings orgSubject orgSelected
                                                                @ [ setting orgSubject "sha-pinning-required" (SettingValue.Boolean orgPinning) ]
                                                                @ [ setting repoSubject "enabled" (SettingValue.Boolean repoEnabled)
                                                                    setting repoSubject "allowed-actions" (SettingValue.Text repoAllowed) ]
                                                                @ selectedSettings repoSubject repoSelected
                                                                @ [ setting repoSubject "sha-pinning-required" (SettingValue.Boolean pinning)
                                                                    setting orgSubject "default-workflow-permissions" (SettingValue.Text orgWorkflow)
                                                                    setting orgSubject "can-approve-pull-request-reviews" (SettingValue.Boolean orgApprove)
                                                                    setting repoSubject "default-workflow-permissions" (SettingValue.Text repoWorkflow)
                                                                    setting repoSubject "can-approve-pull-request-reviews" (SettingValue.Boolean repoApprove)
                                                                    setting orgSubject "artifact-and-log-retention-days" (SettingValue.Integer orgDays)
                                                                    setting orgSubject "maximum-artifact-and-log-retention-days" (SettingValue.Integer orgMaximum)
                                                                    setting repoSubject "artifact-and-log-retention-days" (SettingValue.Integer repoDays)
                                                                    setting repoSubject "maximum-artifact-and-log-retention-days" (SettingValue.Integer repoMaximum)
                                                                    setting orgSubject "fork-pull-request-approval-policy" (SettingValue.Text orgForkApproval)
                                                                    setting repoSubject "fork-pull-request-approval-policy" (SettingValue.Text repoForkApproval)
                                                                    setting repoSubject "applicable-actions-policy-count" (SettingValue.Integer policyCount) ]
                                                            let pages =
                                                                [ identityPage options repository ] @ orgCorePages @ repoCorePages
                                                                @ [ orgWorkflowPage; repoWorkflowPage; orgRetentionPage; repoRetentionPage
                                                                    orgForkPage; repoForkPage; policiesPage ]
                                                            { SurfaceRead=
                                                                { RepositoryIdentity=identity; RepositoryRevision=repositoryRevision
                                                                  Surface=ActionsPolicy; Complete=true; Pages=pages; Settings=settings }
                                                              OrganizationEnabledRepositories=orgScope
                                                              OrganizationAllowedActions=orgAllowed
                                                              OrganizationSelectedActions=orgSelected
                                                              OrganizationShaPinningRequired=orgPinning
                                                              RepositoryEnabled=repoEnabled
                                                              RepositoryAllowedActions=repoAllowed
                                                              RepositorySelectedActions=repoSelected
                                                              ShaPinningRequired=pinning
                                                              OrganizationDefaultWorkflowPermissions=orgWorkflow
                                                              OrganizationCanApprovePullRequestReviews=orgApprove
                                                              RepositoryDefaultWorkflowPermissions=repoWorkflow
                                                              RepositoryCanApprovePullRequestReviews=repoApprove
                                                              OrganizationArtifactAndLogRetentionDays=orgDays
                                                              OrganizationMaximumArtifactAndLogRetentionDays=orgMaximum
                                                              RepositoryArtifactAndLogRetentionDays=repoDays
                                                              RepositoryMaximumArtifactAndLogRetentionDays=repoMaximum
                                                              OrganizationForkPullRequestApprovalPolicy=orgForkApproval
                                                              RepositoryForkPullRequestApprovalPolicy=repoForkApproval
                                                              ApplicableActionsPolicyCount=policyCount })))))))))
                | Ok visibility, Ok "Organization" ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                        $"{visibility}-repository-actions-access-and-fork-policy-unmodeled")
                | Ok _, Ok ownerType -> refuse $"unsupported:owner-type:{ownerType}"
                | Error failure, _ | _, Error failure -> Error failure)

    let private environmentFailure failure =
        match failure with
        | MigrationReadFailure.InvalidOptions -> refuse "invalid-options"
        | MigrationReadFailure.TransportUnavailable ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "transport-unavailable")
        | MigrationReadFailure.HttpRefused status when status = 401 || status = 403 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized $"http:{status}")
        | MigrationReadFailure.HttpRefused status when status = 404 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "environment-http:404")
        | MigrationReadFailure.HttpRefused status -> refuse $"environment-http:{status}"
        | MigrationReadFailure.PaginationRefused reason ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Partial $"environment-pagination:{reason}")
        | MigrationReadFailure.MalformedResponse reason -> refuse $"environment:{reason}"
        | MigrationReadFailure.DuplicateIdentity identity -> refuse $"environment-duplicate:{identity}"
        | MigrationReadFailure.IdentityDrift -> refuse "environment-identity-drift"
        | MigrationReadFailure.PopulationDrift -> refuse "environment-population-drift"
        | MigrationReadFailure.SnapshotMismatch reason -> refuse $"environment-snapshot:{reason}"
        | MigrationReadFailure.GraphQLErrors -> refuse "environment-unexpected-graphql-errors"

    let private environmentPage stream (page: MigrationEnvironmentPageEvidence) =
        { SettingsStream=stream
          SettingsRequestedUri=page.EnvironmentRequestedUri
          SettingsPayloadJson=page.EnvironmentPayloadJson
          SettingsPayloadSha256=page.EnvironmentPayloadSha256
          SettingsNextUri=page.EnvironmentNextUri }

    let private environmentVariableValues (environment: MigrationEnvironmentObservation) =
        environment.VariablePages
        |> List.fold (fun state page ->
            state
            |> Result.bind (fun values ->
                parse page.EnvironmentPayloadJson
                |> Result.bind (prop "variables")
                |> Result.bind arrayRoot
                |> Result.bind (fun entries ->
                    entries
                    |> List.fold (fun parsed entry ->
                        parsed
                        |> Result.bind (fun items ->
                            match text "name" entry, text "value" entry with
                            | Ok name, Ok value -> Ok((name, value) :: items)
                            | Error failure, _ | _, Error failure -> Error failure)) (Ok [])
                    |> Result.map (fun items -> values @ List.rev items)))) (Ok [])
        |> Result.bind (fun values ->
            let observed =
                environment.Variables |> List.map (fun variable -> variable.Name, variable.ValueSha256)
            let extracted = values |> List.map (fun (name, value) -> name, hashText value)
            if observed = extracted then Ok values
            else refuse $"environment-variable-evidence-drift:{environment.Name}")

    let private environmentSettings (environment: MigrationEnvironmentObservation) variableValues =
        let environmentSubject = $"environment:{environment.EnvironmentId}"
        let setting subject name value =
            { Surface=Environments; Subject=subject; Name=name; Value=value }
        let core =
            [ setting environmentSubject "name" (SettingValue.Text environment.Name)
              setting environmentSubject "node-id" (SettingValue.Text environment.EnvironmentNodeId)
              setting environmentSubject "updated-at" (SettingValue.Text(environment.UpdatedAt.ToString("O")))
              setting environmentSubject "protected-branches" (SettingValue.Boolean environment.ProtectedBranches)
              setting environmentSubject "custom-branch-policies" (SettingValue.Boolean environment.CustomBranchPolicies) ]
        let protection =
            environment.ProtectionRules
            |> List.collect (fun rule ->
                let subject = $"{environmentSubject}:protection-rule:{rule.RuleId}"
                [ yield setting subject "node-id" (SettingValue.Text rule.RuleNodeId)
                  yield setting subject "kind" (SettingValue.Text rule.Kind)
                  match rule.WaitMinutes with
                  | Some minutes -> yield setting subject "wait-minutes" (SettingValue.Integer(int64 minutes))
                  | None -> ()
                  match rule.PreventSelfReview with
                  | Some prevent -> yield setting subject "prevent-self-review" (SettingValue.Boolean prevent)
                  | None -> ()
                  for reviewer in rule.Reviewers do
                      let reviewerSubject =
                          $"{subject}:reviewer:{reviewer.Kind.ToLowerInvariant()}:{reviewer.DatabaseId}"
                      yield setting reviewerSubject "node-id" (SettingValue.Text reviewer.NodeId)
                      yield setting reviewerSubject "name" (SettingValue.Text reviewer.Name)
                      yield setting reviewerSubject "kind" (SettingValue.Text reviewer.Kind) ])
        let branches =
            environment.BranchPolicies
            |> List.collect (fun policy ->
                let subject = $"{environmentSubject}:deployment-branch-policy:{policy.PolicyId}"
                [ setting subject "node-id" (SettingValue.Text policy.PolicyNodeId)
                  setting subject "name" (SettingValue.Text policy.Name)
                  setting subject "kind" (SettingValue.Text policy.Kind) ])
        let custom =
            environment.CustomRules
            |> List.collect (fun rule ->
                let subject = $"{environmentSubject}:custom-protection-rule:{rule.RuleId}"
                [ setting subject "node-id" (SettingValue.Text rule.RuleNodeId)
                  setting subject "enabled" (SettingValue.Boolean rule.Enabled)
                  setting subject "app-id" (SettingValue.Integer rule.AppId)
                  setting subject "app-node-id" (SettingValue.Text rule.AppNodeId)
                  setting subject "app-slug" (SettingValue.Text rule.AppSlug) ])
        let variables =
            variableValues
            |> List.map (fun (name, value) ->
                setting $"{environmentSubject}:variable:{name}" "value" (SettingValue.Text value))
        core @ protection @ branches @ custom @ variables

    let readEnvironments
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        if not (validOptions options) then refuse "invalid-options"
        elif identity.Owner <> options.Owner || identity.Name <> options.Repository then
            refuse "repository-identity-drift"
        elif not (validText repositoryRevision) then refuse "invalid-repository-revision"
        else
            repositoryResponse options identity repositoryRevision transport
            |> Result.bind (fun (repository, repositoryRoot) ->
                enumText "visibility" [ "public"; "private"; "internal" ] repositoryRoot
                |> Result.bind (fun visibility ->
                    if visibility <> "public" then
                        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                            $"{visibility}-repository-environment-plan-applicability-unproven")
                    else
                        match MigrationEnvironmentSettingsRead.read options transport with
                        | Error failure -> environmentFailure failure
                        | Ok observed ->
                            let mutable expectedRevision = DateTimeOffset.MinValue
                            if observed.RepositoryId <> identity.DatabaseId
                               || observed.RepositoryNodeId <> identity.NodeId
                               || observed.RepositoryFullName <> $"{identity.Owner}/{identity.Name}"
                               || not (DateTimeOffset.TryParse(repositoryRevision, &expectedRevision))
                               || observed.RepositoryUpdatedAt <> expectedRevision then
                                refuse "environment-identity-drift"
                            elif observed.IdentityPayloadJson <> repository.Body
                                 || observed.TerminalIdentityPayloadJson <> repository.Body then
                                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                                    "environment-repository-raw-identity-drift")
                            elif observed.Environments |> List.exists (fun item -> not (List.isEmpty item.Secrets)) then
                                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                                    "environment-secret-values-provider-inaccessible")
                            else
                                observed.Environments
                                |> List.fold (fun state environment ->
                                    state
                                    |> Result.bind (fun values ->
                                        environmentVariableValues environment
                                        |> Result.map (fun variables -> values @ [ environment, variables ]))) (Ok [])
                                |> Result.map (fun environmentsWithVariables ->
                                    let pages =
                                        [ identityPage options repository ]
                                        @ (observed.Pages |> List.map (environmentPage "environment-roster"))
                                        @ (observed.Environments
                                           |> List.collect (fun environment ->
                                               [ { SettingsStream=$"environment-detail:{environment.EnvironmentId}"
                                                   SettingsRequestedUri=environment.DetailUri
                                                   SettingsPayloadJson=environment.DetailPayloadJson
                                                   SettingsPayloadSha256=environment.DetailPayloadSha256
                                                   SettingsNextUri=None }
                                                 yield! environment.BranchPolicyPages
                                                        |> List.map (environmentPage
                                                            $"environment-branch-policies:{environment.EnvironmentId}")
                                                 { SettingsStream=$"environment-custom-protection-rules:{environment.EnvironmentId}"
                                                   SettingsRequestedUri=environment.CustomRulesUri
                                                   SettingsPayloadJson=environment.CustomRulesPayloadJson
                                                   SettingsPayloadSha256=environment.CustomRulesPayloadSha256
                                                   SettingsNextUri=None }
                                                 yield! environment.SecretPages
                                                        |> List.map (environmentPage
                                                            $"environment-secrets:{environment.EnvironmentId}")
                                                 yield! environment.VariablePages
                                                        |> List.map (environmentPage
                                                            $"environment-variables:{environment.EnvironmentId}") ]))
                                    let settings =
                                        { Surface=Environments; Subject=$"repository:{identity.DatabaseId}"
                                          Name="environment-count"; Value=SettingValue.Integer(int64 observed.TotalCount) }
                                        :: (environmentsWithVariables
                                            |> List.collect (fun (environment, variables) ->
                                                environmentSettings environment variables))
                                    { SurfaceRead=
                                        { RepositoryIdentity=identity; RepositoryRevision=repositoryRevision
                                          Surface=Environments; Complete=true; Pages=pages; Settings=settings }
                                      Environments=observed.Environments })))

    let private readVulnerabilityAlerts
        (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport)
        =
        let uri, request = endpointRequest options "vulnerability-alerts"
        match transport.Send request with
        | NetworkFailure | TimedOut ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "transport-unavailable")
        | Response response when response.StatusCode = 401 || response.StatusCode = 403 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized $"http:{response.StatusCode}")
        | Response response when response.StatusCode = 404 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                "disabled-or-inaccessible:vulnerability-alerts")
        | Response response when response.StatusCode <> 204 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
                $"vulnerability-alerts-http:{response.StatusCode}")
        | Response response when responseHeader "link" response.Headers |> Option.isSome ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                "vulnerability-alerts-unexpected-continuation")
        | Response response when not (isNull response.Body || response.Body = "") ->
            refuse "vulnerability-alerts-unexpected-body"
        | Response response -> Ok(terminalPage "vulnerability-alerts" uri response)

    let private readAutomatedSecurityFixes
        (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport)
        =
        let uri, request = endpointRequest options "automated-security-fixes"
        match transport.Send request with
        | NetworkFailure | TimedOut ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "transport-unavailable")
        | Response response when response.StatusCode = 401 || response.StatusCode = 403 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized $"http:{response.StatusCode}")
        | Response response when response.StatusCode = 404 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                "disabled-or-inaccessible:dependabot-security-updates")
        | Response response when response.StatusCode <> 200 ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
                $"automated-security-fixes-http:{response.StatusCode}")
        | Response response when responseHeader "link" response.Headers |> Option.isSome ->
            Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                "automated-security-fixes-unexpected-continuation")
        | Response response ->
            parse response.Body
            |> Result.bind (exactMembers "automated-security-fixes" [ "enabled"; "paused" ])
            |> Result.bind (fun root ->
                match flag "enabled" root, flag "paused" root with
                | Ok true, Ok paused -> Ok(paused, terminalPage "automated-security-fixes" uri response)
                | Ok false, Ok _ -> refuse "contradictory:automated-security-fixes-200-disabled"
                | Error failure, _ | _, Error failure -> Error failure)

    let readDependencyControls
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        if not (validOptions options) then refuse "invalid-options"
        elif identity.Owner <> options.Owner || identity.Name <> options.Repository then
            refuse "repository-identity-drift"
        elif not (validText repositoryRevision) then refuse "invalid-repository-revision"
        else
            repositoryResponse options identity repositoryRevision transport
            |> Result.bind (fun (repository, _) ->
                let configurationUri, configurationRequest = codeSecurityRequest options
                match transport.Send configurationRequest with
                | NetworkFailure | TimedOut ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "transport-unavailable")
                | Response response when response.StatusCode = 401 || response.StatusCode = 403 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized $"http:{response.StatusCode}")
                | Response response when response.StatusCode = 404 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "http:404")
                | Response response when response.StatusCode = 204 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                        "no-attached-configuration-dependency-controls-unproven")
                | Response response when response.StatusCode <> 200 ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable $"http:{response.StatusCode}")
                | Response response when responseHeader "link" response.Headers |> Option.isSome ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Partial "unexpected-continuation")
                | Response response ->
                    parse response.Body
                    |> Result.bind (fun root ->
                        match text "status" root, prop "configuration" root with
                        | Ok "attached", Ok configuration ->
                            match positive "id" configuration, text "target_type" configuration,
                                  text "name" configuration, text "enforcement" configuration,
                                  text "updated_at" configuration,
                                  explicitSecurityFlag "dependency_graph" configuration,
                                  explicitSecurityFlag "dependency_graph_autosubmit_action" configuration,
                                  dependencyOptions configuration,
                                  explicitSecurityFlag "dependabot_alerts" configuration,
                                  explicitSecurityFlag "dependabot_security_updates" configuration,
                                  explicitSecurityFlag "dependabot_delegated_alert_dismissal" configuration with
                            | Ok id, Ok targetType, Ok name, Ok enforcement, Ok updatedAt,
                              Ok dependencyGraph, Ok autosubmit, Ok labeledRunners,
                              Ok alerts, Ok securityUpdates, Ok delegatedDismissal when
                                (targetType = "organization" || targetType = "enterprise")
                                && (enforcement = "enforced" || enforcement = "unenforced"
                                    || enforcement = "enterprise_enforced") ->
                                configurationUrl options targetType id configuration
                                |> Result.bind (fun _ ->
                                    if not dependencyGraph || not alerts || not securityUpdates then
                                        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                                            "disabled-dependency-controls-effective-state-not-readable")
                                    else
                                        readVulnerabilityAlerts options transport
                                        |> Result.bind (fun alertsPage ->
                                            readAutomatedSecurityFixes options transport
                                            |> Result.map (fun (paused, updatesPage) ->
                                                let subject = $"configuration:{id}"
                                                let setting name value =
                                                    { Surface=DependencyControls; Subject=subject; Name=name; Value=value }
                                                let settings =
                                                    [ setting "configuration-id" (SettingValue.Integer id)
                                                      setting "target-type" (SettingValue.Text targetType)
                                                      setting "configuration-name" (SettingValue.Text name)
                                                      setting "enforcement" (SettingValue.Text enforcement)
                                                      setting "configuration-updated-at" (SettingValue.Text updatedAt)
                                                      setting "dependency-graph" (SettingValue.Boolean dependencyGraph)
                                                      setting "dependency-graph-autosubmit-action" (SettingValue.Boolean autosubmit)
                                                      setting "dependency-graph-autosubmit-uses-labeled-runners"
                                                          (SettingValue.Boolean labeledRunners)
                                                      setting "dependabot-alerts" (SettingValue.Boolean alerts)
                                                      setting "dependabot-security-updates" (SettingValue.Boolean securityUpdates)
                                                      setting "dependabot-security-updates-paused" (SettingValue.Boolean paused)
                                                      setting "dependabot-delegated-alert-dismissal"
                                                          (SettingValue.Boolean delegatedDismissal) ]
                                                let configurationPage =
                                                    terminalPage "dependency-security-configuration"
                                                        configurationUri response
                                                { SurfaceRead=
                                                    { RepositoryIdentity=identity
                                                      RepositoryRevision=repositoryRevision
                                                      Surface=DependencyControls
                                                      Complete=true
                                                      Pages=
                                                        [ identityPage options repository
                                                          configurationPage
                                                          alertsPage
                                                          updatesPage ]
                                                      Settings=settings }
                                                  ConfigurationId=id
                                                  ConfigurationTargetType=targetType
                                                  ConfigurationName=name
                                                  Enforcement=enforcement
                                                  ConfigurationUpdatedAt=updatedAt
                                                  DependencyGraph=dependencyGraph
                                                  DependencyGraphAutosubmitAction=autosubmit
                                                  DependencyGraphAutosubmitUsesLabeledRunners=labeledRunners
                                                  DependabotAlerts=alerts
                                                  DependabotSecurityUpdates=securityUpdates
                                                  DependabotSecurityUpdatesPaused=paused
                                                  DependabotDelegatedAlertDismissal=delegatedDismissal })))
                            | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ ->
                                refuse "unsupported:dependency-configuration-provenance"
                            | Error failure, _, _, _, _, _, _, _, _, _, _
                            | _, Error failure, _, _, _, _, _, _, _, _, _
                            | _, _, Error failure, _, _, _, _, _, _, _, _
                            | _, _, _, Error failure, _, _, _, _, _, _, _
                            | _, _, _, _, Error failure, _, _, _, _, _, _
                            | _, _, _, _, _, Error failure, _, _, _, _, _
                            | _, _, _, _, _, _, Error failure, _, _, _, _
                            | _, _, _, _, _, _, _, Error failure, _, _, _
                            | _, _, _, _, _, _, _, _, Error failure, _, _
                            | _, _, _, _, _, _, _, _, _, Error failure, _
                            | _, _, _, _, _, _, _, _, _, _, Error failure -> Error failure
                        | Ok status, Ok _ -> refuse $"unsupported:attachment-status:{status}"
                        | Error failure, _ | _, Error failure -> Error failure))

    let readReleasesAndTags
        (options: MigrationGitHubReadOptions)
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (transport: IMigrationGitHubReadTransport)
        =
        if not (validOptions options) then refuse "invalid-options"
        elif identity.Owner <> options.Owner || identity.Name <> options.Repository then
            refuse "repository-identity-drift"
        elif not (validText repositoryRevision) then refuse "invalid-repository-revision"
        else
            repositoryEvidence options identity repositoryRevision transport
            |> Result.bind (fun identityPage ->
                collect options transport "tags" "tags" tag
                |> Result.bind (fun (tagPages, tags) ->
                    unique (fun (item: MigrationRepositoryTagSetting) -> item.Name) "duplicate-tag-name" tags
                    |> Result.bind (unique (fun (item: MigrationRepositoryTagSetting) -> item.NodeId)
                                            "duplicate-tag-node")
                    |> Result.bind (fun tags ->
                        collect options transport "releases" "releases" release
                        |> Result.bind (fun (releasePages, releases) ->
                            unique (fun (item: MigrationRepositoryReleaseSetting) -> item.DatabaseId)
                                   "duplicate-release-id" releases
                            |> Result.bind (unique (fun (item: MigrationRepositoryReleaseSetting) -> item.NodeId)
                                                    "duplicate-release-node")
                            |> Result.bind (fun releases ->
                                let settings =
                                    (tags |> List.collect tagSettings)
                                    @ (releases |> List.collect releaseSettings)
                                Ok
                                    { SurfaceRead=
                                        { RepositoryIdentity=identity; RepositoryRevision=repositoryRevision
                                          Surface=ReleasesAndTags; Complete=true
                                          Pages=identityPage :: (tagPages @ releasePages); Settings=settings }
                                      Tags=tags; Releases=releases })))))

type MigrationRepositorySettingsGitHubProvider
    (options: MigrationGitHubReadOptions, transport: IMigrationGitHubReadTransport) =
    interface IMigrationRepositorySettingsSurfaceProvider with
        member _.Read(identity, repositoryRevision, surface) =
            match surface with
            | SettingsSurface.Repository ->
                MigrationRepositorySettingsProviderRead.readRepository
                    options identity repositoryRevision transport
                |> Result.map _.SurfaceRead
            | MergePolicy ->
                MigrationRepositorySettingsProviderRead.readMergePolicy
                    options identity repositoryRevision transport
                |> Result.map _.SurfaceRead
            | ActionsPolicy ->
                MigrationRepositorySettingsProviderRead.readActionsPolicy
                    options identity repositoryRevision transport
                |> Result.map _.SurfaceRead
            | ReleasesAndTags ->
                MigrationRepositorySettingsProviderRead.readReleasesAndTags
                    options identity repositoryRevision transport
                |> Result.map _.SurfaceRead
            | CodeSecurity ->
                MigrationRepositorySettingsProviderRead.readCodeSecurity
                    options identity repositoryRevision transport
                |> Result.map _.SurfaceRead
            | DependencyControls ->
                MigrationRepositorySettingsProviderRead.readDependencyControls
                    options identity repositoryRevision transport
                |> Result.map _.SurfaceRead
            | Environments ->
                MigrationRepositorySettingsProviderRead.readEnvironments
                    options identity repositoryRevision transport
                |> Result.map _.SurfaceRead
            | _ ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Unsupported
                    $"surface-reader-not-installed:{RepositorySettingsAdapter.surfaceId surface}")
