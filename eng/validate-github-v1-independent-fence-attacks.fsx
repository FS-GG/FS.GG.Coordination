open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-v1-independent-fence-attacks.fsx -- <root>"

let read relative = File.ReadAllText(Path.Combine(root, relative))
let bytes relative = File.ReadAllBytes(Path.Combine(root, relative))
let sha256Bytes (value: byte array) = SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()
let sha256 relative = bytes relative |> sha256Bytes
let text (node: JsonElement) (name: string) = node.GetProperty(name).GetString()
let strings (node: JsonElement) (name: string) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList

let evidenceRoot = "evidence/github-substrate-v2/gs2-08-6"
let bindingDocument = JsonDocument.Parse(read $"{evidenceRoot}/source-binding.json")
let expectationsDocument = JsonDocument.Parse(read $"{evidenceRoot}/attack-expectations.json")
let blockersDocument = JsonDocument.Parse(read $"{evidenceRoot}/gs2-08-9-blockers.json")
let producerContractDocument = JsonDocument.Parse(read $"{evidenceRoot}/producer-attack-contract.json")
let censusDocument = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json")
let accepted85Document = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.5.json")
let binding = bindingDocument.RootElement
let expectations = expectationsDocument.RootElement
let producerContract = producerContractDocument.RootElement

if text binding "schema" <> "fsgg.github-substrate.v1-independent-fence-source-binding/1" then failwith "source binding schema differs"
if text binding "producerSourceHead" <> "eed927d9ca544369e2dfdbb347ba9fb6ad1a9768" then failwith "producer source head differs"
if text binding "producerProtectedMerge" <> "cc70abbacf31a7f7ff45aadb2aedbf37e8a8999c" then failwith "producer protected merge differs"
if text binding "producerProtectedTree" <> "5db84daefc48341f8be86efa5268c16ecb9a80ac" then failwith "producer protected tree differs"
if text binding "coordinationSourceMerge" <> "48fa43e67de52d4e728a9abff30686fc029d1d8d" then failwith "Coordination source merge differs"
if text binding "coordinationSourceTree" <> "484e6c53f9f474bfedfd10f22ac301e3227478cd" then failwith "Coordination source tree differs"
if text binding "producerCensusSha256" <> "3355d86beb6df99a66cd8d19fefe24dc574252ab9bdef08ba576216cf2dbbee7" then failwith "refreshed producer census differs"
if text binding "acceptedGS2085Digest" <> text accepted85Document.RootElement "digest" then failwith "GS2-08.5 acceptance binding differs"
if text binding "closedPopulationSha256" <> sha256 "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json" then failwith "closed census population differs"

let expectedEpochs = [ "OperatingV1"; "Preparing"; "FreezeRequested"; "Frozen"; "SwitchedV2"; "VerifiedV2"; "OpenV2"; "ObservingV2"; "ContractingV1"; "OperatingV2"; "RollingBack" ]
let expectedAttacks = [ "stale-cache"; "lost-response"; "ledger-rewind"; "missing-tag"; "wrong-manifest"; "permission-loss"; "old-client" ]
let expectedSurfaces = [ "FencedTransport"; "DurableMutationFence"; "canonical-effect-ids"; "legacy-send-refusal"; "read-allowlist"; "retry-after-proven-absence"; "provider-reconciliation" ]
if text expectations "schema" <> "fsgg.github-substrate.v1-independent-fence-expectations/1" then failwith "expectation schema differs"
if strings expectations "epochs" <> expectedEpochs || strings expectations "attacks" <> expectedAttacks then failwith "attack expectation differs"
if expectations.GetProperty("providerEvidence").ValueKind <> JsonValueKind.Null then failwith "Q4 provider evidence was invented"

if text producerContract "schema" <> "fsgg.github-substrate.v1-producer-attack-contract/1" then failwith "producer attack contract schema differs"
if text producerContract "state" <> "awaiting-exact-producer-result" then failwith "producer attack state falsely claims completion"
if text producerContract "producerCommit" <> text binding "producerProtectedMerge" || text producerContract "producerTree" <> text binding "producerProtectedTree" then failwith "producer attack identity differs"
if strings producerContract "requiredSurfaces" <> expectedSurfaces then failwith "producer attack surfaces differ"
if strings producerContract "requiredEpochs" <> expectedEpochs || strings producerContract "requiredFaults" <> expectedAttacks then failwith "producer attack matrix differs"
if producerContract.GetProperty("result").ValueKind <> JsonValueKind.Null then failwith "unvalidated producer result was attached"

let commandRoots = censusDocument.RootElement.GetProperty("commandRoots").EnumerateArray() |> Seq.toList
let writeCommands = commandRoots |> List.filter (fun row -> text row "writes" <> "never")
let alwaysCount = writeCommands |> List.filter (fun row -> text row "writes" = "always") |> List.length
let conditionalCount = writeCommands |> List.filter (fun row -> text row "writes" = "conditional") |> List.length
if commandRoots.Length <> 54 || alwaysCount <> 20 || conditionalCount <> 6 then failwith "accepted command population differs"

let writerDispositions = Set [ "remote-writer"; "conditional-remote-writer"; "protected-admin-writer"; "publish-writer" ]
let writerSources = censusDocument.RootElement.GetProperty("sources").EnumerateArray() |> Seq.filter (fun row -> writerDispositions.Contains(text row "disposition")) |> Seq.toList
if not (writerSources |> List.exists (fun row -> (text row "path").StartsWith(".github/workflows/", StringComparison.Ordinal))) then failwith "workflow routes omitted"
if not (writerSources |> List.exists (fun row -> (text row "path").StartsWith("scripts/", StringComparison.Ordinal))) then failwith "script routes omitted"
if not (writerSources |> List.exists (fun row -> text row "path" = "tools/routine-delivery.py")) then failwith "non-CLI route omitted"

let blockers = blockersDocument.RootElement.GetProperty("clients").EnumerateArray() |> Seq.toList
if blockers.Length <> 2 then failwith "historical client blocker population differs"
for blocker in blockers do
    if text blocker "disposition" <> "GS2-08.9-retain-or-retire" || text blocker "proof" <> "cannot-prove-fence-refusal" then failwith "historical client falsely passes"

printfn "GITHUB_V1_INDEPENDENT_FENCE_REGISTRATION_OK commands=%d routes=%d state=awaiting-exact-producer-result blockers=%d" writeCommands.Length writerSources.Length blockers.Length
