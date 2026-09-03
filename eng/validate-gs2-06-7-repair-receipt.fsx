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
    | _ -> fail "RR-SHAPE" $"missing %s{name}"
let string (name: string) (value: JsonElement) =
    let child = property name value
    if child.ValueKind <> JsonValueKind.String then fail "RR-TYPE" name
    child.GetString()
let number (name: string) (value: JsonElement) =
    match (property name value).TryGetInt64() with
    | true, result -> result
    | _ -> fail "RR-TYPE" name
let boolean (name: string) (value: JsonElement) =
    let child = property name value
    if child.ValueKind <> JsonValueKind.True && child.ValueKind <> JsonValueKind.False then fail "RR-TYPE" name
    child.GetBoolean()
let array (name: string) (value: JsonElement) =
    let child = property name value
    if child.ValueKind <> JsonValueKind.Array then fail "RR-TYPE" name
    child.EnumerateArray() |> Seq.toList
let exact (path: string) (expected: string list) (value: JsonElement) =
    let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
    let expectedText = String.concat "," expected
    let actualText = String.concat "," actual
    if actual <> expected then fail "RR-SHAPE" $"%s{path}: expected %s{expectedText}; observed %s{actualText}"
let expect path expected actual = if actual <> expected then fail "RR-BINDING" $"%s{path}: expected %A{expected}; observed %A{actual}"

let noDuplicateMembers (bytes: byte array) =
    let mutable reader = Utf8JsonReader(ReadOnlySpan<byte>(bytes), JsonReaderOptions(CommentHandling = JsonCommentHandling.Disallow))
    let objects = Stack<HashSet<string>>()
    while reader.Read() do
        match reader.TokenType with
        | JsonTokenType.StartObject -> objects.Push(HashSet<string>(StringComparer.Ordinal))
        | JsonTokenType.PropertyName ->
            if objects.Count = 0 || not (objects.Peek().Add(reader.GetString())) then fail "RR-DUPLICATE-MEMBER" "duplicate JSON member"
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
    if child.ExitCode <> 0 then fail "RR-GIT" error
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
    if child.ExitCode <> 0 then fail "RR-GIT" error
    bytes.ToArray()

let validateBytes root (bytes: byte array) =
    noDuplicateMembers bytes
    use document = JsonDocument.Parse bytes
    let receipt = document.RootElement
    exact "receipt" [ "schema"; "repairId"; "unitId"; "state"; "digestContract"; "supersedes"; "implementation"; "review"; "exactHeadChecks"; "protectedMainChecks"; "postMergeQualification"; "evidence"; "boundaries"; "acceptedAt"; "digest" ] receipt
    expect "schema" "fsgg.coordination.unit-repair-acceptance/1" (string "schema" receipt)
    expect "repairId" "GS2-06.7-repair-268" (string "repairId" receipt)
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
    expect "original.sourceRevision" (string "sourceRevision" supersedes) (string "sourceRevision" original.RootElement)

    let implementation = property "implementation" receipt
    exact "implementation" [ "issue"; "pullRequest"; "candidateSha"; "candidateTreeSha"; "mergeSha"; "mergedAt" ] implementation
    expect "implementation.issue" 268L (number "issue" implementation)
    expect "implementation.pullRequest" 269L (number "pullRequest" implementation)
    expect "implementation.candidate" "2da51ef3a77ac826e16141418232a46d786388c6" (string "candidateSha" implementation)
    expect "implementation.tree" "a08a24c3a242d0699cecfb01badd28bf52dc367b" (string "candidateTreeSha" implementation)
    expect "implementation.merge" "286bde7afd607ac8e62a4ca71f6f82d363c052b4" (string "mergeSha" implementation)
    expect "implementation.mergedAt" "2026-09-03T05:08:44Z" (string "mergedAt" implementation)
    expect "merge.tree" (string "candidateTreeSha" implementation) (runGit root [ "show"; "-s"; "--format=%T"; string "mergeSha" implementation ])

    let review = property "review" receipt
    exact "review" [ "critic"; "decisionCommentId"; "decisionUrl"; "decisionDigest"; "waitCommentId"; "hostAcceptanceCommentId"; "hostAcceptanceDigest" ] review
    expect "review.critic" "plover-b3ee" (string "critic" review)
    expect "review.decisionCommentId" 5520730651L (number "decisionCommentId" review)
    expect "review.decisionDigest" "f6be2102b687b199853db43b5168addb170cf13684e97ae4ce714b021673fa30" (string "decisionDigest" review)
    expect "review.waitCommentId" 5520733108L (number "waitCommentId" review)
    expect "review.hostAcceptanceCommentId" 5520759515L (number "hostAcceptanceCommentId" review)
    expect "review.hostAcceptanceDigest" "e2aa1ff5e147c063d93224459194b87422ae4bf288301862fa344f9d86ccc548" (string "hostAcceptanceDigest" review)

    let validateRuns path expectedHead expected =
        let runs = array path receipt
        if runs.Length <> 2 then fail "RR-RUNS" path
        let identities = runs |> List.map (fun run -> string "workflow" run, number "runId" run) |> Set.ofList
        expect path expected identities
        for run in runs do
            exact path [ "workflow"; "runId"; "url"; "headSha"; "conclusion" ] run
            expect $"%s{path}.head" expectedHead (string "headSha" run)
            expect $"%s{path}.conclusion" "success" (string "conclusion" run)
            let id = number "runId" run
            expect $"%s{path}.url" $"https://github.com/FS-GG/FS.GG.Coordination/actions/runs/%d{id}" (string "url" run)
    validateRuns "exactHeadChecks" "2da51ef3a77ac826e16141418232a46d786388c6" (set [ "Bootstrap qualification", 33716739336L; "CodeQL", 33716737131L ])
    validateRuns "protectedMainChecks" "286bde7afd607ac8e62a4ca71f6f82d363c052b4" (set [ "Bootstrap qualification", 33717657519L; "CodeQL", 33717657228L ])

    let qualification = property "postMergeQualification" receipt
    exact "postMergeQualification" [ "checkoutSha"; "buildWarnings"; "buildErrors"; "unitPassed"; "unitFailed"; "architecturePassed"; "architectureFailed"; "q3Controls"; "q7Controls"; "repositories"; "fleetEnabled"; "seal" ] qualification
    expect "qualification.checkout" "286bde7afd607ac8e62a4ca71f6f82d363c052b4" (string "checkoutSha" qualification)
    for name in [ "buildWarnings"; "buildErrors"; "unitFailed"; "architectureFailed" ] do expect $"qualification.%s{name}" 0L (number name qualification)
    for name, expected in [ "unitPassed", 189L; "architecturePassed", 464L; "q3Controls", 23L; "q7Controls", 12L; "repositories", 10L ] do expect $"qualification.%s{name}" expected (number name qualification)
    expect "qualification.fleetEnabled" false (boolean "fleetEnabled" qualification)
    expect "qualification.seal" "1a7b8201130401a703c9f65812c23412330b9092e8ffe050b945e3a8d11de868" (string "seal" qualification)

    let evidence = property "evidence" receipt
    exact "evidence" [ "observations"; "sdd" ] evidence
    let observations = property "observations" evidence
    exact "observations" [ "path"; "bytes"; "sha256"; "repositories"; "runs"; "jobs" ] observations
    let implementationMerge = string "mergeSha" implementation
    let observationBytes = readGitObject root implementationMerge (Path.Combine("evidence/github-substrate-v2", string "path" observations))
    expect "observations.bytes" (int (number "bytes" observations)) observationBytes.Length
    expect "observations.sha256" (string "sha256" observations) (sha256 observationBytes)
    use observed = JsonDocument.Parse observationBytes
    let repositories = array "repositories" observed.RootElement
    expect "observations.repositories" (int64 repositories.Length) (number "repositories" observations)
    expect "observations.runs" (repositories |> List.sumBy (fun repository -> array "runs" repository |> List.length) |> int64) (number "runs" observations)
    expect "observations.jobs" (repositories |> List.sumBy (fun repository -> array "runs" repository |> List.sumBy (fun run -> array "jobs" run |> List.length)) |> int64) (number "jobs" observations)

    let sdd = property "sdd" evidence
    exact "sdd" [ "generator"; "version"; "analysisSha256"; "workModelSha256"; "verifySha256"; "shipVerdictSha256" ] sdd
    expect "sdd.generator" "FS.GG.SDD.Artifacts" (string "generator" sdd)
    expect "sdd.version" "1.0.0" (string "version" sdd)
    for field, relative in [ "analysisSha256", "readiness/262-workflow-selection/analysis.json"; "workModelSha256", "readiness/262-workflow-selection/work-model.json"; "verifySha256", "readiness/262-workflow-selection/verify.json"; "shipVerdictSha256", "readiness/262-workflow-selection/ship-verdict.json" ] do
        expect $"sdd.%s{field}" (string field sdd) (readGitObject root implementationMerge relative |> sha256)

    let boundaries = property "boundaries" receipt
    exact "boundaries" [ "productionMutation"; "fleetEnablement"; "consumerMutation"; "packageOrRelease"; "gs2068" ] boundaries
    for name in [ "productionMutation"; "fleetEnablement"; "consumerMutation"; "packageOrRelease"; "gs2068" ] do expect $"boundaries.%s{name}" false (boolean name boundaries)

    let node = JsonNode.Parse bytes |> _.AsObject()
    expect "digest" (canonicalDigest node) (string "digest" receipt)

let validate root =
    let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2")
    let receiptRelative = "repair-receipts/GS2-06.7-repair-268.json"
    let receiptPath = Path.Combine(evidenceRoot, receiptRelative)
    let bytes = File.ReadAllBytes receiptPath
    if JsonNode.Parse(bytes).ToJsonString() + "\n" <> Encoding.UTF8.GetString bytes then fail "RR-CANONICAL" receiptRelative
    validateBytes root bytes
    use policy = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidenceRoot, "storage-policy.json")))
    let categories = array "categories" policy.RootElement |> List.filter (fun category -> string "name" category = "repair-receipts")
    if categories.Length <> 1 then fail "RR-CATEGORY" "repair category must be unique"
    expect "category.path" "repair-receipts" (string "path" categories.Head)
    use index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidenceRoot, "index.json")))
    let entries = array "entries" index.RootElement |> List.filter (fun entry -> string "id" entry = "repair-GS2-06.7-268" || string "path" entry = receiptRelative)
    if entries.Length <> 1 then fail "RR-INDEX" "repair identity/path must be unique"
    let entry = entries.Head
    expect "index.id" "repair-GS2-06.7-268" (string "id" entry)
    expect "index.category" "repair-receipts" (string "category" entry)
    expect "index.bytes" (int64 bytes.Length) (number "bytes" entry)
    expect "index.sha256" (sha256 bytes) (string "sha256" entry)
    bytes

let selfTest (root: string) (baseline: byte array) =
    let reseal (node: JsonObject) =
        node.Remove("digest") |> ignore
        node.Add("digest", canonicalDigest node)
        utf8 (node.ToJsonString() + "\n")
    let reject (name: string) (mutate: JsonObject -> unit) =
        let node = JsonNode.Parse(baseline).AsObject()
        mutate node
        let candidate = reseal node
        try validateBytes root candidate; fail "RR-SELF-TEST" $"%s{name} was accepted"
        with error when not (error.Message.StartsWith("RR-SELF-TEST", StringComparison.Ordinal)) -> ()
    reject "omitted-review" (fun node -> node.Remove("review") |> ignore)
    reject "original-substitution" (fun node -> node["supersedes"]["sha256"] <- String('0', 64))
    reject "fabricated-merge" (fun node -> node["implementation"]["mergeSha"] <- String('0', 40))
    reject "fabricated-review" (fun node -> node["review"]["decisionCommentId"] <- 1)
    reject "qualification-omission" (fun node -> node["postMergeQualification"].AsObject().Remove("q7Controls") |> ignore)
    reject "production-authority" (fun node -> node["boundaries"]["productionMutation"] <- true)
    let duplicate = Encoding.UTF8.GetString(baseline).Replace("\"unitId\":\"GS2-06.7\",", "\"unitId\":\"GS2-06.7\",\"unitId\":\"GS2-06.7\",") |> utf8
    try validateBytes root duplicate; fail "RR-SELF-TEST" "duplicate-member was accepted"
    with error when not (error.Message.StartsWith("RR-SELF-TEST", StringComparison.Ordinal)) -> ()

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let selfTestRequested, rootArg =
    match args with
    | [ "--self-test"; root ] -> true, root
    | [ root ] -> false, root
    | [] -> false, "."
    | _ -> fail "RR-USAGE" "validate-gs2-06-7-repair-receipt.fsx [--self-test] [root]"
let root = Path.GetFullPath rootArg
let baseline = validate root
if selfTestRequested then selfTest root baseline
printfn "GS2_06_7_REPAIR_RECEIPT_OK repairId=GS2-06.7-repair-268 original=9a98a13213c9a6934b362a6cb75dc3b523800205961e76cd4de984157733dc0b merge=286bde7afd607ac8e62a4ca71f6f82d363c052b4 controls=%d" (if selfTestRequested then 7 else 0)
