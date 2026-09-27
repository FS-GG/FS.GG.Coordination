namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json

type EnvironmentCensusRepositoryIdentity =
    {
        EnvironmentRepositoryId: int64
        EnvironmentRepositoryNodeId: string
        EnvironmentRepositoryFullName: string
    }

type EnvironmentCensusItem =
    {
        EnvironmentId: int64
        EnvironmentNodeId: string
        EnvironmentName: string
    }

type EnvironmentCensusRawPage =
    {
        EnvironmentRequestedUri: string
        EnvironmentRequestIdentitySha256: string
        EnvironmentRawBody: string
        EnvironmentRawSha256: string
        EnvironmentNextUri: string option
    }

type EnvironmentCensusPass =
    {
        EnvironmentRepository: EnvironmentCensusRepositoryIdentity
        EnvironmentTotalCount: int
        Environments: EnvironmentCensusItem list
        EnvironmentPages: EnvironmentCensusRawPage list
        EnvironmentFingerprint: string
    }

type EnvironmentCensusCapture =
    {
        EnvironmentFirst: EnvironmentCensusPass
        EnvironmentSecond: EnvironmentCensusPass
        EnvironmentCaptureFingerprint: string
    }

[<RequireQualifiedAccess>]
module MigrationEnvironmentCensusRead =
    let private sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private nonempty (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private combine (baseUri: Uri) (relative: string) = Uri(baseUri, relative)
    let private escaped (value: string) = Uri.EscapeDataString value

    let private identityUri (options: MigrationGitHubReadOptions) =
        combine options.ApiBase $"repos/{escaped options.Owner}/{escaped options.Repository}"

    let private firstEnvironmentUri (options: MigrationGitHubReadOptions) =
        combine options.ApiBase $"repos/{escaped options.Owner}/{escaped options.Repository}/environments?per_page=100"

    let private sameAuthority (left: Uri) (right: Uri) =
        left.Scheme = right.Scheme && left.Host = right.Host && left.Port = right.Port

    let private tryEnvironmentPage (options: MigrationGitHubReadOptions) (uri: Uri) =
        let first = firstEnvironmentUri options

        if
            isNull uri
            || not uri.IsAbsoluteUri
            || not (sameAuthority first uri)
            || uri.AbsolutePath <> first.AbsolutePath
            || not (String.IsNullOrEmpty uri.UserInfo)
            || not (String.IsNullOrEmpty uri.Fragment)
        then
            None
        elif uri.AbsoluteUri = first.AbsoluteUri then
            Some 1
        elif uri.Query.Contains("%", StringComparison.Ordinal) then
            None
        else
            let entries =
                uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun entry -> entry.Split('=', 2))

            if entries.Length <> 2 || entries |> Array.exists (fun entry -> entry.Length <> 2) then
                None
            else
                let values = entries |> Array.map (fun entry -> entry.[0], entry.[1]) |> Map.ofArray

                match values.Count, values |> Map.tryFind "per_page", values |> Map.tryFind "page" with
                | 2, Some "100", Some pageText ->
                    match Int32.TryParse pageText with
                    | true, page when page > 1 && pageText = string page -> Some page
                    | _ -> None
                | _ -> None

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase)
        && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && nonempty options.Owner
        && nonempty options.Repository
        && options.ExpectedRepositoryId > 0L
        && nonempty options.Token
        && nonempty options.UserAgent

    let private allowedUri options uri =
        uri = identityUri options || tryEnvironmentPage options uri |> Option.isSome

    let guardReadTransport (options: MigrationGitHubReadOptions) (inner: IMigrationGitHubReadTransport) =
        { new IMigrationGitHubReadTransport with
            member _.Send request =
                match request with
                | Rest value when
                    validOptions options
                    && value.Method = Get
                    && value.Body.IsNone
                    && value.Idempotency = ReplaySafe
                    && ApiVersion.value value.ApiVersion = ApiVersion.value ApiVersion.required
                    && allowedUri options value.Uri
                    ->
                    inner.Send request
                | _ -> NetworkFailure
        }

    let private headers (options: MigrationGitHubReadOptions) =
        Map
            [
                "Accept", "application/vnd.github+json"
                "Authorization", "Bearer " + options.Token
                "User-Agent", options.UserAgent
            ]

    let private request (options: MigrationGitHubReadOptions) uri =
        Rest
            {
                Method = Get
                Uri = uri
                Headers = headers options
                Body = None
                ApiVersion = ApiVersion.required
                Idempotency = ReplaySafe
            }

    let private send options (transport: IMigrationGitHubReadTransport) uri =
        match transport.Send(request options uri) with
        | Response response when response.StatusCode = 200 -> Ok response
        | Response response -> Error $"environment-census-read-unknown:http-{response.StatusCode}"
        | NetworkFailure -> Error "environment-census-read-unknown:network"
        | TimedOut -> Error "environment-census-read-unknown:timeout"

    let rec private noDuplicateMembers (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let names = HashSet<string>(StringComparer.Ordinal)

            value.EnumerateObject()
            |> Seq.forall (fun property -> names.Add property.Name && noDuplicateMembers property.Value)
        | JsonValueKind.Array -> value.EnumerateArray() |> Seq.forall noDuplicateMembers
        | _ -> true

    let private parse (body: string) =
        try
            let document = JsonDocument.Parse body

            if noDuplicateMembers document.RootElement then
                Ok document
            else
                document.Dispose()
                Error "duplicate-json-member"
        with :? JsonException ->
            Error "malformed-json"

    let private property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>

        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &found) then
            Ok found
        else
            Error $"missing:{name}"

    let private stringProperty (name: string) (value: JsonElement) =
        property name value
        |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.String then
                let text = item.GetString()
                if nonempty text then Ok text else Error $"invalid:{name}"
            else
                Error $"invalid:{name}")

    let private int64Property (name: string) (value: JsonElement) =
        property name value
        |> Result.bind (fun item ->
            if item.ValueKind <> JsonValueKind.Number then
                Error $"invalid:{name}"
            else
                match item.TryGetInt64() with
                | true, number when number > 0L -> Ok number
                | _ -> Error $"invalid:{name}")

    let private intProperty (name: string) (value: JsonElement) =
        property name value
        |> Result.bind (fun item ->
            if item.ValueKind <> JsonValueKind.Number then
                Error $"invalid:{name}"
            else
                match item.TryGetInt32() with
                | true, number when number >= 0 -> Ok number
                | _ -> Error $"invalid:{name}")

    let private rootObject (body: string) (parser: JsonElement -> Result<'a, string>) =
        parse body
        |> Result.bind (fun document ->
            use owned = document

            if owned.RootElement.ValueKind = JsonValueKind.Object then
                parser owned.RootElement
            else
                Error "expected-object")

    let private parseIdentity body : Result<EnvironmentCensusRepositoryIdentity, string> =
        rootObject body (fun root ->
            match int64Property "id" root, stringProperty "node_id" root, stringProperty "full_name" root with
            | Ok id, Ok node, Ok fullName ->
                Ok
                    {
                        EnvironmentRepositoryId = id
                        EnvironmentRepositoryNodeId = node
                        EnvironmentRepositoryFullName = fullName
                    }
            | _ -> Error "environment-repository-identity-shape")

    let private parseEnvironment (value: JsonElement) : Result<EnvironmentCensusItem, string> =
        match int64Property "id" value, stringProperty "node_id" value, stringProperty "name" value with
        | Ok id, Ok node, Ok name ->
            Ok
                {
                    EnvironmentId = id
                    EnvironmentNodeId = node
                    EnvironmentName = name
                }
        | _ -> Error "environment-identity-shape"

    let private parseEnvironmentPage body : Result<int * EnvironmentCensusItem list, string> =
        rootObject body (fun root ->
            match intProperty "total_count" root, property "environments" root with
            | Ok total, Ok environments when environments.ValueKind = JsonValueKind.Array ->
                environments.EnumerateArray()
                |> Seq.map parseEnvironment
                |> Seq.fold
                    (fun state item ->
                        match state, item with
                        | Ok items, Ok value -> Ok(value :: items)
                        | Error reason, _
                        | _, Error reason -> Error reason)
                    (Ok [])
                |> Result.map (fun items -> total, List.rev items)
            | _ -> Error "environment-page-shape")

    let private ensureIdentity (options: MigrationGitHubReadOptions) (identity: EnvironmentCensusRepositoryIdentity) =
        if
            identity.EnvironmentRepositoryId <> options.ExpectedRepositoryId
            || identity.EnvironmentRepositoryFullName
               <> options.Owner + "/" + options.Repository
            || not (nonempty identity.EnvironmentRepositoryNodeId)
        then
            Error "environment-repository-identity-drift"
        else
            Ok identity

    let private nextLink (response: ResponseEnvelope) =
        let links =
            response.Headers
            |> Map.toList
            |> List.choose (fun (name, value) ->
                if name.Equals("Link", StringComparison.OrdinalIgnoreCase) then
                    Some value
                else
                    None)

        match links with
        | [] -> Ok None
        | [ value ] when value.Contains("%", StringComparison.Ordinal) ->
            Error "environment-pagination-escaped-continuation"
        | [ value ] ->
            Transport.tryNextLink value
            |> Result.mapError (fun _ -> "environment-pagination-link")
        | _ -> Error "environment-pagination-link"

    let private requestIdentity uri =
        sha256 $"GET\n{uri}\n{ApiVersion.value ApiVersion.required}\n"

    let private rawPage (uri: Uri) (response: ResponseEnvelope) (next: Uri option) =
        {
            EnvironmentRequestedUri = uri.AbsoluteUri
            EnvironmentRequestIdentitySha256 = requestIdentity uri.AbsoluteUri
            EnvironmentRawBody = response.Body
            EnvironmentRawSha256 = sha256 response.Body
            EnvironmentNextUri = next |> Option.map _.AbsoluteUri
        }

    let private duplicateEnvironmentIdentity environments =
        (environments
         |> List.groupBy _.EnvironmentId
         |> List.exists (fun (_, values) -> values.Length > 1))
        || (environments
            |> List.groupBy _.EnvironmentNodeId
            |> List.exists (fun (_, values) -> values.Length > 1))
        || (environments
            |> List.groupBy _.EnvironmentName
            |> List.exists (fun (_, values) -> values.Length > 1))

    let private passFingerprint identity total environments pages =
        [
            $"{identity.EnvironmentRepositoryId}|{identity.EnvironmentRepositoryNodeId}|{identity.EnvironmentRepositoryFullName}"
            $"total={total}"
            yield!
                environments
                |> List.map (fun item -> $"{item.EnvironmentId}|{item.EnvironmentNodeId}|{item.EnvironmentName}")
            yield!
                pages
                |> List.map (fun page ->
                    let next = page.EnvironmentNextUri |> Option.defaultValue "-"
                    $"{page.EnvironmentRequestedUri}|{page.EnvironmentRequestIdentitySha256}|{page.EnvironmentRawSha256}|{next}")
        ]
        |> String.concat "\n"
        |> sha256

    let private parseRequestedUri value =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri -> Some uri
        | _ -> None

    let private validateEnvironmentPages options pages =
        let rec loop expectedPage expectedTotal accumulated remaining =
            match remaining with
            | [] -> Error "environment-pagination-empty"
            | page :: tail ->
                match parseRequestedUri page.EnvironmentRequestedUri with
                | None -> Error "environment-pagination-request"
                | Some uri ->
                    match tryEnvironmentPage options uri, parseEnvironmentPage page.EnvironmentRawBody with
                    | Some actualPage, Ok(total, entries) when actualPage = expectedPage ->
                        if
                            page.EnvironmentRequestIdentitySha256
                            <> requestIdentity page.EnvironmentRequestedUri
                        then
                            Error "environment-request-identity-drift"
                        elif page.EnvironmentRawSha256 <> sha256 page.EnvironmentRawBody then
                            Error "environment-raw-hash-drift"
                        elif expectedTotal |> Option.exists ((<>) total) then
                            Error "environment-total-count-drift"
                        else
                            let combined = accumulated @ entries

                            match page.EnvironmentNextUri, tail with
                            | Some _, _ when combined.Length >= total ->
                                Error "environment-pagination-unexpected-continuation"
                            | Some next, nextPage :: _ when nextPage.EnvironmentRequestedUri <> next ->
                                Error "environment-pagination-continuation-drift"
                            | Some next, _ ->
                                match parseRequestedUri next with
                                | Some nextUri when tryEnvironmentPage options nextUri = Some(expectedPage + 1) ->
                                    loop (expectedPage + 1) (Some total) combined tail
                                | _ -> Error "environment-pagination-continuation"
                            | None, [] when combined.Length = total -> Ok(total, combined)
                            | None, [] -> Error "environment-pagination-incomplete"
                            | None, _ -> Error "environment-pagination-lost-terminal"
                    | Some _, Ok _ -> Error "environment-pagination-nonconsecutive"
                    | _, Error reason -> Error reason
                    | _ -> Error "environment-pagination-request"

        loop 1 None [] pages

    let validatePass (options: MigrationGitHubReadOptions) (observed: EnvironmentCensusPass) =
        if not (validOptions options) then
            Error "environment-census-invalid-options"
        elif isNull (box observed.EnvironmentPages) || observed.EnvironmentPages.Length < 2 then
            Error "environment-census-capture-shape"
        else
            let identityPage = observed.EnvironmentPages.Head
            let environmentPages = observed.EnvironmentPages.Tail

            let allHashesValid =
                observed.EnvironmentPages
                |> List.forall (fun page ->
                    page.EnvironmentRequestIdentitySha256 = requestIdentity page.EnvironmentRequestedUri
                    && page.EnvironmentRawSha256 = sha256 page.EnvironmentRawBody)

            if
                identityPage.EnvironmentRequestedUri <> (identityUri options).AbsoluteUri
                || identityPage.EnvironmentNextUri.IsSome
                || not allHashesValid
            then
                Error "environment-census-capture-shape"
            else
                match
                    parseIdentity identityPage.EnvironmentRawBody, validateEnvironmentPages options environmentPages
                with
                | Ok identity, Ok(total, environments) ->
                    ensureIdentity options identity
                    |> Result.bind (fun identity ->
                        if duplicateEnvironmentIdentity environments then
                            Error "duplicate-environment-identity"
                        elif
                            observed.EnvironmentRepository <> identity
                            || observed.EnvironmentTotalCount <> total
                            || observed.Environments <> environments
                        then
                            Error "environment-raw-typed-drift"
                        else
                            let fingerprint =
                                passFingerprint identity total environments observed.EnvironmentPages

                            if observed.EnvironmentFingerprint <> fingerprint then
                                Error "environment-pass-fingerprint-drift"
                            else
                                Ok observed)
                | Error reason, _
                | _, Error reason -> Error reason

    let capturePass (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validOptions options) then
            Error "environment-census-invalid-options"
        else
            let guarded = guardReadTransport options transport
            let identityUriValue = identityUri options

            send options guarded identityUriValue
            |> Result.bind (fun identityResponse ->
                nextLink identityResponse
                |> Result.bind (function
                    | Some _ -> Error "environment-identity-pagination-refused"
                    | None ->
                        parseIdentity identityResponse.Body
                        |> Result.bind (fun identity ->
                            ensureIdentity options identity
                            |> Result.bind (fun identity ->
                                let identityRaw = rawPage identityUriValue identityResponse None

                                let rec readEnvironments expectedPage uri total accumulated raw =
                                    match tryEnvironmentPage options uri with
                                    | Some actualPage when actualPage = expectedPage ->
                                        send options guarded uri
                                        |> Result.bind (fun response ->
                                            match parseEnvironmentPage response.Body, nextLink response with
                                            | Ok(pageTotal, entries), Ok next ->
                                                if total |> Option.exists ((<>) pageTotal) then
                                                    Error "environment-total-count-drift"
                                                else
                                                    let combined = accumulated @ entries
                                                    let evidence = rawPage uri response next

                                                    match next with
                                                    | Some _ when combined.Length >= pageTotal ->
                                                        Error "environment-pagination-unexpected-continuation"
                                                    | Some continuation ->
                                                        match tryEnvironmentPage options continuation with
                                                        | Some nextPage when nextPage = expectedPage + 1 ->
                                                            readEnvironments
                                                                nextPage
                                                                continuation
                                                                (Some pageTotal)
                                                                combined
                                                                (raw @ [ evidence ])
                                                        | _ -> Error "environment-pagination-continuation"
                                                    | None when combined.Length = pageTotal ->
                                                        Ok(pageTotal, combined, raw @ [ evidence ])
                                                    | None -> Error "environment-pagination-incomplete"
                                            | Error reason, _
                                            | _, Error reason -> Error reason)
                                    | _ -> Error "environment-pagination-request"

                                readEnvironments 1 (firstEnvironmentUri options) None [] []
                                |> Result.bind (fun (total, environments, environmentRaw) ->
                                    if duplicateEnvironmentIdentity environments then
                                        Error "duplicate-environment-identity"
                                    else
                                        let pages = identityRaw :: environmentRaw

                                        {
                                            EnvironmentRepository = identity
                                            EnvironmentTotalCount = total
                                            Environments = environments
                                            EnvironmentPages = pages
                                            EnvironmentFingerprint =
                                                passFingerprint identity total environments pages
                                        }
                                        |> validatePass options)))))

    let captureTwoPass (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        capturePass options transport
        |> Result.bind (fun first ->
            capturePass options transport
            |> Result.bind (fun second ->
                if first <> second || first.EnvironmentFingerprint <> second.EnvironmentFingerprint then
                    Error "environment-census-pass-drift"
                else
                    Ok
                        {
                            EnvironmentFirst = first
                            EnvironmentSecond = second
                            EnvironmentCaptureFingerprint =
                                sha256 (first.EnvironmentFingerprint + "\n" + second.EnvironmentFingerprint)
                        }))

    let proveEmpty (options: MigrationGitHubReadOptions) (observed: EnvironmentCensusCapture) =
        validatePass options observed.EnvironmentFirst
        |> Result.bind (fun first ->
            validatePass options observed.EnvironmentSecond
            |> Result.bind (fun second ->
                if first <> second || first.EnvironmentFingerprint <> second.EnvironmentFingerprint then
                    Error "environment-census-pass-drift"
                elif
                    observed.EnvironmentCaptureFingerprint
                    <> sha256 (first.EnvironmentFingerprint + "\n" + second.EnvironmentFingerprint)
                then
                    Error "environment-census-capture-fingerprint-drift"
                elif first.EnvironmentTotalCount <> 0 || not first.Environments.IsEmpty then
                    Error "environment-detail-capture-required"
                else
                    Ok observed))
