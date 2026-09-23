module FS.GG.Coordination.MigrationGitHubReadTests

open System
open System.Collections.Generic
open System.Net.Http
open Xunit
open FS.GG.Coordination.GitHub

let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="test-token"
      UserAgent="fsgg-migration-test"
      Owner="FS-GG"
      Repository="copy"
      ExpectedRepositoryId=42L }

let private ok headers body =
    Response
        { StatusCode=200; Headers=headers; Body=body; ETag=None
          RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=Some(DateTimeOffset.UtcNow.AddHours 1.); Cost=Some 1 } }

let private repo = ok Map.empty """{"id":42,"full_name":"FS-GG/copy"}"""

let private issue number nodeId =
    $"""{{"number":{number},"id":{number + 100},"node_id":"{nodeId}","state":"open","updated_at":"2026-09-23T10:00:00Z"}}"""

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

[<Fact>]
let ``read-only issue census follows all pages and excludes pull requests explicitly`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=100&page=2"
    let firstIssue = issue 1 "ISSUE_1"
    let secondIssue = issue 3 "ISSUE_3"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ])
                    $"[{firstIssue},{{\"number\":2,\"pull_request\":{{}}}}]"
    let second = ok Map.empty $"[{secondIssue}]"
    let transport = FakeTransport [ repo; first; second ]
    let observed = MigrationGitHubRead.readIssues options transport
    match observed with
    | Ok population ->
        Assert.Equal(42L, population.RepositoryId)
        Assert.Equal(2, population.PageCount)
        Assert.True(population.Terminal)
        Assert.Equal<int list>([ 1; 3 ], population.Issues |> List.map _.Number)
        Assert.Equal(1, population.PullRequestCount)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "a read attempted a GraphQL mutation")
    | Error failure -> failwithf "unexpected refusal: %A" failure

[<Fact>]
let ``missing terminal page and off-scope continuation refuse`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    let missing = FakeTransport [ repo; first ]
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readIssues options missing)
    let escaped = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://elsewhere.test/issues?state=all&per_page=100>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssues options escaped)
    Assert.Equal(2, escaped.Requests.Length)

[<Fact>]
let ``duplicate issue identity and repository drift refuse`` () =
    let firstIssue = issue 1 "ISSUE_1"
    let secondIssue = issue 2 "ISSUE_1"
    let duplicate = FakeTransport [ repo; ok Map.empty $"[{firstIssue},{secondIssue}]" ]
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "ISSUE_1"),
                 MigrationGitHubRead.readIssues options duplicate)
    let drift = FakeTransport [ ok Map.empty """{"id":43,"full_name":"FS-GG/copy"}""" ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readIssues options drift)
    Assert.Single(drift.Requests) |> ignore

[<Fact>]
let ``GraphQL partial data with an authorization error is never a complete type census`` () =
    let partial =
        """{"data":{"repository":{"databaseId":42,"issueTypes":{"nodes":[{"id":"IT_1","name":"Task"}],"pageInfo":{"hasNextPage":false,"endCursor":"NA"}}}},"errors":[{"type":"FORBIDDEN","message":"not accessible"}]}"""
    let transport = FakeTransport [ ok Map.empty partial ]
    Assert.Equal(Error MigrationReadFailure.GraphQLErrors,
                 MigrationGitHubRead.readIssueTypes options transport)
    Assert.Single(transport.Requests) |> ignore

[<Fact>]
let ``issue type census accepts GitHub terminal cursor and rejects missing continuation`` () =
    let terminal =
        """{"data":{"repository":{"databaseId":42,"issueTypes":{"nodes":[{"id":"IT_1","name":"Task"}],"pageInfo":{"hasNextPage":false,"endCursor":"NA"}}}}}"""
    let observed = MigrationGitHubRead.readIssueTypes options (FakeTransport [ ok Map.empty terminal ])
    match observed with
    | Ok population ->
        Assert.Equal(1, population.PageCount)
        Assert.Equal("Task", population.IssueTypes.Head.Name)
    | Error failure -> failwithf "unexpected refusal: %A" failure

    let missingCursor =
        """{"data":{"repository":{"databaseId":42,"issueTypes":{"nodes":[],"pageInfo":{"hasNextPage":true,"endCursor":null}}}}}"""
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "missing-end-cursor"),
                 MigrationGitHubRead.readIssueTypes options (FakeTransport [ ok Map.empty missingCursor ]))

[<Fact>]
let ``HTTP migration read transport refuses mutation-shaped requests before network send`` () =
    use client = new HttpClient()
    let transport = HttpMigrationGitHubReadTransport(client) :> IMigrationGitHubReadTransport
    let headers = Map.ofList [ "x-github-api-version", ApiVersion.value ApiVersion.required ]
    let rest =
        Rest { Method=Post; Uri=Uri "https://api.github.test/repos/FS-GG/copy/issues"
               Headers=headers; Body=Some "{}"; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
    let graphQL =
        GraphQL { Uri=options.GraphQLUri; Document="mutation { createIssue(input:{}) { clientMutationId } }"
                  Variables=Map.empty; Headers=headers; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
    Assert.Equal(NetworkFailure, transport.Send rest)
    Assert.Equal(NetworkFailure, transport.Send graphQL)
