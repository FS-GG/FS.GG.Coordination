module FS.GG.Coordination.MigrationClaimEventInspectBinderTests

open System
open System.Collections.Generic
open System.IO
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
    let issueRaw =
        JsonSerializer.Serialize(
            {|
                number = 7
                id = 107L
                node_id = "I_7"
                state = "open"
                updated_at = "2026-09-24T10:00:00Z"
                body = "ordinary issue body"
            |}
        )

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
                                PayloadJson = issueRaw
                                PayloadSha256 = shaText issueRaw
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

let private withIssueBody body (capture: MigrationNativeActivityCapture) =
    let issue = capture.Input.Issues.Issues.Head

    let raw =
        JsonSerializer.Serialize(
            {|
                number = issue.Number
                id = issue.DatabaseId
                node_id = issue.NodeId
                state = issue.State
                updated_at = "2026-09-24T10:00:00Z"
                body = body
            |}
        )

    let changedIssue =
        { issue with
            PayloadJson = raw
            PayloadSha256 = shaText raw
        }

    let input =
        { capture.Input with
            Issues =
                { capture.Input.Issues with
                    Issues = [ changedIssue ]
                }
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

let private pinnedIntakeMarkerBytes =
    // Exact ca6dd7bd:src/FS.GG.Coord.Core/IntakeReceipt.fs blob (SHA-256 6ae65a6b...).
    [
            "bmFtZXNwYWNlIEZTLkdHLkNvb3JkCgptb2R1bGUgSW50YWtlUmVjZWlwdCA9CiAgICBvcGVuIFN5c3RlbS5TZWN1cml0eS5Dcnlw"
            "dG9ncmFwaHkKICAgIG9wZW4gU3lzdGVtLlRleHQKCiAgICB0eXBlIFJlY2VpcHQgPQogICAgICAgIHsKICAgICAgICAgICAgRHJh"
            "ZnRJZDogc3RyaW5nCiAgICAgICAgICAgIE93bmVyOiBzdHJpbmcKICAgICAgICAgICAgUmVwb3NpdG9yeTogc3RyaW5nCiAgICAg"
            "ICAgICAgIElzc3VlTnVtYmVyOiBpbnQKICAgICAgICAgICAgRHJhZnREaWdlc3Q6IHN0cmluZwogICAgICAgIH0KCiAgICBsZXQg"
            "cHJpdmF0ZSBkaWdlc3RXaXRoU2V2ZXJpdHkgc2V2ZXJpdHkgKGRyYWZ0OiBJbnRha2UuRHJhZnQpID0KICAgICAgICBsZXQgcGFy"
            "dHMgPQogICAgICAgICAgICBbCiAgICAgICAgICAgICAgICBkcmFmdC5TY2hlbWEKICAgICAgICAgICAgICAgIGRyYWZ0LklkCiAg"
            "ICAgICAgICAgICAgICBkcmFmdC5Pd25lcgogICAgICAgICAgICAgICAgZHJhZnQuUmVwb3NpdG9yeQogICAgICAgICAgICAgICAg"
            "ZHJhZnQuVGl0bGUKICAgICAgICAgICAgICAgIGRyYWZ0Lk9ic2VydmVkCiAgICAgICAgICAgICAgICBkcmFmdC5Sb290Q2F1c2UK"
            "ICAgICAgICAgICAgICAgIGRyYWZ0LkFjY2VwdGFuY2UKICAgICAgICAgICAgICAgIGRyYWZ0LlZlcmlmaWNhdGlvbgogICAgICAg"
            "ICAgICAgICAgU3RyaW5nLmNvbmNhdCAiXHUwMDFmIiBkcmFmdC5QYXRocwogICAgICAgICAgICAgICAgZHJhZnQuQ2xhc3MKICAg"
            "ICAgICAgICAgICAgIGRyYWZ0LlN0YXR1cwogICAgICAgICAgICAgICAgc3RyaW5nIGRyYWZ0LkRpc3Bvc2l0aW9uCiAgICAgICAg"
            "ICAgICAgICBzdHJpbmcgZHJhZnQuUGhhc2UKICAgICAgICAgICAgICAgIHN0cmluZyBzZXZlcml0eQogICAgICAgICAgICAgICAg"
            "c3RyaW5nIGRyYWZ0LkJsb2NrZWRCeQogICAgICAgICAgICAgICAgc3RyaW5nIGRyYWZ0LkJsb2NrZWRPbgogICAgICAgICAgICAg"
            "ICAgc3RyaW5nIGRyYWZ0LkJhY2tsb2dSZWFzb24KICAgICAgICAgICAgICAgIHN0cmluZyBkcmFmdC5KdWRnZW1lbnRRdWVzdGlv"
            "bgogICAgICAgICAgICBdCgogICAgICAgIFNIQTI1Ni5IYXNoRGF0YShFbmNvZGluZy5VVEY4LkdldEJ5dGVzKFN0cmluZy5jb25j"
            "YXQgIlx1MDAxZSIgcGFydHMpKQogICAgICAgIHw+IFN5c3RlbS5Db252ZXJ0LlRvSGV4U3RyaW5nCiAgICAgICAgfD4gZnVuIHZh"
            "bHVlIC0+IHZhbHVlLlRvTG93ZXJJbnZhcmlhbnQoKQoKICAgIGxldCBkaWdlc3QgKGRyYWZ0OiBJbnRha2UuRHJhZnQpID0gZGln"
            "ZXN0V2l0aFNldmVyaXR5IGRyYWZ0LlNldmVyaXR5IGRyYWZ0CgogICAgLy8gRXZlcnkgYWNjZXB0ZWQgcHJlZGVjZXNzb3IgaXMg"
            "YW4gRVhQTElDSVQgbWlncmF0aW9uLCBuZXZlciAiYW55IGRpZ2VzdCB3aXRoIHRoaXMgaWQiLiAgVGhhdCBrZWVwcwogICAgLy8g"
            "dGhlIHJlY2VpcHQgYW4gZXhhY3QgY29udGVudCBiaW5kaW5nIHdoaWxlIGFsbG93aW5nIHRoZSBkZWNvZGVyIHRvIHJlcGFpciBh"
            "IHZhbHVlIHRoYXQgYW4gb2xkZXIKICAgIC8vIHZhbGlkYXRvciBhY2NlcHRlZCBldmVuIHRob3VnaCB0aGUgYm9hcmQgY291bGQg"
            "bm90IHByb2plY3QgaXQuICBDcm9zcy1wcm9kdWN0IHRoZSB0d28gYm91bmRlZAogICAgLy8gbWlncmF0aW9ucyBiZWNhdXNlIGEg"
            "ZHJhZnQgbWF5IGhhdmUgcGFzc2VkIHRocm91Z2ggYm90aCBoaXN0b3JpY2FsIGRlZmVjdHMuCiAgICBsZXQgY29tcGF0aWJsZURy"
            "YWZ0cyAoZHJhZnQ6IEludGFrZS5EcmFmdCkgPQogICAgICAgIGxldCBjbGFzc1ZhcmlhbnRzID0KICAgICAgICAgICAgWwogICAg"
            "ICAgICAgICAgICAgeWllbGQgZHJhZnQKICAgICAgICAgICAgICAgIGlmIGRyYWZ0LkNsYXNzID0gImhhcmRlbmluZyIgdGhlbgog"
            "ICAgICAgICAgICAgICAgICAgIHlpZWxkIHsgZHJhZnQgd2l0aCBDbGFzcyA9ICJjYXBhYmlsaXR5IiB9CiAgICAgICAgICAgIF0K"
            "CiAgICAgICAgY2xhc3NWYXJpYW50cwogICAgICAgIHw+IExpc3QuY29sbGVjdCAoZnVuIGNsYXNzVmFyaWFudCAtPgogICAgICAg"
            "ICAgICBbCiAgICAgICAgICAgICAgICB5aWVsZCBjbGFzc1ZhcmlhbnQKICAgICAgICAgICAgICAgIG1hdGNoIGNsYXNzVmFyaWFu"
            "dC5TZXZlcml0eSB3aXRoCiAgICAgICAgICAgICAgICB8IFNvbWUgdmFsdWUgd2hlbiB2YWx1ZS5Ub0xvd2VySW52YXJpYW50KCkg"
            "PD4gdmFsdWUgLT4KICAgICAgICAgICAgICAgICAgICB5aWVsZAogICAgICAgICAgICAgICAgICAgICAgICB7IGNsYXNzVmFyaWFu"
            "dCB3aXRoCiAgICAgICAgICAgICAgICAgICAgICAgICAgICBTZXZlcml0eSA9IFNvbWUodmFsdWUuVG9Mb3dlckludmFyaWFudCgp"
            "KQogICAgICAgICAgICAgICAgICAgICAgICB9CiAgICAgICAgICAgICAgICB8IF8gLT4gKCkKICAgICAgICAgICAgXSkKICAgICAg"
            "ICB8PiBMaXN0LmRpc3RpbmN0CgogICAgbGV0IGNvbXBhdGlibGVEaWdlc3RzIChkcmFmdDogSW50YWtlLkRyYWZ0KSA9CiAgICAg"
            "ICAgY29tcGF0aWJsZURyYWZ0cyBkcmFmdCB8PiBMaXN0Lm1hcCBkaWdlc3QKCiAgICBsZXQgbWFya2VyIChkcmFmdDogSW50YWtl"
            "LkRyYWZ0KSA9CiAgICAgICAgJCI8IS0tIGZzZ2c6aW50YWtlOnYxIGlkPSVze2RyYWZ0LklkfSBkaWdlc3Q9JXN7ZGlnZXN0IGRy"
            "YWZ0fSAtLT4iCgogICAgbGV0IHZhbGlkYXRlIChkcmFmdDogSW50YWtlLkRyYWZ0KSAocmVjZWlwdDogUmVjZWlwdCkgPQogICAg"
            "ICAgIGlmIHJlY2VpcHQuSXNzdWVOdW1iZXIgPD0gMCB0aGVuCiAgICAgICAgICAgIEVycm9yICJyZWNlaXB0IGlzc3VlTnVtYmVy"
            "IG11c3QgYmUgcG9zaXRpdmUiCiAgICAgICAgZWxpZiByZWNlaXB0LkRyYWZ0SWQgPD4gZHJhZnQuSWQgdGhlbgogICAgICAgICAg"
            "ICBFcnJvciAicmVjZWlwdCBkcmFmdCBpZCBkb2VzIG5vdCBtYXRjaCB0aGlzIGRyYWZ0IgogICAgICAgIGVsaWYgcmVjZWlwdC5P"
            "d25lciA8PiBkcmFmdC5Pd25lciB8fCByZWNlaXB0LlJlcG9zaXRvcnkgPD4gZHJhZnQuUmVwb3NpdG9yeSB0aGVuCiAgICAgICAg"
            "ICAgIEVycm9yICJyZWNlaXB0IG93bmVyL3JlcG9zaXRvcnkgZG9lcyBub3QgbWF0Y2ggdGhpcyBkcmFmdCIKICAgICAgICBlbGlm"
            "IGNvbXBhdGlibGVEaWdlc3RzIGRyYWZ0IHw+IExpc3QuY29udGFpbnMgcmVjZWlwdC5EcmFmdERpZ2VzdCB8PiBub3QgdGhlbgog"
            "ICAgICAgICAgICBFcnJvciAicmVjZWlwdCBjb250ZW50IGRpZ2VzdCBkb2VzIG5vdCBtYXRjaCB0aGlzIGRyYWZ0IgogICAgICAg"
            "IGVsc2UKICAgICAgICAgICAgT2sgcmVjZWlwdAo="
    ]
    |> String.concat ""
    |> Convert.FromBase64String

let private producerRevision = "ca6dd7bd5d14cd3c44f54c89ee87f602c3a3abce"

let private inventory () =
    let rows =
        MigrationClaimEventCaptureContract.requiredLegacySchemaFamilies
        |> List.map (fun family ->
            let source =
                if family = "intake-marker" then
                    Encoding.UTF8.GetString pinnedIntakeMarkerBytes
                else
                    "protected source for " + family

            let bytes = Encoding.UTF8.GetBytes source
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
                    Uri =
                        if family = "intake-marker" then
                            $"https://api.github.com/repos/FS-GG/.github/contents/src/FS.GG.Coord.Core/IntakeReceipt.fs?ref={producerRevision}"
                        else
                            $"https://api.github.com/repos/FS-GG/.github/contents/src/{family}.fs?ref={producerRevision}"
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

let private inventoryWithIntakeSource uri (bytes: byte array) =
    let current = inventory ()
    let source = current.Sources |> List.find (_.SchemaFamily >> (=) "intake-marker")

    let read =
        current.ProducerReads
        |> List.find (fun value -> source.SourceIdentity.StartsWith(value.Request.Uri, StringComparison.Ordinal))

    let oid = gitSha "blob" bytes

    let raw =
        JsonSerializer.Serialize
            {|
                sha = oid
                encoding = "base64"
                content = Convert.ToBase64String bytes
            |}

    let changedRead =
        { read with
            Request = { read.Request with Uri = uri }
            RequestSha256 =
                MigrationReviewDeliveryCaptureContract.requestSha256 { read.Request with Uri = uri }
            RawBody = raw
            RawSha256 = shaText raw
        }

    let changedSource =
        { source with
            SourceIdentity =
                uri
                + "#sha256:"
                + (bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant())
        }

    let partial =
        { current with
            ProducerReads =
                current.ProducerReads
                |> List.map (fun value ->
                    if value.Request.Uri = read.Request.Uri then
                        changedRead
                    else
                        value)
            Sources =
                current.Sources
                |> List.map (fun value ->
                    if value.SchemaFamily = "intake-marker" then
                        changedSource
                    else
                        value)
            Fingerprint = ""
        }

    { partial with
        Fingerprint = MigrationClaimEventCaptureContract.legacyInventoryFingerprint partial
    }

let private inventoryWithoutIntakeProducerBytes () =
    let current = inventory ()
    let source = current.Sources |> List.find (_.SchemaFamily >> (=) "intake-marker")
    let uri = source.SourceIdentity.Split('#').[0]
    inventoryWithIntakeSource uri (Encoding.UTF8.GetBytes "intake parser without a protected marker writer")

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
    Assert.Empty capture.IntakeMarkers

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

[<Fact>]
let ``protected intake marker is parsed from exact issue census bytes`` () =
    let claim =
        $"<!-- fsgg:claim worker=worker-a lease=30 renewed=1 session={operation} -->"

    let draftDigest = String.replicate 64 "a"

    let activity =
        native claim
        |> withIssueBody $"<!-- fsgg:intake:v1 id=intake-42 digest={draftDigest} -->\n\n## Observed behavior\nvalue"

    let capture =
        MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
        |> Result.defaultWith failwith

    let marker = Assert.Single capture.IntakeMarkers
    Assert.Equal(7, marker.IssueNumber)
    Assert.Equal("I_7", marker.IssueNodeId)
    Assert.Equal("intake-42", marker.DraftId)
    Assert.Equal(draftDigest, marker.DraftDigest)

    Assert.Equal(
        Error "claim-event-partial-fingerprint",
        MigrationClaimEventInspectBinder.qualifyCanonical { capture with IntakeMarkers = [] }
    )

[<Fact>]
let ``quoted and malformed intake marker lookalikes refuse`` () =
    let claim =
        $"<!-- fsgg:claim worker=worker-a lease=30 renewed=1 session={operation} -->"

    for body in
        [
            "<!-- fsgg:intake:v2 id=intake-42 digest=" + String.replicate 64 "a" + " -->"
            "prose\n<!-- fsgg:intake:v1 id=intake-42 digest="
            + String.replicate 64 "a"
            + " -->"
            "<!-- fsgg:intake:v1 id=intake/42 digest=" + String.replicate 64 "a" + " -->"
            "<!-- fsgg:intake:v1 id=intake-42 digest=" + String.replicate 64 "A" + " -->"
        ] do
        let activity = native claim |> withIssueBody body

        Assert.Equal(
            Error "claim-event-unknown-intake-marker",
            MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) (inventory ())
        )

[<Fact>]
let ``caller producer label cannot replace protected intake marker bytes`` () =
    let claim =
        $"<!-- fsgg:claim worker=worker-a lease=30 renewed=1 session={operation} -->"

    let activity = native claim

    Assert.Equal(
        Error "claim-event-intake-producer-unavailable",
        MigrationClaimEventInspectBinder.bindPartial
            options
            activity
            activity
            (journals ())
            (inventoryWithoutIntakeProducerBytes ())
    )

    let current = inventory ()
    let source = current.Sources |> List.find (_.SchemaFamily >> (=) "intake-marker")
    let uri = source.SourceIdentity.Split('#').[0]
    let copiedMarker =
        Encoding.UTF8.GetBytes "let marker draft = \"<!-- fsgg:intake:v1 id=42 digest=abc -->\""
    let retainedConsumer =
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "evidence", "github-substrate-v2", "corpus", "originals", "C-intake.source"
        )
        |> File.ReadAllBytes

    for candidate in
        [
            inventoryWithIntakeSource uri copiedMarker
            inventoryWithIntakeSource
                ($"https://api.github.com/repos/FS-GG/.github/contents/tests/FS.GG.Coord.Cli.BoardOps.Tests/IntakeTransactionTests.fs?ref={producerRevision}")
                retainedConsumer
        ] do
        Assert.Equal(
            Error "claim-event-intake-producer-unavailable",
            MigrationClaimEventInspectBinder.bindPartial options activity activity (journals ()) candidate
        )
