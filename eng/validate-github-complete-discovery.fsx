#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubCompleteDiscoveryQualification

let args = fsi.CommandLineArgs |> Array.skip 1

let root, phase =
    match args with
    | [| "--root"; root; "--phase"; phase |] when phase = "discovery" || phase = "recovery" ->
        Path.GetFullPath root, phase
    | _ -> failwith "usage: dotnet fsi eng/validate-github-complete-discovery.fsx -- --root <root> --phase <discovery|recovery>"

let path relative = Path.Combine(root, relative)
let shaBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let shaText (value: string) = value |> Encoding.UTF8.GetBytes |> shaBytes
let shaFile relative = File.ReadAllBytes(path relative) |> shaBytes

let contractDocument =
    JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-1/contract.json"))

let contract = contractDocument.RootElement
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()

if text "schema" contract <> "fsgg.coordination.github-complete-discovery-evidence/1" || text "unit" contract <> "GS2-09.1" then
    failwith "discovery evidence contract identity differs"

let predecessor = contract.GetProperty("predecessor")
let handoff = contract.GetProperty("callableHandoff")

if shaFile (text "path" predecessor) <> text "fileSha256" predecessor then
    failwith "accepted GS2-08.9 receipt bytes differ"

if shaFile (text "path" handoff) <> text "fileSha256" handoff then
    failwith "callable discovery handoff bytes differ"

let instant = DateTimeOffset.Parse("2026-09-22T10:00:00Z")

let pass (start: DateTimeOffset) =
    let finish = start.AddMinutes 2.
    {
        SourceRevision = text "sourceRevision" contract
        StartedAt = start
        CompletedAt = finish
        Authorities =
            expectedAuthorities
            |> List.map (fun authority ->
                {
                    Authority = authority
                    ObservedAt = start.AddMinutes 1.
                    PageCount = 2
                    ItemCount = 2
                    Terminal = true
                    NextCursor = None
                    HighWaterMark = $"{authority}:42"
                    Subjects =
                        [
                            { Identity = $"{authority}:1"; Revision = "41"; PayloadSha256 = shaText $"{authority}:one" }
                            { Identity = $"{authority}:2"; Revision = "42"; PayloadSha256 = shaText $"{authority}:two" }
                        ]
                })
    }

let first = pass instant
let second = pass (first.CompletedAt.AddSeconds 1.)

let receipts =
    [ text "fileSha256" predecessor; text "fileSha256" handoff ] |> List.sort

let result =
    qualify
        (text "roadmapRevision" contract)
        (text "roadmapSha256" contract)
        (text "unitContractSha256" contract)
        receipts
        first
        second

let qualified =
    match result with
    | Ok value -> value
    | Error findings -> failwithf "baseline complete discovery refused: %A" findings

if verify qualified.Seal qualified <> Ok qualified then failwith "sealed discovery replay failed"

let changedAuthority = second.Authorities.Head
let lostPage =
    { second with Authorities = { changedAuthority with Terminal = false; NextCursor = Some "next" } :: second.Authorities.Tail }

if qualify qualified.RoadmapRevision qualified.RoadmapSha256 qualified.UnitContractSha256 receipts first lostPage |> Result.isOk then
    failwith "lost terminal page qualified"

let added =
    { changedAuthority with
        ItemCount = 3
        HighWaterMark = $"{changedAuthority.Authority}:43"
        Subjects = changedAuthority.Subjects @ [ { Identity = $"{changedAuthority.Authority}:3"; Revision = "43"; PayloadSha256 = shaText "new" } ] }

let changed = { second with Authorities = added :: second.Authorities.Tail }

if qualify qualified.RoadmapRevision qualified.RoadmapSha256 qualified.UnitContractSha256 receipts first changed |> Result.isOk then
    failwith "non-quiescent population qualified"

let controls: GitHubCompleteDiscoveryControlResult list =
    requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })

if validateControls controls controls <> Ok() then failwith "complete control inventory refused"

if phase = "recovery" then
    let resumed = qualify qualified.RoadmapRevision qualified.RoadmapSha256 qualified.UnitContractSha256 receipts first second
    if resumed <> Ok qualified then failwith "fresh-process deterministic replay differs"

printfn "GS2091-%s-QUALIFIED %s" (phase.ToUpperInvariant()) qualified.NormalizedDigest
