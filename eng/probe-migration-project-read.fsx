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
    match MigrationGitHubRead.readProjectItems options transport,
          MigrationGitHubRead.readProjectFields options transport,
          MigrationGitHubRead.readProjectValues options transport with
    | Ok items, Ok fields, Ok values ->
        match MigrationGitHubRead.reconcileProject items fields values with
        | Ok snapshot -> items, fields, values, snapshot
        | Error failure -> failwithf "migration Project reconciliation refused: %A" failure
    | Error failure, _, _ | _, Error failure, _ | _, _, Error failure ->
        failwithf "migration Project read refused: %A" failure

let fingerprintItems (population: MigrationProjectItemPopulation) =
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

let fingerprintFields (population: MigrationProjectFieldPopulation) =
    let values =
        [ population.ProjectNodeId; string population.TotalCount; string population.PageCount ]
        @ (population.Fields |> List.collect (fun field ->
            [ field.FieldNodeId; field.Name; field.DataType; string field.Kind; field.PayloadSha256 ]
            @ (field.Options |> List.collect (fun option -> [ option.Id; option.Name ]))))
    values
    |> List.map (fun value -> $"{Encoding.UTF8.GetByteCount value}:{value}")
    |> String.concat ""
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let fingerprintValues (population: MigrationProjectValuePopulation) =
    let values =
        [ population.ProjectNodeId; string population.TotalCount; string population.PageCount ]
        @ (population.Items |> List.collect (fun item ->
            [ item.ItemNodeId; string item.FieldValueCount ]
            @ (item.FieldValues |> List.collect (fun field ->
                [ field.FieldNodeId; field.ValueKind; field.PayloadSha256 ]))))
    values
    |> List.map (fun value -> $"{Encoding.UTF8.GetByteCount value}:{value}")
    |> String.concat ""
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let first = read ()
let second = read ()
let firstItems, firstFields, firstValues, firstSnapshot = first
let secondItems, secondFields, secondValues, secondSnapshot = second
let firstItemsDigest = fingerprintItems firstItems
let secondItemsDigest = fingerprintItems secondItems
let firstFieldsDigest = fingerprintFields firstFields
let secondFieldsDigest = fingerprintFields secondFields
let firstValuesDigest = fingerprintValues firstValues
let secondValuesDigest = fingerprintValues secondValues
if firstSnapshot.NormalizedSha256 <> secondSnapshot.NormalizedSha256
   || firstItemsDigest <> secondItemsDigest || firstFieldsDigest <> secondFieldsDigest
   || firstValuesDigest <> secondValuesDigest then
    failwith "two Project read-only passes were not quiescent"

let fieldValueCount = secondValues.Items |> List.sumBy _.FieldValueCount
printfn "MIGRATION_PROJECT_READ_PROBE_OK project=%s itemPages=%d items=%d fieldPages=%d fields=%d valuePages=%d fieldValues=%d itemSha256=%s fieldSha256=%s valueSha256=%s snapshotSha256=%s"
    secondItems.ProjectNodeId secondItems.PageCount secondItems.TotalCount
    secondFields.PageCount secondFields.TotalCount secondValues.PageCount fieldValueCount
    secondItemsDigest secondFieldsDigest secondValuesDigest secondSnapshot.NormalizedSha256
client.Dispose()
