#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubV1AdmissionQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-v1-admission.fsx -- <root>"

let read relative = File.ReadAllText(Path.Combine(root, relative))
let bytes relative = File.ReadAllBytes(Path.Combine(root, relative))
let sha256Bytes (value: byte array) = SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()
let casesDocument = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-5/independent-cases.json")
let subsetDocument = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-5/source-subset.json")

if casesDocument.RootElement.GetProperty("schema").GetString() <> "fsgg.github-substrate.v1-admission-independent-cases/1" then
    failwith "case schema differs"

if subsetDocument.RootElement.GetProperty("schema").GetString() <> "fsgg.github-substrate.v1-admission-source-subset/1" then
    failwith "source subset schema differs"

if not (subsetDocument.RootElement.GetProperty("sourceOnly").GetBoolean())
   || subsetDocument.RootElement.GetProperty("importMode").GetString() <> "exact-byte-copy" then
    failwith "source subset is not an exact source-only import"

for file in subsetDocument.RootElement.GetProperty("files").EnumerateArray() do
    let path = file.GetProperty("path").GetString()
    let expected = file.GetProperty("sha256").GetString()
    if sha256Bytes (bytes path) <> expected then failwith ("source subset digest differs: " + path)

let cases =
    casesDocument.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.map (fun value ->
        { Name = value.GetProperty("name").GetString()
          Outcome = value.GetProperty("outcome").GetString()
          JournalAppends = value.GetProperty("journalAppends").GetInt32()
          ProviderEffects = value.GetProperty("providerEffects").GetInt32() })
    |> List.ofSeq

let qualification = GitHubV1AdmissionQualification.qualify cases
if not qualification.Findings.IsEmpty then failwith ("independent admission controls failed: " + String.concat "," qualification.Findings)

let implementation = read "src/FS.GG.Coordination.GitHub/V1AdmissionRegistry.fs"
let signature = read "src/FS.GG.Coordination.GitHub/V1AdmissionRegistry.fsi"
let protocol = read "src/FS.GG.Coordination.Protocol/Protocol.md"

for required in
    [ "MutationContext"; "OperationHandle"; "DispatchFence"; "refreshDispatch"; "OutboundRequest"; "ReadRequest"; "MutationRequest"
      "AdmissionsClosing"; "sealAdmissions"; "retryAfterProvenAbsence"; "EffectSettlementIndeterminate"
      "fleet-v1-admission:fs-gg-production"; "SealCommit"; "SealGeneration"
      "ClaimJournals"; "ShardedJournalAdapter.validate"
      "authority-head-moved"; "authority-parent-ancestry"; "event.json"; "head.json"; "TreeBytes"; "CommitBytes"
      "lowerHex 40"; "lowerHex 64"; "preparing-requires-exact-seal"; "read-request-cannot-dispatch-mutation" ] do
    if not ((implementation + signature).Contains required) then failwith ("runtime contract omitted " + required)

for required in
    [ "pure def fleetAdmissionMayAppend"; "pure def admissionRoundMayClose"
      "pure def admissionRoundMaySeal"; "pure def admittedEffectMayDispatch"
      "pure def clientReadExternalEffectRaceRefuses"; "pure def fleetMayFreeze"
      "testMutationAdmissionCloseSealAndEffectOwnership" ] do
    if not (protocol.Contains required) then failwith ("canonical model omitted " + required)

for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Authorization:"; "credential"; "repair" ] do
    if implementation.Contains(forbidden, StringComparison.OrdinalIgnoreCase) || signature.Contains(forbidden, StringComparison.OrdinalIgnoreCase) then
        failwith ("runtime boundary contains forbidden provider capability " + forbidden)

let receipt = JsonDocument.Parse(bytes "src/FS.GG.Coordination.Protocol/Generated/receipt.json")
let sourceDigest = sha256Bytes (bytes "src/FS.GG.Coordination.Protocol/Protocol.md")
if receipt.RootElement.GetProperty("sourceSha256").GetString() <> sourceDigest then failwith "generated protocol receipt is stale"

if subsetDocument.RootElement.GetProperty("protocolSourceSha256").GetString() <> sourceDigest then
    failwith "source subset protocol digest is stale"

printfn "GITHUB_V1_ADMISSION_OK cases=%d controls=typed-journal-cas,seal,effects,git-objects" cases.Length
