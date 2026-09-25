namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub

type MigrationDeliveryJournalDeclaration =
    { Repository: string
      RefName: string
      Path: string
      PullRequestNumber: int
      OperationId: string }

type MigrationReviewDeliveryPage =
    { RequestUri: string
      RawBody: string
      RawSha256: string
      NextUri: string option }

type MigrationReviewDeliveryStream =
    { Kind: string
      Subject: string
      Pages: MigrationReviewDeliveryPage list
      RecordIds: string list }

type MigrationDeliveryJournalEvidence =
    { Declaration: MigrationDeliveryJournalDeclaration
      RefHead: string
      RequestUri: string
      RawBody: string
      RawSha256: string
      ContentSha256: string
      MergeCommit: string option }

type MigrationReviewDeliveryPass =
    { RepositoryId: int64
      PullRequests: MigrationPullRequestPopulation
      Reviews: MigrationPullRequestReviewPopulation list
      InlineComments: MigrationPullRequestReviewCommentPopulation list
      Streams: MigrationReviewDeliveryStream list
      Journals: MigrationDeliveryJournalEvidence list
      SnapshotSha256: string }

type MigrationReviewDeliveryTwoPass =
    { First: MigrationReviewDeliveryPass
      Second: MigrationReviewDeliveryPass }

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryCapture =
    let private fail reason : 'a = invalidOp reason
    let private sha (bytes: byte[]) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let private hash (value: string) = value |> Encoding.UTF8.GetBytes |> sha
    let private hex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
    let private nonblank (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private unique<'a when 'a: comparison> (values: 'a list) = values.Length = (values |> Set.ofList |> Set.count)
    let private prop (name: string) (item: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if item.ValueKind <> JsonValueKind.Object || not (item.TryGetProperty(name, &value)) then
            fail $"missing:{name}"
        value
    let private str (name: string) (item: JsonElement) =
        let value = prop name item
        if value.ValueKind <> JsonValueKind.String || not (nonblank (value.GetString())) then fail $"invalid:{name}"
        value.GetString()
    let private optionalString (name: string) (item: JsonElement) =
        let value = prop name item
        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String when nonblank (value.GetString()) -> Some(value.GetString())
        | _ -> fail $"invalid:{name}"
    let private number (name: string) (item: JsonElement) =
        let value = prop name item
        let mutable parsed = 0L
        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt64(&parsed)) || parsed <= 0L then
            fail $"invalid:{name}"
        parsed
    let private count (name: string) (item: JsonElement) =
        let value = prop name item
        let mutable parsed = 0L
        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt64(&parsed)) || parsed < 0L then
            fail $"invalid:{name}"
        parsed
    let private array (item: JsonElement) =
        if item.ValueKind <> JsonValueKind.Array then fail "invalid:array"
        item.EnumerateArray() |> Seq.toList
    let private document (body: string) =
        try JsonDocument.Parse body
        with :? JsonException -> fail "invalid:json"
    let private fieldsUnique (item: JsonElement) =
        if item.ValueKind <> JsonValueKind.Object then fail "invalid:object"
        let names = item.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if not (unique names) then fail "duplicate:json-field"

    let private path (options: MigrationGitHubReadOptions) =
        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"
    let private uri (options: MigrationGitHubReadOptions) (value: string) = Uri(options.ApiBase, value)
    let private send (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (target: Uri) =
        if target.Scheme <> Uri.UriSchemeHttps || target.Host <> options.ApiBase.Host
           || target.Port <> options.ApiBase.Port then fail "foreign:request-uri"
        let headers =
            [ "accept", "application/vnd.github+json"
              "x-github-api-version", ApiVersion.value ApiVersion.required
              "user-agent", options.UserAgent
              "authorization", $"Bearer {options.Token}" ] |> Map.ofList
        let request =
            Rest { Method=Get; Uri=target; Headers=headers; Body=None
                   ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        match transport.Send request with
        | Response response when response.StatusCode = 200 -> response
        | Response response -> fail $"http:{response.StatusCode}"
        | NetworkFailure | TimedOut -> fail "transport:unavailable"

    let private single options transport target =
        let response = send options transport target
        if response.Headers |> Map.exists (fun key _ -> key.Equals("link", StringComparison.OrdinalIgnoreCase)) then
            fail "pagination:unexpected-singleton-link"
        response

    let private nextLink (headers: Map<string,string>) =
        let link =
            headers |> Map.toList
            |> List.tryPick (fun (key, value) -> if key.Equals("link", StringComparison.OrdinalIgnoreCase) then Some value else None)
        match link with
        | None -> None
        | Some value ->
            let parts = value.Split(',') |> Array.map _.Trim() |> Array.toList
            let next = parts |> List.filter (fun part -> part.EndsWith("rel=\"next\"", StringComparison.Ordinal))
            if next.Length > 1 then fail "pagination:duplicate-next"
            next |> List.tryHead |> Option.map (fun part ->
                let openAt, closeAt = part.IndexOf('<'), part.IndexOf('>')
                if openAt <> 0 || closeAt < 2 then fail "pagination:malformed-link"
                part.Substring(1, closeAt - 1))

    let private linkedPages (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport)
                            (path: string) (parsePage: string -> string list) =
        let initial = uri options path
        let rec walk (current: Uri) (page: int) (seen: Set<string>)
                     (pages: MigrationReviewDeliveryPage list) (ids: string list) =
            if page > 1000 || Set.contains current.AbsoluteUri seen then fail "pagination:cycle-or-limit"
            if current.Scheme <> initial.Scheme || current.Authority <> initial.Authority
               || current.AbsolutePath <> initial.AbsolutePath then fail "pagination:escaped"
            let query =
                current.Query.TrimStart('?').Split('&')
                |> Array.map (fun item ->
                    let parts = item.Split('=', 2)
                    if parts.Length <> 2 then fail "pagination:query"
                    parts[0], parts[1]) |> Array.toList
            let expectedQuery =
                if page = 1 then [ "per_page", "100" ]
                else [ "per_page", "100"; "page", string page ]
            if List.sort query <> List.sort expectedQuery then fail "pagination:query"
            let response = send options transport current
            let newIds = parsePage response.Body
            let next = nextLink response.Headers
            let proof =
                { RequestUri=current.AbsoluteUri; RawBody=response.Body
                  RawSha256=hash response.Body; NextUri=next }
            match next with
            | None -> List.rev (proof :: pages), List.rev (List.rev newIds @ ids)
            | Some value ->
                let nextUri = Uri value
                let pageValues =
                    nextUri.Query.TrimStart('?').Split('&')
                    |> Array.choose (fun part ->
                        let components = part.Split('=', 2)
                        if components.Length = 2 && components[0] = "page" then Some components[1] else None)
                if pageValues <> [| string (page + 1) |] then
                    fail "pagination:nonconsecutive"
                walk nextUri (page + 1) (Set.add current.AbsoluteUri seen) (proof :: pages) (List.rev newIds @ ids)
        walk initial 1 Set.empty [] []

    let private stream (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport)
                       (kind: string) (subject: string) (path: string) (parsePage: string -> string list) =
        let pages, ids = linkedPages options transport path parsePage
        if not (unique ids) then fail $"duplicate:{kind}"
        { Kind=kind; Subject=subject; Pages=pages; RecordIds=ids }

    let private checkRuns (head: string) (body: string) =
        use parsed = document body
        let root = parsed.RootElement
        fieldsUnique root
        let total = count "total_count" root
        let records = array (prop "check_runs" root)
        let ids = records |> List.map (fun record ->
            fieldsUnique record
            let id = number "id" record
            let observed = str "head_sha" record
            if observed <> head || not (hex 40 observed) then fail "changed:check-head"
            ignore (str "name" record)
            let status = str "status" record
            if not (Set.contains status (set [ "queued"; "in_progress"; "completed"; "waiting"; "pending"; "requested" ])) then
                fail "invalid:check-status"
            let conclusion = prop "conclusion" record
            let validConclusion =
                match conclusion.ValueKind with
                | JsonValueKind.Null -> status <> "completed"
                | JsonValueKind.String ->
                    status = "completed"
                    && Set.contains (conclusion.GetString())
                        (set [ "action_required"; "cancelled"; "failure"; "neutral"; "skipped"; "stale"; "success"; "timed_out" ])
                | _ -> false
            if not validConclusion then fail "invalid:check-conclusion"
            string id)
        total, ids

    let private statusRecords (head: string) (body: string) =
        use parsed = document body
        array parsed.RootElement |> List.map (fun record ->
            fieldsUnique record
            let id = number "id" record
            let observed = str "sha" record
            if observed <> head then fail "changed:status-head"
            ignore (str "context" record)
            if not (Set.contains (str "state" record) (set [ "error"; "failure"; "pending"; "success" ])) then
                fail "invalid:status-state"
            string id)

    let private releaseRecords (body: string) =
        use parsed = document body
        array parsed.RootElement |> List.map (fun record ->
            fieldsUnique record
            let id = number "id" record
            ignore (str "tag_name" record)
            let draft = prop "draft" record
            if draft.ValueKind <> JsonValueKind.True && draft.ValueKind <> JsonValueKind.False then fail "invalid:release-draft"
            let published = prop "published_at" record
            if draft.ValueKind = JsonValueKind.False && published.ValueKind <> JsonValueKind.String then
                fail "invalid:release-publication"
            string id)

    let private tagRecords (body: string) =
        use parsed = document body
        array parsed.RootElement |> List.map (fun record ->
            fieldsUnique record
            let name = str "name" record
            let commit = str "sha" (prop "commit" record)
            if not (hex 40 commit) then fail "invalid:tag-target"
            name)

    let private pullReceipt (expectedNumber: int) (expectedNode: string) (expectedHead: string) (body: string) =
        use parsed = document body
        let root = parsed.RootElement
        fieldsUnique root
        if number "number" root <> int64 expectedNumber
           || str "node_id" root <> expectedNode
           || str "sha" (prop "head" root) <> expectedHead then fail "changed:pull-request"
        let mergedAt = optionalString "merged_at" root
        let mergeCommit = optionalString "merge_commit_sha" root
        if mergedAt.IsSome && (mergeCommit |> Option.exists (hex 40) |> not) then fail "invalid:merge-commit"
        if mergedAt.IsSome then mergeCommit else None

    let private validJournal (declaration: MigrationDeliveryJournalDeclaration) =
        let names = declaration.Repository.Split('/')
        let safePart (value: string) =
            nonblank value && (value |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.'))
        names.Length = 2 && names |> Array.forall safePart
        && declaration.RefName.StartsWith("refs/heads/fsgg/v2/journal/", StringComparison.Ordinal)
        && not (declaration.RefName.Contains("..", StringComparison.Ordinal))
        && (declaration.RefName.Substring("refs/heads/fsgg/v2/journal/".Length).Split('/')
            |> Array.forall safePart)
        && declaration.Path.StartsWith("ordinary/", StringComparison.Ordinal)
        && declaration.Path.EndsWith(".json", StringComparison.Ordinal)
        && not (declaration.Path.Contains("..", StringComparison.Ordinal))
        && not (declaration.Path.Contains('\\'))
        && nonblank declaration.OperationId

    let private journal (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport)
                        (declaration: MigrationDeliveryJournalDeclaration) (mergeCommit: string option) =
        if not (validJournal declaration) then fail "invalid:journal-declaration"
        let names = declaration.Repository.Split('/')
        let repositoryPath = $"repos/{Uri.EscapeDataString names[0]}/{Uri.EscapeDataString names[1]}"
        let refPath =
            declaration.RefName.Substring("refs/".Length).Split('/')
            |> Array.map Uri.EscapeDataString |> String.concat "/"
        let refUri = uri options $"{repositoryPath}/git/ref/{refPath}"
        let readRef () =
            let response = single options transport refUri
            use parsed = document response.Body
            let root = parsed.RootElement
            fieldsUnique root
            if str "ref" root <> declaration.RefName then fail "changed:journal-ref"
            let observed = str "sha" (prop "object" root)
            if not (hex 40 observed) then fail "invalid:journal-ref-head"
            observed
        let refHead = readRef ()
        let contentPath = declaration.Path.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/"
        let refQuery = Uri.EscapeDataString refHead
        let requestUri = uri options $"{repositoryPath}/contents/{contentPath}?ref={refQuery}"
        let response = single options transport requestUri
        use parsed = document response.Body
        let root = parsed.RootElement
        fieldsUnique root
        if str "path" root <> declaration.Path || str "encoding" root <> "base64" then fail "changed:journal-path"
        let encoded = str "content" root |> fun value -> value.Replace("\n", "")
        let content =
            try Convert.FromBase64String encoded
            with :? FormatException -> fail "invalid:journal-base64"
        let gitBlob = Array.concat [ Encoding.ASCII.GetBytes($"blob {content.Length}\u0000"); content ]
        let blobSha = SHA1.HashData gitBlob |> Convert.ToHexString |> _.ToLowerInvariant()
        if str "sha" root <> blobSha then fail "changed:journal-blob"
        let text = Encoding.UTF8.GetString content
        use contentJson = document text
        let record = contentJson.RootElement
        fieldsUnique record
        if str "operationId" record <> declaration.OperationId then fail "changed:journal-operation"
        if number "generation" record < 1L then fail "invalid:journal-generation"
        if str "schema" record <> "fsgg.coordination.ordinary-delivery-journal/1" then
            fail "invalid:journal-schema"
        if not (hex 64 (str "planDigest" record)) then fail "invalid:journal-plan-digest"
        let stage = str "stage" record
        if not (Set.contains stage (set [ "intent-persisted"; "effect-pending"; "settled" ])) then
            fail "invalid:journal-stage"
        let recordedCommit = optionalString "mergeCommit" record
        if stage = "settled" && recordedCommit.IsNone then fail "invalid:settled-journal"
        if recordedCommit <> mergeCommit then fail "changed:journal-merge"
        if readRef () <> refHead then fail "changed:journal-ref-head"
        { Declaration=declaration; RefHead=refHead; RequestUri=requestUri.AbsoluteUri
          RawBody=response.Body; RawSha256=hash response.Body
          ContentSha256=sha content; MergeCommit=recordedCommit }

    let private framed (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private fingerprint (snapshot: MigrationReviewDeliveryPass) =
        let parts =
            [ string snapshot.RepositoryId
              snapshot.PullRequests.Pages |> List.map (fun p -> p.RequestedUri + p.PayloadSha256) |> String.concat "|"
              snapshot.PullRequests.PullRequests |> List.map (fun p -> string p.Number + p.PayloadSha256) |> String.concat "|" ]
            @ (snapshot.Reviews |> List.collect (fun r ->
                r.Pages |> List.map (fun p -> p.RequestedUri + p.PayloadSha256)))
            @ (snapshot.InlineComments |> List.collect (fun r ->
                r.Pages |> List.map (fun p -> p.RequestedUri + p.PayloadSha256)))
            @ (snapshot.Streams |> List.collect (fun s ->
                [ s.Kind; s.Subject; String.concat "," s.RecordIds ]
                @ (s.Pages |> List.collect (fun p -> [ p.RequestUri; p.RawSha256; defaultArg p.NextUri "" ]))))
            @ (snapshot.Journals |> List.collect (fun j ->
                [ j.Declaration.Repository; j.Declaration.RefName; j.Declaration.Path
                  j.Declaration.OperationId; j.RefHead; j.RawSha256; j.ContentSha256 ]))
        parts |> List.map framed |> String.concat "" |> hash

    let private readPass (options: MigrationGitHubReadOptions) (expected: (int * string * string) list)
                         (journals: MigrationDeliveryJournalDeclaration list)
                         (transport: IMigrationGitHubReadTransport) =
        let issues =
            match MigrationGitHubRead.readIssues options transport with
            | Ok value -> value
            | Error failure -> fail $"issues:{failure}"
        let pulls =
            match MigrationGitHubRead.readPullRequests options issues transport with
            | Ok value -> value
            | Error failure -> fail $"pull-requests:{failure}"
        let actual = pulls.PullRequests |> List.map (fun p -> p.Number, p.NodeId, p.HeadSha) |> List.sort
        if actual <> List.sort expected then fail "changed:pull-request-population"
        let mutable streams = []
        let mutable reviews = []
        let mutable inlineComments = []
        let mutable merges = Map.empty
        for (number, node, head) in List.sort expected do
            let review =
                match MigrationGitHubRead.readPullRequestReviews options pulls number transport with
                | Ok value -> value
                | Error failure -> fail $"reviews:{number}:{failure}"
            let comments =
                match MigrationGitHubRead.readPullRequestReviewComments options pulls number transport with
                | Ok value -> value
                | Error failure -> fail $"inline-comments:{number}:{failure}"
            reviews <- review :: reviews
            inlineComments <- comments :: inlineComments
            let basePath = path options
            let checkPath = $"{basePath}/commits/{head}/check-runs?per_page=100"
            let mutable expectedTotal = None
            let checks = stream options transport "check-runs" (string number) checkPath (fun body ->
                let total, ids = checkRuns head body
                match expectedTotal with
                | Some previous when previous <> total -> fail "changed:check-total"
                | _ -> expectedTotal <- Some total
                ids)
            if int64 checks.RecordIds.Length <> Option.get expectedTotal then fail "incomplete:check-runs"
            let statuses =
                stream options transport "statuses" (string number)
                    $"{basePath}/commits/{head}/statuses?per_page=100" (statusRecords head)
            let pullPath = $"{basePath}/pulls/{number}"
            let pullResponse = single options transport (uri options pullPath)
            let merge = pullReceipt number node head pullResponse.Body
            merges <- Map.add number merge merges
            let receipt =
                { Kind="pull-delivery"; Subject=string number
                  Pages=[ { RequestUri=(uri options pullPath).AbsoluteUri; RawBody=pullResponse.Body
                            RawSha256=hash pullResponse.Body; NextUri=None } ]
                  RecordIds=merge |> Option.toList }
            let mergeStream =
                merge |> Option.map (fun commit ->
                    let commitPath = $"{basePath}/commits/{commit}"
                    let response = single options transport (uri options commitPath)
                    use parsed = document response.Body
                    if str "sha" parsed.RootElement <> commit then fail "changed:merge-object"
                    { Kind="merge-object"; Subject=string number
                      Pages=[ { RequestUri=(uri options commitPath).AbsoluteUri; RawBody=response.Body
                                RawSha256=hash response.Body; NextUri=None } ]
                      RecordIds=[ commit ] })
            streams <- streams @ [ checks; statuses; receipt ] @ (Option.toList mergeStream)
        let basePath = path options
        streams <- streams @ [ stream options transport "tags" "repository"
                                 $"{basePath}/tags?per_page=100" tagRecords
                               stream options transport "releases" "repository"
                                 $"{basePath}/releases?per_page=100" releaseRecords ]
        let tagNames =
            streams |> List.find (fun value -> value.Kind = "tags") |> _.RecordIds |> Set.ofList
        let releasePages =
            streams |> List.find (fun value -> value.Kind = "releases") |> _.Pages
        for page in releasePages do
            use parsed = document page.RawBody
            for release in array parsed.RootElement do
                let draft = prop "draft" release
                if draft.ValueKind = JsonValueKind.False
                   && not (Set.contains (str "tag_name" release) tagNames) then fail "missing:release-tag"
        let journalEvidence =
            journals |> List.map (fun declaration ->
                let merge = Map.find declaration.PullRequestNumber merges
                journal options transport declaration merge)
        let finalIssues =
            match MigrationGitHubRead.readIssues options transport with
            | Ok value -> value
            | Error failure -> fail $"final-issues:{failure}"
        let finalPulls =
            match MigrationGitHubRead.readPullRequests options finalIssues transport with
            | Ok value -> value
            | Error failure -> fail $"final-pull-requests:{failure}"
        if finalIssues <> issues || finalPulls <> pulls then fail "changed:terminal-census"
        let snapshot =
            { RepositoryId=options.ExpectedRepositoryId; PullRequests=pulls
              Reviews=List.rev reviews; InlineComments=List.rev inlineComments
              Streams=streams; Journals=journalEvidence; SnapshotSha256="" }
        { snapshot with SnapshotSha256=fingerprint snapshot }

    let captureTwoPass (options: MigrationGitHubReadOptions)
                       (expectedPullRequests: (int * string * string) list)
                       (journals: MigrationDeliveryJournalDeclaration list)
                       (transport: IMigrationGitHubReadTransport) =
        try
            if options.ExpectedRepositoryId <= 0L || not (nonblank options.Owner)
               || not (nonblank options.Repository) || not (nonblank options.Token)
               || not (nonblank options.UserAgent) || options.ApiBase.Scheme <> Uri.UriSchemeHttps
               || not (unique (expectedPullRequests |> List.map (fun (number, _, _) -> number)))
               || (expectedPullRequests |> List.exists (fun (number, node, head) ->
                   number <= 0 || not (nonblank node) || not (hex 40 head)))
               || not (unique (journals |> List.map (fun j -> j.Repository, j.RefName, j.Path)))
               || (journals |> List.exists (validJournal >> not))
               || (journals |> List.exists (fun j ->
                   not (expectedPullRequests |> List.exists (fun (number, _, _) -> number = j.PullRequestNumber)))) then
                Error "invalid:declaration"
            else
                let first = readPass options expectedPullRequests journals transport
                let second = readPass options expectedPullRequests journals transport
                if first <> second then Error "changed:two-pass"
                else Ok { First=first; Second=second }
        with :? InvalidOperationException as error -> Error error.Message
           | :? JsonException -> Error "invalid:json"
           | :? UriFormatException -> Error "invalid:uri"
