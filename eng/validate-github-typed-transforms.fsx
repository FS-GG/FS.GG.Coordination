#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubTypedTransformQualification

let args = fsi.CommandLineArgs |> Array.skip 1

let root, phase =
    match args with
    | [| "--root"; root; "--phase"; phase |] when phase = "transforms" || phase = "recovery" ->
        Path.GetFullPath root, phase
    | _ -> failwith "usage: dotnet fsi eng/validate-github-typed-transforms.fsx -- --root <root> --phase <transforms|recovery>"

let path relative = Path.Combine(root, relative)
let shaBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let shaText (value: string) = value |> Encoding.UTF8.GetBytes |> shaBytes
let shaFile relative = File.ReadAllBytes(path relative) |> shaBytes

let contractDocument =
    JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-3/contract.json"))

let contract = contractDocument.RootElement
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()

if text "schema" contract <> "fsgg.coordination.github-typed-transform-evidence/1" || text "unit" contract <> "GS2-09.3" then
    failwith "typed transform evidence identity differs"

let predecessor = contract.GetProperty("predecessor")
if shaFile (text "path" predecessor) <> text "fileSha256" predecessor then
    failwith "accepted GS2-09.2 receipt bytes differ"

let predecessorDocument = JsonDocument.Parse(File.ReadAllBytes(path (text "path" predecessor)))
let predecessorReceipt = predecessorDocument.RootElement
if text "state" predecessorReceipt <> "accepted"
   || text "unitId" predecessorReceipt <> "GS2-09.2"
   || text "digest" predecessorReceipt <> text "receiptDigest" predecessor then
    failwith "accepted GS2-09.2 receipt identity differs"

let subjectIdentity = "issues-open-and-relevant-closed:1"
let obligations = requiredFamilies |> List.map (fun family -> { SubjectIdentity = subjectIdentity; Family = family })

let candidate identity =
    {
        TargetIdentity = identity
        TargetSchema = "github-v2/1"
        PayloadSha256 = shaText $"payload:{identity}"
        MappingSha256 = shaText $"mapping:{identity}"
    }

let transforms =
    obligations
    |> List.mapi (fun index obligation ->
        let globalId = $"GLOBAL_{index:D2}"
        let decision =
            match index % 3 with
            | 0 ->
                GitHubTypedTransformDecision.Migrated
                    {
                        TargetIdentity = $"v2:{subjectIdentity}:{familyId obligation.Family}"
                        GlobalId = globalId
                        TargetSchema = "github-v2/1"
                        PayloadSha256 = shaText $"result:{index}"
                        MappingSha256 = shaText $"mapping:{index}"
                    }
            | 1 ->
                GitHubTypedTransformDecision.Ambiguous
                    {
                        Reason = "two canonical targets remain"
                        Candidates = [ candidate $"candidate-a:{index}"; candidate $"candidate-b:{index}" ]
                        EvidenceSha256 = shaText $"ambiguity:{index}"
                    }
            | _ ->
                GitHubTypedTransformDecision.Unsupported
                    {
                        Code = "UNSUPPORTED-SOURCE-SHAPE"
                        Reason = "source shape has no v2 representation"
                        EvidenceSha256 = shaText $"unsupported:{index}"
                    }

        {
            SubjectIdentity = obligation.SubjectIdentity
            GlobalId = globalId
            Family = obligation.Family
            SourceSchema = "github-v1/1"
            SourceBytesSha256 = shaText $"bytes:{index}"
            SourceValueSha256 = shaText $"value:{index}"
            Decision = decision
        })

let qualifyFixture () =
    qualify
        "gs2-09-3-controlled-transforms"
        (text "roadmapRevision" contract)
        (text "roadmapSha256" contract)
        (text "unitContractSha256" contract)
        (text "receiptDigest" predecessor)
        (text "manifestNormalizedDigest" contract)
        (text "manifestSeal" contract)
        { Name = "github-v2-transformer"; Version = "1.0.0"; Sha256 = shaText "transformer"; Bytes = 42L }
        obligations
        transforms
        (DateTimeOffset.Parse "2026-09-23T01:00:00Z")

let qualified =
    match qualifyFixture () with
    | Ok value -> value
    | Error findings -> failwithf "baseline typed transforms refused: %A" findings

if verify obligations qualified.Seal qualified <> Ok qualified then
    failwith "sealed typed transform replay failed"

let missing = { qualified with Transforms = qualified.Transforms.Tail }
if verify obligations qualified.Seal missing |> Result.isOk then failwith "omitted typed transform qualified"

let ambiguous = qualified.Transforms[1]
let badAmbiguous =
    match ambiguous.Decision with
    | GitHubTypedTransformDecision.Ambiguous value ->
        { ambiguous with Decision = GitHubTypedTransformDecision.Ambiguous { value with Candidates = value.Candidates.Tail } }
    | _ -> failwith "controlled outcome differs"

let malformed = { qualified with Transforms = qualified.Transforms |> List.updateAt 1 badAmbiguous }
if verify obligations qualified.Seal malformed |> Result.isOk then failwith "one-candidate ambiguity qualified"

let changed = { qualified with Transformer = { qualified.Transformer with Sha256 = shaText "changed" } }
if verify obligations qualified.Seal changed |> Result.isOk then failwith "altered transformer qualified"

let controls: GitHubTypedTransformControlResult list =
    requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })

if validateControls controls controls <> Ok() then failwith "complete typed-transform control inventory refused"

if phase = "recovery" && qualifyFixture () <> Ok qualified then
    failwith "fresh-process deterministic replay differs"

printfn "GS2093-%s-QUALIFIED %s" (phase.ToUpperInvariant()) qualified.NormalizedDigest
