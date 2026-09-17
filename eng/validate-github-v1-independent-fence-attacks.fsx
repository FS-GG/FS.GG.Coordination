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
let producerResultDocument = JsonDocument.Parse(read $"{evidenceRoot}/producer-attack-result.json")
let censusDocument = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json")
let accepted85Document = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.5.json")
let binding = bindingDocument.RootElement
let expectations = expectationsDocument.RootElement
let producerContract = producerContractDocument.RootElement
let producerResult = producerResultDocument.RootElement

if text binding "schema" <> "fsgg.github-substrate.v1-independent-fence-source-binding/1" then failwith "source binding schema differs"
if text binding "producerSourceHead" <> "eed927d9ca544369e2dfdbb347ba9fb6ad1a9768" then failwith "producer source head differs"
if text binding "producerProtectedMerge" <> "cc70abbacf31a7f7ff45aadb2aedbf37e8a8999c" then failwith "producer protected merge differs"
if text binding "producerProtectedTree" <> "5db84daefc48341f8be86efa5268c16ecb9a80ac" then failwith "producer protected tree differs"
if text binding "coordinationSourceMerge" <> "48fa43e67de52d4e728a9abff30686fc029d1d8d" then failwith "Coordination source merge differs"
if text binding "coordinationSourceTree" <> "484e6c53f9f474bfedfd10f22ac301e3227478cd" then failwith "Coordination source tree differs"
if text binding "producerCensusSha256" <> "3355d86beb6df99a66cd8d19fefe24dc574252ab9bdef08ba576216cf2dbbee7" then failwith "refreshed producer census differs"
if text binding "producerAttackSourceHead" <> "50a89965b09595244f7def7757d7aef91f0ce0e2" then failwith "producer attack source head differs"
if text binding "producerAttackSourceTree" <> "a3302c16a0ed5f44e6490a87d3a28e5563ea25b9" then failwith "producer attack source tree differs"
if text binding "producerAttackProtectedMerge" <> "068d5dc3fa24d7e1fca99401c755e2f1f5fafe1d" then failwith "producer attack protected merge differs"
if text binding "producerAttackProtectedTree" <> "a3302c16a0ed5f44e6490a87d3a28e5563ea25b9" then failwith "producer attack protected tree differs"
if text binding "producerAttackEvidenceSha256" <> "9007aa34a34d4d957f0948cf3b3059d5c5bf189c4b2ec7645f35548884c60b15" then failwith "producer attack evidence differs"
if text binding "acceptedGS2085Digest" <> text accepted85Document.RootElement "digest" then failwith "GS2-08.5 acceptance binding differs"
if text binding "closedPopulationSha256" <> sha256 "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json" then failwith "closed census population differs"

let expectedEpochs = [ "OperatingV1"; "Preparing"; "FreezeRequested"; "Frozen"; "SwitchedV2"; "VerifiedV2"; "OpenV2"; "ObservingV2"; "ContractingV1"; "OperatingV2"; "RollingBack" ]
let expectedAttacks = [ "stale-cache"; "lost-response"; "ledger-rewind"; "missing-tag"; "wrong-manifest"; "permission-loss"; "old-client" ]
let expectedSurfaces = [ "FencedTransport"; "DurableMutationFence"; "canonical-effect-ids"; "legacy-send-refusal"; "read-allowlist"; "retry-after-proven-absence"; "provider-reconciliation" ]
if text expectations "schema" <> "fsgg.github-substrate.v1-independent-fence-expectations/1" then failwith "expectation schema differs"
if strings expectations "epochs" <> expectedEpochs || strings expectations "attacks" <> expectedAttacks then failwith "attack expectation differs"
if expectations.GetProperty("providerEvidence").ValueKind <> JsonValueKind.Null then failwith "Q4 provider evidence was invented"

if text producerContract "schema" <> "fsgg.github-substrate.v1-producer-attack-contract/1" then failwith "producer attack contract schema differs"
if text producerContract "state" <> "bounded-producer-result-attached" then failwith "producer attack state differs"
if text producerContract "producerCommit" <> text binding "producerProtectedMerge" || text producerContract "producerTree" <> text binding "producerProtectedTree" then failwith "producer attack identity differs"
if text producerContract "producerAttackCommit" <> text binding "producerAttackSourceHead" || text producerContract "producerAttackSourceTree" <> text binding "producerAttackSourceTree" || text producerContract "producerAttackProtectedMerge" <> text binding "producerAttackProtectedMerge" || text producerContract "producerAttackProtectedTree" <> text binding "producerAttackProtectedTree" then failwith "producer attack result identity differs"
if strings producerContract "requiredSurfaces" <> expectedSurfaces then failwith "producer attack surfaces differ"
if strings producerContract "requiredEpochs" <> expectedEpochs || strings producerContract "requiredFaults" <> expectedAttacks then failwith "producer attack matrix differs"
let resultReference = producerContract.GetProperty("result")
if text resultReference "path" <> $"{evidenceRoot}/producer-attack-result.json" || text resultReference "sha256" <> sha256 $"{evidenceRoot}/producer-attack-result.json" then failwith "producer result file binding differs"
if text resultReference "executionEvidenceSha256" <> "9007aa34a34d4d957f0948cf3b3059d5c5bf189c4b2ec7645f35548884c60b15" then failwith "producer execution evidence digest differs"

if text producerResult "schema" <> "fsgg.github-substrate.v1-producer-attack-result/1" then failwith "producer result schema differs"
if text producerResult "producerAttackSourceHead" <> text binding "producerAttackSourceHead" || text producerResult "producerAttackSourceTree" <> text binding "producerAttackSourceTree" || text producerResult "producerAttackProtectedMerge" <> text binding "producerAttackProtectedMerge" || text producerResult "producerAttackProtectedTree" <> text binding "producerAttackProtectedTree" then failwith "producer result source identity differs"
let execution = producerResult.GetProperty("executionEvidence")
if text execution "schema" <> "fsgg.gs2-08.6-offline-execution-evidence/1" || text execution "status" <> "pass" then failwith "producer execution evidence did not pass"
if execution.GetProperty("compiledTests").GetInt32() <> 21 || execution.GetProperty("compiledFailures").GetInt32() <> 0 then failwith "compiled producer attack count differs"
if execution.GetProperty("typedWriteCommands").GetInt32() <> 26 || execution.GetProperty("epochs").GetInt32() <> 11 then failwith "producer attack population differs"
if execution.GetProperty("externalWriterBlockers").GetInt32() <> 22 || execution.GetProperty("legacyReceiverBlockers").GetInt32() <> 6 then failwith "producer blocker population differs"
if text execution "loopbackWriteFixtureSha256" <> "f679f9f3c9be295ff578f1e71cf91c5cd975b45d74452d93c860cf848b01d7c8" then failwith "loopback write fixture identity differs"
if text execution "q4" <> "unclaimed" then failwith "Q4 provider evidence was invented"
let canonicalExecution = JsonSerializer.Serialize(execution, JsonSerializerOptions(WriteIndented = false)) |> Text.Encoding.UTF8.GetBytes |> sha256Bytes
if canonicalExecution <> text producerResult "executionEvidenceSha256" then failwith "canonical producer execution digest differs"
let boundedClaims = producerResult.GetProperty("boundedClaims")
if not ((text boundedClaims "q3").Contains("remains unclaimed", StringComparison.Ordinal)) then failwith "full Q3 was falsely claimed"
if not ((text boundedClaims "q6").Contains("remain unclaimed", StringComparison.Ordinal)) then failwith "full Q6 was falsely claimed"
let expectedInputs =
    Map [
        "tests/producer-fence-attacks/oracle.json", "89d0137434ff3590c4b9def719134d76ac777628cbd39b8ea88234fed47257bd"
        "tests/producer-fence-attacks/run.py", "8bbf71e4a51300a591509b07cfd3694b1c949122168eab0d290f5af1f102c127"
        "tests/FS.GG.Coord.GitHub.Tests/ProducerFenceAttackTests.fs", "8da3b3dbe0ea19edc5d4363b93d4ec861895916150908effd7af072114998dfe"
        ".github/workflows/coord-github.yml", "ef52d1d9f1135cdd8feac8be45d44952883018b87497ca037e181b6f603d0152"
        "scripts/change-completeness", "8d22a51c5edee2164caea23395af0011d8c56004599e2b21aead8089fdf8b441"
        "tests/coord-engine-e2e/writes.sh", "f679f9f3c9be295ff578f1e71cf91c5cd975b45d74452d93c860cf848b01d7c8"
    ]
let actualInputs = producerResult.GetProperty("reproducibleInputs").EnumerateArray() |> Seq.map (fun value -> text value "path", text value "sha256") |> Map.ofSeq
if actualInputs <> expectedInputs then failwith "producer reproducible input identity differs"

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

let externalBlockers = blockersDocument.RootElement.GetProperty("externalWriterSources").EnumerateArray() |> Seq.toList
if externalBlockers.Length <> 22 then failwith "external writer blocker population differs"
if externalBlockers |> List.exists (fun blocker -> text blocker "disposition" <> "GS2-08.9-retain-or-retire" || text blocker "proof" <> "producer-harness-does-not-execute-route") then failwith "external writer route falsely passes"

printfn "GITHUB_V1_INDEPENDENT_FENCE_EVIDENCE_OK commands=%d epochs=11 compiled=21 external-blockers=%d legacy-receiver-blockers=6 state=bounded-result-attached-unit-not-accepted" writeCommands.Length externalBlockers.Length
