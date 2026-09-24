#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 4 then
    failwith "usage: dotnet fsi eng/probe-migration-issue-comments.fsx -- OWNER REPOSITORY EXPECTED_REPOSITORY_ID ISSUE_NUMBER"

let positive name (value: string) =
    match Int64.TryParse value with
    | true, parsed when parsed > 0L -> parsed
    | _ -> failwithf "%s must be a positive integer" name

let repositoryId = positive "repository ID" args.[2]
let issueNumber = positive "issue number" args.[3] |> int
let token = Environment.GetEnvironmentVariable "MIGRATION_GITHUB_TOKEN"
if String.IsNullOrWhiteSpace token then failwith "MIGRATION_GITHUB_TOKEN is required"

let options =
    { ApiBase=Uri "https://api.github.com/"
      GraphQLUri=Uri "https://api.github.com/graphql"
      Token=token
      UserAgent="fsgg-coordination-migration-comment-probe"
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
    let comments =
        match MigrationGitHubRead.readIssueComments options issues issueNumber transport with
        | Ok result -> result
        | Error failure -> failwithf "issue comment stream refused: %A" failure
    issues, comments

let fingerprint (issues: MigrationIssuePopulation, comments: MigrationIssueCommentPopulation) =
    let values =
        [ string issues.RepositoryId; string issues.PageCount; string issues.PullRequestCount
          string comments.RepositoryId; string comments.SubjectNumber; comments.SubjectNodeId
          string comments.PageCount ]
        @ (issues.Issues |> List.collect (fun item -> [ item.NodeId; item.PayloadSha256 ]))
        @ (comments.Pages |> List.collect (fun page ->
            [ page.RequestedUri; page.PayloadSha256; page.NextUri |> Option.defaultValue "" ]))
        @ (comments.Comments |> List.collect (fun item ->
            [ string item.DatabaseId; item.NodeId; item.PayloadSha256 ]))
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
if firstDigest <> secondDigest then failwith "two read-only issue comment passes were not quiescent"

let issues, comments = second
printfn "MIGRATION_ISSUE_COMMENTS_PROBE_OK repositoryId=%d issuePages=%d issues=%d subject=%d commentPages=%d comments=%d twoPassSha256=%s"
    repositoryId issues.PageCount issues.Issues.Length issueNumber
    comments.PageCount comments.Comments.Length secondDigest
client.Dispose()
