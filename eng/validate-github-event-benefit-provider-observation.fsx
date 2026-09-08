#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubEventBenefitQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-event-benefit-provider-observation.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let read relative = File.ReadAllText(path relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let parse relative = JsonDocument.Parse(read relative)
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
let contract = parse "evidence/github-substrate-v2/gs2-07-7/contract.json"
let observation = parse "evidence/github-substrate-v2/gs2-07-7/provider-observation.json"
let declaration = parse "evidence/github-substrate-v2/gs2-07-7/observation-declaration.json"
let reportBytes = read "evidence/github-substrate-v2/gs2-07-7/measurement-report.json"
let report = match GitHubEventBenefitQualification.parse reportBytes with Ok value -> value | Error errors -> failwithf "measurement report refused: %A" errors
let o = observation.RootElement
let d = declaration.RootElement
if text "schema" o <> "fsgg.github-event-benefit-provider-observation/v1" then failwith "provider schema differs"
if text "sourceCategory" o <> "current-provider-observation" then failwith "provider source category differs"
if shaFile "evidence/github-substrate-v2/gs2-07-7/observation-declaration.json" <> contract.RootElement.GetProperty("observationDeclarationSha256").GetString() then failwith "declaration bytes differ"
if shaFile "evidence/github-substrate-v2/gs2-07-7/provider-observation.json" <> "4db6b0ab1ab32e22186890a988a469fa14c0c8992788fd11ef7d29e3fd9df06b" then failwith "provider bytes differ"
let declared = d.GetProperty("population").EnumerateArray() |> Seq.map (fun value -> value.GetProperty("runId").GetInt64(), value.GetProperty("attempts").EnumerateArray() |> Seq.map _.GetInt32() |> Seq.toList) |> Seq.toList
let observed = o.GetProperty("runAttempts").EnumerateArray() |> Seq.map (fun value -> value.GetProperty("runId").GetInt64(), value.GetProperty("observed").EnumerateArray() |> Seq.map _.GetInt32() |> Seq.toList, value.GetProperty("complete").GetBoolean()) |> Seq.toList
if (declared |> List.map fst) <> (observed |> List.map (fun (runId, _, _) -> runId)) then failwith "run population differs"
if observed |> List.exists (fun (_, _, complete) -> not complete) then failwith "run attempts incomplete"
for runId, attempts in declared do
    let _, actual, _ = observed |> List.find (fun (candidate, _, _) -> candidate = runId)
    if attempts <> actual then failwithf "attempt coverage differs for %d" runId
let pages = o.GetProperty("pages").EnumerateArray() |> Seq.toList
let pagination = o.GetProperty("pagination")
if pages.Length <> 2 || pages |> List.map (fun value -> value.GetProperty("page").GetInt32()) <> [ 1; 2 ] then failwith "provider pages incomplete"
if not(pagination.GetProperty("complete").GetBoolean()) || not(pagination.GetProperty("terminal").GetBoolean()) then failwith "provider pagination not terminal"
for page in pages do
    if page.GetProperty("httpStatus").GetInt32() <> 200 then failwith "provider call did not succeed"
    let rate = page.GetProperty("rate")
    if rate.GetProperty("limit").GetInt32() <= 0 || rate.GetProperty("remaining").GetInt32() < 0 || rate.GetProperty("resource").GetString() <> "core" then failwith "provider rate outcome missing"
    let projection = page.GetProperty("projection")
    if projection.GetProperty("attempt").GetInt32() <> 1 || projection.GetProperty("headSha").GetString() <> "a8b10e073eb7098014ea38ce6edadc35b784ff5c" then failwith "provider head/attempt differs"
    if projection.GetProperty("startedAt").GetString() = "" || projection.GetProperty("endedAt").GetString() = "" then failwith "provider run timestamps missing"
if o.GetProperty("writesAttempted").GetInt32() <> 0 then failwith "provider observation attempted a write"
if o.GetProperty("workloadCalls").GetInt32() <> 0 || o.GetProperty("collectorCalls").GetInt32() <> 2 then failwith "collector/workload accounting differs"
if text "pollingDecision" o <> "retain" || report.PollingDecision <> "retain" then failwith "polling decision differs"
if report.HostedIdentity.IsSome || report.InstalledBenefit || report.ProductionBenefit then failwith "read-only provider observation was mislabeled as hosted/installed/production benefit"
if report.Limits |> List.contains "replay duration is not provider dispatch latency" |> not then failwith "provider dispatch latency distinction missing"
if report.UnknownOutcomes |> List.exists (fun value -> value.Contains("current-registration-runs:event-timestamp-unknown")) |> not then failwith "missing provider event timestamp was not retained as unknown"
printfn "GITHUB_EVENT_BENEFIT_PROVIDER_OBSERVATION_OK pages=%d runs=%d attempts=%d calls=%d writes=0 head=a8b10e073eb7098014ea38ce6edadc35b784ff5c" pages.Length declared.Length (observed |> List.sumBy (fun (_, attempts, _) -> attempts.Length)) (o.GetProperty("callAttempts").GetInt32())
