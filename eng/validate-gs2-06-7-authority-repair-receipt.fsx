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
let property (name: string) (value: JsonElement) =
    match value.TryGetProperty name with
    | true, child -> child
    | _ -> fail "AR-SHAPE" $"missing %s{name}"
let string name value =
    let child = property name value
    if child.ValueKind <> JsonValueKind.String then fail "AR-TYPE" name
    child.GetString()
let number name value =
    match (property name value).TryGetInt64() with
    | true, result -> result
    | _ -> fail "AR-TYPE" name
let boolean name value =
    let child = property name value
    if child.ValueKind <> JsonValueKind.True && child.ValueKind <> JsonValueKind.False then fail "AR-TYPE" name
    child.GetBoolean()
let array name value =
    let child = property name value
    if child.ValueKind <> JsonValueKind.Array then fail "AR-TYPE" name
    child.EnumerateArray() |> Seq.toList
let exact path expected (value: JsonElement) =
    let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
    let expectedText = String.concat "," expected
    let actualText = String.concat "," actual
    if actual <> expected then fail "AR-SHAPE" $"%s{path}: expected %s{expectedText}; observed %s{actualText}"
let expect path expected actual = if actual <> expected then fail "AR-BINDING" $"%s{path}: expected %A{expected}; observed %A{actual}"

let noDuplicateMembers (bytes: byte array) =
    let mutable reader = Utf8JsonReader(ReadOnlySpan<byte>(bytes), JsonReaderOptions(CommentHandling = JsonCommentHandling.Disallow))
    let objects = Stack<HashSet<string>>()
    while reader.Read() do
        match reader.TokenType with
        | JsonTokenType.StartObject -> objects.Push(HashSet<string>(StringComparer.Ordinal))
        | JsonTokenType.PropertyName ->
            if objects.Count = 0 || not (objects.Peek().Add(reader.GetString())) then fail "AR-DUPLICATE-MEMBER" "duplicate JSON member"
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
    if child.ExitCode <> 0 then fail "AR-GIT" error
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
    if child.ExitCode <> 0 then fail "AR-GIT" error
    bytes.ToArray()

let validateBytes root (bytes: byte array) =
    noDuplicateMembers bytes
    use document = JsonDocument.Parse bytes
    let receipt = document.RootElement
    exact "receipt" [ "schema"; "repairId"; "unitId"; "state"; "digestContract"; "supersedes"; "previousRepair"; "implementation"; "review"; "exactHeadChecks"; "protectedMainChecks"; "postMergeQualification"; "authorityProof"; "evidence"; "boundaries"; "acceptedAt"; "digest" ] receipt
    expect "schema" "fsgg.coordination.unit-repair-acceptance/1" (string "schema" receipt)
    expect "repairId" "GS2-06.7-repair-272" (string "repairId" receipt)
    expect "unitId" "GS2-06.7" (string "unitId" receipt)
    expect "state" "superseding-repair-accepted" (string "state" receipt)
    expect "digestContract" "canonical-json-without-digest-v1" (string "digestContract" receipt)

    let supersedes = property "supersedes" receipt
    exact "supersedes" [ "path"; "bytes"; "sha256"; "digest"; "sourceRevision" ] supersedes
    expect "supersedes.path" "accepted/GS2-06.7.json" (string "path" supersedes)
    expect "supersedes.bytes" 2242L (number "bytes" supersedes)
    expect "supersedes.sha256" "9a98a13213c9a6934b362a6cb75dc3b523800205961e76cd4de984157733dc0b" (string "sha256" supersedes)
    expect "supersedes.digest" "c6d1662e7df93f8b6ca8f577b5143e1e8a45eb9ac6fe55922488659ff9363036" (string "digest" supersedes)
    expect "supersedes.sourceRevision" "3277a7a581f9b75001a851e0b614de3c1eadf812" (string "sourceRevision" supersedes)
    let originalBytes = File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-06.7.json"))
    expect "original.bytes" 2242 originalBytes.Length
    expect "original.sha256" (string "sha256" supersedes) (sha256 originalBytes)
    use original = JsonDocument.Parse originalBytes
    expect "original.digest" (string "digest" supersedes) (string "digest" original.RootElement)

    let previous = property "previousRepair" receipt
    exact "previousRepair" [ "path"; "bytes"; "sha256"; "digest"; "implementationMerge" ] previous
    expect "previous.path" "repair-receipts/GS2-06.7-repair-268.json" (string "path" previous)
    expect "previous.bytes" 3171L (number "bytes" previous)
    expect "previous.sha256" "37d36961589dbcd5db2b1d9deab09932dc9204fedaddb618eea5be6dfddbfd27" (string "sha256" previous)
    expect "previous.digest" "f0360e50a1c94262d8c1b83a12871276c8b1c58e6efceba14b49926dca00d45d" (string "digest" previous)
    expect "previous.implementationMerge" "286bde7afd607ac8e62a4ca71f6f82d363c052b4" (string "implementationMerge" previous)
    let previousBytes = File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2", string "path" previous))
    expect "previous.currentBytes" (int (number "bytes" previous)) previousBytes.Length
    expect "previous.currentSha" (string "sha256" previous) (sha256 previousBytes)
    use previousDocument = JsonDocument.Parse previousBytes
    expect "previous.currentDigest" (string "digest" previous) (string "digest" previousDocument.RootElement)
    expect "previous.currentMerge" (string "implementationMerge" previous) (previousDocument.RootElement |> property "implementation" |> string "mergeSha")

    let implementation = property "implementation" receipt
    exact "implementation" [ "issue"; "pullRequest"; "candidateSha"; "candidateTreeSha"; "mergeSha"; "mergedAt" ] implementation
    expect "implementation.issue" 272L (number "issue" implementation)
    expect "implementation.pr" 273L (number "pullRequest" implementation)
    expect "implementation.candidate" "0705405ff76eaf5cf34b627017a40240396616f9" (string "candidateSha" implementation)
    expect "implementation.tree" "db42165acbb6e0f0ee3dee4f0b63990f63c12c96" (string "candidateTreeSha" implementation)
    let merge = "588e1a4bcceeef1cc5a110c924aa52636f263b07"
    expect "implementation.merge" merge (string "mergeSha" implementation)
    expect "implementation.mergedAt" "2026-09-03T07:09:48Z" (string "mergedAt" implementation)
    expect "merge.tree" (string "candidateTreeSha" implementation) (runGit root [ "show"; "-s"; "--format=%T"; merge ])

    let review = property "review" receipt
    exact "review" [ "critic"; "decisionCommentId"; "decisionUrl"; "decisionDigest"; "waitCommentId"; "hostAcceptanceCommentId"; "hostAcceptanceDigest" ] review
    expect "review.critic" "successor-273-r1" (string "critic" review)
    expect "review.decisionCommentId" 5521926908L (number "decisionCommentId" review)
    expect "review.decisionUrl" "https://github.com/FS-GG/FS.GG.Coordination/pull/273#issuecomment-5521926908" (string "decisionUrl" review)
    expect "review.decisionDigest" "604e7c479baee9985e7d62df89bd3ed4732b079bd41ca118bc3a3120a6daaa54" (string "decisionDigest" review)
    expect "review.waitCommentId" 5521928736L (number "waitCommentId" review)
    expect "review.hostAcceptanceCommentId" 5521933056L (number "hostAcceptanceCommentId" review)
    expect "review.hostAcceptanceDigest" "ec7c842c2b1f09eaf6bcf8e4a49a99bd64ca35136dbd97d4b7fe05b866ce1e9c" (string "hostAcceptanceDigest" review)

    let validateRuns path expectedHead expected =
        let runs = array path receipt
        if runs.Length <> 2 then fail "AR-RUNS" path
        let identities = runs |> List.map (fun run -> string "workflow" run, number "runId" run) |> Set.ofList
        expect path expected identities
        for run in runs do
            exact path [ "workflow"; "runId"; "url"; "headSha"; "conclusion" ] run
            expect $"%s{path}.head" expectedHead (string "headSha" run)
            expect $"%s{path}.conclusion" "success" (string "conclusion" run)
            let id = number "runId" run
            expect $"%s{path}.url" $"https://github.com/FS-GG/FS.GG.Coordination/actions/runs/%d{id}" (string "url" run)
    validateRuns "exactHeadChecks" "0705405ff76eaf5cf34b627017a40240396616f9" (set [ "Bootstrap qualification", 33725627516L; "CodeQL", 33725618051L ])
    validateRuns "protectedMainChecks" merge (set [ "Bootstrap qualification", 33726702726L; "CodeQL", 33726701922L ])

    let qualification = property "postMergeQualification" receipt
    exact "postMergeQualification" [ "checkoutSha"; "buildWarnings"; "buildErrors"; "unitPassed"; "unitFailed"; "architecturePassed"; "architectureFailed"; "q3Controls"; "q7Controls"; "repositories"; "fleetEnabled"; "seal" ] qualification
    expect "qualification.checkout" merge (string "checkoutSha" qualification)
    for name in [ "buildWarnings"; "buildErrors"; "unitFailed"; "architectureFailed" ] do expect $"qualification.%s{name}" 0L (number name qualification)
    for name, expected in [ "unitPassed", 189L; "architecturePassed", 466L; "q3Controls", 23L; "q7Controls", 12L; "repositories", 10L ] do expect $"qualification.%s{name}" expected (number name qualification)
    expect "qualification.fleetEnabled" false (boolean "fleetEnabled" qualification)
    expect "qualification.seal" "34cc3b592c9a67d26814cea8768b1864071ff261204d56d42ef8d38bcfbe8ad2" (string "seal" qualification)

    let authority = property "authorityProof" receipt
    exact "authorityProof" [ "checkoutSha"; "baseRevision"; "currentRevision"; "settingsSha256"; "queuedHead"; "selectionClosure"; "productionMutation" ] authority
    let baseRevision = "6d3b7662ac4d9474a9976ac093ec910f55fb6087"
    expect "authority.checkout" merge (string "checkoutSha" authority)
    expect "authority.base" baseRevision (string "baseRevision" authority)
    expect "authority.current" merge (string "currentRevision" authority)
    expect "authority.settings" "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518" (string "settingsSha256" authority)
    expect "authority.queued" "none" (string "queuedHead" authority)
    expect "authority.closure" [ "policy"; "coordination" ] (array "selectionClosure" authority |> List.map _.GetString())
    expect "authority.productionMutation" false (boolean "productionMutation" authority)
    expect "authority.parent" baseRevision (runGit root [ "show"; "-s"; "--format=%P"; merge ])
    expect "authority.distance" "1" (runGit root [ "rev-list"; "--count"; $"%s{baseRevision}..%s{merge}" ])
    for relative in [ "evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json"; "evidence/github-substrate-v2/gs2-06-7/runtime-request-sentinel.json" ] do
        use current = JsonDocument.Parse(readGitObject root merge relative)
        expect $"authority.%s{relative}.base" baseRevision (string "baseRevision" current.RootElement)
    use request = JsonDocument.Parse(readGitObject root merge "evidence/github-substrate-v2/gs2-06-7/runtime-request-sentinel.json")
    expect "authority.request.settings" (string "settingsSha256" authority) (string "settingsSha256" request.RootElement)
    use settings = JsonDocument.Parse(readGitObject root merge "eng/repository-settings/receipt.json")
    expect "authority.reviewed.settings" (string "settingsSha256" authority) (string "desiredSha256" settings.RootElement)

    let evidence = property "evidence" receipt
    exact "evidence" [ "observations"; "sdd" ] evidence
    let observations = property "observations" evidence
    exact "observations" [ "path"; "bytes"; "sha256"; "repositories"; "runs"; "jobs" ] observations
    let observationBytes = readGitObject root merge (Path.Combine("evidence/github-substrate-v2", string "path" observations))
    expect "observations.bytes" (int (number "bytes" observations)) observationBytes.Length
    expect "observations.sha256" (string "sha256" observations) (sha256 observationBytes)
    for field, expected in [ "repositories", 10L; "runs", 80L; "jobs", 305L ] do expect $"observations.%s{field}" expected (number field observations)
    let sdd = property "sdd" evidence
    exact "sdd" [ "generator"; "version"; "analysisSha256"; "workModelSha256"; "verifySha256"; "shipVerdictSha256" ] sdd
    expect "sdd.generator" "FS.GG.SDD.Artifacts" (string "generator" sdd)
    expect "sdd.version" "1.0.0" (string "version" sdd)
    for field, relative in [ "analysisSha256", "readiness/262-workflow-selection/analysis.json"; "workModelSha256", "readiness/262-workflow-selection/work-model.json"; "verifySha256", "readiness/262-workflow-selection/verify.json"; "shipVerdictSha256", "readiness/262-workflow-selection/ship-verdict.json" ] do
        expect $"sdd.%s{field}" (string field sdd) (readGitObject root merge relative |> sha256)

    let boundaries = property "boundaries" receipt
    exact "boundaries" [ "productionMutation"; "fleetEnablement"; "consumerMutation"; "packageOrRelease"; "gs2068" ] boundaries
    for name in [ "productionMutation"; "fleetEnablement"; "consumerMutation"; "packageOrRelease"; "gs2068" ] do expect $"boundaries.%s{name}" false (boolean name boundaries)
    expect "acceptedAt" "2026-09-03T07:19:56Z" (string "acceptedAt" receipt)
    let node = JsonNode.Parse bytes |> _.AsObject()
    expect "digest" (canonicalDigest node) (string "digest" receipt)

let validate root =
    let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2")
    let receiptRelative = "repair-receipts/GS2-06.7-repair-272.json"
    let bytes = File.ReadAllBytes(Path.Combine(evidenceRoot, receiptRelative))
    if JsonNode.Parse(bytes).ToJsonString() + "\n" <> Encoding.UTF8.GetString bytes then fail "AR-CANONICAL" receiptRelative
    validateBytes root bytes
    use index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidenceRoot, "index.json")))
    let entries = array "entries" index.RootElement |> List.filter (fun entry -> string "id" entry = "repair-GS2-06.7-272" || string "path" entry = receiptRelative)
    if entries.Length <> 1 then fail "AR-INDEX" "repair identity/path must be unique"
    let entry = entries.Head
    expect "index.id" "repair-GS2-06.7-272" (string "id" entry)
    expect "index.category" "repair-receipts" (string "category" entry)
    expect "index.bytes" (int64 bytes.Length) (number "bytes" entry)
    expect "index.sha256" (sha256 bytes) (string "sha256" entry)
    bytes

let selfTest root (baseline: byte array) =
    let reseal (node: JsonObject) =
        node.Remove("digest") |> ignore
        node.Add("digest", canonicalDigest node)
        utf8 (node.ToJsonString() + "\n")
    let reject name mutate =
        let node = JsonNode.Parse(baseline).AsObject()
        mutate node
        try validateBytes root (reseal node); fail "AR-SELF-TEST" $"%s{name} was accepted"
        with error when not (error.Message.StartsWith("AR-SELF-TEST", StringComparison.Ordinal)) -> ()
    reject "previous-repair-substitution" (fun node -> node["previousRepair"]["sha256"] <- String('0', 64))
    reject "fabricated-merge" (fun node -> node["implementation"]["mergeSha"] <- String('0', 40))
    reject "fabricated-review" (fun node -> node["review"]["decisionCommentId"] <- 1)
    reject "stale-current-authority" (fun node -> node["authorityProof"]["currentRevision"] <- String('0', 40))
    reject "stale-settings-authority" (fun node -> node["authorityProof"]["settingsSha256"] <- String('0', 64))
    reject "wrong-selection" (fun node -> node["authorityProof"]["selectionClosure"] <- JsonArray("build"))
    reject "production-authority" (fun node -> node["authorityProof"]["productionMutation"] <- true)
    reject "boundary-escape" (fun node -> node["boundaries"]["gs2068"] <- true)
    let duplicate = Encoding.UTF8.GetString(baseline).Replace("\"unitId\":\"GS2-06.7\",", "\"unitId\":\"GS2-06.7\",\"unitId\":\"GS2-06.7\",") |> utf8
    try validateBytes root duplicate; fail "AR-SELF-TEST" "duplicate-member was accepted"
    with error when not (error.Message.StartsWith("AR-SELF-TEST", StringComparison.Ordinal)) -> ()

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let selfTestRequested, rootArg =
    match args with
    | [ "--self-test"; root ] -> true, root
    | [ root ] -> false, root
    | [] -> false, "."
    | _ -> fail "AR-USAGE" "validate-gs2-06-7-authority-repair-receipt.fsx [--self-test] [root]"
let root = Path.GetFullPath rootArg
let baseline = validate root
if selfTestRequested then selfTest root baseline
printfn "GS2_06_7_AUTHORITY_REPAIR_RECEIPT_OK repairId=GS2-06.7-repair-272 previous=37d36961589dbcd5db2b1d9deab09932dc9204fedaddb618eea5be6dfddbfd27 merge=588e1a4bcceeef1cc5a110c924aa52636f263b07 controls=%d" (if selfTestRequested then 9 else 0)
