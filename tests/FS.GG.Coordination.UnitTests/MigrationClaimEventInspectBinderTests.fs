module FS.GG.Coordination.MigrationClaimEventInspectBinderTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli

let private shaText (value: string) =
    value
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private gitSha kind (bytes: byte array) =
    Array.append (Encoding.ASCII.GetBytes($"{kind} {bytes.LongLength}\u0000")) bytes
    |> SHA1.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private options =
    {
        ApiBase = Uri "https://api.github.test/"
        GraphQLUri = Uri "https://api.github.test/graphql"
        Owner = "FS-GG"
        Repository = "copy"
        ExpectedRepositoryId = 42L
        Token = "test"
        UserAgent = "claim-event-binder-test"
    }

let private page path =
    let query =
        if path = "issues" || path = "pulls" then
            "?state=all&per_page=100"
        else
            "?per_page=100"

    {
        RequestedUri = "https://api.github.test/repos/FS-GG/copy/" + path + query
        PayloadSha256 = shaText "[]"
        NextUri = None
    }

let private stamp = DateTimeOffset.Parse "2026-09-24T10:00:00Z"
let private operation = "0123456789abcdef0123456789abcdef"

let private native markerBody =
    let commentRaw =
        JsonSerializer.Serialize(
            {|
                id = 201L
                node_id = "IC_201"
                issue_url = "https://api.github.test/repos/FS-GG/copy/issues/7"
                body = markerBody
                created_at = "2026-09-24T10:00:00Z"
                updated_at = "2026-09-24T10:00:00Z"
                user = {| login = "worker-a" |}
            |}
        )

    let input: MigrationNativeActivityInput =
        {
            Issues =
                {
                    RepositoryId = 42L
                    PageCount = 1
                    Terminal = true
                    Pages = [ page "issues" ]
                    Issues =
                        [
                            {
                                Number = 7
                                DatabaseId = 107L
                                NodeId = "I_7"
                                State = "open"
                                UpdatedAt = stamp
                                PayloadJson = "{}"
                                PayloadSha256 = shaText "{}"
                            }
                        ]
                    PullRequestCount = 0
                    PullRequestMarkerNumbers = []
                }
            PullRequests =
                {
                    RepositoryId = 42L
                    PageCount = 1
                    Terminal = true
                    Pages = [ page "pulls" ]
                    PullRequests = []
                }
            IssueComments =
                [
                    {
                        RepositoryId = 42L
                        SubjectNumber = 7
                        SubjectNodeId = "I_7"
                        PageCount = 1
                        Terminal = true
                        Pages = [ page "issues/7/comments" ]
                        Comments =
                            [
                                {
                                    DatabaseId = 201L
                                    NodeId = "IC_201"
                                    SubjectNumber = 7
                                    ActorLogin = "worker-a"
                                    CreatedAt = stamp
                                    UpdatedAt = stamp
                                    Body = markerBody
                                    PayloadJson = commentRaw
                                    PayloadSha256 = shaText commentRaw
                                }
                            ]
                    }
                ]
            IssueEvents =
                [
                    {
                        RepositoryId = 42L
                        SubjectNumber = 7
                        SubjectNodeId = "I_7"
                        PageCount = 1
                        Terminal = true
                        Pages = [ page "issues/7/events" ]
                        Events = []
                    }
                ]
            IssueTimelines =
                [
                    {
                        RepositoryId = 42L
                        SubjectNumber = 7
                        SubjectNodeId = "I_7"
                        PageCount = 1
                        Terminal = true
                        Pages = [ page "issues/7/timeline" ]
                        Records = []
                    }
                ]
            PullRequestComments = []
            PullRequestEvents = []
            PullRequestTimelines = []
            PullRequestReviews = []
            PullRequestInlineComments = []
        }

    let snapshot =
        MigrationNativeActivity.reconcile options input
        |> Result.defaultWith (failwithf "%A")

    { Input = input; Snapshot = snapshot }

let private ok body =
    Response
        {
            StatusCode = 200
            Headers = Map.empty
            Body = body
            ETag = None
            RateBudget =
                {
                    Limit = Some 5000
                    Remaining = Some 4999
                    ResetAt = None
                    Cost = Some 1
                }
        }

type private FakeTransport(route: GitHubRequest -> TransportOutcome) =
    interface IMigrationGitHubReadTransport with
        member _.Send request = route request

let private requestPath =
    function
    | Rest request -> request.Uri.AbsolutePath
    | GraphQL _ -> failwith "unexpected GraphQL"

type private JournalFixture =
    {
        Address: AggregateAddress
        EventBytes: byte array
        EventOid: string
        HeadBytes: byte array
        HeadOid: string
        Tree: string
        Commit: string
    }

let private journalFixture () =
    let record: ClaimAuthorityRecord =
        {
            SchemaVersion = 1
            Subject = "FS-GG/copy#7"
            Owner = "worker-a"
            Touches =
                [
                    {
                        Repository = "FS-GG/copy"
                        Path = "src/Claims"
                    }
                ]
            LeaseExpiresAt = 100L
            OperationId = operation
        }

    let address =
        ClaimTouchSetAdapter.claimAddress record.Subject
        |> Result.defaultWith (failwithf "%A")

    let eventBytes =
        ClaimTouchSetAdapter.authorityBytes record
        |> Result.defaultWith (failwithf "%A")

    let eventOid = gitSha "blob" eventBytes

    let unsigned: JournalHead =
        {
            SchemaVersion = 1
            Address = address
            Generation = 1L
            EventDigest = ShardedJournalAdapter.sha256 eventBytes
            SnapshotDigest = None
            Terminal = false
            PriorHeadDigest = None
            HeadDigest = String.replicate 64 "0"
        }

    let initial = ShardedJournalAdapter.journalHeadBytes unsigned

    let headBytes =
        ShardedJournalAdapter.journalHeadBytes
            { unsigned with
                HeadDigest = ShardedJournalAdapter.sha256 initial
            }

    let headOid = gitSha "blob" headBytes

    let treeBytes =
        [ "event.json", eventOid; "head.json", headOid ]
        |> List.collect (fun (name, oid) ->
            (Encoding.UTF8.GetBytes($"100644 {name}\u0000") |> Array.toList)
            @ (Convert.FromHexString oid |> Array.toList))
        |> List.toArray

    let tree = gitSha "tree" treeBytes

    let commitBytes =
        String.concat
            "\n"
            [
                $"tree {tree}"
                "author Test <test@example.com> 0 +0000"
                "committer Test <test@example.com> 0 +0000"
                ""
                "claim journal"
            ]
        |> Encoding.UTF8.GetBytes

    {
        Address = address
        EventBytes = eventBytes
        EventOid = eventOid
        HeadBytes = headBytes
        HeadOid = headOid
        Tree = tree
        Commit = gitSha "commit" commitBytes
    }

let private blobBody oid (bytes: byte array) =
    $"""{{"sha":"{oid}","encoding":"base64","content":"{Convert.ToBase64String bytes}","size":{bytes.Length}}}"""

let private journals () =
    let fixture = journalFixture ()

    let route request =
        let path = requestPath request
        let relative = fixture.Address.Ref.Substring("refs/".Length)

        let refBody =
            $"""[{{"ref":"{fixture.Address.Ref}","node_id":"REF_1","url":"https://api.github.test/repos/FS-GG/copy/git/refs/{relative}","object":{{"type":"commit","sha":"{fixture.Commit}","url":"https://api.github.test/repos/FS-GG/copy/git/commits/{fixture.Commit}"}}}}]"""

        if path = "/repos/FS-GG/copy" then
            ok """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""
        elif path.EndsWith("/claim/", StringComparison.Ordinal) then
            ok refBody
        elif path.EndsWith("/operation/", StringComparison.Ordinal) then
            ok "[]"
        elif path.EndsWith("/git/commits/" + fixture.Commit, StringComparison.Ordinal) then
            ok (
                $"""{{"sha":"{fixture.Commit}","tree":{{"sha":"{fixture.Tree}"}},"parents":[],"author":{{"name":"Test","email":"test@example.com","date":"1970-01-01T00:00:00Z"}},"committer":{{"name":"Test","email":"test@example.com","date":"1970-01-01T00:00:00Z"}},"message":"claim journal"}}"""
            )
        elif path.EndsWith("/git/trees/" + fixture.Tree, StringComparison.Ordinal) then
            ok (
                $"""{{"sha":"{fixture.Tree}","truncated":false,"tree":[{{"path":"event.json","mode":"100644","type":"blob","sha":"{fixture.EventOid}"}},{{"path":"head.json","mode":"100644","type":"blob","sha":"{fixture.HeadOid}"}}]}}"""
            )
        elif path.EndsWith("/git/blobs/" + fixture.EventOid, StringComparison.Ordinal) then
            ok (blobBody fixture.EventOid fixture.EventBytes)
        elif path.EndsWith("/git/blobs/" + fixture.HeadOid, StringComparison.Ordinal) then
            ok (blobBody fixture.HeadOid fixture.HeadBytes)
        else
            failwithf "unexpected request %s" path

    MigrationClaimJournalCapture.captureTwoPass options (FakeTransport route)
    |> Result.defaultWith failwith

let private producerRevision = "ca6dd7bd5d14cd3c44f54c89ee87f602c3a3abce"

let private inventory () =
    let rows =
        MigrationClaimEventCaptureContract.requiredLegacySchemaFamilies
        |> List.map (fun family ->
            let bytes = Encoding.UTF8.GetBytes("protected source for " + family)
            let oid = gitSha "blob" bytes

            let raw =
                JsonSerializer.Serialize
                    {|
                        sha = oid
                        encoding = "base64"
                        content = Convert.ToBase64String bytes
                    |}

            let request =
                {
                    Kind = "rest"
                    Method = "Get"
                    Uri = $"https://api.github.com/repos/FS-GG/.github/contents/src/{family}.fs?ref={producerRevision}"
                    Headers = Map.empty
                    Body = None
                    Variables = Map.empty
                    ApiVersion = "2022-11-28"
                    Idempotency = "ReplaySafe"
                }

            let read =
                {
                    Request = request
                    RequestSha256 = MigrationReviewDeliveryCaptureContract.requestSha256 request
                    StatusCode = 200
                    ResponseHeaders = Map.empty
                    RawBody = raw
                    RawSha256 = shaText raw
                    NextRequestUri = None
                }

            let sourceKind =
                match family with
                | "delivery-receipt"
                | "legacy-done-receipt" -> ProtectedParserOnly
                | "intake-receipt" -> LocalCacheOnly
                | _ -> ProtectedProducer

            read,
            {
                ProducerId = "FS-GG/.github:" + family
                ProducerRevision = producerRevision
                SourceIdentity =
                    request.Uri
                    + "#sha256:"
                    + (bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant())
                SchemaFamily = family
                SourceKind = sourceKind
            })

    let partial =
        {
            ProducerReads = rows |> List.map fst
            Sources = rows |> List.map snd
            Fingerprint = ""
        }

    { partial with
        Fingerprint = MigrationClaimEventCaptureContract.legacyInventoryFingerprint partial
    }

[<Fact>]
let ``partial binder joins native claim session to protected journal and retains producer gaps`` () =
    let marker =
        $"<!-- fsgg:claim worker=worker-a lease=30 renewed=1 session={operation} -->"

    let capture =
        let activity = native marker

        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
        |> Result.defaultWith failwith

    let observed = Assert.Single capture.ClaimMarkers
    Assert.Equal(Some operation, observed.SessionOperationId)

    Assert.Contains(
        "legacy-inventory-producer-unavailable:delivery-receipt,intake-receipt,legacy-done-receipt",
        capture.MissingAuthorities
    )

    Assert.Equal(64, capture.Fingerprint.Length)

    Assert.Equal(
        Error
            "claim-event-authority-incomplete:legacy-inventory-producer-unavailable:delivery-receipt,intake-receipt,legacy-done-receipt",
        MigrationClaimEventInspectBinder.qualifyCanonical capture
    )

    Assert.Equal(
        Error "claim-event-partial-fingerprint",
        MigrationClaimEventInspectBinder.qualifyCanonical
            { capture with
                Fingerprint = String.replicate 64 "0"
            }
    )

[<Fact>]
let ``wrong journal session and malformed claim marker refuse`` () =
    let unmatched =
        "<!-- fsgg:claim worker=worker-a lease=30 renewed=1 session=ffffffffffffffffffffffffffffffff -->"

    Assert.Equal(
        Error "claim-event-journal-correspondence:ffffffffffffffffffffffffffffffff",
        let activity = native unmatched
        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
    )

    let malformed = "<!-- fsgg:claim worker=worker-a lease=thirty renewed=1 -->"

    Assert.Equal(
        Error "claim-event-unknown-claim-marker",
        let activity = native malformed
        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
    )

    let wrongOwner =
        $"<!-- fsgg:claim worker=other-worker lease=30 renewed=1 session={operation} -->"

    Assert.Equal(
        Error $"claim-event-journal-correspondence:{operation}",
        let activity = native wrongOwner
        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
    )

    Assert.Equal(
        Error "claim-event-historical-claim-parser-unavailable",
        let activity = native "<!-- C-claim worker=worker-a lease=30 -->"
        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
    )

[<Fact>]
let ``sessionless historical marker stays partial and altered source inventory refuses`` () =
    let marker = "<!-- fsgg:claim worker=worker-a lease=30 renewed=1 -->"

    let capture =
        let activity = native marker

        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
        |> Result.defaultWith failwith

    Assert.Contains("legacy-sessionless-claim-correspondence", capture.MissingAuthorities)
    let current = inventory ()
    let first = current.ProducerReads.Head

    let changed =
        { current with
            ProducerReads =
                { first with
                    RawBody = first.RawBody + " "
                }
                :: current.ProducerReads.Tail
        }

    Assert.Equal(
        Error "claim-event-legacy:legacy-inventory-source-read",
        let activity = native marker
        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) changed
    )

[<Fact>]
let ``second native pass drift refuses before correspondence`` () =
    let marker =
        $"<!-- fsgg:claim worker=worker-a lease=30 renewed=1 session={operation} -->"

    let first = native marker
    let second = native "<!-- fsgg:claim worker=worker-a lease=30 renewed=1 -->"

    Assert.Equal(
        Error "claim-event-native-pass-drift",
        MigrationClaimEventInspectBinder.bindPartial options first second (journals ()) (inventory ())
    )
