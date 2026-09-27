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

    let private repositoryEvidence
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
                      text "default_branch" root, text "updated_at" root, sourceNodeId root,
                      prop "permissions" root |> Result.bind (flag "push") with
                | Ok id, Ok nodeId, Ok fullName, Ok defaultBranch, Ok updatedAt, Ok source, Ok canPush when
                    id = options.ExpectedRepositoryId
                    && id = identity.DatabaseId
                    && nodeId = identity.NodeId
                    && fullName = $"{identity.Owner}/{identity.Name}"
                    && fullName = $"{options.Owner}/{options.Repository}"
                    && defaultBranch = identity.DefaultBranch
                    && source = identity.SourceRepositoryNodeId
                    && updatedAt = revision ->
                    if not canPush then
                        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
                            "draft-release-visibility-unproven")
                    else
                        Ok
                            { SettingsStream="repository-identity"
                              SettingsRequestedUri=uri.AbsoluteUri
                              SettingsPayloadJson=response.Body
                              SettingsPayloadSha256=hashText response.Body
                              SettingsNextUri=None }
                | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ ->
                    Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "repository-identity-drift")
                | Error failure, _, _, _, _, _, _ | _, Error failure, _, _, _, _, _
                | _, _, Error failure, _, _, _, _ | _, _, _, Error failure, _, _, _
                | _, _, _, _, Error failure, _, _ | _, _, _, _, _, Error failure, _
                | _, _, _, _, _, _, Error failure -> Error failure))

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
            | Environments ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Partial
                    "environment-secrets-variables-and-plan-conditions-remain-unbound")
            | _ ->
                Error(MigrationRepositorySettingsSurfaceRefusal.Unsupported
                    $"surface-reader-not-installed:{RepositorySettingsAdapter.surfaceId surface}")
