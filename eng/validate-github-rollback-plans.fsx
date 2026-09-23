#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubRollbackPlanQualification

let args = fsi.CommandLineArgs |> Array.skip 1
let root, phase =
    match args with
    | [| "--root"; root; "--phase"; phase |] when phase = "plan" || phase = "recovery" -> Path.GetFullPath root, phase
    | _ -> failwith "usage: dotnet fsi eng/validate-github-rollback-plans.fsx -- --root <root> --phase <plan|recovery>"
let path relative = Path.Combine(root, relative)
let shaBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let shaText (value: string) = value |> Encoding.UTF8.GetBytes |> shaBytes
let shaFile relative = File.ReadAllBytes(path relative) |> shaBytes
let contractDocument = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-6/contract.json"))
let contract = contractDocument.RootElement
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
if text "schema" contract <> "fsgg.coordination.github-rollback-plan-evidence/1" || text "unit" contract <> "GS2-09.6" then failwith "rollback-plan evidence identity differs"
let predecessor = contract.GetProperty("predecessor")
if shaFile (text "path" predecessor) <> text "fileSha256" predecessor then failwith "accepted GS2-09.5 receipt bytes differ"
let receiptDocument = JsonDocument.Parse(File.ReadAllBytes(path (text "path" predecessor)))
let receipt = receiptDocument.RootElement
if text "state" receipt <> "accepted" || text "unitId" receipt <> "GS2-09.5" || text "digest" receipt <> text "receiptDigest" predecessor then failwith "accepted GS2-09.5 receipt identity differs"

let step order id domain target =
    { Order=order; StepId=id; Domain=domain; TargetIdentity=target
      CapturedStateSha256=shaText $"captured:{id}"; RestorePayloadSha256=shaText $"restore:{id}" }
let steps =
    [ step 5 "restore-authority-snapshot" AuthoritySnapshot "coordination:authority-snapshot"
      step 4 "restore-schedules" Schedule "fleet:schedules"
      step 3 "restore-v1-projections" V1Projection "fleet:v1-projections"
      step 2 "restore-receiver-pins" ReceiverPin "fleet:receiver-pins"
      step 1 "restore-settings" Settings "fleet:settings" ]
let qualifyFixture () =
    qualify "gs2-09-6-controlled-rollback-plan" (text "roadmapRevision" contract) (text "roadmapSha256" contract)
        (text "unitContractSha256" contract) (text "receiptDigest" predecessor)
        (text "manifestNormalizedDigest" contract) (text "manifestSeal" contract)
        (text "historyNormalizedDigest" contract) (text "historySeal" contract)
        (text "startEpoch" contract) steps (DateTimeOffset.Parse "2026-09-23T10:00:00Z")
let qualified = match qualifyFixture () with Ok value -> value | Error findings -> failwithf "baseline rollback plan refused: %A" findings
if qualified.TerminalEpoch <> text "terminalEpoch" contract
   || qualified.NormalizedDigest <> text "rollbackNormalizedDigest" contract
   || qualified.Seal <> text "rollbackSeal" contract then
    failwithf "rollback-plan artifact differs normalized=%s seal=%s" qualified.NormalizedDigest qualified.Seal
if verify qualified.Seal qualified <> Ok qualified then failwith "rollback-plan replay failed"
if verify qualified.Seal { qualified with Steps=qualified.Steps.Tail } |> Result.isOk then failwith "missing restoration domain qualified"
if verify qualified.Seal { qualified with Steps=List.rev qualified.Steps } |> Result.isOk then failwith "forward-order rollback qualified"
let first = createReceipt qualified None qualified.Steps[0] (shaText "restored:authority")
let second = createReceipt qualified (Some first) qualified.Steps[1] (shaText "restored:schedules")
if resume qualified [ first; second ] <> Ok(Some qualified.Steps[2]) then failwith "receipt-prefix resume differs"
if resume qualified [ second ] |> Result.isOk then failwith "receipt gap qualified"
if resume qualified [ first; { second with PreviousReceiptSha256=None } ] |> Result.isOk then failwith "broken receipt chain qualified"
let controls: GitHubRollbackPlanControlResult list = requiredControls |> List.map (fun control -> { Control=control; ControlPassed=true; BaselineGreen=true })
if validateControls controls controls <> Ok() then failwith "complete rollback-plan controls refused"
if phase = "recovery" && qualifyFixture () <> Ok qualified then failwith "fresh-process deterministic replay differs"
if contract.GetProperty("rollbackExecuted").GetBoolean() || contract.GetProperty("providerMutation").GetBoolean() then failwith "controlled qualification claims a live effect"
printfn "GS2096-%s-QUALIFIED %s %s" (phase.ToUpperInvariant()) qualified.NormalizedDigest qualified.Seal
