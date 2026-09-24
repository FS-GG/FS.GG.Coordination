#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 4 then
    failwith "usage: dotnet fsi eng/probe-migration-pull-request-reviews.fsx -- OWNER REPOSITORY EXPECTED_REPOSITORY_ID PULL_REQUEST_NUMBER"

let positive name (value: string) =
    match Int64.TryParse value with
    | true, parsed when parsed > 0L -> parsed
    | _ -> failwithf "%s must be a positive integer" name

let repositoryId = positive "repository ID" args.[2]
let pullRequestNumber = positive "pull request number" args.[3] |> int
let token = Environment.GetEnvironmentVariable "MIGRATION_GITHUB_TOKEN"
if String.IsNullOrWhiteSpace token then failwith "MIGRATION_GITHUB_TOKEN is required"

let options =
    { ApiBase=Uri "https://api.github.com/"
      GraphQLUri=Uri "https://api.github.com/graphql"
      Token=token
      UserAgent="fsgg-coordination-migration-review-probe"
      Owner=args.[0]
      Repository=args.[1]
      ExpectedRepositoryId=repositoryId }

let client = new HttpClient(Timeout=TimeSpan.FromSeconds 45.)
let transport = HttpMigrationGitHubReadTransport(client) :> IMigrationGitHubReadTransport

let read () =
    let issues =
        match MigrationGitHubRead.readIssues options transport with
        | Ok result -> result
        | Error failure -> failwithf "issue census refused: %A" failure
    let pullRequests =
        match MigrationGitHubRead.readPullRequests options issues transport with
        | Ok result -> result
        | Error failure -> failwithf "pull request census refused: %A" failure
    let reviews =
        match MigrationGitHubRead.readPullRequestReviews options pullRequests pullRequestNumber transport with
        | Ok result -> result
        | Error failure -> failwithf "review stream refused: %A" failure
    let inlineComments =
        match MigrationGitHubRead.readPullRequestReviewComments options pullRequests pullRequestNumber transport with
        | Ok result -> result
        | Error failure -> failwithf "inline review comment stream refused: %A" failure
    issues, pullRequests, reviews, inlineComments

let fingerprint (issues: MigrationIssuePopulation, pullRequests: MigrationPullRequestPopulation,
                 reviews: MigrationPullRequestReviewPopulation,
                 comments: MigrationPullRequestReviewCommentPopulation) =
    let values =
        [ string issues.RepositoryId; string issues.PageCount; string issues.PullRequestCount
          string pullRequests.RepositoryId; string pullRequests.PageCount
          string reviews.PullRequestNumber; reviews.PullRequestNodeId; string reviews.PageCount
          string comments.PullRequestNumber; comments.PullRequestNodeId; string comments.PageCount ]
        @ (issues.Issues |> List.collect (fun item -> [ item.NodeId; item.PayloadSha256 ]))
        @ (pullRequests.Pages |> List.collect (fun page ->
            [ page.RequestedUri; page.PayloadSha256; page.NextUri |> Option.defaultValue "" ]))
        @ (pullRequests.PullRequests |> List.collect (fun item -> [ item.NodeId; item.PayloadSha256 ]))
        @ (reviews.Pages |> List.collect (fun page ->
            [ page.RequestedUri; page.PayloadSha256; page.NextUri |> Option.defaultValue "" ]))
        @ (reviews.Reviews |> List.collect (fun item -> [ item.NodeId; item.PayloadSha256 ]))
        @ (comments.Pages |> List.collect (fun page ->
            [ page.RequestedUri; page.PayloadSha256; page.NextUri |> Option.defaultValue "" ]))
        @ (comments.Comments |> List.collect (fun item -> [ item.NodeId; item.PayloadSha256 ]))
    values
    |> List.map (fun value -> $"{Encoding.UTF8.GetByteCount value}:{value}")
    |> String.concat ""
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let first = read ()
let second = read ()
let firstDigest = fingerprint first
let secondDigest = fingerprint second
if firstDigest <> secondDigest then failwith "two read-only pull request review passes were not quiescent"

let issues, pullRequests, reviews, inlineComments = second
printfn "MIGRATION_PULL_REQUEST_REVIEWS_PROBE_OK repositoryId=%d issues=%d pullRequests=%d subject=%d reviewPages=%d reviews=%d inlineCommentPages=%d inlineComments=%d twoPassSha256=%s"
    repositoryId issues.Issues.Length pullRequests.PullRequests.Length pullRequestNumber
    reviews.PageCount reviews.Reviews.Length inlineComments.PageCount inlineComments.Comments.Length secondDigest
client.Dispose()
