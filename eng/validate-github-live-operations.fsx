#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubLiveOperationQualification

let args = fsi.CommandLineArgs |> Array.skip 1
let root, phase =
    match args with
    | [| "--root"; root; "--phase"; phase |] when phase = "operations" || phase = "recovery" -> Path.GetFullPath root, phase
    | _ -> failwith "usage: dotnet fsi eng/validate-github-live-operations.fsx -- --root <root> --phase <operations|recovery>"

let path relative = Path.Combine(root, relative)
let shaBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let shaText (value: string) = value |> Encoding.UTF8.GetBytes |> shaBytes
let shaFile relative = File.ReadAllBytes(path relative) |> shaBytes
let contractDocument = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-4/contract.json"))
let contract = contractDocument.RootElement
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()

if text "schema" contract <> "fsgg.coordination.github-live-operation-evidence/1" || text "unit" contract <> "GS2-09.4" then
    failwith "live-operation evidence identity differs"
let predecessor = contract.GetProperty("predecessor")
if shaFile (text "path" predecessor) <> text "fileSha256" predecessor then failwith "accepted GS2-09.3 receipt bytes differ"
let predecessorDocument = JsonDocument.Parse(File.ReadAllBytes(path (text "path" predecessor)))
let predecessorReceipt = predecessorDocument.RootElement
if text "state" predecessorReceipt <> "accepted" || text "unitId" predecessorReceipt <> "GS2-09.3"
   || text "digest" predecessorReceipt <> text "receiptDigest" predecessor then failwith "accepted GS2-09.3 receipt identity differs"

let obligations = requiredFamilies |> List.map (fun family -> { OperationIdentity = $"live:{familyId family}:1"; Family = family })
let decisions =
    obligations
    |> List.mapi (fun index obligation ->
        let globalId = $"LIVE_GLOBAL_{index:D2}"
        let disposition =
            match index % 4 with
            | 0 -> GitHubLiveOperationDisposition.Drain { CompletionReceiptSha256 = shaText $"receipt:{index}"; DrainFence = $"authority-fence:{index}" }
            | 1 -> GitHubLiveOperationDisposition.Migrate { TargetOperationIdentity = $"v2-live:{index}"; GlobalId = globalId; TargetSchema = "github-v2-operation/1"; PayloadSha256 = shaText $"payload:{index}"; MappingSha256 = shaText $"mapping:{index}" }
            | 2 -> GitHubLiveOperationDisposition.Park { ParkingIdentity = $"parked:{index}"; ResumeCondition = "verified-open-v2"; PayloadSha256 = shaText $"park-payload:{index}"; EvidenceSha256 = shaText $"park-evidence:{index}" }
            | _ -> GitHubLiveOperationDisposition.Invalid { Code = "INVALID-STALE-AUTHORITY"; Reason = "operation authority is stale"; EvidenceSha256 = shaText $"invalid:{index}" }
        { OperationIdentity = obligation.OperationIdentity; GlobalId = globalId; Family = obligation.Family; SourceState = "pending"
          SourceBytesSha256 = shaText $"source:{index}"; DependencySetSha256 = shaText $"dependencies:{index}"; Disposition = disposition })

let qualifyFixture () =
    qualify "gs2-09-4-controlled-live-operations" (text "roadmapRevision" contract) (text "roadmapSha256" contract)
        (text "unitContractSha256" contract) (text "receiptDigest" predecessor)
        (text "manifestNormalizedDigest" contract) (text "manifestSeal" contract)
        (text "transformNormalizedDigest" contract) (text "transformSeal" contract)
        { Name = "github-v2-live-operation-planner"; Version = "1.0.0"; Sha256 = shaText "planner"; Bytes = 64L }
        obligations decisions (DateTimeOffset.Parse "2026-09-23T02:00:00Z")

let qualified = match qualifyFixture () with Ok value -> value | Error findings -> failwithf "baseline live-operation plan refused: %A" findings
if qualified.NormalizedDigest <> text "liveOperationNormalizedDigest" contract || qualified.Seal <> text "liveOperationSeal" contract then
    failwith "sealed live-operation artifact differs"
if verify obligations qualified.Seal qualified <> Ok qualified then failwith "sealed live-operation replay failed"
if verify obligations qualified.Seal { qualified with Decisions = qualified.Decisions.Tail } |> Result.isOk then failwith "omitted operation qualified"
let migrated = qualified.Decisions[1]
let badMigrate =
    match migrated.Disposition with
    | GitHubLiveOperationDisposition.Migrate value -> { migrated with Disposition = GitHubLiveOperationDisposition.Migrate { value with GlobalId = "DIFFERENT" } }
    | _ -> failwith "controlled disposition differs"
if verify obligations qualified.Seal { qualified with Decisions = qualified.Decisions |> List.updateAt 1 badMigrate } |> Result.isOk then failwith "changed global ID qualified"
if verify obligations qualified.Seal { qualified with Planner = { qualified.Planner with Sha256 = shaText "changed" } } |> Result.isOk then failwith "altered planner qualified"
let controls: GitHubLiveOperationControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
if validateControls controls controls <> Ok() then failwith "complete live-operation control inventory refused"
if phase = "recovery" && qualifyFixture () <> Ok qualified then failwith "fresh-process deterministic replay differs"
printfn "GS2094-%s-QUALIFIED %s" (phase.ToUpperInvariant()) qualified.NormalizedDigest
