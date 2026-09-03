open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let fail code detail = failwith $"%s{code}: %s{detail}"
let sha256 (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let utf8 (value: string) = Encoding.UTF8.GetBytes value
let expect path expected actual = if expected <> actual then fail "DR-BINDING" $"%s{path}: expected %A{expected}; observed %A{actual}"
let obj (name: string) (node: JsonNode) = node[name].AsObject()
let arr (name: string) (node: JsonNode) = node[name].AsArray()
let str (name: string) (node: JsonNode) = node[name].GetValue<string>()
let number (name: string) (node: JsonNode) = node[name].GetValue<int64>()
let boolean (name: string) (node: JsonNode) = node[name].GetValue<bool>()

let noDuplicateMembers (bytes: byte array) =
    let mutable reader = Utf8JsonReader(ReadOnlySpan<byte>(bytes), JsonReaderOptions(CommentHandling = JsonCommentHandling.Disallow))
    let objects = Stack<HashSet<string>>()
    while reader.Read() do
        match reader.TokenType with
        | JsonTokenType.StartObject -> objects.Push(HashSet<string>(StringComparer.Ordinal))
        | JsonTokenType.PropertyName ->
            if objects.Count = 0 || not (objects.Peek().Add(reader.GetString())) then
                fail "DR-DUPLICATE-MEMBER" "duplicate JSON member"
        | JsonTokenType.EndObject -> objects.Pop() |> ignore
        | _ -> ()

let canonicalDigest (node: JsonObject) =
    let clone = node.DeepClone().AsObject()
    clone.Remove("digest") |> ignore
    clone.ToJsonString() |> utf8 |> sha256

let runGit root args =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- root
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    for argument in args do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd().Trim()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then fail "DR-GIT" error
    output

let readGitObject root revision relative =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- root
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    for argument in [ "show"; $"%s{revision}:%s{relative}" ] do start.ArgumentList.Add argument
    use child = Process.Start start
    use bytes = new MemoryStream()
    child.StandardOutput.BaseStream.CopyTo bytes
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then fail "DR-GIT" error
    bytes.ToArray()

let validateBytes root (bytes: byte array) =
    noDuplicateMembers bytes
    let receipt = JsonNode.Parse(bytes).AsObject()
    expect "schema" "fsgg.coordination.unit-repair-acceptance/1" (str "schema" receipt)
    expect "repairId" "GS2-06.7-repair-276" (str "repairId" receipt)
    expect "unitId" "GS2-06.7" (str "unitId" receipt)
    expect "state" "superseding-repair-accepted" (str "state" receipt)
    expect "digestContract" "canonical-json-without-digest-v1" (str "digestContract" receipt)

    let original = obj "supersedes" receipt
    expect "original.path" "accepted/GS2-06.7.json" (str "path" original)
    expect "original.bytes" 2242L (number "bytes" original)
    expect "original.sha" "9a98a13213c9a6934b362a6cb75dc3b523800205961e76cd4de984157733dc0b" (str "sha256" original)
    expect "original.digest" "c6d1662e7df93f8b6ca8f577b5143e1e8a45eb9ac6fe55922488659ff9363036" (str "digest" original)
    let originalBytes = File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2", str "path" original))
    expect "original.currentBytes" (int (number "bytes" original)) originalBytes.Length
    expect "original.currentSha" (str "sha256" original) (sha256 originalBytes)

    let previous = obj "previousRepair" receipt
    expect "previous.path" "repair-receipts/GS2-06.7-repair-272.json" (str "path" previous)
    expect "previous.bytes" 3846L (number "bytes" previous)
    expect "previous.sha" "fa0d1e78ac9528d1793d43e850bfc5479628ee242f0407c49d65d81cc74063da" (str "sha256" previous)
    expect "previous.digest" "e7d660e32f8f94762f9556ddf3552f343eb1fea6d4d53e3258b79b7387e4e3bf" (str "digest" previous)
    let previousBytes = File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2", str "path" previous))
    expect "previous.currentBytes" (int (number "bytes" previous)) previousBytes.Length
    expect "previous.currentSha" (str "sha256" previous) (sha256 previousBytes)
    let repair268 = File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/repair-receipts/GS2-06.7-repair-268.json"))
    expect "repair268.currentSha" "37d36961589dbcd5db2b1d9deab09932dc9204fedaddb618eea5be6dfddbfd27" (sha256 repair268)

    let implementation = obj "implementation" receipt
    let candidate = "45b2e994e2d49df1700f03e8051918ae3ac4b4f2"
    let tree = "1ac0b3ff2b94204ffcc55c2d09ed8ab62c6df9ef"
    let merge = "48a3880c695111df360fbe0efd8bf35071ce8194"
    expect "implementation.issue" 276L (number "issue" implementation)
    expect "implementation.pr" 277L (number "pullRequest" implementation)
    expect "implementation.candidate" candidate (str "candidateSha" implementation)
    expect "implementation.tree" tree (str "candidateTreeSha" implementation)
    expect "implementation.merge" merge (str "mergeSha" implementation)
    expect "implementation.mergedAt" "2026-09-03T08:45:24Z" (str "mergedAt" implementation)
    expect "merge.tree" tree (runGit root [ "show"; "-s"; "--format=%T"; merge ])

    let review = obj "review" receipt
    expect "review.critic" "brant-cb70" (str "critic" review)
    expect "review.decision" 5523013563L (number "decisionCommentId" review)
    expect "review.digest" "26055bbd0438ba2b40482592b18b413c2975ecae033bc683bb88c3f25852a799" (str "decisionDigest" review)
    expect "review.wait" 5523017060L (number "waitCommentId" review)
    expect "review.acceptance" 5523059801L (number "hostAcceptanceCommentId" review)
    expect "review.acceptanceDigest" "317751fcbef4fcf86247c0add1bc2778239170fb5b14b2f0aa4f38f46918b549" (str "hostAcceptanceDigest" review)

    let validateRuns name head expected =
        let runs = arr name receipt |> Seq.map _.AsObject() |> Seq.toList
        expect $"%s{name}.count" 2 runs.Length
        let actual = runs |> List.map (fun run -> str "workflow" run, number "runId" run) |> Set.ofList
        expect name expected actual
        for run in runs do
            expect $"%s{name}.head" head (str "headSha" run)
            expect $"%s{name}.conclusion" "success" (str "conclusion" run)
    validateRuns "exactHeadChecks" candidate (set [ "Bootstrap qualification", 33733293653L; "CodeQL", 33733291319L ])
    validateRuns "protectedMainChecks" merge (set [ "Bootstrap qualification", 33735084054L; "CodeQL", 33735083336L ])

    let qualification = obj "postMergeQualification" receipt
    expect "qualification.checkout" merge (str "checkoutSha" qualification)
    for name in [ "buildWarnings"; "buildErrors"; "unitFailed"; "architectureFailed" ] do expect $"qualification.%s{name}" 0L (number name qualification)
    for name, expected in [ "unitPassed", 189L; "architecturePassed", 467L; "q3Controls", 23L; "q7Controls", 12L; "repositories", 10L ] do expect $"qualification.%s{name}" expected (number name qualification)
    expect "qualification.fleet" false (boolean "fleetEnabled" qualification)
    let seal = "34cc3b592c9a67d26814cea8768b1864071ff261204d56d42ef8d38bcfbe8ad2"
    expect "qualification.seal" seal (str "seal" qualification)

    let authority = obj "authorityProof" receipt
    let inventoryBase = "6d3b7662ac4d9474a9976ac093ec910f55fb6087"
    let inventorySha = "25ec1d81d2a5c5b9de6cf2411f26c288a365e6ea1e2f9eac40ec3449322d291a"
    let requestSha = "dc8f3ebe3f240f0a14fda28736076793631f25e41f035e8e8318a8622183e4e6"
    let settingsSha = "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518"
    expect "authority.checkout" merge (str "checkoutSha" authority)
    expect "authority.base" inventoryBase (str "baseRevision" authority)
    expect "authority.current" merge (str "currentRevision" authority)
    expect "authority.settings" settingsSha (str "settingsSha256" authority)
    expect "authority.inventory" inventorySha (str "inventorySha256" authority)
    expect "authority.request" requestSha (str "requestSha256" authority)
    expect "authority.sourceRequest" requestSha (str "sourceRequestSha256" authority)
    expect "authority.queued" "none" (str "queuedHead" authority)
    expect "authority.mutation" false (boolean "productionMutation" authority)
    runGit root [ "merge-base"; "--is-ancestor"; inventoryBase; merge ] |> ignore
    expect "authority.rolloverDistance" "3" (runGit root [ "rev-list"; "--count"; $"%s{inventoryBase}..%s{merge}" ])
    let inventoryBytes = readGitObject root merge "evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json"
    let requestBytes = readGitObject root merge "evidence/github-substrate-v2/gs2-06-7/runtime-request-sentinel.json"
    expect "authority.inventoryBytes" inventorySha (sha256 inventoryBytes)
    expect "authority.requestBytes" requestSha (sha256 requestBytes)
    let reviewed = JsonNode.Parse(readGitObject root merge "evidence/github-substrate-v2/gs2-06-7/runtime-reviewed-authority.json").AsObject()
    expect "reviewed.base" inventoryBase (str "baseRevision" (obj "inventory" reviewed))
    expect "reviewed.inventory" inventorySha (str "sha256" (obj "inventory" reviewed))
    expect "reviewed.request" requestSha (str "sha256" (obj "request" reviewed))
    expect "reviewed.settings" settingsSha (str "desiredSha256" (obj "settings" reviewed))

    let evidence = obj "evidence" receipt
    let observations = obj "observations" evidence
    let observationBytes = readGitObject root merge (Path.Combine("evidence/github-substrate-v2", str "path" observations))
    expect "observations.bytes" (int (number "bytes" observations)) observationBytes.Length
    expect "observations.sha" (str "sha256" observations) (sha256 observationBytes)
    let sdd = obj "sdd" evidence
    for field, relative in [ "analysisSha256", "readiness/262-workflow-selection/analysis.json"; "workModelSha256", "readiness/262-workflow-selection/work-model.json"; "verifySha256", "readiness/262-workflow-selection/verify.json"; "shipVerdictSha256", "readiness/262-workflow-selection/ship-verdict.json" ] do
        expect $"sdd.%s{field}" (str field sdd) (readGitObject root merge relative |> sha256)
    let roadmap = obj "roadmap" evidence
    expect "roadmap.manifest" "d69cf02083aafa750e069aa1781964ed13ce6ab76b9a8bab25e937736cba7b08" (str "manifestSha256" roadmap)
    expect "roadmap.candidate" candidate (str "candidateSha" roadmap)
    expect "roadmap.tree" tree (str "candidateTreeSha" roadmap)
    expect "roadmap.boundary" true (boolean "stoppedAtUnitBoundary" roadmap)

    let decision = obj "operationalDecision" receipt
    expect "decision.schema" "fsgg.coordination.workflow-selection-sentinel/1" (str "schema" decision)
    expect "decision.sha" "67c9d6283df2ec528e99052d0c9e88d10b083f0e7b3f36a813b8ecfa42040a6a" (str "sha256" decision)
    expect "decision.suite" "passed" (str "fullSuite" decision)
    expect "decision.missed" false (boolean "missedObligation" decision)
    expect "decision.missedCount" 0 (arr "missedObligations" decision).Count
    expect "decision.selection" "eligible" (str "fleetSelection" decision)
    expect "decision.mutation" false (boolean "productionMutation" decision)
    expect "decision.seal" seal (str "q7Seal" decision)
    let boundaries = obj "boundaries" receipt
    for name in [ "productionMutation"; "fleetEnablement"; "consumerMutation"; "packageOrRelease"; "gs2068" ] do expect $"boundaries.%s{name}" false (boolean name boundaries)
    expect "receipt.digest" (canonicalDigest receipt) (str "digest" receipt)

let validate root =
    let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2")
    let relative = "repair-receipts/GS2-06.7-repair-276.json"
    let bytes = File.ReadAllBytes(Path.Combine(evidenceRoot, relative))
    expect "canonical" (JsonNode.Parse(bytes).ToJsonString() + "\n") (Encoding.UTF8.GetString bytes)
    validateBytes root bytes
    let index = JsonNode.Parse(File.ReadAllBytes(Path.Combine(evidenceRoot, "index.json"))).AsObject()
    let entries = arr "entries" index |> Seq.map _.AsObject() |> Seq.filter (fun entry -> str "id" entry = "repair-GS2-06.7-276" || str "path" entry = relative) |> Seq.toList
    expect "index.cardinality" 1 entries.Length
    let entry = entries.Head
    expect "index.category" "repair-receipts" (str "category" entry)
    expect "index.bytes" (int64 bytes.Length) (number "bytes" entry)
    expect "index.sha" (sha256 bytes) (str "sha256" entry)
    bytes

let selfTest root (baseline: byte array) =
    let reseal (node: JsonObject) =
        node.Remove("digest") |> ignore
        node.Add("digest", canonicalDigest node)
        utf8 (node.ToJsonString() + "\n")
    let reject name mutate =
        let node = JsonNode.Parse(baseline).AsObject()
        mutate node
        try validateBytes root (reseal node); fail "DR-SELF-TEST" $"%s{name} was accepted"
        with error when not (error.Message.StartsWith("DR-SELF-TEST", StringComparison.Ordinal)) -> ()
    reject "previous-substitution" (fun node -> node["previousRepair"]["sha256"] <- String('0', 64))
    reject "fabricated-merge" (fun node -> node["implementation"]["mergeSha"] <- String('0', 40))
    reject "fabricated-review" (fun node -> node["review"]["decisionDigest"] <- String('0', 64))
    reject "stale-protected-check" (fun node -> (arr "protectedMainChecks" node).[0]["headSha"] <- String('0', 40))
    reject "stale-current-authority" (fun node -> node["authorityProof"]["currentRevision"] <- String('0', 40))
    reject "stale-settings" (fun node -> node["authorityProof"]["settingsSha256"] <- String('0', 64))
    reject "stale-inventory" (fun node -> node["authorityProof"]["inventorySha256"] <- String('0', 64))
    reject "stale-request" (fun node -> node["authorityProof"]["requestSha256"] <- String('0', 64))
    reject "stale-roadmap" (fun node -> (obj "roadmap" (obj "evidence" node))["candidateSha"] <- String('0', 40))
    reject "dead-sentinel" (fun node -> node["operationalDecision"]["fullSuite"] <- "not-run")
    reject "disabled-selection" (fun node -> node["operationalDecision"]["fleetSelection"] <- "disabled")
    reject "boundary-escape" (fun node -> node["boundaries"]["gs2068"] <- true)
    let duplicate = Encoding.UTF8.GetString(baseline).Replace("\"unitId\":\"GS2-06.7\",", "\"unitId\":\"GS2-06.7\",\"unitId\":\"GS2-06.7\",") |> utf8
    try validateBytes root duplicate; fail "DR-SELF-TEST" "duplicate member was accepted"
    with error when not (error.Message.StartsWith("DR-SELF-TEST", StringComparison.Ordinal)) -> ()

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let selfTestRequested, rootArg =
    match args with
    | [ "--self-test"; root ] -> true, root
    | [ root ] -> false, root
    | [] -> false, "."
    | _ -> fail "DR-USAGE" "validate-gs2-06-7-durable-authority-repair-receipt.fsx [--self-test] [root]"
let root = Path.GetFullPath rootArg
let baseline = validate root
if selfTestRequested then selfTest root baseline
printfn "GS2_06_7_DURABLE_AUTHORITY_REPAIR_RECEIPT_OK repairId=GS2-06.7-repair-276 previous=fa0d1e78ac9528d1793d43e850bfc5479628ee242f0407c49d65d81cc74063da merge=48a3880c695111df360fbe0efd8bf35071ce8194 distance=3 controls=%d" (if selfTestRequested then 13 else 0)
