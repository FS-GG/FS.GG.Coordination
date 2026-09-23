#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubSealedHistoryQualification

let args = fsi.CommandLineArgs |> Array.skip 1
let root, phase =
    match args with
    | [| "--root"; root; "--phase"; phase |] when phase = "history" || phase = "recovery" -> Path.GetFullPath root, phase
    | _ -> failwith "usage: dotnet fsi eng/validate-github-sealed-history.fsx -- --root <root> --phase <history|recovery>"
let path relative = Path.Combine(root, relative)
let shaBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let shaText (value: string) = value |> Encoding.UTF8.GetBytes |> shaBytes
let shaFile relative = File.ReadAllBytes(path relative) |> shaBytes
let contractDocument = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-5/contract.json"))
let contract = contractDocument.RootElement
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
if text "schema" contract <> "fsgg.coordination.github-sealed-history-evidence/1" || text "unit" contract <> "GS2-09.5" then failwith "sealed-history evidence identity differs"
let predecessor = contract.GetProperty("predecessor")
if shaFile (text "path" predecessor) <> text "fileSha256" predecessor then failwith "accepted GS2-09.4 receipt bytes differ"
let predecessorDocument = JsonDocument.Parse(File.ReadAllBytes(path (text "path" predecessor)))
let receipt = predecessorDocument.RootElement
if text "state" receipt <> "accepted" || text "unitId" receipt <> "GS2-09.4" || text "digest" receipt <> text "receiptDigest" predecessor then failwith "accepted GS2-09.4 receipt identity differs"

let makeRecord index (source: string) rejected =
    let bytes = Encoding.UTF8.GetBytes source
    let outcome =
        if rejected then GitHubHistoryExpectedOutcome.Rejected { Code="LEGACY-INVALID"; Reason="legacy source is intentionally rejected"; EvidenceSha256=shaText $"rejected:{index}" }
        else GitHubHistoryExpectedOutcome.Verified { OutputSchema="github-v2/1"; OutputSha256=shaText $"verified:{index}" }
    { ArchiveIdentity=$"archive:{index:D2}"; SourceIdentity=$"source:{index:D2}"; SourceSchema="github-v1/1"
      SourceBytesBase64=Convert.ToBase64String bytes; SourceBytesSha256=shaBytes bytes; SourceValueSha256=shaText $"value:{index}"; ExpectedOutcome=outcome }
let records = [ makeRecord 1 "legacy issue bytes" false; makeRecord 2 "legacy receipt bytes" true; makeRecord 3 "legacy settings bytes" false ]
let lookups = records |> List.map (fun value -> { LookupKey=value.SourceIdentity; ArchiveIdentity=value.ArchiveIdentity; RecordSha256=recordSha256 value })
let fingerprint name = { Name=name; Version="1.0.0"; Sha256=shaText name; Bytes=64L }
let qualifyFixture () =
    qualify "gs2-09-5-controlled-sealed-history" (text "roadmapRevision" contract) (text "roadmapSha256" contract)
        (text "unitContractSha256" contract) (text "receiptDigest" predecessor)
        (text "manifestNormalizedDigest" contract) (text "manifestSeal" contract)
        (text "transformNormalizedDigest" contract) (text "transformSeal" contract)
        (text "liveOperationNormalizedDigest" contract) (text "liveOperationSeal" contract)
        (fingerprint "github-v1-history-verifier")
        { Artifact=fingerprint "github-v2-production-closure"; V1UpcasterCount=0; ArchiveVerifierOnly=true; LookupReadOnly=true }
        records lookups (DateTimeOffset.Parse "2026-09-23T04:00:00Z")
let qualified = match qualifyFixture () with Ok value -> value | Error findings -> failwithf "baseline sealed history refused: %A" findings
if qualified.ArchiveDigest <> text "archiveDigest" contract || qualified.LookupDigest <> text "lookupDigest" contract
   || qualified.NormalizedDigest <> text "historyNormalizedDigest" contract || qualified.Seal <> text "historySeal" contract then
    failwith "sealed-history artifact differs"
if verify records qualified.Seal qualified <> Ok qualified then failwith "sealed history replay failed"
if verify records qualified.Seal { qualified with Records=qualified.Records.Tail } |> Result.isOk then failwith "omitted history record qualified"
let changed = { qualified.Records.Head with SourceBytesBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes "changed") }
if verify records qualified.Seal { qualified with Records=changed::qualified.Records.Tail } |> Result.isOk then failwith "altered source bytes qualified"
if verify records qualified.Seal { qualified with ProductionClosure={ qualified.ProductionClosure with V1UpcasterCount=1 } } |> Result.isOk then failwith "production v1 upcaster qualified"
let controls: GitHubSealedHistoryControlResult list = requiredControls |> List.map (fun control -> { Control=control; ControlPassed=true; BaselineGreen=true })
if validateControls controls controls <> Ok() then failwith "complete sealed-history control inventory refused"
if phase = "recovery" && qualifyFixture () <> Ok qualified then failwith "fresh-process deterministic replay differs"
printfn "GS2095-%s-QUALIFIED %s %s %s %s" (phase.ToUpperInvariant()) qualified.ArchiveDigest qualified.LookupDigest qualified.NormalizedDigest qualified.Seal
