#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 3 then
    failwith "usage: dotnet fsi eng/probe-migration-project-read.fsx -- ORGANIZATION PROJECT_NUMBER PROJECT_NODE_ID"

let projectNumber =
    match Int32.TryParse args.[1] with
    | true, value when value > 0 -> value
    | _ -> failwith "Project number must be positive"

let token = Environment.GetEnvironmentVariable "MIGRATION_GITHUB_TOKEN"
if String.IsNullOrWhiteSpace token then failwith "MIGRATION_GITHUB_TOKEN is required"

let options =
    { GraphQLUri=Uri "https://api.github.com/graphql"
      Token=token
      UserAgent="fsgg-coordination-migration-project-probe"
      Organization=args.[0]
      ProjectNumber=projectNumber
      ExpectedProjectNodeId=args.[2] }

let client = new HttpClient(Timeout=TimeSpan.FromSeconds 45.)
let transport = HttpMigrationGitHubReadTransport(client) :> IMigrationGitHubReadTransport

let read () =
    match MigrationGitHubRead.readProjectItems options transport with
    | Ok population -> population
    | Error failure -> failwithf "migration Project read refused: %A" failure

let fingerprint (population: MigrationProjectItemPopulation) =
    let values =
        [ population.ProjectNodeId; string population.TotalCount; string population.PageCount ]
        @ (population.Items |> List.collect (fun item ->
            [ item.ItemNodeId; string item.Archived
              item.UpdatedAt.ToUniversalTime().ToString("O"); string item.Content
              item.PayloadSha256 ]))
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
if firstDigest <> secondDigest then failwith "two Project read-only passes were not quiescent"

printfn "MIGRATION_PROJECT_READ_PROBE_OK project=%s pages=%d items=%d twoPassSha256=%s"
    second.ProjectNodeId second.PageCount second.TotalCount secondDigest
client.Dispose()
