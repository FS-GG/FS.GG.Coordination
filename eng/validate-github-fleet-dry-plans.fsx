#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubFleetDryPlanQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubFleetDryPlanQualification

let root = if fsi.CommandLineArgs.Length > 1 then Path.GetFullPath fsi.CommandLineArgs[1] else Path.GetFullPath "."
let read relative = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)))
let text (node: JsonElement) (name: string) = node.GetProperty(name).GetString()
let disposition value =
    match value with
    | "supported" -> GitHubFleetDisposition.Supported | "unsupported" -> GitHubFleetDisposition.Unsupported
    | "unauthorized" -> GitHubFleetDisposition.Unauthorized | "unavailable" -> GitHubFleetDisposition.Unavailable
    | "incomplete" -> GitHubFleetDisposition.Incomplete | "unreadable" -> GitHubFleetDisposition.Unreadable
    | "stale" -> GitHubFleetDisposition.Stale | "indeterminate" -> GitHubFleetDisposition.Indeterminate
    | value -> failwithf "unknown disposition %s" value
let observations (document: JsonDocument) =
    let observedAt = DateTimeOffset.Parse(text document.RootElement "observedAt")
    document.RootElement.GetProperty("repositories").EnumerateArray()
    |> Seq.map (fun repository ->
        { Repository = text repository "repository"; DefaultBranch = text repository "defaultBranch"
          ObservedAt = observedAt; Complete = document.RootElement.GetProperty("complete").GetBoolean()
          Endpoints = repository.GetProperty("endpoints").EnumerateArray() |> Seq.map (fun endpoint ->
              let terminal = endpoint.GetProperty("terminal").GetBoolean()
              { Endpoint = text endpoint "endpoint"; StatusCode = endpoint.GetProperty("statusCode").GetInt32()
                Permission = text endpoint "permission"
                Pagination = { Kind = "terminal-page"; Pages = 1; ItemCount = 1; Terminal = terminal; Next = if terminal then None else Some "next" }
                PayloadSha256 = text endpoint "payloadSha256"; RelevantFingerprint = text endpoint "payloadSha256"
                Disposition = disposition (text endpoint "disposition") }) |> Seq.toList }) |> Seq.toList
let targets (document: JsonDocument) =
    document.RootElement.GetProperty("repositories").EnumerateArray()
    |> Seq.map (fun repository ->
        { Repository = text repository "repository"; ExternalOwner = repository.GetProperty("externalOwner").GetBoolean()
          Settings = repository.GetProperty("settings").EnumerateArray() |> Seq.map (fun setting ->
              { Setting = text setting "setting"; DesiredSha256 = text setting "desiredSha256"
                RequiredPermission = text setting "requiredPermission"; RollbackOrForwardRepair = text setting "rollbackOrForwardRepair" }) |> Seq.toList }) |> Seq.toList

let live = read "evidence/github-substrate-v2/gs2-06-8/live-observations.json"
let desired = read "evidence/github-substrate-v2/gs2-06-8/desired-state.json"
let second = read "evidence/github-substrate-v2/gs2-06-8/reinspection.json"
let observed = observations live
let plan =
    compile "ac05985f0d60c33fb40a5dccecb271a3e00bec4b" "888d1c3307ba119f6c7075b0d8963f7fa14d1e357ce1f97fdb7c803f1aa5465f"
        "316343c921c7444cb95bee292bec8d6da3c6546ffe8805bf93a0490249c76717" "4864d12f13190f2665ddd5e8b5fed3fc29f77cf4"
        expectedReceiptDigests expectedRepositories observed (targets desired)
    |> Result.defaultWith (failwithf "fleet plan refused: %A")
let bytes = serialize plan
if parse bytes <> Ok plan || verify plan.Seal plan <> Ok plan then failwith "canonical replay failed"
if plan.Plans |> List.exists (fun item -> not item.PreservesUnrelatedSettings || not item.Operations.IsEmpty) then failwith "snapshot-preservation plan was not minimal/no-op"
if plan.Plans.Head.Disposition <> GitHubFleetDisposition.ExternalObserveOnly then failwith "external repository was not observe-only"
let secondObserved = observations second
let secondAt = secondObserved.Head.ObservedAt
let secondPlan =
    compile plan.RoadmapRevision plan.RoadmapSha256 plan.UnitContractSha256 plan.SourceRevision plan.ReceiptDigests plan.Roster secondObserved (targets desired)
    |> Result.defaultWith (failwithf "second fleet observation refused: %A")
let reinspection: GitHubFleetReinspection list =
    List.map3 (fun (plan: GitHubFleetRepositoryPlan) (second: GitHubFleetRepositoryPlan) (observation: GitHubFleetRepositoryObservation) ->
        { Repository = plan.Repository; ObservedAt = secondAt; RelevantFingerprint = second.PreStateSha256
          Complete = observation.Complete; Authoritative = true }) plan.Plans secondPlan.Plans secondObserved
let reviewer = review "independent-plan-reviewer/gs2-06.8" (secondAt.AddSeconds(-1.)) bytes
if reinspect plan reviewer reinspection <> Ok Confirmed then failwith "authoritative reinspection did not confirm the plan"
let controls: GitHubFleetControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
if validateControls controls controls <> Ok () then failwith "Q5 inventory failed"
let corpus = read "evidence/github-substrate-v2/gs2-06-8/corpus.json"
let independent = read "evidence/github-substrate-v2/gs2-06-8/independent-expectations.json"
let ids (node: JsonDocument) = node.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let expectedIds = requiredControls |> List.map controlId
if ids corpus <> expectedIds || ids independent <> expectedIds then failwith "retained control inventories differ"
printfn "GITHUB_FLEET_DRY_PLANS_OK repositories=%d endpoints=%d operations=0 seal=%s" plan.Plans.Length (observed |> List.sumBy _.Endpoints.Length) plan.Seal
