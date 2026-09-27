module FS.GG.Coordination.MigrationClaimJournalCaptureTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli

let private options =
    {
        ApiBase = Uri "https://api.github.test/"
        GraphQLUri = Uri "https://api.github.test/graphql"
        Owner = "FS-GG"
        Repository = "copy"
        ExpectedRepositoryId = 42L
        Token = "test"
        UserAgent = "claim-journal-test"
    }

let private ok headers body =
    Response
        {
            StatusCode = 200
            Headers = headers
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
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList

    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            route request

let private path =
    function
    | Rest request -> request.Uri.AbsolutePath
    | GraphQL _ -> failwith "claim journal capture emitted GraphQL"

let private emptyRoute request =
    match path request with
    | "/repos/FS-GG/copy" -> ok Map.empty """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""
    | value when value.Contains("/git/matching-refs/heads/fsgg/v2/journal/") -> ok Map.empty "[]"
    | value -> failwithf "unexpected request %s" value

[<Fact>]
let ``empty fixed namespaces are independently censused in two passes using GET only`` () =
    let transport = FakeTransport emptyRoute

    match MigrationClaimJournalCapture.captureTwoPass options transport with
    | Error failure -> failwithf "empty capture refused: %s" failure
    | Ok capture ->
        Assert.Equal(2, capture.ClaimFirst.ClaimNamespaces.Length)

        Assert.Equal<MigrationClaimJournalNamespace list>(
            [ ClaimJournalNamespace; OperationJournalNamespace ],
            capture.ClaimFirst.ClaimNamespaces |> List.map _.ClaimNamespace
        )

        Assert.Empty(capture.ClaimFirst.ClaimHistories)
        Assert.Equal(capture.ClaimFirst.ClaimFingerprint, capture.ClaimSecond.ClaimFingerprint)
        Assert.Equal(6, transport.Requests.Length)

        Assert.All(
            transport.Requests,
            fun request ->
                match request with
                | Rest value ->
                    Assert.Equal(Get, value.Method)
                    Assert.True(value.Body.IsNone)
                | _ -> failwith "write-shaped request escaped"
        )

[<Fact>]
let ``matching ref continuation is refused instead of inventing pagination`` () =
    let route request =
        match path request with
        | value when value.EndsWith("/claim/", StringComparison.Ordinal) ->
            ok (Map [ "Link", "<https://api.github.test/next>; rel=next" ]) "[]"
        | _ -> emptyRoute request

    Assert.Equal(
        Error "unexpected-pagination",
        MigrationClaimJournalCapture.captureTwoPass options (FakeTransport route)
    )

let private gitSha kind (bytes: byte array) =
    Array.append (Encoding.ASCII.GetBytes($"{kind} {bytes.LongLength}\u0000")) bytes
    |> SHA1.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private treeSha (entries: (string * string) list) =
    entries
    |> List.collect (fun (name, oid) ->
        (Encoding.UTF8.GetBytes($"100644 {name}\u0000") |> Array.toList)
        @ (Convert.FromHexString oid |> Array.toList))
    |> List.toArray
    |> gitSha "tree"

type private Fixture =
    {
        Address: AggregateAddress
        EventBytes: byte array
        EventOid: string
        HeadBytes: byte array
        HeadOid: string
        CheckpointBytes: byte array option
        CheckpointOid: string option
        Tree: string
        Commit: string
    }

let private journalFixture address generation eventBytes checkpointBytes =
    let eventOid = gitSha "blob" eventBytes
    let checkpointOid = checkpointBytes |> Option.map (gitSha "blob")

    let unsigned: JournalHead =
        {
            SchemaVersion = 1
            Address = address
            Generation = generation
            EventDigest = ShardedJournalAdapter.sha256 eventBytes
            SnapshotDigest = checkpointBytes |> Option.map ShardedJournalAdapter.sha256
            Terminal = checkpointBytes.IsSome
            PriorHeadDigest = None
            HeadDigest = String.replicate 64 "0"
        }

    let firstHeadBytes = ShardedJournalAdapter.journalHeadBytes unsigned

    let head =
        { unsigned with
            HeadDigest = ShardedJournalAdapter.sha256 firstHeadBytes
        }

    let headBytes = ShardedJournalAdapter.journalHeadBytes head
    let headOid = gitSha "blob" headBytes

    let treeEntries =
        [
            match checkpointOid with
            | Some oid -> yield "checkpoint.json", oid
            | None -> ()
            yield "event.json", eventOid
            yield "head.json", headOid
        ]

    let tree = treeSha treeEntries

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
        CheckpointBytes = checkpointBytes
        CheckpointOid = checkpointOid
        Tree = tree
        Commit = gitSha "commit" commitBytes
    }

let private claimFixture generation addressTransform =
    let authority: ClaimAuthorityRecord =
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
            OperationId = "claim-root"
        }

    let produced =
        ClaimTouchSetAdapter.claimAddress authority.Subject
        |> Result.defaultWith (failwithf "%A")

    let address = addressTransform produced authority

    let eventBytes =
        ClaimTouchSetAdapter.authorityBytes authority
        |> Result.defaultWith (failwithf "%A")

    journalFixture address generation eventBytes None

let private blobBody oid (bytes: byte array) =
    $"""{{"sha":"{oid}","encoding":"base64","content":"{Convert.ToBase64String bytes}","size":{bytes.Length}}}"""

let private fixtureRoute (fixture: Fixture) request =
    let refBody =
        let relative = fixture.Address.Ref.Substring("refs/".Length)
        let refUrl = $"https://api.github.test/repos/FS-GG/copy/git/refs/{relative}"

        let objectUrl =
            $"https://api.github.test/repos/FS-GG/copy/git/commits/{fixture.Commit}"

        $"""[{{"ref":"{fixture.Address.Ref}","node_id":"REF_fixture","url":"{refUrl}","object":{{"type":"commit","sha":"{fixture.Commit}","url":"{objectUrl}"}}}}]"""

    let treeBody =
        let checkpoint =
            match fixture.CheckpointOid with
            | Some oid -> $"""{{"path":"checkpoint.json","mode":"100644","type":"blob","sha":"{oid}"}},"""
            | None -> ""

        $"""{{"sha":"{fixture.Tree}","truncated":false,"tree":[{checkpoint}{{"path":"event.json","mode":"100644","type":"blob","sha":"{fixture.EventOid}"}},{{"path":"head.json","mode":"100644","type":"blob","sha":"{fixture.HeadOid}"}}]}}"""

    match path request with
    | "/repos/FS-GG/copy" -> ok Map.empty """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""
    | value when
        value.EndsWith("/claim/", StringComparison.Ordinal)
        && fixture.Address.Kind = JournalKind.Claim
        ->
        ok Map.empty refBody
    | value when value.EndsWith("/claim/", StringComparison.Ordinal) -> ok Map.empty "[]"
    | value when
        value.EndsWith("/operation/", StringComparison.Ordinal)
        && fixture.Address.Kind = JournalKind.Operation
        ->
        ok Map.empty refBody
    | value when value.EndsWith("/operation/", StringComparison.Ordinal) -> ok Map.empty "[]"
    | value when value.EndsWith("/git/commits/" + fixture.Commit, StringComparison.Ordinal) ->
        ok
            Map.empty
            $"""{{"sha":"{fixture.Commit}","tree":{{"sha":"{fixture.Tree}"}},"parents":[],"author":{{"name":"Test","email":"test@example.com","date":"1970-01-01T00:00:00Z"}},"committer":{{"name":"Test","email":"test@example.com","date":"1970-01-01T00:00:00Z"}},"message":"claim journal"}}"""
    | value when value.EndsWith("/git/trees/" + fixture.Tree, StringComparison.Ordinal) -> ok Map.empty treeBody
    | value when value.EndsWith("/git/blobs/" + fixture.EventOid, StringComparison.Ordinal) ->
        ok Map.empty (blobBody fixture.EventOid fixture.EventBytes)
    | value when value.EndsWith("/git/blobs/" + fixture.HeadOid, StringComparison.Ordinal) ->
        ok Map.empty (blobBody fixture.HeadOid fixture.HeadBytes)
    | value when
        fixture.CheckpointOid
        |> Option.exists (fun oid -> value.EndsWith("/git/blobs/" + oid, StringComparison.Ordinal))
        ->
        ok Map.empty (blobBody fixture.CheckpointOid.Value fixture.CheckpointBytes.Value)
    | value -> failwithf "unexpected request %s" value

[<Fact>]
let ``producer claim record is hash bound and walked to root in both passes`` () =
    let fixture = claimFixture 1L (fun address _ -> address)
    let transport = FakeTransport(fixtureRoute fixture)

    match MigrationClaimJournalCapture.captureTwoPass options transport with
    | Error failure -> failwithf "claim capture refused: %s" failure
    | Ok capture ->
        let history = Assert.Single(capture.ClaimFirst.ClaimHistories)
        let entry = Assert.Single(history.ClaimEntries)
        Assert.Equal(ClaimSchemaFamily, entry.ClaimRecord.Family)
        Assert.Equal("fsgg.coordination.claim-authority/1", entry.ClaimRecord.Schema)
        Assert.Equal(fixture.Address.CanonicalId, entry.ClaimRecord.CanonicalId)
        Assert.Equal(Some "claim-root", entry.ClaimRecord.OperationId)
        Assert.Equal(1L, entry.ClaimRecord.Generation)
        Assert.Equal(14, transport.Requests.Length)

[<Fact>]
let ``producer conflict claim address is classified from a declared touch`` () =
    let fixture =
        claimFixture 1L (fun _ authority ->
            ClaimTouchSetAdapter.conflictAddress authority.Touches.Head
            |> Result.defaultWith (failwithf "%A"))

    match MigrationClaimJournalCapture.captureTwoPass options (FakeTransport(fixtureRoute fixture)) with
    | Error failure -> failwithf "conflict claim capture refused: %s" failure
    | Ok capture ->
        Assert.StartsWith("conflict:", capture.ClaimFirst.ClaimHistories.Head.ClaimEntries.Head.ClaimRecord.CanonicalId)

[<Fact>]
let ``provider ref metadata is retained and mismatched metadata refuses`` () =
    let fixture = claimFixture 1L (fun address _ -> address)

    let corrupt request =
        match path request with
        | value when value.EndsWith("/claim/", StringComparison.Ordinal) ->
            match fixtureRoute fixture request with
            | Response response ->
                Response
                    { response with
                        Body = response.Body.Replace("/git/refs/heads/", "/git/refs/wrong/heads/")
                    }
            | outcome -> outcome
        | _ -> fixtureRoute fixture request

    Assert.Equal(
        Error "ref-metadata-drift",
        MigrationClaimJournalCapture.captureTwoPass options (FakeTransport corrupt)
    )

[<Fact>]
let ``conditional checkpoint blob is hash bound decoded and validated`` () =
    let basic = claimFixture 1L (fun address _ -> address)
    let aggregateDigest = String.replicate 64 "f"

    let checkpointBytes =
        ShardedJournalAdapter.canonicalJson $"{{\"aggregateDigest\":\"{aggregateDigest}\",\"highWaterGeneration\":1}}"
        |> Result.defaultWith failwith

    let fixture =
        journalFixture basic.Address 1L basic.EventBytes (Some checkpointBytes)

    let transport = FakeTransport(fixtureRoute fixture)

    match MigrationClaimJournalCapture.captureTwoPass options transport with
    | Error failure -> failwithf "checkpoint capture refused: %s" failure
    | Ok capture ->
        let entry = capture.ClaimFirst.ClaimHistories.Head.ClaimEntries.Head
        Assert.Equal(5, entry.ClaimReads.Length)
        Assert.Equal(16, transport.Requests.Length)

[<Fact>]
let ``delivery authority is classified as review family and wrong address refuses`` () =
    let subject = "fs-gg/copy#7"

    let record: DeliveryAuthorityRecord =
        {
            SchemaVersion = 1
            Subject = subject
            Kind = DeliveryGenesis
            ReviewChainId = ""
            ReviewEpochKey = ""
            ReviewSeat = ""
            MergeCommit = ""
            ProtectedRunId = None
            ProtectedRunCommit = None
            ProtectedRunConclusion = None
            OperationId = "delivery-root"
        }

    let eventBytes =
        ReviewDeliveryAdapter.deliveryAuthorityBytes record
        |> Result.defaultWith (failwithf "%A")

    let address =
        ReviewDeliveryAdapter.deliveryAddress subject
        |> Result.defaultWith (failwithf "%A")

    let fixture = journalFixture address 1L eventBytes None

    match MigrationClaimJournalCapture.captureTwoPass options (FakeTransport(fixtureRoute fixture)) with
    | Error failure -> failwithf "delivery capture refused: %s" failure
    | Ok capture ->
        let decoded = capture.ClaimFirst.ClaimHistories.Head.ClaimEntries.Head.ClaimRecord
        Assert.Equal(ReviewSchemaFamily, decoded.Family)
        Assert.Equal("fsgg.coordination.delivery-authority/1", decoded.Schema)

    let wrongAddress =
        ShardedJournalAdapter.address JournalKind.Operation "ordinary:42:PR_7"
        |> Result.defaultWith (failwithf "%A")

    let wrong = journalFixture wrongAddress 1L eventBytes None

    Assert.Equal(
        Error "delivery-address-drift",
        MigrationClaimJournalCapture.captureTwoPass options (FakeTransport(fixtureRoute wrong))
    )

[<Fact>]
let ``wrong claim shard and root generation fail closed`` () =
    let wrong =
        claimFixture 1L (fun address _ ->
            { address with
                Shard = "ff"
                Ref = "refs/heads/fsgg/v2/journal/claim/ff"
            })

    Assert.True(
        MigrationClaimJournalCapture.captureTwoPass options (FakeTransport(fixtureRoute wrong))
        |> Result.isError
    )

    let gap = claimFixture 2L (fun address _ -> address)

    match MigrationClaimJournalCapture.captureTwoPass options (FakeTransport(fixtureRoute gap)) with
    | Error failure -> Assert.Contains("NonMonotonicJournalGeneration", failure)
    | Ok _ -> failwith "generation gap was accepted"

[<Fact>]
let ``missing discovered blob and a ref appearing in pass two are refusals`` () =
    let fixture = claimFixture 1L (fun address _ -> address)

    let missing request =
        if (path request).EndsWith("/git/blobs/" + fixture.EventOid, StringComparison.Ordinal) then
            NetworkFailure
        else
            fixtureRoute fixture request

    Assert.Equal(
        Error "transport-unavailable",
        MigrationClaimJournalCapture.captureTwoPass options (FakeTransport missing)
    )

    let mutable claimReads = 0

    let drift request =
        match path request with
        | value when value.EndsWith("/claim/", StringComparison.Ordinal) ->
            claimReads <- claimReads + 1

            if claimReads = 1 then
                ok Map.empty "[]"
            else
                fixtureRoute fixture request
        | _ -> fixtureRoute fixture request

    Assert.True(
        MigrationClaimJournalCapture.captureTwoPass options (FakeTransport drift)
        |> Result.isError
    )
