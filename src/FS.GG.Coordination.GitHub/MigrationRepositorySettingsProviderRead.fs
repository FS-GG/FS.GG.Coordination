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
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                    "environment-secrets-variables-and-plan-conditions-remain-unbound")
            | _ ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Unsupported
                    $"surface-reader-not-installed:{RepositorySettingsAdapter.surfaceId surface}")
