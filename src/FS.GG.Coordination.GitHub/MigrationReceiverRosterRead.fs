namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationReceiverRosterReadOptions =
    {
        ApiBase: Uri
        InstallationId: int64
        AccountLogin: string
        AccountId: int64
        AccountNodeId: string
        RequiredPermissions: Map<string, string>
        AppToken: string
        InstallationToken: string
        UserAgent: string
    }

type MigrationReceiverScopeSettings =
    {
        InstallationId: int64
        AccountLogin: string
        AccountId: int64
        AccountNodeId: string
        RepositorySelection: string
        Permissions: Map<string, string>
        RepositoriesUrl: string
    }

type MigrationReceiverRosterRepository =
    {
        RosterRepositoryId: int64
        RosterRepositoryNodeId: string
        RosterRepositoryFullName: string
        RosterPrivate: bool
        RosterArchived: bool
        RosterDisabled: bool
        RosterPermissions: Map<string, bool>
    }

type MigrationReceiverRosterRawPage =
    {
        RosterRequestedUri: string
        RosterRequestIdentitySha256: string
        RosterRawBody: string
        RosterRawSha256: string
        RosterNextUri: string option
    }

type MigrationReceiverRosterPass =
    {
        ScopeSettings: MigrationReceiverScopeSettings
        RepositoryTotalCount: int
        Repositories: MigrationReceiverRosterRepository list
        Pages: MigrationReceiverRosterRawPage list
        PassFingerprint: string
    }

type MigrationReceiverRosterCapture =
    {
        First: MigrationReceiverRosterPass
        Second: MigrationReceiverRosterPass
        CaptureFingerprint: string
    }

type MigrationReceiverRosterDeclaredRepository =
    {
        DeclaredRepositoryId: int64
        DeclaredRepositoryNodeId: string
        DeclaredRepositoryFullName: string
    }

type MigrationReceiverRosterAssessment =
    {
        TokenScopeExhaustive: bool
        CohortRepositoriesMatchScope: bool
        CallerReceiverRosterProtected: bool
        ReceiverIdentityComplete: bool
        ScopedSettingsSha256: string
        RepositoryRosterSha256: string
    }

[<RequireQualifiedAccess>]
module MigrationReceiverRosterRead =
    let private sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private frame (value: string) =
        $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private combine (root: Uri) (relative: string) = Uri(root, relative)

    let private headers token userAgent =
        Map
            [
                "Accept", "application/vnd.github+json"
                "Authorization", $"Bearer {token}"
                "User-Agent", userAgent
                "X-GitHub-Api-Version", ApiVersion.value ApiVersion.required
            ]

    let private validPermissionName (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value
           |> Seq.forall (fun character ->
               character >= 'a' && character <= 'z'
               || character >= '0' && character <= '9'
               || character = '_')

    let private validOptions (options: MigrationReceiverRosterReadOptions) =
        not (isNull options.ApiBase)
        && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && options.InstallationId > 0L
        && options.AccountId > 0L
        && not (String.IsNullOrWhiteSpace options.AccountLogin)
        && not (String.IsNullOrWhiteSpace options.AccountNodeId)
        && not (String.IsNullOrWhiteSpace options.AppToken)
        && not (String.IsNullOrWhiteSpace options.InstallationToken)
        && not (String.IsNullOrWhiteSpace options.UserAgent)
        && not options.RequiredPermissions.IsEmpty
        && options.RequiredPermissions
           |> Map.forall (fun name level ->
               validPermissionName name
               && Set.contains level (set [ "read"; "write"; "admin" ]))

    let private parse (payload: string) =
        try
            Ok(JsonDocument.Parse(payload))
        with :? JsonException ->
            Error "receiver-roster-malformed-json"

    let private uniqueObjectMembers (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            Error "receiver-roster-object-shape"
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if names.Length <> (names |> Set.ofList |> Set.count) then
                Error "receiver-roster-duplicate-json-member"
            else
                Ok()

    let private property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>

        if value.TryGetProperty(name, &found) then
            Ok found
        else
            Error $"receiver-roster-missing:{name}"

    let private text name value =
        property name value
        |> Result.bind (fun item ->
            if
                item.ValueKind = JsonValueKind.String
                && not (String.IsNullOrWhiteSpace(item.GetString()))
            then
                Ok(item.GetString())
            else
                Error $"receiver-roster-invalid:{name}")

    let private int64 name value =
        property name value
        |> Result.bind (fun item ->
            let mutable parsed = 0L

            if
                item.ValueKind = JsonValueKind.Number
                && item.TryGetInt64(&parsed)
                && parsed > 0L
            then
                Ok parsed
            else
                Error $"receiver-roster-invalid:{name}")

    let private integer name value =
        property name value
        |> Result.bind (fun item ->
            let mutable parsed = 0

            if
                item.ValueKind = JsonValueKind.Number
                && item.TryGetInt32(&parsed)
                && parsed >= 0
            then
                Ok parsed
            else
                Error $"receiver-roster-invalid:{name}")

    let private boolean name value =
        property name value
        |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.True -> Ok true
            | JsonValueKind.False -> Ok false
            | _ -> Error $"receiver-roster-invalid:{name}")

    let private stringMap allowedLevels name (value: JsonElement) =
        property name value
        |> Result.bind (fun permissions ->
            uniqueObjectMembers permissions
            |> Result.bind (fun () ->
                permissions.EnumerateObject()
                |> Seq.fold
                    (fun state item ->
                        state
                        |> Result.bind (fun accumulated ->
                            if
                                not (validPermissionName item.Name)
                                || item.Value.ValueKind <> JsonValueKind.String
                                || not (Set.contains (item.Value.GetString()) allowedLevels)
                            then
                                Error $"receiver-roster-invalid:{name}"
                            else
                                Ok(Map.add item.Name (item.Value.GetString()) accumulated)))
                    (Ok Map.empty)))

    let private boolMap name (value: JsonElement) =
        property name value
        |> Result.bind (fun permissions ->
            uniqueObjectMembers permissions
            |> Result.bind (fun () ->
                let add state (item: JsonProperty) =
                    state
                    |> Result.bind (fun accumulated ->
                        if not (validPermissionName item.Name) then
                            Error $"receiver-roster-invalid:{name}"
                        else
                            match item.Value.ValueKind with
                            | JsonValueKind.True -> Ok(Map.add item.Name true accumulated)
                            | JsonValueKind.False -> Ok(Map.add item.Name false accumulated)
                            | _ -> Error $"receiver-roster-invalid:{name}")

                permissions.EnumerateObject() |> Seq.fold add (Ok Map.empty)))

    let private optionalBoolMap (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if value.TryGetProperty(name, &property) then boolMap name value
        else Ok Map.empty

    let private nextLink (response: ResponseEnvelope) =
        let link =
            response.Headers
            |> Map.toSeq
            |> Seq.tryPick (fun (name, value) ->
                if String.Equals(name, "link", StringComparison.OrdinalIgnoreCase) then
                    Some value
                else
                    None)
            |> Option.defaultValue ""

        Transport.tryNextLink link
        |> Result.mapError (fun _ -> "receiver-roster-pagination-link")

    let private response (transport: IMigrationGitHubReadTransport) (request: GitHubRequest) =
        match transport.Send request with
        | Response value when value.StatusCode = 200 -> Ok value
        | Response value -> Error $"receiver-roster-read-unknown:http-{value.StatusCode}"
        | NetworkFailure -> Error "receiver-roster-read-unknown:network"
        | TimedOut -> Error "receiver-roster-read-unknown:timeout"

    let private get (options: MigrationReceiverRosterReadOptions) token uri (transport: IMigrationGitHubReadTransport) =
        response
            transport
            (Rest
                {
                    Method = Get
                    Uri = uri
                    Headers = headers token options.UserAgent
                    Body = None
                    ApiVersion = ApiVersion.required
                    Idempotency = ReplaySafe
                })

    let private rawPage (uri: Uri) (response: ResponseEnvelope) (next: Uri option) =
        {
            RosterRequestedUri = uri.AbsoluteUri
            RosterRequestIdentitySha256 = sha256 uri.AbsoluteUri
            RosterRawBody = response.Body
            RosterRawSha256 = sha256 response.Body
            RosterNextUri = next |> Option.map _.AbsoluteUri
        }

    let private parseScope options payload =
        parse payload
        |> Result.bind (fun document ->
            use document = document
            let root = document.RootElement

            uniqueObjectMembers root
            |> Result.bind (fun () ->
                match
                    int64 "id" root,
                    int64 "target_id" root,
                    text "target_type" root,
                    text "repository_selection" root,
                    property "account" root,
                    stringMap (set [ "read"; "write"; "admin" ]) "permissions" root,
                    text "repositories_url" root,
                    property "suspended_at" root
                with
                | Ok installationId,
                  Ok targetId,
                  Ok targetType,
                  Ok selection,
                  Ok account,
                  Ok permissions,
                  Ok repositoriesUrl,
                  Ok suspended ->
                    uniqueObjectMembers account
                    |> Result.bind (fun () ->
                        match text "login" account, int64 "id" account, text "node_id" account with
                        | Ok login, Ok accountId, Ok accountNodeId ->
                            let expectedRepositoriesUrl = combine options.ApiBase "installation/repositories"

                            if
                                installationId <> options.InstallationId
                                || targetId <> options.AccountId
                                || accountId <> options.AccountId
                                || accountNodeId <> options.AccountNodeId
                                || not (
                                    String.Equals(login, options.AccountLogin, StringComparison.OrdinalIgnoreCase)
                                )
                                || targetType <> "Organization"
                            then
                                Error "receiver-roster-installation-identity-drift"
                            elif selection <> "selected" then
                                Error "receiver-roster-installation-not-selected"
                            elif suspended.ValueKind <> JsonValueKind.Null then
                                Error "receiver-roster-installation-suspended"
                            elif repositoriesUrl <> expectedRepositoriesUrl.AbsoluteUri then
                                Error "receiver-roster-repositories-url-drift"
                            elif
                                options.RequiredPermissions
                                |> Map.exists (fun name level -> Map.tryFind name permissions <> Some level)
                            then
                                Error "receiver-roster-installation-permission-drift"
                            else
                                Ok
                                    {
                                        InstallationId = installationId
                                        AccountLogin = login
                                        AccountId = accountId
                                        AccountNodeId = accountNodeId
                                        RepositorySelection = selection
                                        Permissions = permissions
                                        RepositoriesUrl = repositoriesUrl
                                    }
                        | Error reason, _, _
                        | _, Error reason, _
                        | _, _, Error reason -> Error reason)
                | Error reason, _, _, _, _, _, _, _
                | _, Error reason, _, _, _, _, _, _
                | _, _, Error reason, _, _, _, _, _
                | _, _, _, Error reason, _, _, _, _
                | _, _, _, _, Error reason, _, _, _
                | _, _, _, _, _, Error reason, _, _
                | _, _, _, _, _, _, Error reason, _
                | _, _, _, _, _, _, _, Error reason -> Error reason))

    let private parseRepository (options: MigrationReceiverRosterReadOptions) (value: JsonElement) =
        uniqueObjectMembers value
        |> Result.bind (fun () ->
            match
                int64 "id" value,
                text "node_id" value,
                text "full_name" value,
                boolean "private" value,
                boolean "archived" value,
                boolean "disabled" value,
                optionalBoolMap "permissions" value
            with
            | Ok id, Ok nodeId, Ok fullName, Ok privateRepository, Ok archived, Ok disabled, Ok permissions ->
                let pieces = fullName.Split('/')

                if
                    pieces.Length <> 2
                    || not (String.Equals(pieces.[0], options.AccountLogin, StringComparison.OrdinalIgnoreCase))
                    || String.IsNullOrWhiteSpace pieces.[1]
                then
                    Error "receiver-roster-repository-scope-drift"
                elif Map.tryFind "pull" permissions = Some false then
                    Error "receiver-roster-repository-permission-drift"
                else
                    Ok
                        {
                            RosterRepositoryId = id
                            RosterRepositoryNodeId = nodeId
                            RosterRepositoryFullName = fullName
                            RosterPrivate = privateRepository
                            RosterArchived = archived
                            RosterDisabled = disabled
                            RosterPermissions = permissions
                        }
            | Error reason, _, _, _, _, _, _
            | _, Error reason, _, _, _, _, _
            | _, _, Error reason, _, _, _, _
            | _, _, _, Error reason, _, _, _
            | _, _, _, _, Error reason, _, _
            | _, _, _, _, _, Error reason, _
            | _, _, _, _, _, _, Error reason -> Error reason)

    let private parseRepositoryPage (options: MigrationReceiverRosterReadOptions) payload =
        parse payload
        |> Result.bind (fun document ->
            use document = document
            let root = document.RootElement

            uniqueObjectMembers root
            |> Result.bind (fun () ->
                match integer "total_count" root, property "repositories" root with
                | Ok total, Ok repositories when repositories.ValueKind = JsonValueKind.Array ->
                    repositories.EnumerateArray()
                    |> Seq.map (parseRepository options)
                    |> Seq.fold
                        (fun state current ->
                            state
                            |> Result.bind (fun accumulated ->
                                current |> Result.map (fun item -> item :: accumulated)))
                        (Ok [])
                    |> Result.map (fun repositories -> total, List.rev repositories)
                | Ok _, Ok _ -> Error "receiver-roster-page-shape"
                | Error reason, _
                | _, Error reason -> Error reason))

    let private exactRepositoryPage (options: MigrationReceiverRosterReadOptions) expectedPage (uri: Uri) =
        let expectedPath = "/installation/repositories"

        let pieces =
            uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)

        let values =
            pieces
            |> Array.choose (fun piece ->
                let pair = piece.Split('=', 2)

                if pair.Length = 2 then
                    Some(Uri.UnescapeDataString pair.[0], Uri.UnescapeDataString pair.[1])
                else
                    None)

        let expected = Map [ "per_page", "100"; "page", string expectedPage ]

        uri.Scheme = options.ApiBase.Scheme
        && uri.Authority = options.ApiBase.Authority
        && uri.AbsolutePath = expectedPath
        && not (uri.OriginalString.Contains('%'))
        && values.Length = pieces.Length
        && values.Length = expected.Count
        && (values |> Array.map fst |> Set.ofArray |> Set.count) = expected.Count
        && values
           |> Array.forall (fun (name, value) -> Map.tryFind name expected = Some value)

    let private scopeFingerprint (settings: MigrationReceiverScopeSettings) =
        [
            yield string settings.InstallationId
            yield settings.AccountLogin.ToLowerInvariant()
            yield string settings.AccountId
            yield settings.AccountNodeId
            yield settings.RepositorySelection
            yield settings.RepositoriesUrl
            for KeyValue(name, level) in settings.Permissions do
                yield name
                yield level
        ]
        |> List.map frame
        |> String.concat ""
        |> sha256

    let private rosterFingerprint (repositories: MigrationReceiverRosterRepository list) =
        [
            for repository in repositories do
                yield string repository.RosterRepositoryId
                yield repository.RosterRepositoryNodeId
                yield repository.RosterRepositoryFullName.ToLowerInvariant()
                yield string repository.RosterPrivate
                yield string repository.RosterArchived
                yield string repository.RosterDisabled

                for KeyValue(name, enabled) in repository.RosterPermissions do
                    yield name
                    yield string enabled
        ]
        |> List.map frame
        |> String.concat ""
        |> sha256

    let private passFingerprint
        (settings: MigrationReceiverScopeSettings)
        total
        (repositories: MigrationReceiverRosterRepository list)
        (pages: MigrationReceiverRosterRawPage list)
        =
        [
            yield scopeFingerprint settings
            yield string total
            yield rosterFingerprint repositories
            for page in pages do
                yield page.RosterRequestedUri
                yield page.RosterRequestIdentitySha256
                yield page.RosterRawSha256
                yield page.RosterNextUri |> Option.defaultValue ""
        ]
        |> List.map frame
        |> String.concat ""
        |> sha256

    let capturePass (options: MigrationReceiverRosterReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validOptions options) then
            Error "receiver-roster-invalid-options"
        else
            let installationUri =
                combine options.ApiBase $"app/installations/{options.InstallationId}"

            get options options.AppToken installationUri transport
            |> Result.bind (fun installationResponse ->
                nextLink installationResponse
                |> Result.bind (function
                    | Some _ -> Error "receiver-roster-installation-pagination-refused"
                    | None ->
                        parseScope options installationResponse.Body
                        |> Result.bind (fun settings ->
                            let installationPage = rawPage installationUri installationResponse None
                            let first = combine options.ApiBase "installation/repositories?per_page=100&page=1"

                            let rec readPages expectedPage total accumulated pages current =
                                if expectedPage > 1000 || not (exactRepositoryPage options expectedPage current) then
                                    Error "receiver-roster-pagination-continuation"
                                else
                                    get options options.InstallationToken current transport
                                    |> Result.bind (fun pageResponse ->
                                        match
                                            parseRepositoryPage options pageResponse.Body, nextLink pageResponse
                                        with
                                        | Ok(pageTotal, repositories), Ok next ->
                                            if total |> Option.exists ((<>) pageTotal) then
                                                Error "receiver-roster-total-count-drift"
                                            else
                                                let combined = accumulated @ repositories
                                                let raw = rawPage current pageResponse next

                                                match next with
                                                | Some continuation when combined.Length < pageTotal ->
                                                    readPages
                                                        (expectedPage + 1)
                                                        (Some pageTotal)
                                                        combined
                                                        (pages @ [ raw ])
                                                        continuation
                                                | Some _ -> Error "receiver-roster-unexpected-continuation"
                                                | None when combined.Length = pageTotal ->
                                                    Ok(pageTotal, combined, pages @ [ raw ])
                                                | None -> Error "receiver-roster-pagination-incomplete"
                                        | Error reason, _
                                        | _, Error reason -> Error reason)

                            readPages 1 None [] [] first
                            |> Result.bind (fun (total, repositories, repositoryPages) ->
                                let sorted = repositories |> List.sortBy _.RosterRepositoryFullName
                                let uniqueIds = sorted |> List.map _.RosterRepositoryId |> Set.ofList |> Set.count

                                let uniqueNodes =
                                    sorted |> List.map _.RosterRepositoryNodeId |> Set.ofList |> Set.count

                                let uniqueNames =
                                    sorted
                                    |> List.map (fun repository ->
                                        repository.RosterRepositoryFullName.ToLowerInvariant())
                                    |> Set.ofList
                                    |> Set.count

                                if
                                    uniqueIds <> sorted.Length
                                    || uniqueNodes <> sorted.Length
                                    || uniqueNames <> sorted.Length
                                then
                                    Error "receiver-roster-duplicate-repository"
                                else
                                    let pages = installationPage :: repositoryPages

                                    Ok
                                        {
                                            ScopeSettings = settings
                                            RepositoryTotalCount = total
                                            Repositories = sorted
                                            Pages = pages
                                            PassFingerprint = passFingerprint settings total sorted pages
                                        }))))

    let capturePassForMintedToken
        (options: MigrationReceiverRosterReadOptions)
        expectedTokenSha256
        (transport: IMigrationGitHubReadTransport) =
        if String.IsNullOrWhiteSpace expectedTokenSha256
           || sha256 options.InstallationToken <> expectedTokenSha256 then
            Error "receiver-roster-minted-token-binding"
        else capturePass options transport

    let captureTwoPass (options: MigrationReceiverRosterReadOptions) (transport: IMigrationGitHubReadTransport) =
        capturePass options transport
        |> Result.bind (fun first ->
            capturePass options transport
            |> Result.bind (fun second ->
                if first <> second || first.PassFingerprint <> second.PassFingerprint then
                    Error "receiver-roster-pass-drift"
                else
                    Ok
                        {
                            First = first
                            Second = second
                            CaptureFingerprint = sha256 (first.PassFingerprint + "\n" + second.PassFingerprint)
                        }))

    let assessCallerCohort
        isolated
        (declaredRepositories: MigrationReceiverRosterDeclaredRepository list)
        (receiverRepositoryIds: int64 list)
        (capture: MigrationReceiverRosterCapture)
        =
        if
            capture.First <> capture.Second
            || capture.First.PassFingerprint <> capture.Second.PassFingerprint
            || capture.CaptureFingerprint
               <> sha256 (capture.First.PassFingerprint + "\n" + capture.Second.PassFingerprint)
        then
            Error "receiver-roster-capture-drift"
        elif not isolated || declaredRepositories.IsEmpty || receiverRepositoryIds.IsEmpty then
            Error "receiver-roster-invalid-cohort"
        else
            let provider =
                capture.First.Repositories |> List.map _.RosterRepositoryId |> Set.ofList

            let declaredRepositoryIds =
                declaredRepositories |> List.map _.DeclaredRepositoryId |> Set.ofList

            let receiverRepositories = receiverRepositoryIds |> Set.ofList

            if
                declaredRepositoryIds.Count <> declaredRepositories.Length
                || receiverRepositories <> provider
                || declaredRepositoryIds <> provider
            then
                Error "receiver-roster-cohort-scope-mismatch"
            else
                let expectedById =
                    declaredRepositories
                    |> List.map (fun repository -> repository.DeclaredRepositoryId, repository)
                    |> Map.ofList

                let identityMismatch =
                    capture.First.Repositories
                    |> List.exists (fun repository ->
                        match Map.tryFind repository.RosterRepositoryId expectedById with
                        | None -> true
                        | Some expected ->
                            expected.DeclaredRepositoryNodeId <> repository.RosterRepositoryNodeId
                            || not (
                                String.Equals(
                                    expected.DeclaredRepositoryFullName,
                                    repository.RosterRepositoryFullName,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            ))

                if identityMismatch then
                    Error "receiver-roster-cohort-identity-mismatch"
                else
                    Ok
                        {
                            TokenScopeExhaustive = true
                            CohortRepositoriesMatchScope = true
                            CallerReceiverRosterProtected = false
                            ReceiverIdentityComplete = false
                            ScopedSettingsSha256 = scopeFingerprint capture.First.ScopeSettings
                            RepositoryRosterSha256 = rosterFingerprint capture.First.Repositories
                        }
