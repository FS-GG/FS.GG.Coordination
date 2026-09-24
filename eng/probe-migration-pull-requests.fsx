#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 3 then
    failwith "usage: dotnet fsi eng/probe-migration-pull-requests.fsx -- OWNER REPOSITORY EXPECTED_REPOSITORY_ID"

let repositoryId =
    match Int64.TryParse args.[2] with
    | true, value when value > 0L -> value
    | _ -> failwith "expected repository ID must be a positive integer"

let token = Environment.GetEnvironmentVariable "MIGRATION_GITHUB_TOKEN"
if String.IsNullOrWhiteSpace token then failwith "MIGRATION_GITHUB_TOKEN is required"

let options =
    { ApiBase=Uri "https://api.github.com/"
      GraphQLUri=Uri "https://api.github.com/graphql"
      Token=token
      UserAgent="fsgg-coordination-migration-pr-probe"
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
    issues, pullRequests

let fingerprint (issues: MigrationIssuePopulation, pullRequests: MigrationPullRequestPopulation) =
    let values =
        [ string issues.RepositoryId; string issues.PageCount; string issues.PullRequestCount
          string pullRequests.RepositoryId; string pullRequests.PageCount ]
        @ (issues.Issues |> List.collect (fun item -> [ item.NodeId; item.PayloadSha256 ]))
        @ (pullRequests.Pages |> List.collect (fun page ->
            [ page.RequestedUri; page.PayloadSha256; page.NextUri |> Option.defaultValue "" ]))
        @ (pullRequests.PullRequests |> List.collect (fun item ->
            [ string item.Number; item.NodeId; item.HeadSha; item.BaseSha; item.PayloadSha256 ]))
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
if firstDigest <> secondDigest then failwith "two read-only pull request passes were not quiescent"

let issues, pullRequests = second
printfn "MIGRATION_PULL_REQUEST_PROBE_OK repositoryId=%d issuePages=%d issues=%d pullRequestPages=%d pullRequests=%d twoPassSha256=%s"
    repositoryId issues.PageCount issues.Issues.Length pullRequests.PageCount
    pullRequests.PullRequests.Length secondDigest
client.Dispose()
