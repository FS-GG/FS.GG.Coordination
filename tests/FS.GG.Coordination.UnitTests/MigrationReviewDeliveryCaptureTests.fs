module FS.GG.Coordination.MigrationReviewDeliveryCaptureTests

open System
open System.Collections.Generic
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli

let private head = String.replicate 40 "a"
let private otherHead = String.replicate 40 "c"
let private merge = String.replicate 40 "b"
let private api = "https://api.github.test/"
let private options =
    { ApiBase=Uri api; GraphQLUri=Uri(api + "graphql")
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

let private next (path: string) query =
    Map.ofList [ "link", $"<{api}{path.TrimStart('/')}?{query}>; rel=\"next\"" ]

let private pullJson sha =
    $"""{{"number":2,"node_id":"P_2","head":{{"sha":"{sha}"}},"base":{{"repo":{{"id":42}}}}}}"""

let private responseForPull request =
    match request with
    | GraphQL _ -> NetworkFailure
    | Rest rest when rest.Method <> Get -> NetworkFailure
    | Rest rest ->
        let path, query = rest.Uri.AbsolutePath, rest.Uri.Query
        match path with
        | "/repos/FS-GG/copy" ->
            ok Map.empty """{"id":42,"node_id":"R_42","full_name":"FS-GG/copy"}"""
        | "/repos/FS-GG/copy/pulls" when query.Contains("state=all") && query.Contains("per_page=100") ->
            ok Map.empty ($"[{pullJson head}]")
        | "/repos/FS-GG/copy/pulls/2/reviews" ->
            ok Map.empty """[{"id":201,"pull_request_url":"https://api.github.test/repos/FS-GG/copy/pulls/2"}]"""
        | "/repos/FS-GG/copy/pulls/2/comments" ->
            ok Map.empty """[{"id":202,"pull_request_url":"https://api.github.test/repos/FS-GG/copy/pulls/2"}]"""
        | p when p.EndsWith($"/commits/{head}/check-runs", StringComparison.Ordinal)
                 && query.Contains("filter=all") && query.Contains("per_page=100") ->
            ok Map.empty ($"""{{"total_count":2,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}},{{"id":302,"name":"build","status":"completed","conclusion":"failure","head_sha":"{head}"}}]}}""")
        | p when p.EndsWith($"/commits/{head}/statuses", StringComparison.Ordinal) ->
            ok Map.empty ($"""[{{"id":401,"url":"{api}repos/FS-GG/copy/statuses/{head}","context":"ci/build","state":"success"}}]""")
        | "/repos/FS-GG/copy/pulls/2" ->
            ok Map.empty ($"""{{"number":2,"node_id":"P_2","head":{{"sha":"{head}"}},"merged_at":"2026-09-24T10:00:00Z","merge_commit_sha":"{merge}"}}""")
        | p when p.EndsWith($"/commits/{merge}", StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"sha":"{merge}"}}""")
        | "/repos/FS-GG/copy/tags" ->
            ok Map.empty ($"""[{{"name":"v1","commit":{{"sha":"{merge}"}}}}]""")
        | "/repos/FS-GG/copy/releases" ->
            ok Map.empty """[{"id":501,"tag_name":"v1","draft":false}]"""
        | _ -> NetworkFailure

let private capture transport =
    MigrationReviewDeliveryCapture.captureTwoPass options transport

let private assertError expected (result: Result<MigrationReviewDeliveryNativeTwoPass,string>) =
    match result with
    | Error actual -> Assert.Equal(expected, actual)
    | Ok _ -> failwithf "Expected refusal: %s" expected

[<Fact>]
let ``two fresh passes capture exact GET evidence and bind nonempty statuses to request SHA`` () =
    let transport = FakeTransport responseForPull
    match capture transport with
    | Error reason -> failwithf "Unexpected refusal: %s" reason
    | Ok result ->
        Assert.Equal(result.First, result.Second)
        Assert.Equal(64, result.First.Fingerprint.Length)
        Assert.Equal(1, result.First.PullRequests.Length)
        let checks = result.First.Streams |> List.find (fun stream -> stream.Kind = "check-runs")
        Assert.Equal(2, checks.Records.Length)
        Assert.Contains(checks.Reads, fun read -> read.Request.Uri.Contains("filter=all"))
        let statuses = result.First.Streams |> List.find (fun stream -> stream.Kind = "statuses")
        Assert.Equal<MigrationReviewDeliveryRecord list>(
            [ CommitStatus(2, head, 401L, "ci/build", "success") ], statuses.Records)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest rest -> Assert.Equal(Get, rest.Method)
            | GraphQL _ -> failwith "unexpected GraphQL request")
        Assert.All(result.First.Streams |> List.collect _.Reads, fun read ->
            Assert.False(Map.containsKey "authorization" read.Request.Headers))

[<Fact>]
let ``missing check continuation refuses incomplete population`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"total_count":3,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}},{{"id":302,"name":"build","status":"completed","conclusion":"failure","head_sha":"{head}"}}]}}""")
        | _ -> responseForPull request
    assertError "incomplete:check-runs" (capture (FakeTransport route))

[<Fact>]
let ``extra terminal page refuses`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal)
                         && not (rest.Uri.Query.Contains("page=2")) ->
            ok (next rest.Uri.AbsolutePath $"filter=all&per_page=100&page=2")
                ($"""{{"total_count":2,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}},{{"id":302,"name":"build","status":"completed","conclusion":"failure","head_sha":"{head}"}}]}}""")
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal) ->
            ok Map.empty """{"total_count":2,"check_runs":[]}"""
        | _ -> responseForPull request
    assertError "pagination:extra-page" (capture (FakeTransport route))

[<Fact>]
let ``check continuation without filter all refuses before page dispatch`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal) ->
            ok (next rest.Uri.AbsolutePath "per_page=100&page=2")
                ($"""{{"total_count":3,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}},{{"id":302,"name":"build","status":"completed","conclusion":"failure","head_sha":"{head}"}}]}}""")
        | _ -> responseForPull request
    let transport = FakeTransport route
    assertError "pagination:query" (capture transport)
    Assert.DoesNotContain(transport.Requests, fun request ->
        match request with
        | Rest rest -> rest.Uri.Query.Contains("page=2")
        | _ -> false)

[<Fact>]
let ``status URL for a different commit refuses raw typed correspondence`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/statuses", StringComparison.Ordinal) ->
            ok Map.empty ($"""[{{"id":401,"url":"{api}repos/FS-GG/copy/statuses/{otherHead}","context":"ci/build","state":"success"}}]""")
        | _ -> responseForPull request
    assertError "changed:status-url" (capture (FakeTransport route))

[<Fact>]
let ``changed second census refuses population drift`` () =
    let mutable censusReads = 0
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath = "/repos/FS-GG/copy/pulls" ->
            censusReads <- censusReads + 1
            if censusReads = 2 then ok Map.empty "[]" else responseForPull request
        | _ -> responseForPull request
    assertError "review-delivery-native-pass-drift" (capture (FakeTransport route))

[<Fact>]
let ``check record with a different head refuses typed correspondence`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal) ->
            ok Map.empty ($"""{{"total_count":1,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{otherHead}"}}]}}""")
        | _ -> responseForPull request
    assertError "changed:check-head" (capture (FakeTransport route))

[<Fact>]
let ``invalid options refuse before provider dispatch`` () =
    let invalid = { options with ApiBase=Uri "http://api.github.test/" }
    let transport = FakeTransport responseForPull
    assertError "review-delivery-native-invalid-options"
        (MigrationReviewDeliveryCapture.captureTwoPass invalid transport)
    Assert.Empty(transport.Requests)

[<Fact>]
let ``transport observes no write shaped request`` () =
    let route request =
        match request with
        | Rest rest when rest.Method = Get && rest.Body.IsNone -> responseForPull request
        | _ -> failwith "write request reached transport"
    match capture (FakeTransport route) with
    | Ok _ -> ()
    | Error reason -> failwithf "Unexpected refusal: %s" reason

[<Fact>]
let ``published release without a terminal tag refuses`` () =
    let route request =
        match request with
        | Rest rest when rest.Uri.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) -> ok Map.empty "[]"
        | _ -> responseForPull request
    assertError "missing:release-tag" (capture (FakeTransport route))
