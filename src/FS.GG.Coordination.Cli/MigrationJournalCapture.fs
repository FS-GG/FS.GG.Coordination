namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationJournalCapture =
    exception private CaptureFailure of string

    let private refuse reason = raise (CaptureFailure reason)
    let private nonblank (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private lowerHex length (value: string) =
        nonblank value && value.Length = length
        && value |> Seq.forall (fun value -> value >= '0' && value <= '9' || value >= 'a' && value <= 'f')
    let private unique values = values |> List.length = (values |> Set.ofList |> Set.count)
    let private sha256 (bytes: byte[]) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private rawSha (value: string) = value |> Encoding.UTF8.GetBytes |> sha256
    let private gitSha1 kind (bytes: byte[]) =
        Array.append (Encoding.ASCII.GetBytes($"{kind} {bytes.LongLength}\u0000")) bytes
        |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if value.ValueKind <> JsonValueKind.Object || not (value.TryGetProperty(name, &found)) then
            refuse $"missing:{name}"
        found
    let private string (name: string) (value: JsonElement) =
        let found = property name value
        if found.ValueKind <> JsonValueKind.String || not (nonblank (found.GetString())) then refuse $"invalid:{name}"
        found.GetString()
    let private jsonString (name: string) (value: JsonElement) =
        let found = property name value
        if found.ValueKind <> JsonValueKind.String || isNull (found.GetString()) then refuse $"invalid:{name}"
        found.GetString()
    let private optionalString (name: string) (value: JsonElement) =
        let found = property name value
        match found.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String when nonblank (found.GetString()) -> Some(found.GetString())
        | _ -> refuse $"invalid:{name}"
    let private jsonInt64 (name: string) (value: JsonElement) =
        let found = property name value
        let mutable parsed = 0L
        if found.ValueKind <> JsonValueKind.Number || not (found.TryGetInt64(&parsed)) then refuse $"invalid:{name}"
        parsed
    let private boolean (name: string) (value: JsonElement) =
        let found = property name value
        match found.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> refuse $"invalid:{name}"
    let private jsonArray (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Array then refuse "invalid:array"
        value.EnumerateArray() |> Seq.toList
    let private parse (body: string) =
        try JsonDocument.Parse body
        with :? JsonException -> refuse "invalid:json"

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && String.IsNullOrEmpty options.ApiBase.Query && String.IsNullOrEmpty options.ApiBase.Fragment
        && nonblank options.UserAgent && nonblank options.Owner && nonblank options.Repository
        && not (options.Owner.Contains('/')) && not (options.Repository.Contains('/'))
        && options.ExpectedRepositoryId > 0L

    let private headers (options: MigrationGitHubReadOptions) =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", options.UserAgent
          if nonblank options.Token then "authorization", $"Bearer {options.Token}" ]
        |> Map.ofList

    let private repositoryPath (options: MigrationGitHubReadOptions) =
        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"

    let private makeRequest (options: MigrationGitHubReadOptions) (relative: string) =
        let request =
            Rest
                { Method = Get
                  Uri = Uri(options.ApiBase, relative)
                  Headers = headers options
                  Body = None
                  ApiVersion = ApiVersion.required
                  Idempotency = ReplaySafe }
        match request with
        | Rest value when value.Method = Get && value.Body.IsNone && value.Uri.Scheme = Uri.UriSchemeHttps -> request
        | _ -> refuse "write-shaped-request"

    let private hasLink (headers: Map<string, string>) =
        headers |> Map.exists (fun name _ -> String.Equals(name, "link", StringComparison.OrdinalIgnoreCase))

    let private send (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (relative: string) : MigrationReviewDeliveryRead =
        let request = makeRequest options relative
        match transport.Send request with
        | Response response when response.StatusCode = 200 ->
            if hasLink response.Headers then refuse "unexpected-pagination"
            let captured = MigrationReviewDeliveryCaptureContract.captureRequest request
            { Request = captured
              RequestSha256 = MigrationReviewDeliveryCaptureContract.requestSha256 captured
              StatusCode = response.StatusCode
              ResponseHeaders = response.Headers
              RawBody = response.Body
              RawSha256 = rawSha response.Body
              NextRequestUri = None }
        | Response response -> refuse $"github-read:{response.StatusCode}"
        | NetworkFailure | TimedOut -> refuse "transport-unavailable"

    let private repository (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) : MigrationReviewDeliveryRepository =
        let read = send options transport (repositoryPath options)
        use document = parse read.RawBody
        let root = document.RootElement
        let id = jsonInt64 "id" root
        let node = string "node_id" root
        let fullName = string "full_name" root
        if id <> options.ExpectedRepositoryId
           || not (String.Equals(fullName, $"{options.Owner}/{options.Repository}", StringComparison.OrdinalIgnoreCase)) then
            refuse "repository-identity-drift"
        { RepositoryId = id; NodeId = node; FullName = fullName; Read = read }

    let private namespaceCensus (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (prefix: string) : MigrationJournalNamespaceCensus =
        let relative = prefix.Substring("refs/".Length)
        let read = send options transport $"{repositoryPath options}/git/matching-refs/{relative}"
        use document = parse read.RawBody
        let refs =
            document.RootElement |> jsonArray
            |> List.map (fun item ->
                let name = string "ref" item
                if not (name.StartsWith(prefix, StringComparison.Ordinal))
                   || not (lowerHex 2 (name.Substring(prefix.Length))) then refuse "invalid-journal-ref"
                let objectValue = property "object" item
                if string "type" objectValue <> "commit" then refuse "invalid-ref-object"
                let head = string "sha" objectValue
                if not (lowerHex 40 head) then refuse "invalid-ref-head"
                { RefName = name; HeadSha = head })
            |> List.sortBy _.RefName
        if not (refs |> List.map _.RefName |> unique) then refuse "duplicate-journal-ref"
        { Prefix = prefix; Reads = [ read ]; Refs = refs }

    let private decodeBase64 (value: string) : byte[] =
        try Convert.FromBase64String(value.Replace("\n", ""))
        with :? FormatException -> refuse "invalid:base64"

    let private blob (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (oid: string) : MigrationReviewDeliveryRead * byte[] =
        let read = send options transport $"{repositoryPath options}/git/blobs/{oid}"
        use document = parse read.RawBody
        let root = document.RootElement
        if string "sha" root <> oid || string "encoding" root <> "base64" then refuse "blob-identity-drift"
        let bytes = string "content" root |> decodeBase64
        if jsonInt64 "size" root <> Microsoft.FSharp.Core.Operators.int64 bytes.LongLength || gitSha1 "blob" bytes <> oid then refuse "blob-hash-drift"
        read, bytes

    let private gitTreeSha (entries: (string * string * string) list) =
        entries
        |> List.collect (fun (mode, path, oid) ->
            (Encoding.UTF8.GetBytes($"{mode} {path}\u0000") |> Array.toList)
            @ (Convert.FromHexString oid |> Array.toList))
        |> List.toArray |> gitSha1 "tree"

    let private tree (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (oid: string) : MigrationReviewDeliveryRead * (string * string * string) list =
        let read = send options transport $"{repositoryPath options}/git/trees/{oid}"
        use document = parse read.RawBody
        let root = document.RootElement
        if string "sha" root <> oid || boolean "truncated" root then refuse "tree-identity-drift"
        let entries =
            property "tree" root |> jsonArray
            |> List.map (fun item ->
                let mode, path, kind, sha = string "mode" item, string "path" item, string "type" item, string "sha" item
                if mode <> "100644" || kind <> "blob" || not (lowerHex 40 sha) then refuse "invalid-tree-entry"
                mode, path, sha)
        if entries |> List.map (fun (_, path, _) -> path) <> [ "event.json"; "head.json" ] then
            refuse "unsupported-journal-tree"
        if gitTreeSha entries <> oid then refuse "tree-hash-drift"
        read, entries

    let private commitBytes (root: JsonElement) =
        let identity name =
            let value = property name root
            let actorName = string "name" value
            let email = string "email" value
            let dateText = string "date" value
            let mutable date = DateTimeOffset.MinValue
            if not (DateTimeOffset.TryParse(dateText, Globalization.CultureInfo.InvariantCulture,
                                            Globalization.DateTimeStyles.RoundtripKind, &date)) then
                refuse "invalid:commit-date"
            let offset = date.ToString("zzz", Globalization.CultureInfo.InvariantCulture).Replace(":", "")
            $"{actorName} <{email}> {date.ToUnixTimeSeconds()} {offset}"
        let treeSha = property "tree" root |> string "sha"
        let parents = property "parents" root |> jsonArray |> List.map (string "sha")
        let message = string "message" root
        let author = identity "author"
        let committer = identity "committer"
        let unsigned =
            [ $"tree {treeSha}" ] @ (parents |> List.map (fun value -> $"parent {value}"))
            @ [ $"author {author}"; $"committer {committer}"; ""; message ]
            |> String.concat "\n" |> Encoding.UTF8.GetBytes
        let mutable verification = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty("verification", &verification) && verification.ValueKind = JsonValueKind.Object then
            match optionalString "payload" verification, optionalString "signature" verification with
            | Some payload, Some signature ->
                let boundary = payload.IndexOf("\n\n", StringComparison.Ordinal)
                if boundary < 0 then refuse "invalid-signed-commit"
                (payload.Substring(0, boundary) + "\ngpgsig " + signature.Replace("\n", "\n ") + payload.Substring(boundary))
                |> Encoding.UTF8.GetBytes
            | None, None -> unsigned
            | _ -> refuse "invalid-signed-commit"
        else unsigned

    let private commit (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (expected: string) : MigrationReviewDeliveryRead * string * string option =
        let read = send options transport $"{repositoryPath options}/git/commits/{expected}"
        use document = parse read.RawBody
        let root = document.RootElement
        let sha = string "sha" root
        let treeSha = property "tree" root |> string "sha"
        let parents = property "parents" root |> jsonArray |> List.map (string "sha")
        if sha <> expected || not (lowerHex 40 treeSha) || parents |> List.exists (lowerHex 40 >> not) then
            refuse "commit-identity-drift"
        if parents.Length > 1 then refuse "non-linear-journal-history"
        if gitSha1 "commit" (commitBytes root) <> sha then refuse "commit-hash-drift"
        read, treeSha, List.tryHead parents

    let private reviewRecord (eventBytes: byte[]) : MigrationJournalDecodedRecord =
        use document = parse (Encoding.UTF8.GetString eventBytes)
        let root = document.RootElement
        let typedVerdict =
            match string "verdict" root with
            | "pending" -> ReviewPending
            | "pass" -> ReviewPass
            | "changes-required" -> ReviewChangesRequired
            | _ -> refuse "unknown-review-verdict"
        let record: ReviewAuthorityRecord =
            { SchemaVersion = int (jsonInt64 "schemaVersion" root); ChainId = string "chainId" root
              EpochKey = string "epochKey" root; SnapshotDigest = string "snapshotDigest" root
              AccountableAuthority = string "accountableAuthority" root; PhaseSeat = string "phaseSeat" root
              SeatOrdinal = jsonInt64 "seatOrdinal" root; Verdict = typedVerdict; OperationId = string "operationId" root }
        match ReviewDeliveryAdapter.reviewAuthorityBytes record with
        | Ok canonical when canonical = eventBytes ->
            { Schema = "fsgg.coordination.review-authority/1"; Kind = "review"; Subject = record.ChainId
              OperationId = record.OperationId; Generation = 0L; MergeCommit = None; ProtectedRunId = None
              ProtectedRunCommit = None; ProtectedRunConclusion = None }
        | _ -> refuse "invalid-review-record"

    let private positiveOptionalInt64 (name: string) (root: JsonElement) =
        let value = property name root
        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.Number ->
            let mutable parsed = 0L
            if not (value.TryGetInt64(&parsed)) || parsed <= 0L then refuse $"invalid:{name}"
            Some parsed
        | _ -> refuse $"invalid:{name}"

    let private deliveryRecord (eventBytes: byte[]) : MigrationJournalDecodedRecord =
        use document = parse (Encoding.UTF8.GetString eventBytes)
        let root = document.RootElement
        let kindText = string "kind" root
        let kind =
            match kindText with
            | "genesis" -> DeliveryGenesis
            | "delivery" -> DeliveryReceipt
            | "done" -> DoneReceipt
            | _ -> refuse "unknown-delivery-kind"
        let record: DeliveryAuthorityRecord =
            { SchemaVersion = int (jsonInt64 "schemaVersion" root); Subject = string "subject" root; Kind = kind
              ReviewChainId = jsonString "reviewChainId" root; ReviewEpochKey = jsonString "reviewEpochKey" root
              ReviewSeat = jsonString "reviewSeat" root; MergeCommit = jsonString "mergeCommit" root
              ProtectedRunId = positiveOptionalInt64 "protectedRunId" root
              ProtectedRunCommit = optionalString "protectedRunCommit" root
              ProtectedRunConclusion = optionalString "protectedRunConclusion" root
              OperationId = string "operationId" root }
        match ReviewDeliveryAdapter.deliveryAuthorityBytes record with
        | Ok canonical when canonical = eventBytes ->
            { Schema = "fsgg.coordination.delivery-authority/1"; Kind = kindText; Subject = record.Subject
              OperationId = record.OperationId; Generation = 0L
              MergeCommit = if nonblank record.MergeCommit then Some record.MergeCommit else None
              ProtectedRunId = record.ProtectedRunId; ProtectedRunCommit = record.ProtectedRunCommit
              ProtectedRunConclusion = record.ProtectedRunConclusion }
        | _ -> refuse "invalid-delivery-record"

    let private headAndRecord (refName: string) (headBytes: byte[]) (eventBytes: byte[]) : JournalHead * MigrationJournalDecodedRecord =
        use document = parse (Encoding.UTF8.GetString headBytes)
        let root = document.RootElement
        let kind =
            match string "journalKind" root with
            | "review" -> JournalKind.Review
            | "operation" -> JournalKind.Operation
            | _ -> refuse "unknown-journal-kind"
        let address =
            ShardedJournalAdapter.address kind (string "aggregateId" root)
            |> Result.defaultWith (fun _ -> refuse "invalid-journal-address")
        let generation = jsonInt64 "generation" root
        let eventDigest = string "eventDigest" root
        if address.Ref <> refName || address.Digest <> string "aggregateDigest" root || address.Shard <> string "shard" root
           || jsonInt64 "schemaVersion" root <> 1L || generation < 1L || optionalString "snapshotDigest" root |> Option.isSome
           || eventDigest <> sha256 eventBytes then refuse "journal-head-drift"
        let unsigned =
            { SchemaVersion = 1; Address = address; Generation = generation; EventDigest = eventDigest
              SnapshotDigest = None; Terminal = boolean "terminal" root
              PriorHeadDigest = optionalString "priorHeadDigest" root; HeadDigest = String.replicate 64 "0" }
        let head = { unsigned with HeadDigest = sha256 headBytes }
        if ShardedJournalAdapter.journalHeadBytes head <> headBytes then refuse "journal-head-bytes-drift"
        let decoded = if kind = JournalKind.Review then reviewRecord eventBytes else deliveryRecord eventBytes
        let expectedAddress =
            match kind with
            | JournalKind.Review ->
                ReviewDeliveryAdapter.reviewAddress decoded.Subject
                |> Result.mapError (fun _ -> "review")
            | JournalKind.Operation ->
                ReviewDeliveryAdapter.deliveryAddress decoded.Subject
                |> Result.mapError (fun _ -> "delivery")
            | _ -> Error "kind"
        match expectedAddress with
        | Ok expected when expected = address -> ()
        | _ -> refuse "journal-subject-address-drift"
        head, { decoded with Generation = generation }

    let private history (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (journalRef: MigrationJournalRef) : MigrationJournalHistoryEntry list =
        let rec walk seen current acc =
            if Set.contains current seen then refuse "commit-cycle"
            let commitRead, treeSha, parent = commit options transport current
            let treeRead, entries = tree options transport treeSha
            let find path = entries |> List.find (fun (_, item, _) -> item = path) |> fun (_, _, sha) -> sha
            let eventRead, eventBytes = blob options transport (find "event.json")
            let headRead, headBytes = blob options transport (find "head.json")
            let head, record = headAndRecord journalRef.RefName headBytes eventBytes
            let journalCommit =
                { CommitOid = current; ParentOid = parent; TreeOid = treeSha; OperationId = record.OperationId
                  Head = head; HeadBytes = headBytes; Event = { Bytes = eventBytes; Digest = sha256 eventBytes }
                  Checkpoint = None }
            let entry =
                { RefName = journalRef.RefName; CommitSha = current; ParentSha = parent; TreeSha = treeSha
                  HeadPath = "head.json"; EventPath = "event.json"; Reads = [ commitRead; treeRead; eventRead; headRead ]
                  Record = record }
            match parent with
            | None -> List.rev ((entry, journalCommit) :: acc)
            | Some value -> walk (Set.add current seen) value ((entry, journalCommit) :: acc)
        let pairs = walk Set.empty journalRef.HeadSha []
        let entries, commits = pairs |> List.unzip
        match ShardedJournalAdapter.validate commits.Head.Head.Address (JournalComplete(journalRef.HeadSha, commits)) with
        | Error failure -> refuse $"invalid-journal-history:{failure}"
        | Ok snapshot when snapshot.Current.CommitOid <> journalRef.HeadSha -> refuse "journal-head-drift"
        | Ok _ -> entries

    let private capturePass (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) : MigrationJournalPass =
        let repository = repository options transport
        let namespaces = MigrationReviewDeliveryCaptureContract.journalRefPrefixes |> List.map (namespaceCensus options transport)
        let refs = namespaces |> List.collect _.Refs
        if not (refs |> List.map _.RefName |> unique) then refuse "duplicate-journal-ref"
        let captured = refs |> List.collect (history options transport)
        let reviewSubjects =
            captured
            |> List.filter (fun item -> item.Record.Schema = "fsgg.coordination.delivery-authority/1")
            |> List.map (fun item ->
                let chain =
                    ReviewDeliveryAdapter.chainId item.Record.Subject
                    |> Result.defaultWith (fun _ -> refuse "invalid-delivery-subject")
                chain, item.Record.Subject)
            |> List.distinct
            |> List.groupBy fst
            |> List.map (fun (chain, values) -> chain, values |> List.map snd |> List.distinct)
            |> Map.ofList
        let histories =
            captured |> List.map (fun item ->
                if item.Record.Schema <> "fsgg.coordination.review-authority/1" then item
                else
                    match Map.tryFind item.Record.Subject reviewSubjects with
                    | Some [ subject ] -> { item with Record = { item.Record with Subject = subject } }
                    | _ -> refuse "unbound-review-subject")
        let partial = { Repository = repository; Namespaces = namespaces; Histories = histories; Fingerprint = "" }
        { partial with Fingerprint = MigrationReviewDeliveryCaptureContract.journalFingerprint partial }

    let captureTwoPass (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if obj.ReferenceEquals(options, null) || obj.ReferenceEquals(transport, null) || not (validOptions options) then
            Error "invalid:journal-capture-options"
        else
            try
                let value: MigrationJournalTwoPass = { First = capturePass options transport; Second = capturePass options transport }
                MigrationReviewDeliveryCaptureContract.validateJournalTwoPass value
            with
            | CaptureFailure reason -> Error reason
            | :? InvalidOperationException | :? OverflowException -> Error "invalid-journal-record"
