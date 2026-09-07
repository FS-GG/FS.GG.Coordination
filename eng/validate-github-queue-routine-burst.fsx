#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts

module Sandbox = FS.GG.Coordination.Qualification.Contracts.GitHubQueueSandbox

let root = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue (Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..")))
let json relative = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)))
let burstDocument = json "evidence/github-substrate-v2/gs2-07-6/routine-burst.json"
let burst = burstDocument.RootElement
let staleDocument = json "evidence/github-substrate-v2/gs2-07-6/stale-green-refusal.json"
let stale = staleDocument.RootElement
let cleanupDocument = json "evidence/github-substrate-v2/gs2-07-6/burst-cleanup.json"
let cleanup = cleanupDocument.RootElement
let prestateDocument = json "evidence/github-substrate-v2/gs2-07-6/hosted-prestate.json"
let prestate = prestateDocument.RootElement
let stringAt (node:JsonElement) (name:string) = node.GetProperty(name).GetString()
let intAt (node:JsonElement) (name:string) = node.GetProperty(name).GetInt32()
let int64At (node:JsonElement) (name:string) = node.GetProperty(name).GetInt64()
let stringsAt (node:JsonElement) (name:string) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let hints =
    burst.GetProperty("hints").EnumerateArray()
    |> Seq.map(fun row ->
        let prior = row.GetProperty("supersedesHintId")
        { Subject=stringAt row "subject"; HintId=stringAt row "hintId"; Sequence=intAt row "sequence"; SupersedesHintId=if prior.ValueKind=JsonValueKind.Null then None else Some(prior.GetString()) })
    |> Seq.toList
let decisionRows = burst.GetProperty("decisions").EnumerateArray() |> Seq.toList
let decisions =
    decisionRows
    |> List.map(fun row ->
        { Subject=stringAt row "subject"; PriorHeadSha=stringAt row "priorHeadSha"; HeadSha=stringAt row "headSha"; AuthorizationHeadSha=stringAt row "authorizationHeadSha"
          PriorBaseSha=stringAt row "priorBaseSha"; BaseSha=stringAt row "baseSha"; PriorRequiredChecks=stringsAt row "priorRequiredChecks"; RequiredChecks=stringsAt row "requiredChecks"; SuccessfulChecks=stringsAt row "successfulChecks"
          WorkMilliseconds=int64At row "workMilliseconds"; WaitingMilliseconds=int64At row "waitingMilliseconds"; Delivered=row.GetProperty("delivered").GetBoolean() })
let facts =
    { Hints=hints; Decisions=decisions; MaxHintsPerSubject=intAt burst "maxHintsPerSubject"; InFlightEffectCount=intAt burst "inFlightEffectCount"; CancelledInFlightEffectCount=intAt burst "cancelledInFlightEffectCount"
      WorkMilliseconds=int64At burst "workMilliseconds"; WaitingMilliseconds=int64At burst "waitingMilliseconds" }
let get = function Ok value -> value | Error errors -> failwithf "routine burst refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let receipt = Sandbox.compileBurst facts |> get
let primary = decisionRows.Head
let unrelated = decisionRows.Tail.Head
let primaryInitial = primary.GetProperty("initialRun")
let primaryCurrent = primary.GetProperty("currentRun")
let unrelatedRun = unrelated.GetProperty("run")
let providerEdits = burst.GetProperty("providerEdits")
let editIds = providerEdits.GetProperty("nodes").EnumerateArray() |> Seq.map(fun row -> stringAt row "id") |> Seq.toList
let hosted () =
    burst.GetProperty("schemaVersion").GetInt32()=1
    && stringAt burst "disposition"=Sandbox.burstDisposition
    && facts.Hints.Length=5
    && (facts.Hints |> List.filter(fun h -> h.Subject=decisions.Head.Subject) |> List.length)=4
    && providerEdits.GetProperty("totalCount").GetInt32()>=4
    && editIds.Length>=4 && editIds.Length=(editIds |> List.distinct |> List.length)
    && stringAt primaryInitial "headSha"=decisions.Head.PriorHeadSha
    && stringAt primaryInitial "conclusion"="success"
    && stringAt primaryCurrent "headSha"=decisions.Head.HeadSha
    && stringAt primaryCurrent "conclusion"="success"
    && stringAt unrelatedRun "headSha"=decisions.Tail.Head.HeadSha
    && stringAt unrelatedRun "conclusion"="success"
    && decisions.Tail.Head.Delivered
    && stringAt stale "authorizationHeadSha"=decisions.Head.PriorHeadSha
    && stringAt stale "currentHeadSha"=decisions.Head.HeadSha
    && stringAt stale "decision"="refused-stale-green"
    && stringAt cleanup "visibility"=stringAt (prestate.GetProperty("settings")) "visibility"
    && stringAt cleanup "settingsDigest"=stringAt (prestate.GetProperty("settings")) "digest"
    && stringAt cleanup "branchInventoryDigest"=stringAt prestate "branchInventoryDigest"
    && stringAt cleanup "workflowInventoryDigest"=stringAt prestate "workflowInventoryDigest"
    && cleanup.GetProperty("temporaryResources").GetInt32()=0
    && facts.WorkMilliseconds=(decisions |> List.sumBy _.WorkMilliseconds)
    && facts.WaitingMilliseconds=(decisions |> List.sumBy _.WaitingMilliseconds)
    && facts.CancelledInFlightEffectCount=0
    && facts.InFlightEffectCount>0
if not(hosted()) then failwith "retained routine-burst evidence is not cross-bound"
let primaryFacts=decisions.Head
if not(Sandbox.compileBurst { facts with CancelledInFlightEffectCount=1 } |> has GitHubQueueSandboxFinding.InFlightEffectCancelled) then failwith "cancellation mutant passed"
if not(Sandbox.compileBurst { facts with Decisions=decisions.Tail } |> has GitHubQueueSandboxFinding.DistinctSubjectLost) then failwith "lost-subject mutant passed"
if not(Sandbox.compileBurst { facts with Decisions={ primaryFacts with SuccessfulChecks=[ "queue-pilot" ] }::decisions.Tail } |> has (GitHubQueueSandboxFinding.MissingRequiredContext primaryFacts.Subject)) then failwith "missing-context mutant passed"
if not(Sandbox.compileBurst { facts with Decisions={ primaryFacts with AuthorizationHeadSha=primaryFacts.PriorHeadSha }::decisions.Tail } |> has (GitHubQueueSandboxFinding.StaleGreenAuthorization primaryFacts.Subject)) then failwith "stale-green mutant passed"
if Sandbox.verifyBurst receipt.Seal receipt<>Ok receipt then failwith "burst seal replay failed"
printfn "GITHUB_QUEUE_ROUTINE_BURST_OK subjects=%d hints=%d work_ms=%d waiting_ms=%d seal=%s" receipt.Subjects.Length receipt.HintCount receipt.WorkMilliseconds receipt.WaitingMilliseconds receipt.Seal
