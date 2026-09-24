#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 3 then
    failwith "usage: dotnet fsi eng/probe-migration-native-relations.fsx -- OWNER REPOSITORY EXPECTED_REPOSITORY_ID"

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
      UserAgent="fsgg-coordination-migration-relations-probe"
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
    let relations =
        match MigrationGitHubRead.readNativeRelations options issues transport with
        | Ok result -> result
        | Error failure -> failwithf "native relation census refused: %A" failure
    issues, relations

let fingerprint (issues: MigrationIssuePopulation, relations: MigrationRelationPopulation) =
    let values =
        [ string issues.RepositoryId; string issues.PageCount; string issues.PullRequestCount
          string relations.IssueCount; string relations.ExternalEdgeCount ]
        @ (issues.Issues |> List.collect (fun item -> [item.NodeId; item.PayloadSha256]))
        @ (relations.Issues |> List.collect (fun item ->
            [item.IssueNodeId; item.PayloadSha256]
            @ (item.ContinuationPages |> List.collect (fun page ->
                [page.Connection; page.RequestedCursor; page.PayloadSha256]))))
        @ (relations.Edges |> List.collect (fun edge ->
            [ string edge.Kind; string edge.Source.RepositoryId; edge.Source.NodeId
              string edge.Target.RepositoryId; edge.Target.NodeId ]))
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
if firstDigest <> secondDigest then failwith "two read-only relation passes were not quiescent"

let issues, relations = second
printfn "MIGRATION_RELATIONS_PROBE_OK repositoryId=%d issuePages=%d issues=%d edges=%d externalEdges=%d twoPassSha256=%s"
    repositoryId issues.PageCount relations.IssueCount relations.Edges.Length
    relations.ExternalEdgeCount secondDigest
client.Dispose()
