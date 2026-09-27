namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryCapture =
    let private fail reason : 'a = invalidOp reason

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private nonblank (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private oid (value: string) =
        not (isNull value) && value.Length = 40 && value |> Seq.forall Uri.IsHexDigit

    let private unique values = List.length values = (values |> Set.ofList |> Set.count)

    let private document (body: string) =
        if isNull body then fail "invalid:null-body"
        try JsonDocument.Parse body
        with :? JsonException -> fail "invalid:json"

    let private membersUnique (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then fail "invalid:object"
        let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if not (unique names) then fail "duplicate:json-field"

    let private property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if value.ValueKind <> JsonValueKind.Object || not (value.TryGetProperty(name, &found)) then
            fail $"missing:{name}"
        found

    let private requiredString (name: string) (value: JsonElement) =
        let found = property name value
        if found.ValueKind <> JsonValueKind.String || not (nonblank (found.GetString())) then
            fail $"invalid:{name}"
        found.GetString()

    let private optionalString (name: string) (value: JsonElement) =
        let found = property name value
        match found.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String when nonblank (found.GetString()) -> Some(found.GetString())
        | _ -> fail $"invalid:{name}"

    let private int64 (name: string) (value: JsonElement) =
        let found = property name value
        let mutable parsed = 0L
        if found.ValueKind <> JsonValueKind.Number || not (found.TryGetInt64(&parsed)) || parsed <= 0L then
            fail $"invalid:{name}"
        parsed

    let private requiredInt (name: string) (value: JsonElement) =
        let parsed = int64 name value
        if parsed > System.Int32.MaxValue then fail $"invalid:{name}"
        System.Int32.CreateChecked parsed

    let private boolean (name: string) (value: JsonElement) =
        let found = property name value
        match found.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> fail $"invalid:{name}"

    let private array (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Array then fail "invalid:array"
        value.EnumerateArray() |> Seq.toList

    let private repositoryPath (options: MigrationGitHubReadOptions) =
        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase)
        && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && nonblank options.Token && nonblank options.UserAgent
        && nonblank options.Owner && nonblank options.Repository
        && options.ExpectedRepositoryId > 0L

    let private headers (options: MigrationGitHubReadOptions) =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", options.UserAgent
          "authorization", $"Bearer {options.Token}" ]
        |> Map.ofList

    let private request (options: MigrationGitHubReadOptions) (target: Uri) =
        Rest { Method=Get; Uri=target; Headers=headers options; Body=None
               ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }

    let private header name (values: Map<string,string>) =
        values |> Map.toList |> List.tryPick (fun (key, value) ->
            if key.Equals(name, StringComparison.OrdinalIgnoreCase) then Some value else None)

    let private nextLink (headers: Map<string,string>) =
        match header "link" headers with
        | None -> None
        | Some value ->
            let links = value.Split(',') |> Array.map _.Trim() |> Array.toList
            let next = links |> List.filter (fun item -> item.EndsWith("rel=\"next\"", StringComparison.Ordinal))
            if next.Length > 1 then fail "pagination:duplicate-next"
            next |> List.tryHead |> Option.map (fun item ->
                let left, right = item.IndexOf('<'), item.IndexOf('>')
                if left <> 0 || right < 2 then fail "pagination:malformed-link"
                item.Substring(1, right - 1))

    let private send (options: MigrationGitHubReadOptions)
                     (transport: IMigrationGitHubReadTransport) (target: Uri) =
        if target.Scheme <> options.ApiBase.Scheme || target.Authority <> options.ApiBase.Authority then
            fail "request:foreign-authority"
        let sent = request options target
        match transport.Send sent with
        | NetworkFailure | TimedOut -> fail "transport:unavailable"
        | Response response when response.StatusCode <> 200 -> fail $"http:{response.StatusCode}"
        | Response response when isNull response.Body -> fail "invalid:null-body"
        | Response response ->
            let captured = MigrationReviewDeliveryCaptureContract.captureRequest sent
            let next = nextLink response.Headers
            response,
            { Request=captured
              RequestSha256=MigrationReviewDeliveryCaptureContract.requestSha256 captured
              StatusCode=response.StatusCode
              ResponseHeaders=response.Headers
              RawBody=response.Body
              RawSha256=sha response.Body
              NextRequestUri=next }

    let private queryEntries (uri: Uri) =
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun item -> item.Split('=', 2))
        |> Array.map (fun pieces ->
            if pieces.Length <> 2 then fail "pagination:query"
            Uri.UnescapeDataString pieces[0], Uri.UnescapeDataString pieces[1])
        |> Array.toList

    let private expectedQuery page (fixedQuery: (string * string) list) =
        fixedQuery @ (if page = 1 then [] else [ "page", string page ])

    let private paged<'record>
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport)
        (path: string) (fixedQuery: (string * string) list) (parse: string -> 'record list) =
        let initial = Uri(options.ApiBase, path)
        let rec loop page (seen: Set<string>) (reads: MigrationReviewDeliveryRead list)
                     (records: 'record list) (current: Uri) =
            if page > 1000 || Set.contains current.AbsoluteUri seen then fail "pagination:cycle-or-limit"
            if current.Scheme <> initial.Scheme || current.Authority <> initial.Authority
               || current.AbsolutePath <> initial.AbsolutePath then fail "pagination:escaped"
            let actualQuery = queryEntries current
            if not (unique (actualQuery |> List.map fst))
               || List.sort actualQuery <> List.sort (expectedQuery page fixedQuery) then
                fail "pagination:query"
            let response, read = send options transport current
            let values = parse response.Body
            if page > 1 && List.isEmpty values then fail "pagination:extra-page"
            match read.NextRequestUri with
            | None -> List.rev (read :: reads), List.rev (List.rev values @ records)
            | Some next ->
                let nextUri =
                    match Uri.TryCreate(next, UriKind.Absolute) with
                    | true, value -> value
                    | _ -> fail "pagination:malformed-link"
                let pages = queryEntries nextUri |> List.filter (fst >> (=) "page") |> List.map snd
                if pages <> [ string (page + 1) ] then fail "pagination:nonconsecutive"
                loop (page + 1) (Set.add current.AbsoluteUri seen) (read :: reads)
                     (List.rev values @ records) nextUri
        loop 1 Set.empty [] [] initial

    let private singleton<'record>
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport)
        (path: string) (parse: string -> 'record) =
        let response, read = send options transport (Uri(options.ApiBase, path))
        if read.NextRequestUri.IsSome then fail "pagination:unexpected-singleton-link"
        read, parse response.Body

    let private parseRepository (options: MigrationGitHubReadOptions) (body: string) =
        use parsed = document body
        let root = parsed.RootElement
        membersUnique root
        let id = int64 "id" root
        let node = requiredString "node_id" root
        let fullName = requiredString "full_name" root
        if id <> options.ExpectedRepositoryId || fullName <> $"{options.Owner}/{options.Repository}" then
            fail "changed:repository"
        id, node, fullName

    let private parsePulls (options: MigrationGitHubReadOptions) (body: string) =
        use parsed = document body
        array parsed.RootElement |> List.map (fun item ->
            membersUnique item
            let number = requiredInt "number" item
            let node = requiredString "node_id" item
            let head = requiredString "sha" (property "head" item)
            let baseRepository = int64 "id" (property "repo" (property "base" item))
            if not (oid head) || baseRepository <> options.ExpectedRepositoryId then fail "changed:pull-request"
            { MigrationReviewDeliveryPullRequest.Number=number; NodeId=node; HeadSha=head })

    let private parseReviews (options: MigrationGitHubReadOptions) pr (body: string) =
        let expected = Uri(options.ApiBase, $"{repositoryPath options}/pulls/{pr}").AbsoluteUri
        use parsed = document body
        array parsed.RootElement |> List.map (fun item ->
            membersUnique item
            if requiredString "pull_request_url" item <> expected then fail "changed:review-pull"
            MigrationReviewDeliveryRecord.Review(pr, int64 "id" item))

    let private parseInlineComments (options: MigrationGitHubReadOptions) pr (body: string) =
        let expected = Uri(options.ApiBase, $"{repositoryPath options}/pulls/{pr}").AbsoluteUri
        use parsed = document body
        array parsed.RootElement |> List.map (fun item ->
            membersUnique item
            if requiredString "pull_request_url" item <> expected then fail "changed:inline-comment-pull"
            MigrationReviewDeliveryRecord.InlineComment(pr, int64 "id" item))

    let private parseChecks pr head (body: string) =
        use parsed = document body
        let root = parsed.RootElement
        membersUnique root
        let total = requiredInt "total_count" root
        let records =
            array (property "check_runs" root) |> List.map (fun item ->
                membersUnique item
                let observedHead = requiredString "head_sha" item
                if observedHead <> head || not (oid observedHead) then fail "changed:check-head"
                let status = requiredString "status" item
                if not (Set.contains status (set [ "queued"; "in_progress"; "completed"; "waiting"; "pending"; "requested" ])) then
                    fail "invalid:check-status"
                let conclusion = optionalString "conclusion" item
                if status = "completed" && conclusion.IsNone || status <> "completed" && conclusion.IsSome then
                    fail "invalid:check-conclusion"
                if conclusion |> Option.exists (fun value ->
                    not (Set.contains value
                        (set [ "action_required"; "cancelled"; "failure"; "neutral"; "skipped"
                               "stale"; "success"; "timed_out" ]))) then
                    fail "invalid:check-conclusion"
                MigrationReviewDeliveryRecord.CheckRun(pr, head, int64 "id" item, requiredString "name" item, status, conclusion))
        total, records

    let private parseStatuses (options: MigrationGitHubReadOptions) pr head (body: string) =
        let expectedUrl = Uri(options.ApiBase, $"{repositoryPath options}/statuses/{head}").AbsoluteUri
        use parsed = document body
        array parsed.RootElement |> List.map (fun item ->
            membersUnique item
            if requiredString "url" item <> expectedUrl then fail "changed:status-url"
            let state = requiredString "state" item
            if not (Set.contains state (set [ "error"; "failure"; "pending"; "success" ])) then
                fail "invalid:status-state"
            MigrationReviewDeliveryRecord.CommitStatus(pr, head, int64 "id" item, requiredString "context" item, state))

    let private parseDelivery pr expectedNode expectedHead (body: string) =
        use parsed = document body
        let root = parsed.RootElement
        membersUnique root
        if requiredInt "number" root <> pr || requiredString "node_id" root <> expectedNode
           || requiredString "sha" (property "head" root) <> expectedHead then fail "changed:pull-delivery"
        let mergedAt = optionalString "merged_at" root
        let commit = optionalString "merge_commit_sha" root
        if (mergedAt.IsSome && commit.IsNone) || (commit |> Option.exists (oid >> not)) then
            fail "invalid:pull-delivery"
        let delivered = if mergedAt.IsSome then commit else None
        MigrationReviewDeliveryRecord.PullDelivery(pr, delivered), delivered

    let private parseMerge pr commit (body: string) =
        use parsed = document body
        let root = parsed.RootElement
        membersUnique root
        if requiredString "sha" root <> commit then fail "changed:merge-object"
        MigrationReviewDeliveryRecord.MergeObject(pr, commit)

    let private parseTags (body: string) =
        use parsed = document body
        array parsed.RootElement |> List.map (fun item ->
            membersUnique item
            let target = requiredString "sha" (property "commit" item)
            if not (oid target) then fail "invalid:tag-target"
            MigrationReviewDeliveryRecord.Tag(requiredString "name" item, target))

    let private parseReleases (body: string) =
        use parsed = document body
        array parsed.RootElement |> List.map (fun item ->
            membersUnique item
            MigrationReviewDeliveryRecord.Release(int64 "id" item, requiredString "tag_name" item, boolean "draft" item))

    let private stream kind subject (reads: MigrationReviewDeliveryRead list)
                       (records: MigrationReviewDeliveryRecord list) =
        let identities =
            records |> List.map (function
                | MigrationReviewDeliveryRecord.Review (_, id) | MigrationReviewDeliveryRecord.InlineComment (_, id) -> string id
                | MigrationReviewDeliveryRecord.CheckRun (_, _, id, _, _, _) | MigrationReviewDeliveryRecord.CommitStatus (_, _, id, _, _) -> string id
                | MigrationReviewDeliveryRecord.PullDelivery (pr, _) | MigrationReviewDeliveryRecord.MergeObject (pr, _) -> string pr
                | MigrationReviewDeliveryRecord.Tag (name, _) -> name
                | MigrationReviewDeliveryRecord.Release (id, _, _) -> string id)
        if not (unique identities) then fail $"duplicate:{kind}"
        { Kind=kind; Subject=subject; Reads=reads; Records=records }

    let private readPass (options: MigrationGitHubReadOptions)
                         (transport: IMigrationGitHubReadTransport) =
        let basePath = repositoryPath options
        let repositoryRead, (repositoryId, nodeId, fullName) =
            singleton options transport basePath (parseRepository options)
        let pullReads, pulls =
            paged options transport ($"{basePath}/pulls?state=all&per_page=100")
                [ "state", "all"; "per_page", "100" ] (parsePulls options)
        if not (unique (pulls |> List.map _.Number)) || not (unique (pulls |> List.map _.NodeId)) then
            fail "duplicate:pull-request"
        let pulls : MigrationReviewDeliveryPullRequest list = List.sortBy _.Number pulls
        let mutable streams : MigrationReviewDeliveryNativeStream list = []
        for pull in pulls do
            let subject = string pull.Number
            let reviewReads, reviews =
                paged options transport ($"{basePath}/pulls/{pull.Number}/reviews?per_page=100")
                    [ "per_page", "100" ] (parseReviews options pull.Number)
            streams <- stream "reviews" subject reviewReads reviews :: streams
            let inlineReads, inlineComments =
                paged options transport ($"{basePath}/pulls/{pull.Number}/comments?per_page=100")
                    [ "per_page", "100" ] (parseInlineComments options pull.Number)
            streams <- stream "inline-comments" subject inlineReads inlineComments :: streams
            let checkPath = MigrationReviewDeliveryCaptureContract.checkRunsPath basePath pull.HeadSha
            let mutable total = None
            let checkReads, checks =
                paged options transport checkPath [ "filter", "all"; "per_page", "100" ] (fun body ->
                    let observed, records = parseChecks pull.Number pull.HeadSha body
                    match total with
                    | Some previous when previous <> observed -> fail "changed:check-total"
                    | _ -> total <- Some observed
                    records)
            if checks.Length <> Option.defaultValue -1 total then fail "incomplete:check-runs"
            streams <- stream "check-runs" subject checkReads checks :: streams
            let statusPath = MigrationReviewDeliveryCaptureContract.statusesPath basePath pull.HeadSha
            let statusReads, statuses =
                paged options transport statusPath [ "per_page", "100" ]
                    (parseStatuses options pull.Number pull.HeadSha)
            streams <- stream "statuses" subject statusReads statuses :: streams
            let deliveryRead, (delivery, mergeCommit) =
                singleton options transport ($"{basePath}/pulls/{pull.Number}")
                    (parseDelivery pull.Number pull.NodeId pull.HeadSha)
            streams <- stream "pull-delivery" subject [ deliveryRead ] [ delivery ] :: streams
            match mergeCommit with
            | None -> ()
            | Some commit ->
                let mergeRead, merge =
                    singleton options transport ($"{basePath}/commits/{commit}") (parseMerge pull.Number commit)
                streams <- stream "merge-object" subject [ mergeRead ] [ merge ] :: streams
        let tagReads, tags =
            paged options transport ($"{basePath}/tags?per_page=100") [ "per_page", "100" ] parseTags
        streams <- stream "tags" "repository" tagReads tags :: streams
        let releaseReads, releases =
            paged options transport ($"{basePath}/releases?per_page=100") [ "per_page", "100" ] parseReleases
        let tagNames = tags |> List.choose (function MigrationReviewDeliveryRecord.Tag(name, _) -> Some name | _ -> None) |> Set.ofList
        for release in releases do
            match release with
            | MigrationReviewDeliveryRecord.Release(_, tag, false) when not (Set.contains tag tagNames) -> fail "missing:release-tag"
            | _ -> ()
        streams <- stream "releases" "repository" releaseReads releases :: streams
        let partial =
            { Repository={ RepositoryId=repositoryId; NodeId=nodeId; FullName=fullName; Read=repositoryRead }
              PullRequestCensus=pullReads; PullRequests=pulls
              Streams=List.rev streams; Fingerprint="" }
        { partial with Fingerprint=MigrationReviewDeliveryCaptureContract.nativeFingerprint partial }

    let captureTwoPass options transport =
        if not (validOptions options) then Error "review-delivery-native-invalid-options"
        elif isNull (box transport) then Error "review-delivery-native-invalid-transport"
        else
            try
                let first = readPass options transport
                let second = readPass options transport
                MigrationReviewDeliveryCaptureContract.validateNativeTwoPass { First=first; Second=second }
            with
            | :? InvalidOperationException as error -> Error error.Message
            | :? ArgumentException as error -> Error error.Message
