namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json

type ImmutableReleasesOrganizationPolicy =
    | AllRepositories
    | NoRepositories
    | SelectedRepositories

type ImmutableReleasesRepositoryIdentity =
    { RepositoryId: int64
      RepositoryNodeId: string
      FullName: string }

type ImmutableReleasesSelectedRepository =
    { RepositoryId: int64
      RepositoryNodeId: string
      FullName: string }

type ImmutableReleasesRawPage =
    { ImmutableRequestedUri: string
      ImmutableRequestIdentitySha256: string
      ImmutableRawBody: string
      ImmutableRawSha256: string
      ImmutableNextUri: string option }

type ImmutableReleasesPass =
    { Identity: ImmutableReleasesRepositoryIdentity
      RepositoryEnabled: bool
      EnforcedByOwner: bool
      OrganizationPolicy: ImmutableReleasesOrganizationPolicy
      SelectedRepositories: ImmutableReleasesSelectedRepository list
      SelectedTotalCount: int option
      EffectiveEnabled: bool
      ImmutableReleasePages: ImmutableReleasesRawPage list
      Fingerprint: string }

type ImmutableReleasesCapture =
    { First: ImmutableReleasesPass
      Second: ImmutableReleasesPass
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationImmutableReleasesRead =
    let private sha256Bytes (bytes: byte array) =
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let private sha256 (value: string) = value |> Encoding.UTF8.GetBytes |> sha256Bytes

    let private nonempty (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private combine (baseUri: Uri) (relative: string) = Uri(baseUri, relative)
    let private escaped (value: string) = Uri.EscapeDataString value

    let private identityUri (options: MigrationGitHubReadOptions) =
        combine options.ApiBase $"repos/{escaped options.Owner}/{escaped options.Repository}"

    let private repositoryPolicyUri (options: MigrationGitHubReadOptions) =
        combine options.ApiBase $"repos/{escaped options.Owner}/{escaped options.Repository}/immutable-releases"

    let private organizationPolicyUri (options: MigrationGitHubReadOptions) =
        combine options.ApiBase $"orgs/{escaped options.Owner}/settings/immutable-releases"

    let private selectedUri (options: MigrationGitHubReadOptions) page =
        combine options.ApiBase $"orgs/{escaped options.Owner}/settings/immutable-releases/repositories?per_page=100&page={page}"

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase)
        && options.ApiBase.IsAbsoluteUri
        && (options.ApiBase.Scheme = Uri.UriSchemeHttps || options.ApiBase.Host = "localhost")
        && nonempty options.Owner && nonempty options.Repository
        && options.ExpectedRepositoryId > 0L && nonempty options.Token && nonempty options.UserAgent

    let private sameAuthority (left: Uri) (right: Uri) =
        left.Scheme = right.Scheme && left.Host = right.Host && left.Port = right.Port

    let private trySelectedPage (options: MigrationGitHubReadOptions) (uri: Uri) =
        let root = selectedUri options 1
        if not uri.IsAbsoluteUri || not (sameAuthority root uri) || uri.AbsolutePath <> root.AbsolutePath then None
        elif uri.Query.Contains("%", StringComparison.Ordinal) then None
        else
            let entries =
                uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun item -> item.Split('=', 2))
            if entries.Length <> 2 || entries |> Array.exists (fun item -> item.Length <> 2) then None
            else
                let values = entries |> Array.map (fun item -> item.[0], item.[1]) |> Map.ofArray
                if values.Count <> 2 || values |> Map.tryFind "per_page" <> Some "100" then None
                else
                    match values |> Map.tryFind "page" with
                    | Some pageText ->
                        match Int32.TryParse pageText with
                        | true, page when page > 0 && uri.AbsoluteUri = (selectedUri options page).AbsoluteUri -> Some page
                        | _ -> None
                    | None -> None

    let private allowedUri options uri =
        uri = identityUri options || uri = repositoryPolicyUri options
        || uri = organizationPolicyUri options || (trySelectedPage options uri |> Option.isSome)

    let guardReadTransport (options: MigrationGitHubReadOptions) (inner: IMigrationGitHubReadTransport) =
        { new IMigrationGitHubReadTransport with
            member _.Send request =
                match request with
                | Rest value when value.Method = Get && value.Body.IsNone && value.Idempotency = ReplaySafe
                                  && ApiVersion.value value.ApiVersion = ApiVersion.value ApiVersion.required
                                  && allowedUri options value.Uri -> inner.Send request
                | _ -> NetworkFailure }

    let private headers (options: MigrationGitHubReadOptions) =
        Map [ "Accept", "application/vnd.github+json"; "Authorization", "Bearer " + options.Token
              "User-Agent", options.UserAgent ]

    let private request (options: MigrationGitHubReadOptions) (uri: Uri) =
        Rest { Method=Get; Uri=uri; Headers=headers options; Body=None
               ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }

    let private send (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (uri: Uri) =
        match transport.Send(request options uri) with
        | Response response when response.StatusCode = 200 -> Ok response
        | Response response -> Error $"immutable-releases-read-unknown:http-{response.StatusCode}"
        | NetworkFailure -> Error "immutable-releases-read-unknown:network"
        | TimedOut -> Error "immutable-releases-read-unknown:timeout"

    let rec private noDuplicateMembers (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let names = HashSet<string>(StringComparer.Ordinal)
            value.EnumerateObject() |> Seq.forall (fun item -> names.Add item.Name && noDuplicateMembers item.Value)
        | JsonValueKind.Array -> value.EnumerateArray() |> Seq.forall noDuplicateMembers
        | _ -> true

    let private parse (body: string) =
        try
            let document = JsonDocument.Parse body
            if noDuplicateMembers document.RootElement then Ok document
            else document.Dispose(); Error "duplicate-json-member"
        with :? JsonException -> Error "malformed-json"

    let private property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &found) then Ok found
        else Error $"missing:{name}"

    let private stringProperty name value =
        property name value |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.String then
                let text = item.GetString()
                if nonempty text then Ok text else Error $"invalid:{name}"
            else Error $"invalid:{name}")

    let private int64Property name value =
        property name value |> Result.bind (fun item ->
            match item.TryGetInt64() with true, number when number > 0L -> Ok number | _ -> Error $"invalid:{name}")

    let private intProperty name value =
        property name value |> Result.bind (fun item ->
            match item.TryGetInt32() with true, number when number >= 0 -> Ok number | _ -> Error $"invalid:{name}")

    let private boolProperty name value =
        property name value |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.True -> Ok true | JsonValueKind.False -> Ok false | _ -> Error $"invalid:{name}")

    let private rootObject (body: string) (parser: JsonElement -> Result<'a, string>) =
        parse body |> Result.bind (fun document ->
            use owned = document
            if owned.RootElement.ValueKind <> JsonValueKind.Object then Error "expected-object"
            else parser owned.RootElement)

    let private parseIdentity (body: string) : Result<ImmutableReleasesRepositoryIdentity, string> =
        rootObject body (fun root ->
            match int64Property "id" root, stringProperty "node_id" root, stringProperty "full_name" root with
            | Ok id, Ok node, Ok full -> Ok { RepositoryId=id; RepositoryNodeId=node; FullName=full }
            | _ -> Error "identity-shape")

    let private parseRepositoryPolicy body =
        rootObject body (fun root ->
            match boolProperty "enabled" root, boolProperty "enforced_by_owner" root with
            | Ok enabled, Ok enforced -> Ok(enabled, enforced)
            | _ -> Error "repository-policy-shape")

    let private parseOrganizationPolicy body =
        rootObject body (fun root ->
            match stringProperty "enforced_repositories" root with
            | Ok "all" -> Ok AllRepositories | Ok "none" -> Ok NoRepositories
            | Ok "selected" -> Ok SelectedRepositories | _ -> Error "organization-policy-shape")

    let private parseSelectedPage (body: string) : Result<int * ImmutableReleasesSelectedRepository list, string> =
        rootObject body (fun root ->
            match intProperty "total_count" root, property "repositories" root with
            | Ok total, Ok repositories when repositories.ValueKind = JsonValueKind.Array ->
                repositories.EnumerateArray()
                |> Seq.map (fun item ->
                    match int64Property "id" item, stringProperty "node_id" item, stringProperty "full_name" item with
                    | Ok id, Ok node, Ok full -> Ok { RepositoryId=id; RepositoryNodeId=node; FullName=full }
                    | _ -> Error "selected-repository-shape")
                |> Seq.fold (fun state item ->
                    match state, item with
                    | Ok items, Ok value -> Ok(value :: items)
                    | Error reason, _ | _, Error reason -> Error reason) (Ok [])
                |> Result.map (fun items -> total, List.rev items)
            | _ -> Error "selected-page-shape")

    let private nextLink (response: ResponseEnvelope) =
        match response.Headers |> Map.tryFind "Link" with
        | None -> Ok None
        | Some value when value.Contains("%", StringComparison.Ordinal) -> Error "pagination-escaped-continuation"
        | Some value -> Transport.tryNextLink value |> Result.mapError (fun _ -> "pagination-link")

    let private rawPage (uri: Uri) (response: ResponseEnvelope) (next: Uri option) : ImmutableReleasesRawPage =
        let requestIdentity = sha256 $"GET\n{uri.AbsoluteUri}\n{ApiVersion.value ApiVersion.required}\n"
        { ImmutableRequestedUri=uri.AbsoluteUri; ImmutableRequestIdentitySha256=requestIdentity
          ImmutableRawBody=response.Body; ImmutableRawSha256=sha256 response.Body
          ImmutableNextUri=next |> Option.map _.AbsoluteUri }

    let private passFingerprint (identity: ImmutableReleasesRepositoryIdentity) repositoryEnabled enforced policy
                                (selected: ImmutableReleasesSelectedRepository list) total effective
                                (pages: ImmutableReleasesRawPage list) =
        let policyText = match policy with AllRepositories -> "all" | NoRepositories -> "none" | SelectedRepositories -> "selected"
        [ $"{identity.RepositoryId}|{identity.RepositoryNodeId}|{identity.FullName}"
          $"{repositoryEnabled}|{enforced}|{policyText}|{total}|{effective}"
          yield! selected |> List.sortBy _.RepositoryId |> List.map (fun item -> $"{item.RepositoryId}|{item.RepositoryNodeId}|{item.FullName}")
          yield! pages |> List.map (fun page ->
              let next = page.ImmutableNextUri |> Option.defaultValue "-"
              $"{page.ImmutableRequestedUri}|{page.ImmutableRequestIdentitySha256}|{page.ImmutableRawSha256}|{next}") ]
        |> String.concat "\n" |> sha256

    let private reconcile (identity: ImmutableReleasesRepositoryIdentity) enabled enforced policy
                          (selected: ImmutableReleasesSelectedRepository list) =
        let memberById = selected |> List.exists (fun item -> item.RepositoryId = identity.RepositoryId)
        let memberByNode = selected |> List.exists (fun item -> item.RepositoryNodeId = identity.RepositoryNodeId)
        if memberById <> memberByNode then Error "selected-membership-identity-drift"
        else
            match policy with
            | AllRepositories when enabled && enforced -> Ok true
            | AllRepositories -> Error "inheritance-contradiction:all"
            | SelectedRepositories when memberById && enabled && enforced -> Ok true
            | SelectedRepositories when memberById -> Error "inheritance-contradiction:selected"
            | SelectedRepositories when enforced -> Error "inheritance-contradiction:unselected"
            | SelectedRepositories -> Ok enabled
            | NoRepositories when enforced -> Error "inheritance-contradiction:none"
            | NoRepositories -> Ok enabled

    let private ensureIdentity (options: MigrationGitHubReadOptions) (identity: ImmutableReleasesRepositoryIdentity) =
        if identity.RepositoryId <> options.ExpectedRepositoryId
           || identity.FullName <> options.Owner + "/" + options.Repository
           || not (nonempty identity.RepositoryNodeId) then Error "repository-identity-drift"
        else Ok identity

    let private validateSelectedPages (options: MigrationGitHubReadOptions) (pages: ImmutableReleasesRawPage list) =
        let rec loop expectedPage expectedTotal accumulated remaining =
            match remaining with
            | [] -> Error "selected-pagination-empty"
            | page :: tail ->
                match trySelectedPage options (Uri page.ImmutableRequestedUri), parseSelectedPage page.ImmutableRawBody with
                | Some actualPage, Ok(total, entries) when actualPage = expectedPage ->
                    let requestIdentity = sha256 $"GET\n{page.ImmutableRequestedUri}\n{ApiVersion.value ApiVersion.required}\n"
                    if page.ImmutableRequestIdentitySha256 <> requestIdentity then Error "request-identity-drift"
                    elif page.ImmutableRawSha256 <> sha256 page.ImmutableRawBody then Error "raw-hash-drift"
                    elif expectedTotal |> Option.exists ((<>) total) then Error "selected-total-drift"
                    else
                        let combined = accumulated @ entries
                        match page.ImmutableNextUri, tail with
                        | Some next, _ ->
                            match trySelectedPage options (Uri next) with
                            | Some nextPage when nextPage = expectedPage + 1 -> loop nextPage (Some total) combined tail
                            | _ -> Error "selected-pagination-continuation"
                        | None, [] when combined.Length = total -> Ok(total, combined)
                        | None, [] -> Error "selected-pagination-incomplete"
                        | None, _ -> Error "selected-pagination-lost-terminal"
                | Some _, Ok _ -> Error "selected-pagination-nonconsecutive"
                | _, Error reason -> Error reason
                | _ -> Error "selected-pagination-request"
        loop 1 None [] pages

    let validatePass (options: MigrationGitHubReadOptions) (observed: ImmutableReleasesPass) =
        if not (validOptions options) then Error "immutable-releases-invalid-options"
        elif observed.ImmutableReleasePages.Length < 3 then Error "immutable-releases-capture-shape"
        else
            let identityPage, repositoryPage, organizationPage = observed.ImmutableReleasePages.[0], observed.ImmutableReleasePages.[1], observed.ImmutableReleasePages.[2]
            if identityPage.ImmutableRequestedUri <> (identityUri options).AbsoluteUri
               || repositoryPage.ImmutableRequestedUri <> (repositoryPolicyUri options).AbsoluteUri
               || organizationPage.ImmutableRequestedUri <> (organizationPolicyUri options).AbsoluteUri
               || ([ identityPage; repositoryPage; organizationPage ] |> List.exists (fun page -> page.ImmutableNextUri.IsSome))
               || (observed.ImmutableReleasePages |> List.exists (fun page ->
                    page.ImmutableRequestIdentitySha256
                    <> sha256 $"GET\n{page.ImmutableRequestedUri}\n{ApiVersion.value ApiVersion.required}\n"
                    || page.ImmutableRawSha256 <> sha256 page.ImmutableRawBody)) then
                Error "immutable-releases-capture-shape"
            else
                match parseIdentity identityPage.ImmutableRawBody, parseRepositoryPolicy repositoryPage.ImmutableRawBody, parseOrganizationPolicy organizationPage.ImmutableRawBody with
                | Ok identity, Ok(enabled, enforced), Ok policy ->
                    ensureIdentity options identity |> Result.bind (fun identity ->
                        let selectedResult =
                            match policy, observed.ImmutableReleasePages |> List.skip 3 with
                            | SelectedRepositories, pages -> validateSelectedPages options pages |> Result.map (fun (total, items) -> Some total, items)
                            | _, [] -> Ok(None, [])
                            | _ -> Error "unexpected-selected-roster"
                        selectedResult |> Result.bind (fun (total, selected) ->
                            let duplicateId = selected |> List.groupBy _.RepositoryId |> List.exists (fun (_, items) -> items.Length > 1)
                            let duplicateNode = selected |> List.groupBy _.RepositoryNodeId |> List.exists (fun (_, items) -> items.Length > 1)
                            if duplicateId || duplicateNode then Error "duplicate-selected-repository"
                            else
                                reconcile identity enabled enforced policy selected |> Result.bind (fun effective ->
                                    if observed.Identity <> identity || observed.RepositoryEnabled <> enabled
                                       || observed.EnforcedByOwner <> enforced || observed.OrganizationPolicy <> policy
                                       || observed.SelectedRepositories <> selected || observed.SelectedTotalCount <> total
                                       || observed.EffectiveEnabled <> effective then Error "raw-typed-drift"
                                    else
                                        let fingerprint = passFingerprint identity enabled enforced policy selected total effective observed.ImmutableReleasePages
                                        if observed.Fingerprint <> fingerprint then Error "pass-fingerprint-drift" else Ok observed)))
                | Error reason, _, _ | _, Error reason, _ | _, _, Error reason -> Error reason

    let capturePass (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validOptions options) then Error "immutable-releases-invalid-options"
        else
            let guarded = guardReadTransport options transport
            let readSingleton uri parser =
                send options guarded uri |> Result.bind (fun response ->
                    nextLink response |> Result.bind (function
                        | Some _ -> Error "singleton-pagination-refused"
                        | None -> parser response.Body |> Result.map (fun typed -> typed, rawPage uri response None)))
            readSingleton (identityUri options) parseIdentity |> Result.bind (fun (identity, identityRaw) ->
                ensureIdentity options identity |> Result.bind (fun identity ->
                    readSingleton (repositoryPolicyUri options) parseRepositoryPolicy |> Result.bind (fun ((enabled, enforced), repositoryRaw) ->
                        readSingleton (organizationPolicyUri options) parseOrganizationPolicy |> Result.bind (fun (policy, organizationRaw) ->
                            let rec readSelected page total accumulated raw =
                                let uri = selectedUri options page
                                send options guarded uri |> Result.bind (fun response ->
                                    match parseSelectedPage response.Body, nextLink response with
                                    | Ok(pageTotal, entries), Ok next ->
                                        if total |> Option.exists ((<>) pageTotal) then Error "selected-total-drift"
                                        else
                                            let combined = accumulated @ entries
                                            let evidence = rawPage uri response next
                                            match next with
                                            | None when combined.Length = pageTotal -> Ok(Some pageTotal, combined, raw @ [ evidence ])
                                            | None -> Error "selected-pagination-incomplete"
                                            | Some continuation ->
                                                match trySelectedPage options continuation with
                                                | Some nextPage when nextPage = page + 1 -> readSelected nextPage (Some pageTotal) combined (raw @ [ evidence ])
                                                | _ -> Error "selected-pagination-continuation"
                                    | Error reason, _ -> Error reason | _, Error reason -> Error reason)
                            let selectedResult = match policy with SelectedRepositories -> readSelected 1 None [] [] | _ -> Ok(None, [], [])
                            selectedResult |> Result.bind (fun (total, selected, selectedRaw) ->
                                let duplicateId = selected |> List.groupBy _.RepositoryId |> List.exists (fun (_, items) -> items.Length > 1)
                                let duplicateNode = selected |> List.groupBy _.RepositoryNodeId |> List.exists (fun (_, items) -> items.Length > 1)
                                if duplicateId || duplicateNode then Error "duplicate-selected-repository"
                                else
                                    reconcile identity enabled enforced policy selected |> Result.bind (fun effective ->
                                        let pages = [ identityRaw; repositoryRaw; organizationRaw ] @ selectedRaw
                                        let fingerprint = passFingerprint identity enabled enforced policy selected total effective pages
                                        { Identity=identity; RepositoryEnabled=enabled; EnforcedByOwner=enforced
                                          OrganizationPolicy=policy; SelectedRepositories=selected; SelectedTotalCount=total
                                          EffectiveEnabled=effective; ImmutableReleasePages=pages; Fingerprint=fingerprint }
                                        |> validatePass options))))))

    let captureTwoPass (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        capturePass options transport |> Result.bind (fun first ->
            capturePass options transport |> Result.bind (fun second ->
                if first.Fingerprint <> second.Fingerprint || first <> second then Error "immutable-releases-pass-drift"
                else Ok { First=first; Second=second; Fingerprint=sha256 (first.Fingerprint + "\n" + second.Fingerprint) }))
