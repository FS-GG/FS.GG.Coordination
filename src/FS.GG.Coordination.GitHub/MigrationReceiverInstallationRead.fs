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
      WorkflowRunId: int64
      WorkflowRunAttempt: int
      RunNonce: string
      UserAgent: string }

/// The protected mint effect has its own port; observer reads cannot mint tokens.
type IMigrationReceiverTokenMintTransport =
    abstract Mint: RestRequest -> TransportOutcome

type MigrationReceiverTokenMintAttestation =
    { RequestIdentitySha256: string
      ResponseSha256: string
      TokenSha256: string
      ExpiresAt: DateTimeOffset
      WorkflowRunId: int64
      WorkflowRunAttempt: int
      RunNonce: string
      Fingerprint: string }

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
      MintAttestation: MigrationReceiverTokenMintAttestation
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
    [<Literal>]
    let private protectedProbeInstallationId = 143110413L
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
          InstallationToken=""; UserAgent=options.UserAgent }

    let private mintToken options (transport: IMigrationReceiverTokenMintTransport) =
        let uri = combine options.ApiBase $"app/installations/{options.InstallationId}/access_tokens"
        let body = $"{{\"repository_ids\":[{sandboxRepositoryId}],\"permissions\":{{\"contents\":\"read\"}}}}"
        let request =
            { Method=Post; Uri=uri; Headers=headers options.AppToken options.UserAgent
              Body=Some body; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
        let requestIdentity = sha256 ($"POST\000{uri.AbsoluteUri}\000{body}\000{options.WorkflowRunId}\000{options.WorkflowRunAttempt}\000{options.RunNonce}")
        match transport.Mint request with
        | Response response when response.StatusCode = 201 ->
            try
                use document = JsonDocument.Parse response.Body
                let root = document.RootElement
                if not (uniqueMembers root) then unavailable "mint-response-shape"
                else
                    match text "token" root, text "expires_at" root, permissionMap "permissions" root,
                          text "repository_selection" root, property "repositories" root with
                    | Ok token, Ok expiryText, Ok grants, Ok selection, Ok repositories when repositories.ValueKind = JsonValueKind.Array ->
                        let mutable expiry = DateTimeOffset.MinValue
                        let now = DateTimeOffset.UtcNow
                        let selected = repositories.EnumerateArray() |> Seq.toList
                        let repositoryMatches =
                            match selected with
                            | [ repository ] when uniqueMembers repository ->
                                int64 "id" repository = Ok sandboxRepositoryId
                                && text "node_id" repository = Ok sandboxRepositoryNodeId
                                && text "full_name" repository = Ok sandboxRepositoryName
                            | _ -> false
                        let effectiveGrants =
                            if grants = Map [ "contents", "read" ] then
                                Map.add "metadata" "read" grants
                            else grants
                        if not (DateTimeOffset.TryParse(expiryText, &expiry))
                           || expiry <= now || expiry > now.AddHours(1.0) then
                            unavailable "mint-expiry"
                        elif effectiveGrants <> options.RequiredTokenPermissions then
                            unavailable "mint-permissions"
                        elif selection <> "selected" || not repositoryMatches then
                            unavailable "mint-repository-scope"
                        else
                            let responseDigest = sha256 response.Body
                            let tokenDigest = sha256 token
                            let fingerprint =
                                sha256 (String.concat "\000" [ requestIdentity; responseDigest; tokenDigest;
                                                                 expiry.ToUniversalTime().ToString("O");
                                                                 string options.WorkflowRunId; string options.WorkflowRunAttempt; options.RunNonce ])
                            let attestation =
                                { RequestIdentitySha256=requestIdentity; ResponseSha256=responseDigest
                                  TokenSha256=tokenDigest; ExpiresAt=expiry
                                  WorkflowRunId=options.WorkflowRunId; WorkflowRunAttempt=options.WorkflowRunAttempt
                                  RunNonce=options.RunNonce; Fingerprint=fingerprint }
                            Ok(token, effectiveGrants, attestation)
                    | _ -> unavailable "mint-response-shape"
            with :? JsonException -> unavailable "mint-response-json"
        | Response response -> unavailable $"mint-inaccessible:http-{response.StatusCode}"
        | NetworkFailure -> unavailable "mint-inaccessible:network"
        | TimedOut -> unavailable "mint-inaccessible:timeout"

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
        not (isNull options.ApiBase) && options.ApiBase.AbsoluteUri = "https://api.github.com/"
        && options.AppId > 0L && options.InstallationId = protectedProbeInstallationId && options.AccountId > 0L
        && validText options.AppNodeId && validText options.AppSlug
        && validText options.AccountLogin && validText options.AccountNodeId
        && validText options.AppToken && validText options.UserAgent
        && options.WorkflowRunId > 0L && options.WorkflowRunAttempt > 0 && validText options.RunNonce
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

    let captureForComposer options (mintTransport: IMigrationReceiverTokenMintTransport) (transport: IMigrationGitHubReadTransport) =
        if not (validOptions options) then unavailable "invalid-options"
        else
            appRead options transport
            |> Result.bind (fun (firstApp, firstRaw) ->
                mintToken options mintTransport
                |> Result.bind (fun (token, tokenPermissions, attestation) ->
                    let rosterOptions = { expectedRoster options with InstallationToken=token }
                    let pass () =
                        MigrationReceiverRosterRead.capturePassForMintedToken rosterOptions attestation.TokenSha256 transport
                        |> Result.mapError (fun reason -> $"receiver-installation-authority-adapter-unavailable:{reason}")
                        |> Result.bind (fun roster -> exactScope options roster |> Result.map (fun () -> roster))
                    pass ()
                    |> Result.bind (fun firstRoster ->
                        pass ()
                        |> Result.bind (fun secondRoster ->
                            appRead options transport
                            |> Result.bind (fun (secondApp, secondRaw) ->
                                if firstApp <> secondApp || firstRaw <> secondRaw || firstRoster <> secondRoster then
                                    unavailable "two-pass-drift"
                                else
                                    let rosterCapture =
                                        { First=firstRoster; Second=secondRoster
                                          CaptureFingerprint=sha256 (firstRoster.PassFingerprint + "\n" + secondRoster.PassFingerprint) }
                                    let fingerprint =
                                        sha256 (String.concat "\n" [ firstRaw.RosterRawSha256; secondRaw.RosterRawSha256
                                                                     attestation.Fingerprint; rosterCapture.CaptureFingerprint ])
                                    let composerOptions = { rosterOptions with AppToken=""; InstallationToken="" }
                                    Ok { App=firstApp; AppFirst=firstRaw; AppSecond=secondRaw
                                         InstallationPermissions=options.ExpectedInstallationPermissions
                                         TokenPermissions=tokenPermissions; MintAttestation=attestation
                                         ComposerRosterOptions=composerOptions; ComposerRosterCapture=rosterCapture
                                         CaptureFingerprint=fingerprint })))))
