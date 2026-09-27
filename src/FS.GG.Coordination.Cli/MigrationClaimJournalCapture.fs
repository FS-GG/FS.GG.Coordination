namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationClaimJournalCapture =
    exception private CaptureFailure of string
    let private refuse reason = raise (CaptureFailure reason)

    let private nonblank (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private lowerHex length (value: string) =
        nonblank value
        && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private unique values =
        List.length values = (values |> Set.ofList |> Set.count)

    let private sha256 (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private rawSha (value: string) =
        value |> Encoding.UTF8.GetBytes |> sha256

    let private gitSha1 kind (bytes: byte array) =
        Array.append (Encoding.ASCII.GetBytes($"{kind} {bytes.LongLength}\u0000")) bytes
        |> SHA1.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>

        if
            value.ValueKind <> JsonValueKind.Object
            || not (value.TryGetProperty(name, &found))
        then
            refuse $"missing:{name}"

        found

    let private requiredString (name: string) (value: JsonElement) =
        let found = property name value

        if found.ValueKind <> JsonValueKind.String || not (nonblank (found.GetString())) then
            refuse $"invalid:{name}"

        found.GetString()

    let private nullableString (name: string) (value: JsonElement) =
        let found = property name value

        match found.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String when nonblank (found.GetString()) -> Some(found.GetString())
        | _ -> refuse $"invalid:{name}"

    let private int64 (name: string) (value: JsonElement) =
        let found = property name value
        let mutable parsed = 0L

        if found.ValueKind <> JsonValueKind.Number || not (found.TryGetInt64(&parsed)) then
            refuse $"invalid:{name}"

        parsed

    let private boolean (name: string) (value: JsonElement) =
        match property name value |> _.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> refuse $"invalid:{name}"

    let private array (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Array then
            refuse "invalid:array"

        value.EnumerateArray() |> Seq.toList

    let private exactObject (expected: string list) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            refuse "invalid:object"

        let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList

        if not (unique names) || Set.ofList names <> Set.ofList expected then
            refuse "unknown-schema-layout"

    let private parseString (body: string) =
        try
            JsonDocument.Parse body
        with :? JsonException ->
            refuse "invalid:json"

    let private parseBytes (bytes: byte array) =
        try
            JsonDocument.Parse bytes
        with :? JsonException ->
            refuse "invalid:json"

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase)
        && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && String.IsNullOrEmpty options.ApiBase.Query
        && String.IsNullOrEmpty options.ApiBase.Fragment
        && nonblank options.UserAgent
        && nonblank options.Owner
        && nonblank options.Repository
        && not (options.Owner.Contains('/'))
        && not (options.Repository.Contains('/'))
        && options.ExpectedRepositoryId > 0L

    let private headers (options: MigrationGitHubReadOptions) =
        [
            "accept", "application/vnd.github+json"
            "x-github-api-version", ApiVersion.value ApiVersion.required
            "user-agent", options.UserAgent
            if nonblank options.Token then
                "authorization", $"Bearer {options.Token}"
        ]
        |> Map.ofList

    let private repositoryPath (options: MigrationGitHubReadOptions) =
        $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"

    let private makeRequest (options: MigrationGitHubReadOptions) (relative: string) =
        let request =
            Rest
                {
                    Method = Get
                    Uri = Uri(options.ApiBase, relative)
                    Headers = headers options
                    Body = None
                    ApiVersion = ApiVersion.required
                    Idempotency = ReplaySafe
                }

        match request with
        | Rest value when value.Method = Get && value.Body.IsNone && value.Uri.Scheme = Uri.UriSchemeHttps -> request
        | _ -> refuse "write-shaped-request"

    let private hasLink (headers: Map<string, string>) =
        headers
        |> Map.exists (fun name _ -> String.Equals(name, "link", StringComparison.OrdinalIgnoreCase))

    let private send options (transport: IMigrationGitHubReadTransport) relative : MigrationReviewDeliveryRead =
        let request = makeRequest options relative

        match transport.Send request with
        | Response response when response.StatusCode = 200 ->
            if isNull response.Body then
                refuse "invalid:null-body"

            if hasLink response.Headers then
                refuse "unexpected-pagination"

            let captured = MigrationReviewDeliveryCaptureContract.captureRequest request

            {
                Request = captured
                RequestSha256 = MigrationReviewDeliveryCaptureContract.requestSha256 captured
                StatusCode = response.StatusCode
                ResponseHeaders = response.Headers
                RawBody = response.Body
                RawSha256 = rawSha response.Body
                NextRequestUri = None
            }
        | Response response -> refuse $"github-read:{response.StatusCode}"
        | NetworkFailure
        | TimedOut -> refuse "transport-unavailable"

    let private repository options transport : MigrationReviewDeliveryRepository =
        let read = send options transport (repositoryPath options)
        use document = parseString read.RawBody
        let root = document.RootElement

        let id, nodeId, fullName =
            int64 "id" root, requiredString "node_id" root, requiredString "full_name" root

        if
            id <> options.ExpectedRepositoryId
            || fullName <> $"{options.Owner}/{options.Repository}"
        then
            refuse "repository-identity-drift"

        {
            RepositoryId = id
            NodeId = nodeId
            FullName = fullName
            Read = read
        }

    let private namespaceCensus
        (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport)
        namespace'
        =
        let prefix = MigrationClaimEventCaptureContract.namespacePrefix namespace'
        let relative = prefix.Substring("refs/".Length)

        let read =
            send options transport $"{repositoryPath options}/git/matching-refs/{relative}"

        use document = parseString read.RawBody

        let refs =
            document.RootElement
            |> array
            |> List.map (fun item ->
                exactObject [ "ref"; "object" ] item
                let name = requiredString "ref" item

                if
                    not (name.StartsWith(prefix, StringComparison.Ordinal))
                    || not (lowerHex 2 (name.Substring(prefix.Length)))
                then
                    refuse "invalid-journal-ref"

                let target = property "object" item
                exactObject [ "type"; "sha" ] target

                if requiredString "type" target <> "commit" then
                    refuse "invalid-ref-object"

                let head = requiredString "sha" target

                if not (lowerHex 40 head) then
                    refuse "invalid-ref-head"

                {
                    ClaimRefName = name
                    ClaimHeadSha = head
                })
            |> List.sortBy _.ClaimRefName

        if refs |> List.map _.ClaimRefName |> unique |> not then
            refuse "duplicate-journal-ref"

        {
            ClaimNamespace = namespace'
            ClaimPrefix = prefix
            ClaimNamespaceReads = [ read ]
            ClaimRefs = refs
        }

    let private decodeBase64 (value: string) =
        try
            Convert.FromBase64String(value.Replace("\n", ""))
        with :? FormatException ->
            refuse "invalid:base64"

    let private blob options transport oid =
        let read = send options transport $"{repositoryPath options}/git/blobs/{oid}"
        use document = parseString read.RawBody
        let root = document.RootElement

        if requiredString "sha" root <> oid || requiredString "encoding" root <> "base64" then
            refuse "blob-identity-drift"

        let bytes = requiredString "content" root |> decodeBase64

        if
            int64 "size" root <> Microsoft.FSharp.Core.Operators.int64 bytes.LongLength
            || gitSha1 "blob" bytes <> oid
        then
            refuse "blob-hash-drift"

        read, bytes

    let private gitTreeSha (entries: (string * string * string) list) =
        entries
        |> List.collect (fun (mode, path, oid) ->
            (Encoding.UTF8.GetBytes($"{mode} {path}\u0000") |> Array.toList)
            @ (Convert.FromHexString oid |> Array.toList))
        |> List.toArray
        |> gitSha1 "tree"

    let private tree options transport oid =
        let read = send options transport $"{repositoryPath options}/git/trees/{oid}"
        use document = parseString read.RawBody
        let root = document.RootElement

        if requiredString "sha" root <> oid || boolean "truncated" root then
            refuse "tree-identity-drift"

        let entries =
            property "tree" root
            |> array
            |> List.map (fun item ->
                let mode, path, kind, sha =
                    requiredString "mode" item,
                    requiredString "path" item,
                    requiredString "type" item,
                    requiredString "sha" item

                if mode <> "100644" || kind <> "blob" || not (lowerHex 40 sha) then
                    refuse "invalid-tree-entry"

                mode, path, sha)

        if entries |> List.map (fun (_, path, _) -> path) <> [ "event.json"; "head.json" ] then
            refuse "unsupported-journal-tree"

        if gitTreeSha entries <> oid then
            refuse "tree-hash-drift"

        read, entries

    let private commitBytes (root: JsonElement) =
        let identity name =
            let value = property name root

            let actorName, email, dateText =
                requiredString "name" value, requiredString "email" value, requiredString "date" value

            let mutable date = DateTimeOffset.MinValue

            if
                not (
                    DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &date)
                )
            then
                refuse "invalid:commit-date"

            let seconds = date.ToUnixTimeSeconds()
            let offset = date.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "")
            $"{actorName} <{email}> {seconds} {offset}"

        let treeSha = property "tree" root |> requiredString "sha"
        let parents = property "parents" root |> array |> List.map (requiredString "sha")
        let message = requiredString "message" root
        let author = identity "author"
        let committer = identity "committer"

        let unsigned =
            [ $"tree {treeSha}" ]
            @ (parents |> List.map (fun value -> $"parent {value}"))
            @ [ $"author {author}"; $"committer {committer}"; ""; message ]
            |> String.concat "\n"
            |> Encoding.UTF8.GetBytes

        let mutable verification = Unchecked.defaultof<JsonElement>

        if
            root.TryGetProperty("verification", &verification)
            && verification.ValueKind = JsonValueKind.Object
        then
            match nullableString "payload" verification, nullableString "signature" verification with
            | Some payload, Some signature ->
                let boundary = payload.IndexOf("\n\n", StringComparison.Ordinal)

                if boundary < 0 then
                    refuse "invalid-signed-commit"

                (payload.Substring(0, boundary)
                 + "\ngpgsig "
                 + signature.Replace("\n", "\n ")
                 + payload.Substring(boundary))
                |> Encoding.UTF8.GetBytes
            | None, None -> unsigned
            | _ -> refuse "invalid-signed-commit"
        else
            unsigned

    let private commit options transport expected =
        let read = send options transport $"{repositoryPath options}/git/commits/{expected}"
        use document = parseString read.RawBody
        let root = document.RootElement

        let sha, treeSha =
            requiredString "sha" root, property "tree" root |> requiredString "sha"

        let parents = property "parents" root |> array |> List.map (requiredString "sha")

        if
            sha <> expected
            || not (lowerHex 40 treeSha)
            || parents |> List.exists (lowerHex 40 >> not)
        then
            refuse "commit-identity-drift"

        if parents.Length > 1 then
            refuse "non-linear-journal-history"

        if gitSha1 "commit" (commitBytes root) <> sha then
            refuse "commit-hash-drift"

        read, treeSha, List.tryHead parents

    let private canonicalBytes (bytes: byte array) =
        try
            UTF8Encoding(false, true).GetString bytes |> ShardedJournalAdapter.canonicalJson = Ok bytes
        with :? DecoderFallbackException ->
            false

    let private claimRecord (address: AggregateAddress) eventBytes =
        use document = parseBytes eventBytes
        let root = document.RootElement

        exactObject
            [
                "leaseExpiresAt"
                "operationId"
                "owner"
                "schemaVersion"
                "subject"
                "touches"
            ]
            root

        let touches =
            property "touches" root
            |> array
            |> List.map (fun value ->
                exactObject [ "path"; "repository" ] value

                {
                    Repository = requiredString "repository" value
                    Path = requiredString "path" value
                })

        let record: ClaimAuthorityRecord =
            {
                SchemaVersion = int (int64 "schemaVersion" root)
                Subject = requiredString "subject" root
                Owner = requiredString "owner" root
                Touches = touches
                LeaseExpiresAt = int64 "leaseExpiresAt" root
                OperationId = requiredString "operationId" root
            }

        match ClaimTouchSetAdapter.authorityBytes record with
        | Ok canonical when canonical = eventBytes -> ()
        | _ -> refuse "invalid-claim-record"

        let primary = ClaimTouchSetAdapter.claimAddress record.Subject |> Result.toOption

        let conflicts =
            touches
            |> List.map ClaimTouchSetAdapter.conflictAddress
            |> List.choose Result.toOption

        if Some address <> primary && not (List.contains address conflicts) then
            refuse "claim-address-drift"

        {
            Namespace = ClaimJournalNamespace
            Schema = "fsgg.coordination.claim-authority/1"
            Family = ClaimSchemaFamily
            CanonicalId = address.CanonicalId
            OperationId = Some record.OperationId
            Generation = 0L
        }

    let private admissionRecord (address: AggregateAddress) eventBytes =
        use document = parseBytes eventBytes
        let root = document.RootElement
        let schema = requiredString "schema" root

        if schema <> "fsgg.github-substrate.admission-event/1" then
            refuse "unknown-operation-schema"

        match V1AdmissionRegistry.decodeEvent eventBytes with
        | Error _ -> refuse "invalid-admission-record"
        | Ok _ -> ()

        let expected =
            ShardedJournalAdapter.address JournalKind.Operation "fleet-v1-admission:fs-gg-production"
            |> Result.defaultWith (fun _ -> refuse "invalid-admission-address")

        if address <> expected then
            refuse "admission-address-drift"

        {
            Namespace = OperationJournalNamespace
            Schema = schema
            Family = AdmissionSchemaFamily
            CanonicalId = address.CanonicalId
            OperationId = Some(requiredString "commandId" root)
            Generation = 0L
        }

    let private ordinaryRecord (address: AggregateAddress) eventBytes =
        use document = parseBytes eventBytes
        let root = document.RootElement
        exactObject [ "generation"; "mergeCommit"; "operationId"; "planDigest"; "schema"; "stage" ] root
        let schema = requiredString "schema" root

        if schema <> "fsgg.coordination.ordinary-delivery-journal/1" then
            refuse "unknown-operation-schema"

        let generation, operationId =
            int64 "generation" root, requiredString "operationId" root

        let planDigest, mergeCommit, stage =
            requiredString "planDigest" root, nullableString "mergeCommit" root, requiredString "stage" root

        if
            not (canonicalBytes eventBytes)
            || generation < 1L
            || not (lowerHex 64 planDigest)
            || not (address.CanonicalId.StartsWith("ordinary:", StringComparison.Ordinal))
        then
            refuse "invalid-ordinary-record"

        match stage, mergeCommit with
        | ("intent-persisted" | "effect-pending"), None -> ()
        | "settled", Some oid when lowerHex 40 oid -> ()
        | _ -> refuse "invalid-ordinary-record"

        {
            Namespace = OperationJournalNamespace
            Schema = schema
            Family = OrdinarySchemaFamily
            CanonicalId = address.CanonicalId
            OperationId = Some operationId
            Generation = generation
        }

    let private decodeRecord namespace' address eventBytes =
        match namespace' with
        | ClaimJournalNamespace -> claimRecord address eventBytes
        | OperationJournalNamespace ->
            use document = parseBytes eventBytes
            let root = document.RootElement
            let mutable schema = Unchecked.defaultof<JsonElement>

            if
                not (root.TryGetProperty("schema", &schema))
                || schema.ValueKind <> JsonValueKind.String
            then
                refuse "unknown-operation-schema"

            match schema.GetString() with
            | "fsgg.github-substrate.admission-event/1" -> admissionRecord address eventBytes
            | "fsgg.coordination.ordinary-delivery-journal/1" -> ordinaryRecord address eventBytes
            | _ -> refuse "unknown-operation-schema"

    let private headAndRecord namespace' refName headBytes eventBytes =
        use document = parseBytes headBytes
        let root = document.RootElement

        exactObject
            [
                "aggregateDigest"
                "aggregateId"
                "eventDigest"
                "generation"
                "journalKind"
                "priorHeadDigest"
                "schemaVersion"
                "shard"
                "snapshotDigest"
                "terminal"
            ]
            root

        let expectedKind, expectedKindText =
            match namespace' with
            | ClaimJournalNamespace -> JournalKind.Claim, "claim"
            | OperationJournalNamespace -> JournalKind.Operation, "operation"

        if requiredString "journalKind" root <> expectedKindText then
            refuse "journal-namespace-drift"

        let address =
            ShardedJournalAdapter.address expectedKind (requiredString "aggregateId" root)
            |> Result.defaultWith (fun _ -> refuse "invalid-journal-address")

        let generation, eventDigest =
            int64 "generation" root, requiredString "eventDigest" root

        if nullableString "snapshotDigest" root |> Option.isSome then
            refuse "unsupported-journal-checkpoint"

        if
            address.Ref <> refName
            || address.Digest <> requiredString "aggregateDigest" root
            || address.Shard <> requiredString "shard" root
            || int64 "schemaVersion" root <> 1L
            || generation < 1L
            || eventDigest <> sha256 eventBytes
        then
            refuse "journal-head-drift"

        let unsigned =
            {
                SchemaVersion = 1
                Address = address
                Generation = generation
                EventDigest = eventDigest
                SnapshotDigest = None
                Terminal = boolean "terminal" root
                PriorHeadDigest = nullableString "priorHeadDigest" root
                HeadDigest = String.replicate 64 "0"
            }

        let head =
            { unsigned with
                HeadDigest = sha256 headBytes
            }

        if ShardedJournalAdapter.journalHeadBytes head <> headBytes then
            refuse "journal-head-bytes-drift"

        let record = decodeRecord namespace' address eventBytes

        if record.Generation <> 0L && record.Generation <> generation then
            refuse "record-generation-drift"

        head, { record with Generation = generation }

    let private history options transport namespace' (journalRef: MigrationClaimJournalRef) =
        let rec walk seen current acc =
            if Set.contains current seen then
                refuse "commit-cycle"

            let commitRead, treeSha, parent = commit options transport current
            let treeRead, entries = tree options transport treeSha

            let find path =
                entries
                |> List.tryFind (fun (_, item, _) -> item = path)
                |> Option.map (fun (_, _, sha) -> sha)
                |> Option.defaultWith (fun () -> refuse "missing-journal-blob")

            let eventRead, eventBytes = blob options transport (find "event.json")
            let headRead, headBytes = blob options transport (find "head.json")

            let head, record =
                headAndRecord namespace' journalRef.ClaimRefName headBytes eventBytes

            if record.OperationId.IsNone then
                refuse "missing-operation-id"

            let journalCommit =
                {
                    CommitOid = current
                    ParentOid = parent
                    TreeOid = treeSha
                    OperationId = record.OperationId.Value
                    Head = head
                    HeadBytes = headBytes
                    Event =
                        {
                            Bytes = eventBytes
                            Digest = sha256 eventBytes
                        }
                    Checkpoint = None
                }

            let entry =
                {
                    ClaimCommitSha = current
                    ClaimParentSha = parent
                    ClaimTreeSha = treeSha
                    ClaimReads = [ commitRead; treeRead; eventRead; headRead ]
                    ClaimRecord = record
                }

            match parent with
            | None -> (entry, journalCommit) :: acc
            | Some value -> walk (Set.add current seen) value ((entry, journalCommit) :: acc)

        let rootFirst = walk Set.empty journalRef.ClaimHeadSha []
        let commits = rootFirst |> List.map snd

        match
            ShardedJournalAdapter.validate commits.Head.Head.Address (JournalComplete(journalRef.ClaimHeadSha, commits))
        with
        | Error failure -> refuse $"invalid-journal-history:{failure}"
        | Ok snapshot when snapshot.Current.CommitOid <> journalRef.ClaimHeadSha -> refuse "journal-head-drift"
        | Ok _ -> ()

        {
            ClaimHistoryRefName = journalRef.ClaimRefName
            ClaimHistoryHeadSha = journalRef.ClaimHeadSha
            ClaimEntries = rootFirst |> List.map fst |> List.rev
        }

    let private capturePass options transport =
        let repository = repository options transport

        let namespaces =
            [ ClaimJournalNamespace; OperationJournalNamespace ]
            |> List.map (namespaceCensus options transport)

        let refs =
            namespaces
            |> List.collect (fun census -> census.ClaimRefs |> List.map (fun item -> census.ClaimNamespace, item))

        if refs |> List.map (snd >> _.ClaimRefName) |> unique |> not then
            refuse "duplicate-journal-ref"

        let histories =
            refs
            |> List.map (fun (namespace', item) -> history options transport namespace' item)

        let partial =
            {
                ClaimRepository = repository
                ClaimNamespaces = namespaces
                ClaimHistories = histories
                ClaimFingerprint = ""
            }

        { partial with
            ClaimFingerprint = MigrationClaimEventCaptureContract.passFingerprint partial
        }

    let captureTwoPass (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if
            obj.ReferenceEquals(options, null)
            || obj.ReferenceEquals(transport, null)
            || not (validOptions options)
        then
            Error "invalid:claim-journal-capture-options"
        else
            try
                {
                    ClaimFirst = capturePass options transport
                    ClaimSecond = capturePass options transport
                }
                |> MigrationClaimEventCaptureContract.validateTwoPass
            with
            | CaptureFailure reason -> Error reason
            | :? InvalidOperationException
            | :? OverflowException
            | :? FormatException -> Error "invalid-claim-journal-record"
