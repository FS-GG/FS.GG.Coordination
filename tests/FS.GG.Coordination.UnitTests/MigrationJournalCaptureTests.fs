module FS.GG.Coordination.MigrationJournalCaptureTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli

let private options =
    { ApiBase=Uri "https://api.github.test/"; GraphQLUri=Uri "https://api.github.test/graphql"
      Token="secret"; UserAgent="journal-test"; Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=42L }

let private ok headers body =
    Response
        { StatusCode=200; Headers=headers; Body=body; ETag=None
          RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=None; Cost=Some 1 } }

type private FakeTransport(route: GitHubRequest -> TransportOutcome) =
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request = requests.Add request; route request

let private path = function
    | Rest request -> request.Uri.AbsolutePath
    | GraphQL _ -> failwith "journal capture emitted GraphQL"

let private emptyRoute request =
    match path request with
    | "/repos/FS-GG/copy" -> ok Map.empty """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""
    | value when value.Contains("/git/matching-refs/heads/fsgg/v2/journal/") -> ok Map.empty "[]"
    | value -> failwithf "unexpected request %s" value

[<Fact>]
let ``empty journal namespaces retain complete independent proofs in both passes`` () =
    let transport = FakeTransport emptyRoute
    match MigrationJournalCapture.captureTwoPass options transport with
    | Error failure -> failwithf "empty capture refused: %s" failure
    | Ok capture ->
        Assert.Equal(2, capture.First.Namespaces.Length)
        Assert.All(capture.First.Namespaces, fun item -> Assert.Single(item.Reads) |> ignore; Assert.Empty(item.Refs))
        Assert.Empty(capture.First.Histories)
        Assert.Equal(capture.First.Fingerprint, capture.Second.Fingerprint)
        Assert.Equal(6, transport.Requests.Length)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value ->
                Assert.Equal(Get, value.Method)
                Assert.True(value.Body.IsNone)
            | _ -> failwith "write-shaped request escaped")

[<Fact>]
let ``discovered ref outside the exact shard namespace refuses`` () =
    let route request =
        match path request with
        | value when value.EndsWith("/review/", StringComparison.Ordinal) ->
            ok Map.empty """[{"ref":"refs/heads/fsgg/v2/journal/review/aa/extra","object":{"type":"commit","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}]"""
        | _ -> emptyRoute request
    let transport = FakeTransport route
    Assert.Equal(Error "invalid-journal-ref", MigrationJournalCapture.captureTwoPass options transport)
    Assert.Equal(2, transport.Requests.Length)

[<Fact>]
let ``matching ref continuation is refused rather than guessed as page numbers`` () =
    let route request =
        match path request with
        | value when value.EndsWith("/review/", StringComparison.Ordinal) ->
            ok (Map [ "Link", "<https://api.github.test/next>; rel=next" ]) "[]"
        | _ -> emptyRoute request
    Assert.Equal(Error "unexpected-pagination", MigrationJournalCapture.captureTwoPass options (FakeTransport route))

let private gitSha kind (bytes: byte[]) =
    Array.append (Encoding.ASCII.GetBytes($"{kind} {bytes.LongLength}\u0000")) bytes
    |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private treeSha (entries: (string * string) list) =
    entries
    |> List.collect (fun (name, oid) ->
        (Encoding.UTF8.GetBytes($"100644 {name}\u0000") |> Array.toList)
        @ (Convert.FromHexString oid |> Array.toList))
    |> List.toArray |> gitSha "tree"

let private deliveryFixture () =
    let subject = "fs-gg/copy#7"
    let address = ReviewDeliveryAdapter.deliveryAddress subject |> Result.defaultWith (failwithf "%A")
    let record: DeliveryAuthorityRecord =
        { SchemaVersion=1; Subject=subject; Kind=DeliveryGenesis; ReviewChainId=""; ReviewEpochKey=""
          ReviewSeat=""; MergeCommit=""; ProtectedRunId=None; ProtectedRunCommit=None
          ProtectedRunConclusion=None; OperationId="delivery-root" }
    let eventBytes = ReviewDeliveryAdapter.deliveryAuthorityBytes record |> Result.defaultWith (failwithf "%A")
    let eventOid = gitSha "blob" eventBytes
    let eventDigest = ShardedJournalAdapter.sha256 eventBytes
    let unsigned: JournalHead =
        { SchemaVersion=1; Address=address; Generation=1L; EventDigest=eventDigest; SnapshotDigest=None
          Terminal=false; PriorHeadDigest=None; HeadDigest=String.replicate 64 "0" }
    let firstHeadBytes = ShardedJournalAdapter.journalHeadBytes unsigned
    let head = { unsigned with HeadDigest=ShardedJournalAdapter.sha256 firstHeadBytes }
    let headBytes = ShardedJournalAdapter.journalHeadBytes head
    let headOid = gitSha "blob" headBytes
    let tree = treeSha [ "event.json", eventOid; "head.json", headOid ]
    let body =
        String.concat "\n"
            [ $"tree {tree}"; "author Test <test@example.com> 0 +0000"
              "committer Test <test@example.com> 0 +0000"; ""; "journal genesis" ]
        |> Encoding.UTF8.GetBytes
    let commit = gitSha "commit" body
    address, record, eventBytes, eventOid, headBytes, headOid, tree, commit

let private blobBody oid (bytes: byte[]) =
    $"""{{"sha":"{oid}","encoding":"base64","content":"{Convert.ToBase64String bytes}","size":{bytes.Length}}}"""

[<Fact>]
let ``delivery journal walks a hash-bound root and decodes the receipt in two fresh passes`` () =
    let address, record, eventBytes, eventOid, headBytes, headOid, tree, commit = deliveryFixture ()
    let refs prefix =
        if prefix = "operation" then
            $"""[{{"ref":"{address.Ref}","object":{{"type":"commit","sha":"{commit}"}}}}]"""
        else "[]"
    let route request =
        match path request with
        | "/repos/FS-GG/copy" -> ok Map.empty """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""
        | value when value.EndsWith("/review/", StringComparison.Ordinal) -> ok Map.empty (refs "review")
        | value when value.EndsWith("/operation/", StringComparison.Ordinal) -> ok Map.empty (refs "operation")
        | value when value.EndsWith("/git/commits/" + commit, StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"sha":"{commit}","tree":{{"sha":"{tree}"}},"parents":[],"author":{{"name":"Test","email":"test@example.com","date":"1970-01-01T00:00:00Z"}},"committer":{{"name":"Test","email":"test@example.com","date":"1970-01-01T00:00:00Z"}},"message":"journal genesis"}}""")
        | value when value.EndsWith("/git/trees/" + tree, StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"sha":"{tree}","truncated":false,"tree":[{{"path":"event.json","mode":"100644","type":"blob","sha":"{eventOid}"}},{{"path":"head.json","mode":"100644","type":"blob","sha":"{headOid}"}}]}}""")
        | value when value.EndsWith("/git/blobs/" + eventOid, StringComparison.Ordinal) -> ok Map.empty (blobBody eventOid eventBytes)
        | value when value.EndsWith("/git/blobs/" + headOid, StringComparison.Ordinal) -> ok Map.empty (blobBody headOid headBytes)
        | value -> failwithf "unexpected request %s" value
    let transport = FakeTransport route
    match MigrationJournalCapture.captureTwoPass options transport with
    | Error failure -> failwithf "delivery capture refused: %s" failure
    | Ok result ->
        let entry = Assert.Single(result.First.Histories)
        Assert.Equal(record.OperationId, entry.Record.OperationId)
        Assert.Equal(record.Subject, entry.Record.Subject)
        Assert.Equal("genesis", entry.Record.Kind)
        Assert.Equal(14, transport.Requests.Length)

[<Fact>]
let ``two pass namespace drift refuses`` () =
    let mutable reviewReads = 0
    let route request =
        match path request with
        | value when value.EndsWith("/review/", StringComparison.Ordinal) ->
            reviewReads <- reviewReads + 1
            if reviewReads = 1 then ok Map.empty "[]"
            else ok Map.empty " []"
        | _ -> emptyRoute request
    match MigrationJournalCapture.captureTwoPass options (FakeTransport route) with
    | Error _ -> ()
    | Ok _ -> failwith "a ref added between passes was accepted"
