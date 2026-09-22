#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubImmutableManifestQualification

let args = fsi.CommandLineArgs |> Array.skip 1

let root, phase =
    match args with
    | [| "--root"; root; "--phase"; phase |] when phase = "manifest" || phase = "recovery" ->
        Path.GetFullPath root, phase
    | _ -> failwith "usage: dotnet fsi eng/validate-github-immutable-manifest.fsx -- --root <root> --phase <manifest|recovery>"

let path relative = Path.Combine(root, relative)
let shaBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let shaText (value: string) = value |> Encoding.UTF8.GetBytes |> shaBytes
let shaFile relative = File.ReadAllBytes(path relative) |> shaBytes
let revision character = String.replicate 40 character

let contractDocument =
    JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-2/contract.json"))

let contract = contractDocument.RootElement
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()

if text "schema" contract <> "fsgg.coordination.github-immutable-manifest-evidence/1" || text "unit" contract <> "GS2-09.2" then
    failwith "immutable manifest evidence identity differs"

let predecessor = contract.GetProperty("predecessor")
if shaFile (text "path" predecessor) <> text "fileSha256" predecessor then
    failwith "accepted GS2-09.1 receipt bytes differ"

let predecessorDocument = JsonDocument.Parse(File.ReadAllBytes(path (text "path" predecessor)))
let predecessorReceipt = predecessorDocument.RootElement

if text "state" predecessorReceipt <> "accepted"
   || text "unitId" predecessorReceipt <> "GS2-09.1"
   || text "digest" predecessorReceipt <> text "receiptDigest" predecessor then
    failwith "accepted GS2-09.1 receipt identity differs"

let discovered =
    [
        "claim-and-event-streams:1"
        "hierarchy-and-dependencies:1"
        "issues-open-and-relevant-closed:1"
        "project-fields:1"
        "project-items:1"
        "receiver-identities:1"
        "repository-settings:1"
        "review-delivery-release-records:1"
        "workflow-pins:1"
    ]

let fingerprint name version content =
    { Name = name; Version = version; Sha256 = shaText content; Bytes = int64 (Encoding.UTF8.GetByteCount content) }

let subjects =
    discovered
    |> List.mapi (fun index identity ->
        {
            Old =
                {
                    Identity = identity
                    GlobalId = $"OLD_{index:D2}"
                    SourceRevision = $"source-{index:D2}"
                    Schema = "github-v1/1"
                    BytesSha256 = shaText $"old-bytes:{identity}"
                    ValueSha256 = shaText $"old-value:{identity}"
                }
            Result =
                {
                    Identity = $"v2:{identity}"
                    GlobalId = $"NEW_{index:D2}"
                    Revision = $"result-{index:D2}"
                    PayloadSha256 = shaText $"v2-result:{identity}"
                    Outcome = "planned"
                }
            Disposition = "pending-typed-transform"
        })

let archives =
    GitHubCompleteDiscoveryQualification.expectedAuthorities
    |> List.map (fun authority ->
        {
            Authority = authority
            SourceSchema = "github-v1/1"
            SourceBytesSha256 = shaText $"source:{authority}"
            ArchiveSha256 = shaText $"archive:{authority}"
            LookupIndexSha256 = shaText $"lookup:{authority}"
            VerifierSha256 = shaText $"verifier:{authority}"
        })

let qualifyFixture () =
    qualify
        "gs2-09-2-controlled-manifest"
        (text "roadmapRevision" contract)
        (text "roadmapSha256" contract)
        (text "unitContractSha256" contract)
        (text "receiptDigest" predecessor)
        (text "discoverySourceRevision" contract)
        (text "discoveryNormalizedDigest" contract)
        (text "discoverySeal" contract)
        discovered
        (fingerprint "github-v1-model" "1" "old-model")
        (fingerprint "github-v2-model" "2" "new-model")
        [ fingerprint "migration-cli" "0.90.0" "migration-cli"; fingerprint "verifier" "1.0.0" "verifier" ]
        subjects
        [ { Identity = "operation:1"; Generation = 1L; Kind = "queued-write"; Target = "FS-GG/repository#1"; State = "observed"; PayloadSha256 = shaText "operation" } ]
        [ { Receiver = "FS-GG/.github"; RepositoryId = "R_receiver"; CommitSha = revision "1"; TreeSha = revision "2"; PinsSha256 = shaText "receiver-pins" } ]
        [ { Repository = "FS-GG/.github"; PrestateSha256 = shaText "settings-prestate"; DesiredSha256 = shaText "settings-desired"; PlanSha256 = shaText "settings-plan" } ]
        archives
        [
            { Order = 1; PhaseId = "phase-01-prepare"; InputSha256 = shaText "phase-1-input"; OperationsSha256 = shaText "phase-1-operations"; ReceiptSha256 = shaText "phase-1-receipt"; RollbackInputIds = [ "rollback-receivers" ] }
            { Order = 2; PhaseId = "phase-02-apply"; InputSha256 = shaText "phase-2-input"; OperationsSha256 = shaText "phase-2-operations"; ReceiptSha256 = shaText "phase-2-receipt"; RollbackInputIds = [ "rollback-receivers"; "rollback-settings" ] }
        ]
        [
            { Login = "reviewer-a"; GlobalId = "U_A"; DecisionSha256 = shaText "review-a" }
            { Login = "reviewer-b"; GlobalId = "U_B"; DecisionSha256 = shaText "review-b" }
        ]
        [
            { Identity = "rollback-receivers"; Kind = "receiver-heads"; Revision = revision "3"; PayloadSha256 = shaText "rollback-receivers" }
            { Identity = "rollback-settings"; Kind = "repository-settings"; Revision = revision "4"; PayloadSha256 = shaText "rollback-settings" }
        ]
        (DateTimeOffset.Parse "2026-09-23T00:00:00Z")

let qualified =
    match qualifyFixture () with
    | Ok value -> value
    | Error findings -> failwithf "baseline immutable manifest refused: %A" findings

if verify discovered qualified.Seal qualified <> Ok qualified then
    failwith "sealed immutable manifest replay failed"

let omitted = { qualified with Subjects = qualified.Subjects.Tail }
if verify discovered qualified.Seal omitted |> Result.isOk then failwith "omitted discovered subject qualified"

let changedReceiver =
    { qualified with ReceiverHeads = qualified.ReceiverHeads |> List.map (fun value -> { value with PinsSha256 = shaText "changed" }) }
if verify discovered qualified.Seal changedReceiver |> Result.isOk then failwith "altered receiver binding qualified"

let missingArchive = { qualified with Archives = qualified.Archives.Tail }
if verify discovered qualified.Seal missingArchive |> Result.isOk then failwith "incomplete archive population qualified"

let badPhase =
    { qualified with
        PhasePlans =
            qualified.PhasePlans
            |> List.map (fun value -> if value.Order = 1 then { value with RollbackInputIds = [ "unknown" ] } else value) }
if verify discovered qualified.Seal badPhase |> Result.isOk then failwith "unknown rollback reference qualified"

let controls: GitHubImmutableManifestControlResult list =
    requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })

if validateControls controls controls <> Ok() then failwith "complete immutable manifest control inventory refused"

if phase = "recovery" && qualifyFixture () <> Ok qualified then
    failwith "fresh-process deterministic replay differs"

printfn "GS2092-%s-QUALIFIED %s" (phase.ToUpperInvariant()) qualified.NormalizedDigest
