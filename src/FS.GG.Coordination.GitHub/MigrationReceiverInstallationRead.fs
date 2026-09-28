namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationReceiverInstallationReadOptions =
    { ApiBase: Uri
      AppId: int64
      AppNodeId: string
      AppSlug: string
      InstallationId: int64
      AccountLogin: string
      AccountId: int64
      AccountNodeId: string
      ExpectedAppPermissions: Map<string, string>
      ExpectedInstallationPermissions: Map<string, string>
      RequiredTokenPermissions: Map<string, string>
      SelectedRepositories: MigrationReceiverRosterDeclaredRepository list
      AppToken: string
      InstallationToken: string
      UserAgent: string }

type MigrationReceiverProviderApp =
    { ProviderAppId: int64
      ProviderAppNodeId: string
      ProviderAppSlug: string
      ProviderOwnerLogin: string
      ProviderOwnerId: int64
      ProviderOwnerNodeId: string
      ProviderPermissions: Map<string, string> }

type MigrationReceiverInstallationCapture =
    { App: MigrationReceiverProviderApp
      AppFirst: MigrationReceiverRosterRawPage
      AppSecond: MigrationReceiverRosterRawPage
      InstallationPermissions: Map<string, string>
      TokenPermissions: Map<string, string>
      TokenFirst: MigrationReceiverRosterRawPage
      TokenSecond: MigrationReceiverRosterRawPage
      ComposerRosterOptions: MigrationReceiverRosterReadOptions
      ComposerRosterCapture: MigrationReceiverRosterCapture
      CaptureFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverInstallationRead =
    [<Literal>]
    let private sandboxRepositoryId = 1353050537L
    [<Literal>]
    let private sandboxRepositoryNodeId = "R_kgDOUKXpqQ"
    [<Literal>]
    let private sandboxRepositoryName = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
    let private unavailable reason = Error $"receiver-installation-authority-adapter-unavailable:{reason}"
    let private sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private combine (root: Uri) (relative: string) = Uri(root, relative)
    let private headers token userAgent =
        Map [ "Accept", "application/vnd.github+json"
              "Authorization", $"Bearer {token}"
              "User-Agent", userAgent
              "X-GitHub-Api-Version", ApiVersion.value ApiVersion.required ]
    let private property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if value.TryGetProperty(name, &found) then Ok found else Error $"missing-{name}"
    let private uniqueMembers (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> Set.ofList |> Set.count)
    let private text name value =
        property name value
        |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(item.GetString()))
            then Ok(item.GetString()) else Error $"invalid-{name}")
    let private int64 name value =
        property name value
        |> Result.bind (fun item ->
            let mutable parsed = 0L
            if item.ValueKind = JsonValueKind.Number && item.TryGetInt64(&parsed) && parsed > 0L
            then Ok parsed else Error $"invalid-{name}")
    let private permissionMap name value =
        property name value
        |> Result.bind (fun item ->
            if not (uniqueMembers item) then Error $"invalid-{name}"
            else
                item.EnumerateObject()
                |> Seq.fold (fun state member' ->
                    state |> Result.bind (fun accumulated ->
                        if member'.Value.ValueKind <> JsonValueKind.String then Error $"invalid-{name}"
                        else
                            let level = member'.Value.GetString()
                            if not (Set.contains level (set [ "read"; "write"; "admin" ]))
                            then Error $"invalid-{name}"
                            else Ok(Map.add member'.Name level accumulated))) (Ok Map.empty))

    let private parseApp (options: MigrationReceiverInstallationReadOptions) (body: string) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement
            if not (uniqueMembers root) then unavailable "app-shape"
            else
                match int64 "id" root, text "node_id" root, text "slug" root,
                      property "owner" root, permissionMap "permissions" root with
                | Ok id, Ok nodeId, Ok slug, Ok owner, Ok permissions when uniqueMembers owner ->
                    match text "login" owner, int64 "id" owner, text "node_id" owner with
                    | Ok ownerLogin, Ok ownerId, Ok ownerNodeId ->
                        let app =
                            { ProviderAppId=id; ProviderAppNodeId=nodeId; ProviderAppSlug=slug
                              ProviderOwnerLogin=ownerLogin; ProviderOwnerId=ownerId
                              ProviderOwnerNodeId=ownerNodeId; ProviderPermissions=permissions }
                        if id <> options.AppId || nodeId <> options.AppNodeId
                           || not (String.Equals(slug, options.AppSlug, StringComparison.OrdinalIgnoreCase)) then
                            unavailable "app-identity-drift"
                        elif permissions <> options.ExpectedAppPermissions then unavailable "app-permission-settings-unknown"
                        else Ok app
                    | _ -> unavailable "app-owner-shape"
                | _ -> unavailable "app-shape"
        with :? JsonException -> unavailable "app-json"

    let private parseInstallationBinding (options: MigrationReceiverInstallationReadOptions) expectedPermissions (body: string) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement
            if not (uniqueMembers root) then unavailable "installation-shape"
            else
                match int64 "id" root, int64 "app_id" root, text "app_slug" root,
                      property "account" root, permissionMap "permissions" root,
                      text "repository_selection" root with
                | Ok installationId, Ok appId, Ok slug, Ok account, Ok permissions, Ok selection when uniqueMembers account ->
                    match text "login" account, int64 "id" account, text "node_id" account with
                    | Ok login, Ok accountId, Ok accountNodeId when
                        installationId = options.InstallationId
                        && login = options.AccountLogin
                        && accountId = options.AccountId
                        && accountNodeId = options.AccountNodeId ->
                        if appId <> options.AppId
                           || not (String.Equals(slug, options.AppSlug, StringComparison.OrdinalIgnoreCase)) then
                            unavailable "installation-app-drift"
                        elif selection <> "selected" then unavailable "installation-not-selected"
                        elif permissions <> expectedPermissions then unavailable "installation-permission-settings-unknown"
                        else Ok permissions
                    | _ -> unavailable "installation-account-drift"
                | _ -> unavailable "installation-settings-unknown"
        with :? JsonException -> unavailable "installation-json"

    let private appRead options (transport: IMigrationGitHubReadTransport) =
        let uri = combine options.ApiBase "app"
        let request =
            Rest { Method=Get; Uri=uri; Headers=headers options.AppToken options.UserAgent; Body=None
                   ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        match transport.Send request with
        | Response response when response.StatusCode = 200 ->
            let link =
                response.Headers |> Map.toSeq
                |> Seq.tryPick (fun (name, value) ->
                    if String.Equals(name, "link", StringComparison.OrdinalIgnoreCase) then Some value else None)
                |> Option.defaultValue ""
            match Transport.tryNextLink link with
            | Error _ -> unavailable "app-pagination-link"
            | Ok(Some _) -> unavailable "app-pagination-refused"
            | Ok None ->
                parseApp options response.Body
                |> Result.map (fun app ->
                    app,
                    { RosterRequestedUri=uri.AbsoluteUri; RosterRequestIdentitySha256=sha256 uri.AbsoluteUri
                      RosterRawBody=response.Body; RosterRawSha256=sha256 response.Body; RosterNextUri=None })
        | Response response -> unavailable $"app-inaccessible:http-{response.StatusCode}"
        | NetworkFailure -> unavailable "app-inaccessible:network"
        | TimedOut -> unavailable "app-inaccessible:timeout"

    let private expectedRoster options =
        { ApiBase=options.ApiBase; InstallationId=options.InstallationId
          AccountLogin=options.AccountLogin; AccountId=options.AccountId; AccountNodeId=options.AccountNodeId
          RequiredPermissions=options.ExpectedInstallationPermissions; AppToken=options.AppToken
          InstallationToken=options.InstallationToken; UserAgent=options.UserAgent }

    let private tokenRead _ _ =
        // Installation-token permissions are only known from the exact mint
        // response. No installation-token GET can attest them.
        unavailable "token-permission-attestation-unavailable"

    let private validText value = not (String.IsNullOrWhiteSpace value)
    let private validPermissionName (value: string) =
        validText value
        && value |> Seq.forall (fun character ->
            character >= 'a' && character <= 'z'
            || character >= '0' && character <= '9'
            || character = '_')

    let private permissionRank = function
        | "read" -> 1 | "write" -> 2 | "admin" -> 3 | _ -> 0

    let private containedBy (narrow: Map<string, string>) (broad: Map<string, string>) =
        narrow
        |> Map.forall (fun name level ->
            broad |> Map.tryFind name |> Option.exists (fun maximum -> permissionRank level <= permissionRank maximum))

    let private validOptions options =
        not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && options.AppId > 0L && options.InstallationId > 0L && options.AccountId > 0L
        && validText options.AppNodeId && validText options.AppSlug
        && validText options.AccountLogin && validText options.AccountNodeId
        && validText options.AppToken && validText options.InstallationToken && validText options.UserAgent
        && not options.ExpectedAppPermissions.IsEmpty
        && not options.ExpectedInstallationPermissions.IsEmpty
        && not options.RequiredTokenPermissions.IsEmpty
        && Map.tryFind "contents" options.RequiredTokenPermissions = Some "read"
        && Map.tryFind "metadata" options.RequiredTokenPermissions = Some "read"
        && ([ options.ExpectedAppPermissions; options.ExpectedInstallationPermissions; options.RequiredTokenPermissions ]
            |> List.forall (Map.forall (fun name level ->
                validPermissionName name && Set.contains level (set [ "read"; "write"; "admin" ]))))
        && containedBy options.RequiredTokenPermissions options.ExpectedInstallationPermissions
        && containedBy options.ExpectedInstallationPermissions options.ExpectedAppPermissions
        && options.SelectedRepositories =
            [ { DeclaredRepositoryId=sandboxRepositoryId
                DeclaredRepositoryNodeId=sandboxRepositoryNodeId
                DeclaredRepositoryFullName=sandboxRepositoryName } ]
        && options.SelectedRepositories |> List.forall (fun repository ->
            repository.DeclaredRepositoryId > 0L
            && validText repository.DeclaredRepositoryNodeId
            && validText repository.DeclaredRepositoryFullName
            && repository.DeclaredRepositoryFullName.StartsWith(options.AccountLogin + "/", StringComparison.OrdinalIgnoreCase))
        && (options.SelectedRepositories |> List.map _.DeclaredRepositoryId |> Set.ofList |> Set.count) = 1
        && (options.SelectedRepositories |> List.map _.DeclaredRepositoryNodeId |> Set.ofList |> Set.count) = 1
        && (options.SelectedRepositories |> List.map (fun item -> item.DeclaredRepositoryFullName.ToLowerInvariant()) |> Set.ofList |> Set.count) = 1

    let private exactScope options (pass: MigrationReceiverRosterPass) =
        parseInstallationBinding options options.ExpectedInstallationPermissions pass.Pages.Head.RosterRawBody
        |> Result.bind (fun _ ->
            if pass.ScopeSettings.Permissions <> options.ExpectedInstallationPermissions then
                unavailable "installation-permission-settings-unknown"
            elif pass.RepositoryTotalCount <> 1 then unavailable "unselected-repository-grant"
            elif pass.Repositories |> List.exists (fun item -> not item.RosterPrivate || item.RosterArchived || item.RosterDisabled) then
                unavailable "repository-settings-unknown"
            else
                let expected = options.SelectedRepositories |> List.sortBy _.DeclaredRepositoryId
                let observed =
                    pass.Repositories
                    |> List.map (fun item ->
                        { DeclaredRepositoryId=item.RosterRepositoryId
                          DeclaredRepositoryNodeId=item.RosterRepositoryNodeId
                          DeclaredRepositoryFullName=item.RosterRepositoryFullName })
                    |> List.sortBy _.DeclaredRepositoryId
                if observed <> expected then unavailable "unselected-repository-grant" else Ok())

    let captureForComposer options (transport: IMigrationGitHubReadTransport) =
        if not (validOptions options) then unavailable "invalid-options"
        else
            let rosterOptions = expectedRoster options
            let pass () =
                appRead options transport
                |> Result.bind (fun (app, raw) ->
                    MigrationReceiverRosterRead.capturePass rosterOptions transport
                    |> Result.mapError (fun reason -> $"receiver-installation-authority-adapter-unavailable:{reason}")
                    |> Result.bind (fun roster ->
                        exactScope options roster
                        |> Result.bind (fun () -> tokenRead options transport)
                        |> Result.map (fun (tokenPermissions, tokenRaw) -> app, raw, roster, tokenPermissions, tokenRaw)))
            pass ()
            |> Result.bind (fun (firstApp, firstRaw, firstRoster, firstTokenPermissions, firstTokenRaw) ->
                pass ()
                |> Result.bind (fun (secondApp, secondRaw, secondRoster, secondTokenPermissions, secondTokenRaw) ->
                    if firstApp <> secondApp || firstRaw <> secondRaw || firstRoster <> secondRoster
                       || firstTokenPermissions <> secondTokenPermissions || firstTokenRaw <> secondTokenRaw then
                        unavailable "two-pass-drift"
                    else
                        let rosterCapture =
                            { First=firstRoster; Second=secondRoster
                              CaptureFingerprint=sha256 (firstRoster.PassFingerprint + "\n" + secondRoster.PassFingerprint) }
                        let fingerprint =
                            sha256 (firstRaw.RosterRawSha256 + "\n" + secondRaw.RosterRawSha256 + "\n"
                                    + firstTokenRaw.RosterRawSha256 + "\n" + secondTokenRaw.RosterRawSha256 + "\n"
                                    + rosterCapture.CaptureFingerprint)
                        let composerOptions =
                            { rosterOptions with AppToken=""; InstallationToken="" }
                        Ok { App=firstApp; AppFirst=firstRaw; AppSecond=secondRaw
                             InstallationPermissions=options.ExpectedInstallationPermissions
                             TokenPermissions=firstTokenPermissions
                             TokenFirst=firstTokenRaw; TokenSecond=secondTokenRaw
                             ComposerRosterOptions=composerOptions; ComposerRosterCapture=rosterCapture
                             CaptureFingerprint=fingerprint }))
