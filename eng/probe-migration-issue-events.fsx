#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 4 then
    failwith "usage: dotnet fsi eng/probe-migration-issue-events.fsx -- OWNER REPOSITORY EXPECTED_REPOSITORY_ID ISSUE_NUMBER"

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
      UserAgent="fsgg-coordination-migration-event-probe"
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
    let events =
        match MigrationGitHubRead.readIssueEvents options issues issueNumber transport with
        | Ok result -> result
        | Error failure -> failwithf "issue event stream refused: %A" failure
    issues, events

let fingerprint (issues: MigrationIssuePopulation, events: MigrationIssueEventPopulation) =
    let values =
        [ string issues.RepositoryId; string issues.PageCount; string issues.PullRequestCount
          string events.RepositoryId; string events.SubjectNumber; events.SubjectNodeId
          string events.PageCount ]
        @ (issues.Issues |> List.collect (fun item -> [ item.NodeId; item.PayloadSha256 ]))
        @ (events.Pages |> List.collect (fun page ->
            [ page.RequestedUri; page.PayloadSha256; page.NextUri |> Option.defaultValue "" ]))
        @ (events.Events |> List.collect (fun item ->
            [ string item.DatabaseId; item.NodeId; item.EventKind; item.PayloadSha256 ]))
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
if firstDigest <> secondDigest then failwith "two read-only issue event passes were not quiescent"

let issues, events = second
printfn "MIGRATION_ISSUE_EVENTS_PROBE_OK repositoryId=%d issuePages=%d issues=%d subject=%d eventPages=%d events=%d twoPassSha256=%s"
    repositoryId issues.PageCount issues.Issues.Length issueNumber
    events.PageCount events.Events.Length secondDigest
client.Dispose()
