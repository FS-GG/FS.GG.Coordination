open System
open System.IO
open System.Security.Cryptography
open System.Text
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
let accepted86Document = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.6.json")
let unitsDocument = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
let binding = bindingDocument.RootElement
let expectations = expectationsDocument.RootElement
let blockers = blockersDocument.RootElement
let producerContract = producerContractDocument.RootElement
let producerResult = producerResultDocument.RootElement
let accepted86 = accepted86Document.RootElement

let sourceHead = "fa053da18cb59b4546ed6b04c4f0c804952abf37"
let sourceTree = "3e43e1e0126ebe923bae8ce4197f7310b486b7a3"
let protectedMerge = "bc881a6ed1b4ce32e99d4c30a664b1db686280d3"
let evidenceDigest = "6f119f2e8b8f00d19bdf17f1d45cb087f4a1bf6765d6b1a7d8089441993c2dd1"
let expectedEpochs = [ "OperatingV1"; "Preparing"; "FreezeRequested"; "Frozen"; "SwitchedV2"; "VerifiedV2"; "OpenV2"; "ObservingV2"; "ContractingV1"; "OperatingV2"; "RollingBack" ]
let expectedAttacks = [ "stale-cache"; "lost-response"; "ledger-rewind"; "missing-tag"; "wrong-manifest"; "permission-loss"; "old-client" ]
let expectedSurfaces = [ "FencedTransport"; "DurableMutationFence"; "canonical-effect-ids"; "legacy-send-refusal"; "read-allowlist"; "retry-after-proven-absence"; "provider-reconciliation" ]

if text binding "schema" <> "fsgg.github-substrate.v1-independent-fence-source-binding/1" then failwith "source binding schema differs"
if text binding "producerAttackSourceHead" <> sourceHead || text binding "producerAttackSourceTree" <> sourceTree then failwith "producer completion source identity differs"
if text binding "producerAttackProtectedMerge" <> protectedMerge || text binding "producerAttackProtectedTree" <> sourceTree then failwith "producer completion protected identity differs"
if text binding "producerAttackEvidenceSha256" <> evidenceDigest then failwith "producer execution binding differs"
if text binding "producerCensusSha256" <> "a0b381fa9c5c1d43c143b09a722a2bc394b75d3d46ebe1d899d9af58580357f7" then failwith "refreshed producer census differs"
if text binding "acceptedGS2085Digest" <> text (accepted85Document.RootElement) "digest" then failwith "GS2-08.5 acceptance binding differs"
if text binding "closedPopulationSha256" <> sha256 "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json" then failwith "accepted closed population differs"

if text expectations "schema" <> "fsgg.github-substrate.v1-independent-fence-expectations/1" then failwith "expectation schema differs"
if strings expectations "epochs" <> expectedEpochs || strings expectations "attacks" <> expectedAttacks then failwith "attack expectation differs"
if text (expectations.GetProperty("q3")) "status" <> "accepted" || text (expectations.GetProperty("q6")) "status" <> "accepted" then failwith "Q3/Q6 exit is not recorded"
if expectations.GetProperty("providerEvidence").ValueKind <> JsonValueKind.Null then failwith "Q4 provider evidence was invented"
if text expectations "residualOwner" <> "GS2-08.9" then failwith "residual ownership differs"

if text producerContract "schema" <> "fsgg.github-substrate.v1-producer-attack-contract/1" then failwith "producer attack contract schema differs"
if text producerContract "state" <> "exit-evidence-complete" then failwith "producer attack exit evidence is incomplete"
if text producerContract "producerAttackCommit" <> sourceHead || text producerContract "producerAttackSourceTree" <> sourceTree then failwith "producer attack contract source differs"
if text producerContract "producerAttackProtectedMerge" <> protectedMerge || text producerContract "producerAttackProtectedTree" <> sourceTree then failwith "producer attack contract protected source differs"
if strings producerContract "requiredSurfaces" <> expectedSurfaces || strings producerContract "requiredEpochs" <> expectedEpochs || strings producerContract "requiredFaults" <> expectedAttacks then failwith "producer attack matrix differs"
if text producerContract "q4" <> "unclaimed without isolated live GitHub provider evidence" then failwith "Q4 provider claim differs"
let resultReference = producerContract.GetProperty("result")
if text resultReference "path" <> $"{evidenceRoot}/producer-attack-result.json" || text resultReference "sha256" <> sha256 $"{evidenceRoot}/producer-attack-result.json" then failwith "producer result file binding differs"
if text resultReference "executionEvidenceSha256" <> evidenceDigest then failwith "producer execution digest differs"

if text producerResult "schema" <> "fsgg.github-substrate.v1-producer-attack-result/2" then failwith "producer result schema differs"
if text producerResult "producerAttackSourceHead" <> sourceHead || text producerResult "producerAttackSourceTree" <> sourceTree then failwith "producer result source differs"
if text producerResult "producerAttackProtectedMerge" <> protectedMerge || text producerResult "producerAttackProtectedTree" <> sourceTree then failwith "producer result protected source differs"
if text producerResult "executionEvidenceSha256" <> evidenceDigest || text producerResult "disposition" <> "unit-exit-evidence-complete" then failwith "producer result disposition differs"
let execution = producerResult.GetProperty("executionEvidence")
let canonicalExecution = JsonSerializer.Serialize(execution, JsonSerializerOptions(WriteIndented = false)) |> Encoding.UTF8.GetBytes |> sha256Bytes
if canonicalExecution <> evidenceDigest then failwith "canonical producer execution digest differs"
if text execution "schema" <> "fsgg.gs2-08.6-offline-execution-evidence/2" || text execution "status" <> "pass" then failwith "producer execution did not pass"
if text execution "sourceHead" <> sourceHead || text execution "q4" <> "unclaimed" then failwith "producer execution source or Q4 differs"
if execution.GetProperty("compiledTests").GetInt32() <> 66 || execution.GetProperty("compiledFailures").GetInt32() <> 0 then failwith "compiled producer attack count differs"
if execution.GetProperty("epochs").GetInt32() <> 11 || execution.GetProperty("writerCallsites").GetInt32() <> 16 || execution.GetProperty("executedBoundaries").GetInt32() <> 6 then failwith "compiled producer attack population differs"
if execution.GetProperty("externalWriterResiduals").GetInt32() <> 22 || execution.GetProperty("legacyBypasses").GetInt32() <> 1 || execution.GetProperty("legacyUnavailable").GetInt32() <> 1 then failwith "producer residual population differs"
let exactExecutionHashes =
    Map [
        "trxSha256", "825bf156ef882aa491ec5c0d507ebdb58be85169025656f49ed4beeee6d3530f"
        "testAssemblySha256", "1f5492ababd1d94f819ed8de2dfb01a078d300e6b260381c4d6ab1f418ad011f"
        "productAssemblySha256", "4b3149352dfc4fc6a62c2eb67dbad1b8d025a36776bd94e52c15bfb78933cf3c"
        "canonicalOracleSha256", "3aa5e8dfce0bf3b925b4f9be8a9d4c9b584ff1a673948f26dc81af8237c580f8"
        "externalRouteEvidenceSha256", "1d33145d4ad1fd637b3ff1220d659d16d7187a8adb0aab5943a0ad480f1a591d"
        "legacyEvidenceSha256", "f6f12d1f1c8420fa0ba9e734c74dc4acd468fe00f815b0e94f03f0e37143d557"
    ]
for KeyValue(name, expected) in exactExecutionHashes do
    if text execution name <> expected then failwith $"producer execution hash differs: {name}"

let expectedInputs =
    Map [
        "tests/producer-fence-attacks/oracle.json", "12977be9b3afeb3c21c18b2db142cf43d5c4260e17caf95be408b75221f4b536"
        "tests/producer-fence-attacks/run.py", "f20462490982fde6ff6e3c4a452e0084cf81bcb69eab7ec4c6a3cc9a9679b1de"
        "tests/producer-fence-attacks/selftest.py", "e432b92386bf9badc26ae946f206fd81b1ae4e09321de3fdf73d4bf1a24b796b"
        "tests/producer-fence-attacks/probe_legacy.py", "375af975c954c61550c58ac968f2986c454d774997702dd7398f751f25631229"
        "tests/producer-fence-attacks/external-route-evidence.json", "1d33145d4ad1fd637b3ff1220d659d16d7187a8adb0aab5943a0ad480f1a591d"
        "tests/producer-fence-attacks/legacy-probe-evidence.json", "f6f12d1f1c8420fa0ba9e734c74dc4acd468fe00f815b0e94f03f0e37143d557"
        "tests/FS.GG.Coord.GitHub.Tests/ProducerFenceAttackTests.fs", "74d71b29d61fb94343cd0f6d4475df452d523420003b4a7c342ba918c00aef33"
        "tests/FS.GG.Coord.GitHub.Tests/ProducerFenceCompletionTests.fs", "e586f951d2b09bc8914e0f2cf05b7931014cc0945c6a4cd808887075693ef584"
        ".github/workflows/coord-github.yml", "eced9c58e6dc0938d81a79095a11a03899611a13f414e933640dea190111d62c"
    ]
let actualInputs = producerResult.GetProperty("reproducibleInputs").EnumerateArray() |> Seq.map (fun value -> text value "path", text value "sha256") |> Map.ofSeq
if actualInputs <> expectedInputs then failwith "producer reproducible inputs differ"

let commandRoots = censusDocument.RootElement.GetProperty("commandRoots").EnumerateArray() |> Seq.toList
let writeCommands = commandRoots |> List.filter (fun row -> text row "writes" <> "never")
if commandRoots.Length <> 54 || writeCommands.Length <> 26 then failwith "accepted command population differs"
let clients = blockers.GetProperty("clients").EnumerateArray() |> Seq.toList
if clients.Length <> 2 || text clients[0] "status" <> "artifact-unavailable" || text clients[1] "status" <> "bypass-observed" then failwith "historical client findings differ"
if clients |> List.exists (fun row -> text row "disposition" <> "GS2-08.9-retain-or-retire") then failwith "historical client residual was falsely accepted"
let externalResiduals = blockers.GetProperty("externalWriterSources").EnumerateArray() |> Seq.toList
if externalResiduals.Length <> 22 then failwith "external route residual population differs"
if externalResiduals |> List.exists (fun row -> text row "disposition" <> "GS2-08.9-retain-or-retire" || Set [ "pass"; "refusal-observed" ] |> Set.contains (text row "status")) then failwith "external route residual was falsely accepted"
if text blockers "legacyProbeEvidenceSha256" <> "38f07dd947f6315fbe60fcc5528ef57fd142549d52dfa319ce7dc1faefc82794" then failwith "legacy probe binding differs"

let unitValue = unitsDocument.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun row -> text row "id" = "GS2-08.6")
if text accepted86 "schema" <> "fsgg.coordination.unit-acceptance/1" || text accepted86 "state" <> "accepted" then failwith "GS2-08.6 is not accepted"
if text accepted86 "unitContractSha256" <> text unitValue "contractSha256" then failwith "GS2-08.6 accepted contract differs"
if text accepted86 "sourceRevision" <> protectedMerge then failwith "GS2-08.6 accepted source differs"
let acceptedArtifacts = accepted86.GetProperty("artifacts").EnumerateArray() |> Seq.map (fun row -> text row "name", text row "sha256") |> Map.ofSeq
for KeyValue(name, expected) in Map [ "producer-execution-evidence", evidenceDigest; "producer-execution-trx", "825bf156ef882aa491ec5c0d507ebdb58be85169025656f49ed4beeee6d3530f"; "producer-test-assembly", "1f5492ababd1d94f819ed8de2dfb01a078d300e6b260381c4d6ab1f418ad011f"; "producer-product-assembly", "4b3149352dfc4fc6a62c2eb67dbad1b8d025a36776bd94e52c15bfb78933cf3c"; "producer-canonical-oracle", "3aa5e8dfce0bf3b925b4f9be8a9d4c9b584ff1a673948f26dc81af8237c580f8"; "producer-legacy-probe", "38f07dd947f6315fbe60fcc5528ef57fd142549d52dfa319ce7dc1faefc82794" ] do
    if acceptedArtifacts |> Map.tryFind name <> Some expected then failwith $"accepted artifact differs: {name}"

printfn "GITHUB_V1_INDEPENDENT_FENCE_ACCEPTED commands=%d callsites=16 boundaries=6 epochs=11 compiled=66 external-residuals=%d legacy-bypass=1 legacy-unavailable=1 q3=accepted q6=accepted q4=unclaimed" writeCommands.Length externalResiduals.Length
