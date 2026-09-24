#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 3 then
    failwith "usage: dotnet fsi eng/probe-migration-github-read.fsx -- OWNER REPOSITORY EXPECTED_REPOSITORY_ID"

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
      UserAgent="fsgg-coordination-migration-read-probe"
      Owner=args.[0]
      Repository=args.[1]
      ExpectedRepositoryId=repositoryId }

let client = new HttpClient(Timeout=TimeSpan.FromSeconds 45.)
let transport = HttpMigrationGitHubReadTransport(client) :> IMigrationGitHubReadTransport

let read () =
    match MigrationGitHubRead.readIssues options transport,
          MigrationGitHubRead.readIssueTypes options transport with
    | Ok issues, Ok issueTypes -> issues, issueTypes
    | Error failure, _ | _, Error failure -> failwithf "migration read refused: %A" failure

let fingerprint (issues: MigrationIssuePopulation, types: MigrationIssueTypePopulation) =
    let fields =
        [ string issues.RepositoryId; string issues.PageCount; string issues.PullRequestCount
          string types.RepositoryId; string types.PageCount ]
        @ (issues.Issues |> List.collect (fun item ->
            [ string item.Number; string item.DatabaseId; item.NodeId; item.State
              item.UpdatedAt.ToUniversalTime().ToString("O"); item.PayloadSha256 ]))
        @ (types.IssueTypes |> List.collect (fun item -> [ item.NodeId; item.Name; item.PayloadSha256 ]))
    fields
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
if firstDigest <> secondDigest then failwith "two read-only passes were not quiescent"

let issues, types = second
printfn "MIGRATION_READ_PROBE_OK repositoryId=%d issuePages=%d issues=%d pullRequests=%d issueTypePages=%d issueTypes=%d twoPassSha256=%s" 
    repositoryId issues.PageCount issues.Issues.Length issues.PullRequestCount
    types.PageCount types.IssueTypes.Length secondDigest
client.Dispose()
