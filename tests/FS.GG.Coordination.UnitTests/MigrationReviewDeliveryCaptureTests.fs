module FS.GG.Coordination.MigrationReviewDeliveryCaptureTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli

let private head = String.replicate 40 "a"
let private merge = String.replicate 40 "b"
let private journalHead = String.replicate 40 "c"
let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="controlled-token"; UserAgent="controlled-review-capture"
      Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=42L }

let private ok headers body =
    Response { StatusCode=200; Headers=headers; Body=body; ETag=None
               RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }

type private FakeTransport(route: GitHubRequest -> TransportOutcome) =
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request = requests.Add request; route request

let private responseForPull (request: GitHubRequest) =
    match request with
    | GraphQL _ -> NetworkFailure
    | Rest rest ->
        let path = rest.Uri.AbsolutePath
        let query = rest.Uri.Query
        match path with
        | "/repos/FS-GG/copy" -> ok Map.empty """{"id":42,"full_name":"FS-GG/copy"}"""
        | "/repos/FS-GG/copy/issues" ->
            ok Map.empty """[{"number":2,"pull_request":{}}]"""
        | "/repos/FS-GG/copy/pulls" ->
            ok Map.empty ($"""[{{"number":2,"id":102,"node_id":"P_2","state":"closed","updated_at":"2026-09-24T10:00:00Z","head":{{"sha":"{head}"}},"base":{{"sha":"{merge}","repo":{{"id":42}}}}}}]""")
        | "/repos/FS-GG/copy/pulls/2/reviews"
        | "/repos/FS-GG/copy/pulls/2/comments" -> ok Map.empty "[]"
        | p when p.EndsWith($"/commits/{head}/check-runs", StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"total_count":1,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}}]}}""")
        | p when p.EndsWith($"/commits/{head}/statuses", StringComparison.Ordinal) -> ok Map.empty "[]"
        | "/repos/FS-GG/copy/pulls/2" ->
            ok Map.empty ($"""{{"number":2,"node_id":"P_2","head":{{"sha":"{head}"}},"merged_at":"2026-09-24T10:00:00Z","merge_commit_sha":"{merge}"}}""")
        | p when p.EndsWith($"/commits/{merge}", StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"sha":"{merge}"}}""")
        | "/repos/FS-GG/copy/tags" when query.Contains("per_page=100") ->
            ok Map.empty ($"""[{{"name":"v1","commit":{{"sha":"{merge}"}}}}]""")
        | "/repos/FS-GG/copy/releases" when query.Contains("per_page=100") ->
            ok Map.empty """[{"id":501,"tag_name":"v1","draft":false,"published_at":"2026-09-24T10:00:00Z"}]"""
        | _ -> NetworkFailure

let private capture transport =
    MigrationReviewDeliveryCapture.captureTwoPass options [ 2, "P_2", head ] [] transport

let private assertError (expected: string) (result: Result<MigrationReviewDeliveryTwoPass, string>) =
    match result with
    | Error actual -> Assert.Equal(expected, actual)
    | Ok _ -> failwithf "Expected refusal: %s" expected

[<Fact>]
let ``two complete passes bind review checks merge object tags and releases using GET only`` () =
    let transport = FakeTransport responseForPull
    match capture transport with
    | Error reason -> failwithf "Unexpected refusal: %s" reason
    | Ok result ->
        Assert.Equal(result.First, result.Second)
        Assert.Equal(64, result.First.SnapshotSha256.Length)
        Assert.Equal(1, result.First.Reviews.Length)
        Assert.Equal(1, result.First.InlineComments.Length)
        Assert.Equal<string list>([ "check-runs"; "statuses"; "pull-delivery"; "merge-object"; "tags"; "releases" ],
                     result.First.Streams |> List.map _.Kind)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest rest -> Assert.Equal(Get, rest.Method)
            | GraphQL _ -> failwith "unexpected GraphQL request")

[<Fact>]
let ``missing check continuation refuses rather than accepting a partial count`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"total_count":2,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}}]}}""")
        | _ -> responseForPull request
    let transport = FakeTransport route
    assertError "incomplete:check-runs" (capture transport)
    Assert.True(transport.Requests.Length < 16)

[<Fact>]
let ``escaped check continuation refuses before dispatch to foreign authority`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal) ->
            ok (Map.ofList [ "link", "<https://foreign.github.test/repos/FS-GG/copy/commits/" + head +
                                     "/check-runs?per_page=100&page=2>; rel=\"next\"" ])
                ($"""{{"total_count":2,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}}]}}""")
        | _ -> responseForPull request
    let transport = FakeTransport route
    assertError "pagination:escaped" (capture transport)
    Assert.DoesNotContain(transport.Requests, fun request ->
        match request with
        | Rest rest -> rest.Uri.Host = "foreign.github.test"
        | _ -> false)

[<Fact>]
let ``changed second pass refuses a stable claim`` () =
    let mutable tagReads = 0
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) ->
            tagReads <- tagReads + 1
            if tagReads = 2 then ok Map.empty ($"""[{{"name":"v1","commit":{{"sha":"{head}"}}}}]""")
            else responseForPull request
        | _ -> responseForPull request
    assertError "changed:two-pass" (capture (FakeTransport route))

[<Fact>]
let ``foreign PR identity refuses before review or delivery capture`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath = "/repos/FS-GG/copy/pulls" ->
            ok Map.empty "[]"
        | _ -> responseForPull request
    let transport = FakeTransport route
    match capture transport with
    | Error reason -> Assert.Contains("pull", reason)
    | Ok _ -> failwith "Expected pull population refusal"
    Assert.DoesNotContain(transport.Requests, fun request ->
        match request with
        | Rest rest -> rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal)
        | _ -> false)

[<Fact>]
let ``journal correspondence refuses a different operation identity`` () =
    let journal =
        { Repository="FS-GG/copy"; RefName="refs/heads/fsgg/v2/journal/operation/aa"
          Path="ordinary/aa.json"; PullRequestNumber=2; OperationId="expected-operation" }
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/git/ref/heads/fsgg/v2/journal/operation/aa", StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"ref":"{journal.RefName}","object":{{"sha":"{journalHead}"}}}}""")
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/contents/ordinary/aa.json", StringComparison.Ordinal) ->
            let bytes = Encoding.UTF8.GetBytes
                            ($"""{{"schema":"fsgg.coordination.ordinary-delivery-journal/1","generation":1,"operationId":"foreign","mergeCommit":"{merge}"}}""")
            let content = Convert.ToBase64String bytes
            let gitBlob = Array.concat [ Encoding.ASCII.GetBytes($"blob {bytes.Length}\u0000"); bytes ]
            let blob = SHA1.HashData gitBlob |> Convert.ToHexString |> _.ToLowerInvariant()
            ok Map.empty ($"""{{"path":"ordinary/aa.json","encoding":"base64","content":"{content}","sha":"{blob}"}}""")
        | _ -> responseForPull request
    assertError "changed:journal-operation"
        (MigrationReviewDeliveryCapture.captureTwoPass options [ 2, "P_2", head ] [ journal ] (FakeTransport route))

[<Fact>]
let ``invalid journal path refuses without dispatch`` () =
    let journal =
        { Repository="FS-GG/copy"; RefName="refs/heads/fsgg/v2/journal/operation/aa"
          Path="../ordinary/aa.json"; PullRequestNumber=2; OperationId="expected-operation" }
    let transport = FakeTransport responseForPull
    assertError "invalid:declaration"
        (MigrationReviewDeliveryCapture.captureTwoPass options [ 2, "P_2", head ] [ journal ] transport)
    Assert.Empty(transport.Requests)

[<Fact>]
let ``published release without a terminal tag refuses`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) -> ok Map.empty "[]"
        | _ -> responseForPull request
    assertError "missing:release-tag" (capture (FakeTransport route))
