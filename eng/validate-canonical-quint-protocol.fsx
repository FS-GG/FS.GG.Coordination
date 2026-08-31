open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

type FormalMeasurement = {
    mutable StateCount: int
    mutable TransitionCount: int
    mutable SampleCount: int
    mutable ElapsedMs: int64
    mutable PeakMiB: int
    mutable ArtifactBytes: int64
}

let expectedPackage = "FS.GG.SDD.Artifacts/1.5.0"
let expectedProfile = "fsgg-quint-profile/2"

let expectedToolchain =
    "79b32dacc5bb150e23c4017eef16f3f688cde062441583d5ea1ffa5cc9e62486"

let expectedQuint =
    "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"

let expectedLmt = "37e0b0365c2641edce40b48605471f61fa12e97c3e2376152f0e849abdc31f10"

let expectedSource =
    "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"

let expectedContract =
    "947262bc9f70c371d79a917804d2ed4adcabbb1cc2ff683eedc637e36e6b163e"

let expectedBehavior =
    "c60fb49e78385bbd50e21b20bc90a1d682f967de8c2825690aca81d25d3db132"

let expectedSourceVersion = "fsgg.quint.literate-source/1"
let expectedExtractorVersion = "quint-specification-v1@FS.GG.SDD.Artifacts/1.5.0"
let expectedQuintVersion = "sha256:" + expectedQuint
let expectedSchemaVersion = "fsgg.quint.compiled-contract/v2"
let expectedApalacheJar =
    "4753c0ebb2cbb266e2c6ac19ab5ca3827d726cc80fd1fc5d7c1eeb64736cd60b"

let qualificationClock = Stopwatch.StartNew()
let mutable externalProcessCount = 0
let mutable quintProcessCount = 0
let mutable quintRejectedProcessCount = 0
let mutable apalacheVerifyInvocationCount = 0
let mutable verifiedPositiveInvariantCount = 0
let mutable q1Outcome = "not-run"
let mutable q2Outcome = "not-run"
let mutable currentPhase = "q1"
let mutable preparationDurationMs = 0L
let mutable preparationDigest: string option = None
let mutable failureReceiptWriter: (string -> string -> unit) option = None
let formalCounterexampleReceipts = ResizeArray<string * string * string * string>()
let actualInvocationInventory = System.Collections.Concurrent.ConcurrentDictionary<string, int>()
let expectedInvocationInventory = System.Collections.Generic.Dictionary<string, int>()
let mutable formalSafeSteps = Set.empty<string * string>
let mutable formalInvalidSteps = Set.empty<string * string>
let mutable formalRemovedSteps = Set.empty<string * string>
let mutable formalInventoryReady = false
let mutable apalacheEndpointOrdinal = 0
let apalacheEndpointBase =
    match Environment.GetEnvironmentVariable "FSGG_APALACHE_PORT_BASE" with
    | null | "" -> 18820
    | value ->
        match Int32.TryParse value with
        | true, port when port >= 1024 && port <= 65400 -> port
        | _ -> failwith "FSGG_APALACHE_PORT_BASE must be an integer from 1024 through 65400"
let apalacheEndpoints = System.Collections.Concurrent.ConcurrentDictionary<int, byte>()

let incrementInvocation label =
    actualInvocationInventory.AddOrUpdate(label, 1, fun _ count -> count + 1) |> ignore

let argumentValue name arguments =
    arguments
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

let classifyInvocation isQuint arguments =
    if not isQuint then "external/base"
    elif not formalInventoryReady then
        if List.tryHead arguments = Some "verify" then "quint/base-verify" else "quint/base-nonverify"
    else
        let command = List.tryHead arguments |> Option.defaultValue "missing-command"
        let main = argumentValue "--main" arguments |> Option.defaultValue ""
        let step = argumentValue "--step" arguments
        let isFormal memberSet = step |> Option.exists (fun value -> Set.contains (main, value) memberSet)
        let output = argumentValue "--out" arguments |> Option.defaultValue ""
        if output.Contains("root-artifacts", StringComparison.Ordinal) then "quint/selected-root"
        elif command = "verify" && isFormal formalSafeSteps then
            "quint/formal-temporal"
        elif command = "verify" && isFormal formalRemovedSteps then
            "quint/formal-counterexample-temporal"
        elif command = "run" && isFormal formalInvalidSteps then
            "quint/formal-safety-mutant"
        elif command = "run" && isFormal formalRemovedSteps then
            "quint/formal-counterexample-projection"
        elif command = "run" && isFormal formalSafeSteps then
            "quint/formal-simulation"
        elif command = "verify" then "quint/base-verify"
        else "quint/base-nonverify"

let positiveInvariants =
    [ "acceptedVocabularyIsQualified"
      "acceptedAuthoritiesAreQualified"
      "humanIntentIsObservationIndependent"
      "lifecycleStatusIsDerived"
      "nativeRelationEdgesAreValid"
      "relationChangesPreserveUnrelatedEdges"
      "protocolEnvelopesAreValidAndOrdered"
      "durableProtocolCheckpointsArePreserved" ]

let fail code detail =
    eprintfn "CANONICAL_QUINT_PROTOCOL_RED code=%s detail=%s" code detail

    match failureReceiptWriter with
    | Some writer ->
        try writer code detail
        with exceptionValue ->
            eprintfn "CANONICAL_QUINT_RECEIPT_RED code=WRITE detail=%s" exceptionValue.Message
    | None -> ()

    exit 1

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()

let sha256Text (value: string) =
    value
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let normalizeSupportedAuthoring (value: string) =
    let lf = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)

    let normalizedFences =
        lf.Split('\n')
        |> Array.map (fun line ->
            let leadingSpaces = line.Length - line.TrimStart(' ').Length

            if leadingSpaces > 0 && leadingSpaces <= 3 && line.Substring(leadingSpaces).StartsWith("```", StringComparison.Ordinal) then
                line.Substring(leadingSpaces)
            else
                line)
        |> String.concat "\n"

    normalizedFences.Replace("\n```\n\n```quint protocol.qnt +=\n", "\n", StringComparison.Ordinal)

let requireFile code path =
    if not (File.Exists path) then
        fail code path

let isolateApalacheEndpoint isQuint arguments =
    let command = List.tryHead arguments
    let usesApalacheServer =
        isQuint
        && (command = Some "verify"
            || (command = Some "compile" && argumentValue "--target" arguments = Some "tlaplus"))

    if not usesApalacheServer || List.contains "--server-endpoint" arguments then arguments
    else
        let ordinal = Interlocked.Increment(&apalacheEndpointOrdinal) - 1
        let port = apalacheEndpointBase + ordinal
        if port > 65535 then fail "APALACHE-ENDPOINT-RANGE" (string port)
        if not (apalacheEndpoints.TryAdd(port, 0uy)) then fail "APALACHE-ENDPOINT-DUPLICATE" (string port)
        arguments @ [ "--server-endpoint"; $"localhost:%d{port}" ]

let run workingDirectory (executable: string) arguments environment =
    Interlocked.Increment(&externalProcessCount) |> ignore

    let isQuint = executable.EndsWith(expectedQuint, StringComparison.Ordinal)
    let arguments = isolateApalacheEndpoint isQuint arguments
    incrementInvocation (classifyInvocation isQuint arguments)

    if isQuint then
        Interlocked.Increment(&quintProcessCount) |> ignore

        match arguments with
        | "verify" :: _ -> Interlocked.Increment(&apalacheVerifyInvocationCount) |> ignore
        | _ -> ()

    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- workingDirectory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    for argument in arguments do
        info.ArgumentList.Add argument

    for name, value in environment do
        info.Environment[name] <- value

    use child = Process.Start info
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    child.WaitForExit()

    if isQuint && child.ExitCode <> 0 then
        Interlocked.Increment(&quintRejectedProcessCount) |> ignore

    child.ExitCode, output.Result.Trim(), error.Result.Trim()

let private processTreeRssBytes rootPid =
    let rec collect visited pid =
        if Set.contains pid visited then 0L, visited
        else
            let visited' = Set.add pid visited
            let status = $"/proc/%d{pid}/status"
            let rss =
                try
                    File.ReadLines status
                    |> Seq.tryFind (fun line -> line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    |> Option.map (fun line ->
                        let fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        Int64.Parse(fields[1]) * 1024L)
                    |> Option.defaultValue 0L
                with _ -> 0L
            let childrenPath = $"/proc/%d{pid}/task/%d{pid}/children"
            let children =
                try
                    File.ReadAllText(childrenPath).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map Int32.Parse
                    |> Array.toList
                with _ -> []
            children
            |> List.fold (fun (total, seen) childPid ->
                let childTotal, nextSeen = collect seen childPid
                total + childTotal, nextSeen) (rss, visited')
    collect Set.empty rootPid |> fst

let runMeasured workingDirectory (executable: string) arguments environment =
    Interlocked.Increment(&externalProcessCount) |> ignore
    let isQuint = executable.EndsWith(expectedQuint, StringComparison.Ordinal)
    let arguments = isolateApalacheEndpoint isQuint arguments
    incrementInvocation (classifyInvocation isQuint arguments)
    if isQuint then
        Interlocked.Increment(&quintProcessCount) |> ignore
        match arguments with
        | "verify" :: _ -> Interlocked.Increment(&apalacheVerifyInvocationCount) |> ignore
        | _ -> ()
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- workingDirectory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in arguments do info.ArgumentList.Add argument
    for name, value in environment do info.Environment[name] <- value
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    let clock = Stopwatch.StartNew()
    let mutable peakBytes = 0L
    while not child.HasExited do
        peakBytes <- Math.Max(peakBytes, processTreeRssBytes child.Id)
        Thread.Sleep 10
    child.WaitForExit()
    peakBytes <- Math.Max(peakBytes, processTreeRssBytes child.Id)
    if isQuint && child.ExitCode <> 0 then Interlocked.Increment(&quintRejectedProcessCount) |> ignore
    child.ExitCode, output.Result.Trim(), error.Result.Trim(), clock.ElapsedMilliseconds,
    int (Math.Ceiling(float peakBytes / 1048576.0))

let requireGreen code workingDirectory executable arguments environment =
    let exitCode, output, error = run workingDirectory executable arguments environment

    if exitCode <> 0 then
        fail code ($"exit={exitCode}; stdout={output}; stderr={error}")

    output, error

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let rec parse root staticOnly compilerOnly output receiptFailurePhase remaining =
    match remaining with
    | [] -> root, staticOnly, compilerOnly, output, receiptFailurePhase
    | "--root" :: value :: tail -> parse (Path.GetFullPath value) staticOnly compilerOnly output receiptFailurePhase tail
    | "--static-only" :: tail -> parse root true compilerOnly output receiptFailurePhase tail
    | "--compiler-only" :: tail -> parse root staticOnly true output receiptFailurePhase tail
    | "--output" :: value :: tail -> parse root staticOnly compilerOnly (Some value) receiptFailurePhase tail
    | "--exercise-failure-receipt" :: value :: tail -> parse root staticOnly compilerOnly output (Some value) tail
    | value :: _ -> fail "ARGUMENT" value

let root, staticOnly, compilerOnly, outputOption, receiptFailurePhase =
    parse (Path.GetFullPath ".") false false None None arguments
let refreshFormalEvidence = Environment.GetEnvironmentVariable("FSGG_REFRESH_FORMAL_EVIDENCE") = "1"

let qualificationOutput =
    outputOption
    |> Option.map Path.GetFullPath
    |> Option.defaultValue (Path.Combine(root, "artifacts/canonical-quint/qualification.json"))

let writeQualificationReceipt failure =
    let totalDurationMs = qualificationClock.ElapsedMilliseconds
    let q2DurationMs = Math.Max(0L, totalDurationMs - preparationDurationMs)
    let preparationValue = preparationDigest |> Option.defaultValue "none"

    let failureCode, failureDetailSha256 =
        match failure with
        | Some(code, detail) -> code, sha256Text detail
        | None -> "none", "none"

    let formalEvidenceIdentity =
        formalCounterexampleReceipts
        |> Seq.sortBy (fun (id, _, _, _) -> id)
        |> Seq.map (fun (id, manifest, trace, itf) -> $"%s{id}|%s{manifest}|%s{trace}|%s{itf}")
        |> String.concat ";"

    let resultSha256 =
        sha256Text
            ($"%s{q1Outcome}|%s{q2Outcome}|%d{verifiedPositiveInvariantCount}|%d{quintRejectedProcessCount}|%d{externalProcessCount}|%d{quintProcessCount}|%d{apalacheVerifyInvocationCount}|%s{preparationValue}|%s{formalEvidenceIdentity}|%s{failureCode}|%s{failureDetailSha256}")

    let outputDirectory = Path.GetDirectoryName qualificationOutput

    if not (String.IsNullOrWhiteSpace outputDirectory) then
        Directory.CreateDirectory outputDirectory |> ignore

    let temporaryOutput = qualificationOutput + "." + Guid.NewGuid().ToString("N") + ".tmp"

    do
        use outputStream = File.Create temporaryOutput
        use writer = new Utf8JsonWriter(outputStream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.coordination.canonical-quint-qualification/1")
        writer.WriteString("q1Outcome", q1Outcome)
        writer.WriteString("q2Outcome", q2Outcome)
        writer.WriteNumber("positiveInvariantCount", verifiedPositiveInvariantCount)
        writer.WriteNumber("negativeControlCount", quintRejectedProcessCount)
        writer.WriteNumber("preparationDurationMs", preparationDurationMs)
        writer.WriteNumber("q2DurationMs", q2DurationMs)
        writer.WriteNumber("totalDurationMs", totalDurationMs)
        writer.WriteStartObject("processCounts")
        writer.WriteNumber("external", externalProcessCount)
        writer.WriteNumber("quintCli", quintProcessCount)
        writer.WriteNumber("apalacheVerify", apalacheVerifyInvocationCount)
        writer.WriteEndObject()
        writer.WriteStartArray("formalCounterexamples")
        for id, manifestSha256, traceSha256, itfSha256 in formalCounterexampleReceipts |> Seq.sortBy (fun (id, _, _, _) -> id) do
            writer.WriteStartObject()
            writer.WriteString("id", id)
            writer.WriteString("manifestSha256", manifestSha256)
            writer.WriteString("traceSha256", traceSha256)
            writer.WriteString("itfSha256", itfSha256)
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartObject("tools")
        writer.WriteString("toolchainSha256", expectedToolchain)
        writer.WriteString("quintSha256", expectedQuint)
        writer.WriteString("apalacheJarSha256", expectedApalacheJar)
        writer.WriteEndObject()
        writer.WriteStartObject("inputs")
        writer.WriteString("sourceSha256", expectedSource)
        writer.WriteString("contractSha256", expectedContract)
        writer.WriteEndObject()

        match preparationDigest with
        | Some digest -> writer.WriteString("preparationSha256", digest)
        | None -> writer.WriteNull("preparationSha256")

        match failure with
        | Some(code, _) ->
            writer.WriteStartObject("failure")
            writer.WriteString("code", code)
            writer.WriteString("detailSha256", failureDetailSha256)
            writer.WriteEndObject()
        | None -> writer.WriteNull("failure")

        writer.WriteString("resultSha256", resultSha256)
        writer.WriteEndObject()
        writer.Flush()
        outputStream.Flush(true)

    File.Move(temporaryOutput, qualificationOutput, true)

failureReceiptWriter <-
    Some(fun code detail ->
        if currentPhase = "q1" then
            q1Outcome <- "failed"
            q2Outcome <- "not-run"
        else
            q2Outcome <- "failed"

        writeQualificationReceipt (Some(code, detail)))

let requireCompletedProcessInventory () =
    let expected = expectedInvocationInventory |> Seq.map (fun row -> row.Key, row.Value) |> Map.ofSeq
    let actual = actualInvocationInventory |> Seq.map (fun row -> row.Key, row.Value) |> Map.ofSeq
    if actual <> expected then
        fail
            "PROCESS-INVENTORY-COVERAGE"
            ($"expected=%A{expected}; actual=%A{actual}")

match receiptFailurePhase with
| Some "q1" -> fail "RECEIPT-SELF-TEST-Q1" "exercise q1 failure receipt"
| Some "q2" ->
    q1Outcome <- "passed"
    currentPhase <- "q2"
    preparationDurationMs <- qualificationClock.ElapsedMilliseconds
    preparationDigest <- Some(String.replicate 64 "0")
    fail "RECEIPT-SELF-TEST-Q2" "exercise q2 failure receipt"
| Some "process-inventory" ->
    q1Outcome <- "passed"
    currentPhase <- "q2"
    verifiedPositiveInvariantCount <- 8
    quintRejectedProcessCount <- 100
    expectedInvocationInventory["exercise-required"] <- 1
    preparationDurationMs <- qualificationClock.ElapsedMilliseconds
    preparationDigest <- Some(String.replicate 64 "0")
    requireCompletedProcessInventory ()
| Some value -> fail "ARGUMENT" ($"invalid failure receipt phase: %s{value}")
| None -> ()

let source = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")

let selectors =
    Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")

let retained = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated")
let authority = Path.Combine(retained, "typed-authority.json")
let contract = Path.Combine(retained, "contract.json")
let binding = Path.Combine(retained, "Protocol.Generated.fs")
let sourceMap = Path.Combine(retained, "source-map.json")
let receipt = Path.Combine(retained, "receipt.json")
let outputGenerator = Path.Combine(root, "eng/generate-compiled-contract-outputs.fsx")
let qualificationValidator = Path.Combine(root, "eng/validate-quint-qualification.fsx")
let qualificationConfiguration = Path.Combine(root, "eng/quint-qualification.json")
let qualificationBaseline = Path.Combine(root, "eng/quint-qualification-baseline.json")
let compiledOutputs = Path.Combine(retained, "compiled-outputs")
let compiledOutputManifest = Path.Combine(compiledOutputs, "manifest.json")

for code, path in
    [ "SOURCE-MISSING", source
      "SELECTOR-MISSING", selectors
      "AUTHORITY-MISSING", authority
      "CONTRACT-MISSING", contract
      "BINDING-MISSING", binding
      "SOURCE-MAP-MISSING", sourceMap
      "RECEIPT-MISSING", receipt
      "OUTPUT-GENERATOR-MISSING", outputGenerator
      "QUALIFICATION-VALIDATOR-MISSING", qualificationValidator
      "QUALIFICATION-CONFIG-MISSING", qualificationConfiguration
      "QUALIFICATION-BASELINE-MISSING", qualificationBaseline
      "COMPILED-OUTPUT-MANIFEST-MISSING", compiledOutputManifest ] do
    requireFile code path

if sha256 source <> expectedSource then
    fail "SOURCE-DIGEST" (sha256 source)

if sha256 contract <> expectedContract then
    fail "CONTRACT-DIGEST" (sha256 contract)

let authorityDocument = JsonDocument.Parse(File.ReadAllBytes authority)
let authorityRoot = authorityDocument.RootElement

if authorityRoot.GetProperty("schemaVersion").GetInt32() <> 2 then
    fail "AUTHORITY-SCHEMA" "not-v2"

if authorityRoot.GetProperty("backend").GetString() <> "quint-specification-v1" then
    fail "BACKEND" "wrong"

if authorityRoot.GetProperty("profileIdentity").GetString() <> expectedProfile then
    fail "PROFILE" "wrong"

if authorityRoot.GetProperty("packageIdentity").GetString() <> expectedPackage then
    fail "PACKAGE" "wrong"

if authorityRoot.GetProperty("toolchainIdentity").GetString() <> expectedToolchain then
    fail "TOOLCHAIN" "wrong"

let contractDocument = JsonDocument.Parse(File.ReadAllBytes contract)
let contractRoot = contractDocument.RootElement

if
    contractRoot.GetProperty("schema").GetString()
    <> "fsgg.quint.compiled-contract/v2"
then
    fail "CONTRACT-SCHEMA" "wrong"

if contractRoot.GetProperty("profile").GetString() <> expectedProfile then
    fail "CONTRACT-PROFILE" "wrong"

if contractRoot.GetProperty("catalogue").GetArrayLength() <> 134 then
    fail "CATALOGUE" "wrong-cardinality"

if contractRoot.GetProperty("relationships").GetArrayLength() <> 17 then
    fail "RELATIONSHIPS" "wrong-cardinality"

if contractRoot.GetProperty("actionEffects").GetArrayLength() <> 14 then
    fail "ACTIONS" "wrong-cardinality"

let mutationIds =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.map (fun entry -> entry.GetProperty("id").GetString())
    |> Seq.filter (fun id -> id.StartsWith("MUT-", StringComparison.Ordinal) || id.StartsWith("MOUT-", StringComparison.Ordinal))
    |> Set.ofSeq

let expectedMutationIds =
    Set [ "MUT-Create"; "MUT-Append"; "MUT-AddEdge"; "MUT-RemoveEdge"; "MUT-Set"; "MUT-Clear"; "MUT-Transition"; "MUT-Compensate"
          "MOUT-Applied"; "MOUT-Idempotent"; "MOUT-Rejected"; "MOUT-RevisionConflict"
          "MOUT-RateLimited"; "MOUT-Unavailable"; "MOUT-TimedOut"; "MOUT-Incomplete" ]

if mutationIds <> expectedMutationIds then
    fail "MUTATION-CATALOGUES" (String.concat "," mutationIds)

let durablePlanDispositionIds =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.map (fun entry -> entry.GetProperty("id").GetString())
    |> Seq.filter (fun id -> id.StartsWith("PDISP-", StringComparison.Ordinal))
    |> Set.ofSeq

let expectedDurablePlanDispositionIds =
    Set [ "PDISP-Advance"; "PDISP-ReceiptReread"; "PDISP-Replan"; "PDISP-Compensate" ]

if durablePlanDispositionIds <> expectedDurablePlanDispositionIds then
    fail "DURABLE-PLAN-DISPOSITIONS" (String.concat "," durablePlanDispositionIds)

let desiredStateEntries =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.filter (fun entry -> entry.GetProperty("id").GetString() = "DSTATE-Specification")
    |> Seq.toList

match desiredStateEntries with
| [ desiredState ] ->
    let field name =
        desiredState.GetProperty("value").GetProperty("fields").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("name").GetString() = name)
        |> _.GetProperty("value")
        |> _.GetProperty("value")

    let expectedFields =
        [ "authorityClass", "revision-bound"
          "executionClass", "pure-intent-no-writer"
          "issueSchemaContract", "issue-type|issue-field|field-type|allowed-value"
          "repositoryPropertiesContract", "property-schema|property-value"
          "projectsContract", "project-field|project-view|project-workflow|project-visibility|project-membership-policy"
          "repositoryProfileContract", "ruleset|merge-queue|merge-policy|actions-policy|branch-deletion-policy"
          "workflowPinsContract", "reusable-workflow-pin|action-pin"
          "releasesContract", "release-environment|immutable-release|tag-protection|trusted-publisher"
          "permissionsContract", "repository-visibility|team-access|workflow-permission|environment-protection"
          "securitySupplyChainContract", "vulnerability-policy|secret-policy|dependency-policy|sbom-policy|attestation-policy"
          "phaseContract", "DSPH-Inspect>DSPH-Plan>(DSPH-Apply|DSPH-Verify)>DSPH-Verify"
          "phaseAuthorityContract", "subject|profile|family|content|authority-revision|plan-outcome|apply-receipt"
          "refusalContract", "unsupported|unauthorized|incomplete|stale|identity-mismatch" ]

    for name, expected in expectedFields do
        if (field name).GetString() <> expected then fail "DESIRED-STATE-SUMMARY" name
| _ -> fail "DESIRED-STATE-SUMMARY" "expected-one"

let compiledOutputEntries =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.filter (fun entry -> entry.GetProperty("id").GetString() = "COUT-Specification")
    |> Seq.toList

match compiledOutputEntries with
| [ compiledOutput ] ->
    let field name =
        compiledOutput.GetProperty("value").GetProperty("fields").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("name").GetString() = name)
        |> _.GetProperty("value")
        |> _.GetProperty("value")
        |> _.GetString()

    let expectedFields =
        [ "familyContract", "1:schemas|2:command-metadata|3:permission-census|4:mutation-census|5:settings-plans|6:projection-views|7:semantic-diff|8:diagrams|9:model-test-inventory"
          "identityContract", "family|ordinal|source|behavior|source-version|extractor-version|quint-version|profile-version|schema-version|contract|content"
          "qualificationContract", "supported|complete|fresh|qualification-manifest:candidate|input-set|environment|results|reviewers|independent-cases|independent-review"
          "projectionViewFormats", "markdown|json"
          "refusalContract", "missing|duplicate|substituted|unsupported|incomplete|reordered|stale"
          "normalizationAuthority", "typed-effect-json"
          "versionContract", String.concat "|" [ expectedSourceVersion; expectedExtractorVersion; expectedQuintVersion; expectedProfile; expectedSchemaVersion ]
          "semanticDiffContract", "ordinal|json-pointer|value-sha256" ]

    for name, expected in expectedFields do
        if field name <> expected then fail "COMPILED-OUTPUT-SUMMARY" name
| _ -> fail "COMPILED-OUTPUT-SUMMARY" "expected-one"

let expectedCompiledOutputs =
    [ ("COUT-Schemas", 1, [ "schemas.json" ])
      ("COUT-CommandMetadata", 2, [ "command-metadata.json" ])
      ("COUT-PermissionCensus", 3, [ "permission-census.json" ])
      ("COUT-MutationCensus", 4, [ "mutation-census.json" ])
      ("COUT-SettingsPlans", 5, [ "settings-plans.json" ])
      ("COUT-ProjectionViews", 6, [ "projection-view.json"; "projection-view.md" ])
      ("COUT-SemanticDiff", 7, [ "semantic-diff.json" ])
      ("COUT-Diagrams", 8, [ "diagrams.md" ])
      ("COUT-ModelTestInventory", 9, [ "model-test-inventory.json" ]) ]

let compiledOutputDocument = JsonDocument.Parse(File.ReadAllBytes compiledOutputManifest)
let compiledOutputRoot = compiledOutputDocument.RootElement

let receiptDocument = JsonDocument.Parse(File.ReadAllBytes receipt)
let receiptRoot = receiptDocument.RootElement

if receiptRoot.GetProperty("sourceSha256").GetString() <> expectedSource then
    fail "RECEIPT-SOURCE" "stale"

if receiptRoot.GetProperty("contractSha256").GetString() <> expectedContract then
    fail "RECEIPT-CONTRACT" "stale"

if receiptRoot.GetProperty("typedEffectSha256").GetString() <> expectedBehavior then
    fail "RECEIPT-BEHAVIOR" "stale"

let validateDeterministicIdentity code (identity: JsonElement) =
    if identity.GetProperty("schema").GetString() <> "fsgg.quint.deterministic-identity/1" then fail code "schema"
    if identity.GetProperty("sourceSha256").GetString() <> expectedSource then fail code "source"
    if identity.GetProperty("behavioralSha256").GetString() <> expectedBehavior then fail code "behavior"
    if identity.GetProperty("contractSha256").GetString() <> expectedContract then fail code "contract"
    if identity.GetProperty("normalizationAuthority").GetString() <> "typed-effect-json" then fail code "normalization"

    let versions = identity.GetProperty("versions")
    if versions.GetProperty("source").GetString() <> expectedSourceVersion then fail "SOURCE-VERSION" "unsupported"
    if versions.GetProperty("extractor").GetString() <> expectedExtractorVersion then fail "EXTRACTOR-VERSION" "unsupported"
    if versions.GetProperty("quint").GetString() <> expectedQuintVersion then fail "QUINT-VERSION" "unsupported"
    if versions.GetProperty("profile").GetString() <> expectedProfile then fail "PROFILE-VERSION" "unsupported"
    if versions.GetProperty("schema").GetString() <> expectedSchemaVersion then fail "SCHEMA-VERSION" "unsupported"
    if not (identity.GetProperty("supported").GetBoolean()) then fail code "unsupported"
    if not (identity.GetProperty("complete").GetBoolean()) then fail code "incomplete"
    if not (identity.GetProperty("fresh").GetBoolean()) then fail code "stale"

if compiledOutputRoot.GetProperty("schema").GetString() <> "fsgg.quint.compiled-output-manifest/1" then
    fail "COMPILED-OUTPUT-SCHEMA" "wrong"

if compiledOutputRoot.GetProperty("sourceSha256").GetString() <> expectedSource then
    fail "COMPILED-OUTPUT-SOURCE" "stale"

if compiledOutputRoot.GetProperty("behavioralSha256").GetString() <> expectedBehavior then
    fail "COMPILED-OUTPUT-BEHAVIOR" "stale"

if compiledOutputRoot.GetProperty("profile").GetString() <> expectedProfile then
    fail "COMPILED-OUTPUT-PROFILE" "wrong"

if compiledOutputRoot.GetProperty("contractSha256").GetString() <> expectedContract then
    fail "COMPILED-OUTPUT-CONTRACT" "stale"

validateDeterministicIdentity "COMPILED-OUTPUT-IDENTITY" (compiledOutputRoot.GetProperty("identity"))

let actualCompiledOutputs = compiledOutputRoot.GetProperty("outputs").EnumerateArray() |> Seq.toList

if actualCompiledOutputs.Length <> expectedCompiledOutputs.Length then
    fail "COMPILED-OUTPUT-COUNT" (string actualCompiledOutputs.Length)

let expectedFiles =
    expectedCompiledOutputs
    |> List.collect (fun (_, _, files) -> files)
    |> Set.ofList
    |> Set.add "manifest.json"

let actualFiles =
    Directory.EnumerateFiles(compiledOutputs, "*", SearchOption.TopDirectoryOnly)
    |> Seq.map Path.GetFileName
    |> Set.ofSeq

if actualFiles <> expectedFiles then
    fail "COMPILED-OUTPUT-FILES" (String.concat "," actualFiles)

for expected, actual in List.zip expectedCompiledOutputs actualCompiledOutputs do
    let expectedFamily, expectedOrdinal, expectedFamilyFiles = expected

    if actual.GetProperty("family").GetString() <> expectedFamily then
        fail "COMPILED-OUTPUT-ORDER" expectedFamily

    if actual.GetProperty("ordinal").GetInt32() <> expectedOrdinal then
        fail "COMPILED-OUTPUT-ORDER" expectedFamily

    if actual.GetProperty("sourceSha256").GetString() <> expectedSource then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    if actual.GetProperty("behavioralSha256").GetString() <> expectedBehavior then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    if actual.GetProperty("profile").GetString() <> expectedProfile then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    if actual.GetProperty("contractSha256").GetString() <> expectedContract then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    validateDeterministicIdentity "COMPILED-OUTPUT-IDENTITY" (actual.GetProperty("identity"))

    if not (actual.GetProperty("supported").GetBoolean()) then fail "COMPILED-OUTPUT-UNSUPPORTED" expectedFamily
    if not (actual.GetProperty("complete").GetBoolean()) then fail "COMPILED-OUTPUT-INCOMPLETE" expectedFamily
    if not (actual.GetProperty("fresh").GetBoolean()) then fail "COMPILED-OUTPUT-STALE" expectedFamily

    let actualFamilyFiles =
        actual.GetProperty("files").EnumerateArray()
        |> Seq.map (fun file -> file.GetProperty("path").GetString())
        |> Seq.toList

    if actualFamilyFiles <> expectedFamilyFiles then
        fail "COMPILED-OUTPUT-FAMILY-FILES" expectedFamily

    for file in actual.GetProperty("files").EnumerateArray() do
        let relative = file.GetProperty("path").GetString()
        let path = Path.Combine(compiledOutputs, relative)
        requireFile "COMPILED-OUTPUT-FILE-MISSING" path

        if file.GetProperty("contentSha256").GetString() <> sha256 path then
            fail "COMPILED-OUTPUT-CONTENT" relative

let trackedExit, trackedQnt, trackedError =
    run root "git" [ "ls-files"; "*.qnt" ] []

if trackedExit <> 0 then
    fail "GIT-INVENTORY" trackedError

if not (String.IsNullOrWhiteSpace trackedQnt) then
    fail "GENERATED-QNT-TRACKED" trackedQnt

let authoredFsharp =
    Directory.EnumerateFiles(Path.Combine(root, "src"), "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun path -> not (path.EndsWith("Protocol.Generated.fs", StringComparison.Ordinal)))
    |> Seq.map File.ReadAllText
    |> String.concat "\n"

for rival in
    [ "type Subject ="
      "type Authority ="
      "type Mutation ="
      "type ObservationPlan =" ] do
    if authoredFsharp.Contains(rival, StringComparison.Ordinal) then
        fail "PARALLEL-AST" rival

let selectionPlanPath = Path.Combine(Path.GetTempPath(), $"fsgg-quint-selection-{Guid.NewGuid():N}.json")
let qualificationMode =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_QUALIFICATION_MODE" with
    | null | "" -> "protected"
    | value -> value
let protectedMode =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_PROTECTED_MODE" with
    | null | "" -> "main"
    | value -> value
let changedModules =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_CHANGED_MODULES" with
    | null | "" -> []
    | value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.toList
let changedPaths =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_CHANGED_PATHS" with
    | null | "" -> []
    | value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.toList
let changedSurfaces =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_CHANGED_SURFACES" with
    | null | "" -> []
    | value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.toList
let reuseSource = Environment.GetEnvironmentVariable "FSGG_QUINT_REUSE_SOURCE_SHA256"
let reuseReceipt = Environment.GetEnvironmentVariable "FSGG_QUINT_REUSE_RECEIPT"
let proposalPath = Environment.GetEnvironmentVariable "FSGG_QUINT_FUTURE_PROPOSAL"
let selectionArguments =
    [ "--mode"; qualificationMode; "--protected-mode"; protectedMode; "--plan-out"; selectionPlanPath ]
    @ (changedModules |> List.collect (fun moduleId -> [ "--changed-module"; moduleId ]))
    @ (changedPaths |> List.collect (fun path -> [ "--changed-path"; path ]))
    @ (changedSurfaces |> List.collect (fun surface -> [ "--changed-surface"; surface ]))
    @ (if String.IsNullOrWhiteSpace reuseSource then [] else [ "--reuse-source-sha256"; reuseSource ])
    @ (if String.IsNullOrWhiteSpace reuseReceipt then [] else [ "--reuse-receipt"; reuseReceipt ])
    @ (if String.IsNullOrWhiteSpace proposalPath then [] else [ "--proposal"; proposalPath ])

requireGreen
    "QUINT-QUALIFICATION-CONTRACT"
    root
    "dotnet"
    ([ "fsi"; qualificationValidator; "--"; "--self-test"; "--root"; root; "--config"; qualificationConfiguration ] @ selectionArguments)
    []
|> ignore

let selectionDocument = JsonDocument.Parse(File.ReadAllBytes selectionPlanPath)
let selectedRootIds =
    selectionDocument.RootElement.GetProperty("roots").EnumerateArray()
    |> Seq.map _.GetString()
    |> Set.ofSeq
File.Delete selectionPlanPath
let processInventoryConfiguration = JsonDocument.Parse(File.ReadAllBytes qualificationConfiguration)
let declaredFormalTestCount =
    processInventoryConfiguration.RootElement.GetProperty("formalTests").GetArrayLength()

if staticOnly then
    printfn "CANONICAL_QUINT_PROTOCOL_STATIC_OK contract=%s profile=%s" expectedContract expectedProfile
    exit 0

let cacheValue = Environment.GetEnvironmentVariable "FSGG_QUINT_CACHE"

if String.IsNullOrWhiteSpace cacheValue then
    fail "CACHE" "FSGG_QUINT_CACHE is required"

let cache = Path.GetFullPath cacheValue
let quint = Path.Combine(cache, "objects", expectedQuint)
let lmt = Path.Combine(cache, "objects", expectedLmt)
requireFile "QUINT-MISSING" quint
requireFile "LMT-MISSING" lmt

if sha256 quint <> expectedQuint then
    fail "QUINT-DIGEST" (sha256 quint)

if sha256 lmt <> expectedLmt then
    fail "LMT-DIGEST" (sha256 lmt)

let cli =
    match Environment.GetEnvironmentVariable "FSGG_SDD_CLI" with
    | null
    | "" -> "fsgg-sdd"
    | value -> value

let version, _ = requireGreen "CLI-VERSION" root cli [ "--version" ] []

if version.Trim() <> "1.5.0" then
    fail "CLI-VERSION" version

let scratch =
    Path.Combine(Path.GetTempPath(), $"fsgg-canonical-quint-{Guid.NewGuid():N}")

try
    let scratchSource =
        Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Protocol.md")

    let scratchSelectors =
        Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")

    Directory.CreateDirectory(Path.GetDirectoryName scratchSource) |> ignore
    File.Copy(source, scratchSource)
    File.Copy(selectors, scratchSelectors)

    requireGreen
        "AUTHOR"
        root
        cli
        [ "typed-sdd"
          "author"
          "--root"
          scratch
          "--work"
          "66-gs2-02-11-deterministic-identity"
          "--title"
          "GS2-02.11 deterministic identity"
          "--agent"
          "swift-5db0"
          "--session"
          "gs2-02-11-profile2-r1"
          "--backend"
          "quint-specification-v1"
          "--profile"
          expectedProfile
          "--source"
          "src/FS.GG.Coordination.Protocol/Protocol.md"
          "--bindings"
          "src/FS.GG.Coordination.Protocol/Protocol.bindings.json"
          "--cache"
          cache ]
        []
    |> ignore

    requireGreen "INSPECT" root cli [ "typed-sdd"; "inspect"; "--root"; scratch; "--work"; "66-gs2-02-11-deterministic-identity" ] []
    |> ignore

    let generatedRoot = Path.Combine(scratch, "readiness/66-gs2-02-11-deterministic-identity")

    let comparisons =
        [ authority, Path.Combine(generatedRoot, "typed-authority.json")
          contract, Path.Combine(generatedRoot, "quint/contract.json")
          binding, Path.Combine(generatedRoot, "quint/bindings.fs")
          sourceMap, Path.Combine(generatedRoot, "quint/source-map.json")
          receipt, Path.Combine(generatedRoot, "quint/receipt.json") ]

    for expected, actual in comparisons do
        requireFile "REGEN-MISSING" actual

        if not (File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(actual))) then
            fail "STALE-PROJECTION" (Path.GetFileName expected)

    let scratchRetained = Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Generated")
    Directory.CreateDirectory(scratchRetained) |> ignore
    File.Copy(Path.Combine(generatedRoot, "quint/contract.json"), Path.Combine(scratchRetained, "contract.json"), true)
    File.Copy(Path.Combine(generatedRoot, "typed-authority.json"), Path.Combine(scratchRetained, "typed-authority.json"), true)
    File.Copy(Path.Combine(generatedRoot, "quint/receipt.json"), Path.Combine(scratchRetained, "receipt.json"), true)

    let scratchCompiledOutputs = Path.Combine(scratch, "compiled-outputs")

    requireGreen
        "COMPILED-OUTPUT-GENERATOR"
        root
        "dotnet"
        [ "fsi"; outputGenerator; "--"; "--root"; scratch; "--output"; scratchCompiledOutputs ]
        []
    |> ignore

    for relative in expectedFiles do
        let expected = Path.Combine(compiledOutputs, relative)
        let actual = Path.Combine(scratchCompiledOutputs, relative)
        requireFile "COMPILED-OUTPUT-REGEN-MISSING" actual

        if not (File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(actual))) then
            fail "COMPILED-OUTPUT-STALE-PROJECTION" relative

    let canonicalReceipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generatedRoot, "quint/receipt.json"))).RootElement
    let canonicalBehavior = canonicalReceipt.GetProperty("typedEffectSha256").GetString()
    let canonicalSource = canonicalReceipt.GetProperty("sourceSha256").GetString()
    let canonicalContract = canonicalReceipt.GetProperty("contractSha256").GetString()
    let canonicalSemanticDiffPath = Path.Combine(compiledOutputs, "semantic-diff.json")
    let canonicalSemanticContent =
        JsonDocument.Parse(File.ReadAllBytes canonicalSemanticDiffPath).RootElement.GetProperty("content").GetRawText()

    let authorVariant name (sourceText: string) =
        let variantRoot = Path.Combine(scratch, name)
        let variantSource = Path.Combine(variantRoot, "src/FS.GG.Coordination.Protocol/Protocol.md")
        let variantSelectors = Path.Combine(variantRoot, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")
        Directory.CreateDirectory(Path.GetDirectoryName variantSource) |> ignore
        let normalizedSourceText = normalizeSupportedAuthoring sourceText
        File.WriteAllText(variantSource, normalizedSourceText, UTF8Encoding(false))
        File.Copy(selectors, variantSelectors)
        let work = $"66-gs2-02-11-{name}"

        requireGreen
            ($"AUTHOR-{name.ToUpperInvariant()}")
            root
            cli
            [ "typed-sdd"; "author"; "--root"; variantRoot; "--work"; work
              "--title"; $"GS2-02.11 {name}"; "--agent"; "swift-5db0"; "--session"; $"gs2-02-11-{name}"
              "--backend"; "quint-specification-v1"; "--profile"; expectedProfile
              "--source"; "src/FS.GG.Coordination.Protocol/Protocol.md"
              "--bindings"; "src/FS.GG.Coordination.Protocol/Protocol.bindings.json"; "--cache"; cache ]
            []
        |> ignore

        let variantReadiness = Path.Combine(variantRoot, "readiness", work)
        let variantGenerated = Path.Combine(variantReadiness, "quint")
        let variantReceiptPath = Path.Combine(variantGenerated, "receipt.json")
        requireFile "VARIANT-RECEIPT" variantReceiptPath
        let variantReceipt = JsonDocument.Parse(File.ReadAllBytes variantReceiptPath).RootElement

        if variantReceipt.GetProperty("sourceSha256").GetString() <> sha256 variantSource then
            fail "VARIANT-NORMALIZED-SOURCE" name

        let identity =
            sha256Text sourceText,
            variantReceipt.GetProperty("typedEffectSha256").GetString(),
            variantReceipt.GetProperty("contractSha256").GetString()

        let variantRetained = Path.Combine(variantRoot, "src/FS.GG.Coordination.Protocol/Generated")
        Directory.CreateDirectory(variantRetained) |> ignore
        File.Copy(Path.Combine(variantReadiness, "typed-authority.json"), Path.Combine(variantRetained, "typed-authority.json"), true)
        File.Copy(Path.Combine(variantGenerated, "contract.json"), Path.Combine(variantRetained, "contract.json"), true)
        File.Copy(Path.Combine(variantGenerated, "receipt.json"), Path.Combine(variantRetained, "receipt.json"), true)
        let variantOutputs = Path.Combine(variantRoot, "compiled-outputs")

        requireGreen
            ($"COMPILED-OUTPUT-{name.ToUpperInvariant()}")
            root
            "dotnet"
            [ "fsi"; outputGenerator; "--"; "--root"; variantRoot; "--output"; variantOutputs ]
            []
        |> ignore

        let semanticDiffPath = Path.Combine(variantOutputs, "semantic-diff.json")
        requireFile "VARIANT-SEMANTIC-DIFF" semanticDiffPath
        let semanticContent =
            JsonDocument.Parse(File.ReadAllBytes semanticDiffPath).RootElement.GetProperty("content").GetRawText()

        let rawTypedEffect = Path.Combine(variantGenerated, "typed-effect.json")
        if File.Exists rawTypedEffect then File.Delete rawTypedEffect
        identity, semanticContent, variantOutputs

    let executedEquivalentVariants = ResizeArray<string>()

    let requireEquivalentVariant name sourceText =
        let (variantSource, variantBehavior, variantContract), semanticContent, _ = authorVariant name sourceText
        if variantSource = canonicalSource then fail "EQUIVALENT-AUTHORING" $"{name}: raw source did not change"
        if variantBehavior <> canonicalBehavior then fail "EQUIVALENT-AUTHORING" $"{name}: behavior changed"
        if variantContract <> canonicalContract then fail "EQUIVALENT-AUTHORING" $"{name}: public contract changed"
        if semanticContent <> canonicalSemanticContent then fail "EQUIVALENT-AUTHORING" $"{name}: semantic diff changed"
        executedEquivalentVariants.Add name

    let canonicalText = File.ReadAllText(source, Encoding.UTF8)
    let triviaFixture = "}\n```\n\nThe executable witness"
    if not (canonicalText.Contains(triviaFixture, StringComparison.Ordinal)) then fail "EQUIVALENT-AUTHORING" "trivia fixture absent"
    let equivalentText = canonicalText.Replace(triviaFixture, "  // semantically inert authoring trivia\n}\n```\n\nThe executable witness")
    requireEquivalentVariant "equivalent-quint-trivia" equivalentText

    let partitionFixture = "\n}\n```\n\nThe executable witness"
    if not (canonicalText.Contains(partitionFixture, StringComparison.Ordinal)) then fail "EQUIVALENT-AUTHORING" "partition fixture absent"
    let partitionedText =
        canonicalText.Replace(
            partitionFixture,
            "\n```\n\n```quint protocol.qnt +=\n}\n```\n\nThe executable witness"
        )
    requireEquivalentVariant "equivalent-named-block-partition" partitionedText

    let openingFence = "```quint protocol.qnt +=\n"
    if not (canonicalText.StartsWith("# GS2-03.1", StringComparison.Ordinal) && canonicalText.Contains(openingFence, StringComparison.Ordinal)) then
        fail "EQUIVALENT-AUTHORING" "fence indentation fixture absent"
    let indentedFenceText =
        canonicalText
            .Replace(openingFence, "  ```quint protocol.qnt +=\n", StringComparison.Ordinal)
            .Replace(partitionFixture, "\n}\n  ```\n\nThe executable witness", StringComparison.Ordinal)
    requireEquivalentVariant "equivalent-fence-indentation" indentedFenceText

    let crlfText = canonicalText.Replace("\n", "\r\n", StringComparison.Ordinal)
    requireEquivalentVariant "equivalent-crlf" crlfText

    let requiredEquivalentVariants =
        [ "equivalent-quint-trivia"
          "equivalent-named-block-partition"
          "equivalent-fence-indentation"
          "equivalent-crlf" ]

    if List.ofSeq executedEquivalentVariants <> requiredEquivalentVariants then
        fail "EQUIVALENT-AUTHORING-COVERAGE" (String.concat "," executedEquivalentVariants)

    let proseFixture = "This document is the sole authored source for the coordination protocol baseline."
    if not (canonicalText.Contains(proseFixture, StringComparison.Ordinal)) then fail "PROSE-ONLY" "fixture absent"
    let proseText = canonicalText.Replace(proseFixture, proseFixture + " Review prose may evolve independently.")
    let (proseSource, proseBehavior, proseContract), proseSemanticContent, _ = authorVariant "prose-only" proseText

    if proseSource = canonicalSource then fail "PROSE-ONLY" "raw source did not change"
    if proseBehavior <> canonicalBehavior then fail "PROSE-ONLY" "behavior changed"
    if proseContract <> canonicalContract then fail "PROSE-ONLY" "public contract changed"
    if proseSemanticContent <> canonicalSemanticContent then fail "PROSE-ONLY" "semantic diff changed"

    let semanticFixture = "  action observeProtocolEvidence: bool = all {\n    evidenceObserved' = true,"
    if not (canonicalText.Contains(semanticFixture, StringComparison.Ordinal)) then fail "SEMANTIC-CHANGE" "fixture absent"
    let semanticText =
        canonicalText.Replace(
            semanticFixture,
            "  action observeProtocolEvidence: bool = all {\n    evidenceObserved' = lifecycleStatusCurrent,"
        )
    let (_, semanticBehavior, semanticContract), _, semanticOutputs = authorVariant "semantic-change" semanticText

    if semanticBehavior = canonicalBehavior then fail "SEMANTIC-CHANGE" "behavior did not change"
    if semanticContract = canonicalContract then fail "SEMANTIC-CHANGE" "public contract did not change"

    let readSemanticRows path =
        let document = JsonDocument.Parse(File.ReadAllBytes path)
        let rows = document.RootElement.GetProperty("content").GetProperty("rows").EnumerateArray() |> Seq.toList
        let paths = rows |> List.map (fun row -> row.GetProperty("path").GetString())
        if paths <> List.sort paths then fail "SEMANTIC-DIFF-ORDER" path
        for ordinal, row in rows |> List.indexed do
            if row.GetProperty("ordinal").GetInt32() <> ordinal + 1 then fail "SEMANTIC-DIFF-ORDINAL" path
        rows
        |> Seq.map (fun row -> row.GetProperty("path").GetString(), row.GetProperty("valueSha256").GetString())
        |> Map.ofSeq

    let canonicalRows = readSemanticRows canonicalSemanticDiffPath
    let changedRows = readSemanticRows (Path.Combine(semanticOutputs, "semantic-diff.json"))
    let changedPaths =
        Set.union (canonicalRows |> Map.keys |> Set.ofSeq) (changedRows |> Map.keys |> Set.ofSeq)
        |> Set.filter (fun path -> Map.tryFind path canonicalRows <> Map.tryFind path changedRows)

    if not (changedPaths.Contains "/behavioralSha256") then fail "SEMANTIC-DIFF-BEHAVIOR" "missing"
    if changedPaths.Count < 2 then fail "SEMANTIC-DIFF-CONTRACT" "no public contract row changed"

    let requireGeneratorRed (name: string) (fieldName: string) (replacement: string) (expectedCode: string) =
        let mutantRoot = Path.Combine(scratch, "generator-mutant-" + name)
        let mutantSource = Path.Combine(mutantRoot, "src/FS.GG.Coordination.Protocol/Protocol.md")
        let mutantRetained = Path.Combine(mutantRoot, "src/FS.GG.Coordination.Protocol/Generated")
        Directory.CreateDirectory(Path.GetDirectoryName mutantSource) |> ignore
        Directory.CreateDirectory(mutantRetained) |> ignore
        File.Copy(source, mutantSource)
        File.Copy(authority, Path.Combine(mutantRetained, "typed-authority.json"))
        File.Copy(receipt, Path.Combine(mutantRetained, "receipt.json"))

        let mutantContract = JsonNode.Parse(File.ReadAllText(contract))
        let catalogue = mutantContract["catalogue"].AsArray()
        let specification =
            catalogue
            |> Seq.find (fun entry -> entry["id"].GetValue<string>() = "COUT-Specification")
        let fields = (specification["value"]["fields"]).AsArray()
        let field = fields |> Seq.find (fun entry -> entry["name"].GetValue<string>() = fieldName)
        field["value"]["value"] <- JsonValue.Create(replacement)
        File.WriteAllText(Path.Combine(mutantRetained, "contract.json"), mutantContract.ToJsonString(), UTF8Encoding(false))

        let exitCode, output, error =
            run root "dotnet" [ "fsi"; outputGenerator; "--"; "--root"; mutantRoot ] []

        if exitCode = 0 then fail "GENERATOR-NEGATIVE-CONTROL" ($"{name}: mutant passed")
        if not ((output + "\n" + error).Contains($"code={expectedCode}", StringComparison.Ordinal)) then
            fail "GENERATOR-NEGATIVE-CONTROL" ($"{name}: expected={expectedCode}; stdout={output}; stderr={error}")

    let supportedVersions = [ expectedSourceVersion; expectedExtractorVersion; expectedQuintVersion; expectedProfile; expectedSchemaVersion ]
    let versionMutation index replacement =
        supportedVersions |> List.mapi (fun position value -> if position = index then replacement else value) |> String.concat "|"

    requireGeneratorRed "source-version" "versionContract" (versionMutation 0 "fsgg.quint.literate-source/999") "SOURCE-VERSION"
    requireGeneratorRed "extractor-version" "versionContract" (versionMutation 1 "quint-specification-v1@FS.GG.SDD.Artifacts/999") "EXTRACTOR-VERSION"
    requireGeneratorRed "quint-version" "versionContract" (versionMutation 2 "sha256:substituted") "QUINT-VERSION"
    requireGeneratorRed "profile-version" "versionContract" (versionMutation 3 "fsgg-quint-profile/999") "PROFILE-VERSION"
    requireGeneratorRed "schema-version" "versionContract" (versionMutation 4 "fsgg.quint.compiled-contract/v999") "SCHEMA-VERSION"
    requireGeneratorRed "normalization-authority" "normalizationAuthority" "raw-markdown" "NORMALIZATION-AUTHORITY"
    requireGeneratorRed "semantic-diff-contract" "semanticDiffContract" "unordered-raw-values" "SEMANTIC-DIFF-CONTRACT"

    let qnt = Path.Combine(generatedRoot, "quint/protocol.qnt")
    requireGreen "QUINT-TYPECHECK" scratch quint [ "typecheck"; qnt ] [] |> ignore

    let extractQuintTestFence (markdown: string) =
        let lines = File.ReadAllLines markdown
        let mutable inside = false
        let mutable fences = 0
        let body = ResizeArray<string>()

        for line in lines do
            if line.Trim() = "```quint-test" then
                if inside then fail "QUINT-TEST-FENCE" "nested"
                inside <- true
                fences <- fences + 1
            elif inside && line.Trim() = "```" then
                inside <- false
            elif inside then
                body.Add line

        if inside then fail "QUINT-TEST-FENCE" "unterminated"
        if fences <> 1 then fail "QUINT-TEST-FENCE" ($"expected-one; actual=%d{fences}")
        String.Join(Environment.NewLine, body)

    let q2Qnt = Path.Combine(scratch, "protocol-q2.qnt")
    let q2Source = File.ReadAllText(qnt) + Environment.NewLine + extractQuintTestFence(source) + Environment.NewLine
    File.WriteAllText(q2Qnt, q2Source)
    requireGreen "QUINT-Q2-TYPECHECK" scratch quint [ "typecheck"; q2Qnt ] [] |> ignore

    use qualificationConfigurationDocument = JsonDocument.Parse(File.ReadAllBytes qualificationConfiguration)
    let artifactBudgets =
        qualificationConfigurationDocument.RootElement.GetProperty("roots").EnumerateArray()
        |> Seq.map (fun item ->
            item.GetProperty("id").GetString(), item.GetProperty("budget").GetProperty("artifactBytes").GetInt32())
        |> Map.ofSeq
    let formalTests =
        qualificationConfigurationDocument.RootElement.GetProperty("formalTests").EnumerateArray()
        |> Seq.map (fun item ->
            item.GetProperty("id").GetString(),
            item.GetProperty("main").GetString(),
            item.GetProperty("init").GetString(),
            item.GetProperty("step").GetString(),
            item.GetProperty("invariant").GetString(),
            item.GetProperty("witness").GetString(),
            item.GetProperty("temporal").GetString(),
            item.GetProperty("invalid").GetString(),
            item.GetProperty("removedStep").GetString(),
            item.GetProperty("violatedTemporal").GetString(),
            item.GetProperty("blockedInvariant").GetString(),
            item.GetProperty("counterexample").GetString(),
            item.GetProperty("counterexampleTrace").GetString(),
            item.GetProperty("counterexampleManifest").GetString(),
            item.GetProperty("budget").GetProperty("depth").GetInt32(),
            item.GetProperty("budget").GetProperty("states").GetInt32(),
            item.GetProperty("budget").GetProperty("transitions").GetInt32(),
            item.GetProperty("budget").GetProperty("samples").GetInt32(),
            item.GetProperty("budget").GetProperty("elapsedMs").GetInt32(),
            item.GetProperty("budget").GetProperty("peakMiB").GetInt32(),
            item.GetProperty("budget").GetProperty("artifactBytes").GetInt32())
        |> Seq.toList

    formalSafeSteps <- formalTests |> List.map (fun (_, main, _, step, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) -> main, step) |> Set.ofList
    formalInvalidSteps <- formalTests |> List.map (fun (_, main, _, _, _, _, _, invalid, _, _, _, _, _, _, _, _, _, _, _, _, _) -> main, invalid) |> Set.ofList
    formalRemovedSteps <- formalTests |> List.map (fun (_, main, _, _, _, _, _, _, removedStep, _, _, _, _, _, _, _, _, _, _, _, _) -> main, removedStep) |> Set.ofList
    formalInventoryReady <- true

    // This is the executable qualification plan. Each category corresponds to one named
    // orchestration obligation; completion compares the actual invocation multiset to this plan.
    // Adding a formal test extends five explicit categories rather than changing opaque totals.
    for label, count in
        [ "external/base", 25
          "quint/base-nonverify", 63
          "quint/base-verify", 14
          "quint/selected-root", selectedRootIds.Count
          "quint/formal-simulation", declaredFormalTestCount
          "quint/formal-temporal", declaredFormalTestCount
          "quint/formal-safety-mutant", declaredFormalTestCount
          "quint/formal-counterexample-temporal", 2 * declaredFormalTestCount
          "quint/formal-counterexample-projection", 2 * declaredFormalTestCount ] do
        expectedInvocationInventory[label] <- count

    // The base qualification suite has 71 intentional red outcomes. Every formal scenario
    // contributes one safety mutant, two TLC reproductions, and two Rust projections.
    let expectedRejectedProcessCount = 71 + 5 * declaredFormalTestCount
    let rootArtifactDirectory = Path.Combine(scratch, "root-artifacts")
    Directory.CreateDirectory rootArtifactDirectory |> ignore
    let rootArtifactDigests = ResizeArray<string * string>()
    let recordRootArtifact rootId path =
        requireFile "QUINT-ROOT-ARTIFACT-MISSING" path
        let length = FileInfo(path).Length
        if length <= 0L || length > int64 artifactBudgets[rootId] then
            fail "QUINT-ROOT-ARTIFACT-BUDGET" ($"%s{rootId}: bytes=%d{length}; budget=%d{artifactBudgets[rootId]}")
        let digest = sha256 path
        rootArtifactDigests.Add(rootId, digest)
        printfn "QUINT_ROOT_ARTIFACT root=%s bytes=%d sha256=%s" rootId length digest

    preparationDurationMs <- qualificationClock.ElapsedMilliseconds

    let preparationSha256 =
        sha256Text ($"%s{sha256 qnt}|%s{sha256 q2Qnt}|%s{expectedToolchain}")

    preparationDigest <- Some preparationSha256
    q1Outcome <- "passed"
    currentPhase <- "q2"

    printfn
        "CANONICAL_QUINT_COMPILER_OK contract=%s source=%s profile=%s preparation=%s durationMs=%d"
        expectedContract
        expectedSource
        expectedProfile
        preparationSha256
        preparationDurationMs

    if compilerOnly then
        Directory.Delete(scratch, true)
        exit 0

    let stateRoots =
        [ "authority", "QualificationAuthorityRoot"
          "lifecycle", "QualificationLifecycleRoot"
          "relations", "QualificationRelationsRoot"
          "protocol-streams", "QualificationProtocolStreamsRoot" ]

    for rootId, rootModule in stateRoots |> List.filter (fun (rootId, _) -> Set.contains rootId selectedRootIds) do
        let artifactPath = Path.Combine(rootArtifactDirectory, $"%s{rootId}.json")
        requireGreen
            "QUINT-BOUNDED-ROOT"
            scratch
            quint
            [ "run"; q2Qnt; "--main"; rootModule; "--init"; "init"; "--step"; "rootStep"
              "--invariant"; "qualificationInvariant"; "--witnesses"; "positiveWitness"; "adversarialWitness"
              "--max-steps"; "8"; "--max-samples"; "100"; "--seed"; "1"; "--verbosity"; "0"; "--out"; artifactPath ]
            []
        |> ignore
        recordRootArtifact rootId artifactPath

    let testRoots =
        [ "mutation-saga", "^test(Mutation|DurablePlan)"
          "desired-state", "^testDesiredState"
          "qualification", "^test(QualificationManifest|CompiledOutputs|DeterministicIdentity)" ]

    for rootId, rootPattern in testRoots |> List.filter (fun (rootId, _) -> Set.contains rootId selectedRootIds) do
        let artifactPath = Path.Combine(rootArtifactDirectory, $"%s{rootId}.json")
        requireGreen
            "QUINT-BOUNDED-TEST-ROOT"
            scratch
            quint
            [ "test"; q2Qnt; "--main"; "CoordinationProtocolTests"; "--backend"; "rust"
              "--match"; rootPattern; "--verbosity"; "0"; "--out"; artifactPath ]
            []
        |> ignore
        recordRootArtifact rootId artifactPath

    let duplicateRootArtifacts =
        rootArtifactDigests
        |> Seq.groupBy snd
        |> Seq.filter (fun (_, rows) -> Seq.length rows > 1)
        |> Seq.toList
    if not (List.isEmpty duplicateRootArtifacts) then
        fail "QUINT-ROOT-ARTIFACT-IDENTITY" ($"duplicates=%A{duplicateRootArtifacts |> List.map fst}")

    let formalArtifactDirectory = Path.Combine(scratch, "formal-test-artifacts")
    Directory.CreateDirectory formalArtifactDirectory |> ignore
    let formalMeasurements = System.Collections.Generic.Dictionary<string, FormalMeasurement>()

    for formalId, main, init, step, invariantName, witness, _, _, _, _, _, _, _, _, depth, _, _, samples, elapsedBudget, peakBudget, artifactBudget in formalTests do
        let artifactPath = Path.Combine(formalArtifactDirectory, $"%s{formalId}-simulation.json")
        let exitCode, output, error, elapsedMs, peakMiB =
            runMeasured scratch quint
                [ "run"; q2Qnt; "--main"; main; "--init"; init; "--step"; step
                  "--invariant"; invariantName; "--witnesses"; witness
                  "--max-steps"; string depth; "--max-samples"; string samples
                  "--seed"; "1"; "--verbosity"; "1" ] []
        if exitCode <> 0 then
            fail "QUINT-FORMAL-SIMULATION" ($"%s{formalId}: exit=%d{exitCode}; stdout=%s{output}; stderr=%s{error}")
        let exploredMatch = Regex.Match(output + "\n" + error, @"out of (\d+) explored")
        if not exploredMatch.Success then
            fail
                "QUINT-FORMAL-SIMULATION-MEASUREMENT"
                ($"%s{formalId}: exit=%d{exitCode}; stdout=%s{output}; stderr=%s{error}")
        let observedSamples = Int32.Parse exploredMatch.Groups[1].Value
        let normalizedOutput =
            Regex.Replace((output + "\n" + error).Trim(), @"\d+ms at \d+ traces/second", "<measured runtime>")
        File.WriteAllText(artifactPath, normalizedOutput + "\n", UTF8Encoding(false))
        let length = FileInfo(artifactPath).Length
        if length <= 0L || length > int64 artifactBudget || elapsedMs > int64 elapsedBudget || peakMiB > peakBudget then
            fail "QUINT-FORMAL-ARTIFACT-BUDGET" ($"%s{formalId}: bytes=%d{length}; budget=%d{artifactBudget}")
        formalMeasurements[formalId] <- {
            StateCount = 0; TransitionCount = 0; SampleCount = observedSamples
            ElapsedMs = elapsedMs; PeakMiB = peakMiB; ArtifactBytes = length
        }
        printfn "QUINT_FORMAL_SIMULATION id=%s samples=%d elapsedMs=%d peakMiB=%d bytes=%d sha256=%s" formalId observedSamples elapsedMs peakMiB length (sha256 artifactPath)

    requireGreen
        "QUINT-INDEPENDENT-ORACLES"
        scratch
        quint
        [ "test"; q2Qnt; "--main"; "CoordinationProtocolTests"; "--backend"; "rust"
          "--match"; "^oracle"; "--verbosity"; "0" ]
        []
    |> ignore

    requireGreen
        "QUINT-RUN"
        scratch
        quint
        [ "run"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "acceptedVocabularyIsQualified"
          "--max-steps"
          "4"
          "--max-samples"
          "20"
          "--seed"
          "1"
          "--verbosity"
          "0" ]
        []
    |> ignore

    requireGreen
        "QUINT-TEST"
        scratch
        quint
        [ "test"
          q2Qnt
          "--main"
          "CoordinationProtocolTests"
          "--backend"
          "rust"
          "--match"
          "^test"
          "--verbosity"
          "0" ]
        []
    |> ignore

    let quintHome = Environment.GetEnvironmentVariable "FSGG_QUINT_HOME"
    let javaHome = Environment.GetEnvironmentVariable "JAVA_HOME"

    if String.IsNullOrWhiteSpace quintHome then
        fail "APALACHE-CACHE" "FSGG_QUINT_HOME is required"

    if String.IsNullOrWhiteSpace javaHome then
        fail "JAVA" "JAVA_HOME is required"

    let apalacheJar =
        Path.Combine(quintHome, "apalache-dist-0.56.1/apalache/lib/apalache.jar")

    let java = Path.Combine(javaHome, "bin/java")
    requireFile "APALACHE-MISSING" apalacheJar
    requireFile "JAVA-MISSING" java

    if sha256 apalacheJar <> expectedApalacheJar then
        fail "APALACHE-DIGEST" (sha256 apalacheJar)

    let environment =
        [ "QUINT_HOME", quintHome
          "PATH",
          Path.GetDirectoryName(java)
          + string Path.PathSeparator
          + Environment.GetEnvironmentVariable("PATH") ]

    let normalizeItf path =
        let document = JsonNode.Parse(File.ReadAllBytes path).AsObject()
        let metadata = document["#meta"].AsObject()
        metadata.Remove("description") |> ignore
        metadata.Remove("timestamp") |> ignore
        metadata.Remove("source") |> ignore
        if metadata["format"].GetValue<string>() <> "ITF" || metadata["status"].GetValue<string>() <> "violation" then
            fail "QUINT-FORMAL-COUNTEREXAMPLE-SHAPE" path
        if document["states"].AsArray().Count < 2 then fail "QUINT-FORMAL-COUNTEREXAMPLE-TRACE" path
        document.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    // TLC liveness verification is comparatively expensive and cannot emit ITF.  Preflight
    // one deterministic Rust projection for every declared removed-step model before entering
    // the TLC loop.  The result is retained as the first of the two reproducibility samples,
    // so this changes failure order without adding process work.
    let runProjection formalId main init removedStep blockedInvariant depth ordinal =
        let pattern = Path.Combine(formalArtifactDirectory, $"%s{formalId}-counterexample-%d{ordinal}-{{seq}}.itf.json")
        let exitCode, output, error, elapsedMs, peakMiB =
            runMeasured scratch quint
                [ "run"; q2Qnt; "--main"; main; "--init"; init; "--step"; removedStep
                  "--invariant"; blockedInvariant; "--max-steps"; string depth; "--max-samples"; "1"
                  "--seed"; "1"; "--verbosity"; "0"; "--out-itf"; pattern ] []
        if exitCode = 0 || not ((output + "\n" + error).Contains("Invariant violated", StringComparison.Ordinal)) then
            fail "QUINT-FORMAL-ITF-PROJECTION" ($"%s{formalId}: exit=%d{exitCode}; stdout=%s{output}; stderr=%s{error}")
        let path = pattern.Replace("{seq}", "0", StringComparison.Ordinal)
        requireFile "QUINT-FORMAL-COUNTEREXAMPLE-MISSING" path
        path, normalizeItf path, elapsedMs, peakMiB

    let projectionPreflights =
        formalTests
        |> List.map (fun (formalId, main, init, _, _, _, _, _, removedStep, _, blockedInvariant,
                          _, _, _, depth, _, _, _, _, _, _) ->
            formalId, runProjection formalId main init removedStep blockedInvariant depth 1)
        |> Map.ofList

    for formalId, main, init, step, invariantName, _, temporalName, invalid, removedStep, violatedTemporal, blockedInvariant,
        retainedCounterexample, retainedTrace, retainedManifest, depth, stateBudget, transitionBudget, _,
        elapsedBudget, peakBudget, artifactBudget in formalTests do
        let temporalExit, temporalOutput, temporalError, temporalElapsed, temporalPeak =
            runMeasured scratch quint
                [ "verify"; q2Qnt; "--main"; main; "--init"; init; "--step"; step
                  "--invariant"; invariantName; "--temporal"; temporalName
                  "--max-steps"; string depth; "--backend"; "tlc"; "--verbosity"; "5" ] environment
        if temporalExit <> 0 then
            fail "QUINT-FORMAL-TEMPORAL" ($"%s{formalId}: exit=%d{temporalExit}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")
        let stateMatches = Regex.Matches(temporalOutput + "\n" + temporalError, @"(\d+) states generated, (\d+) distinct states found")
        if stateMatches.Count = 0 then
            fail
                "QUINT-FORMAL-TLC-MEASUREMENT"
                ($"%s{formalId}: exit=%d{temporalExit}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")
        let stateMatch = stateMatches[stateMatches.Count - 1]
        let generatedStates = Int32.Parse stateMatch.Groups[1].Value
        let distinctStates = Int32.Parse stateMatch.Groups[2].Value
        let transitions = Math.Max(0, generatedStates - 1)
        let measurement = formalMeasurements[formalId]
        measurement.StateCount <- distinctStates
        measurement.TransitionCount <- transitions
        measurement.ElapsedMs <- measurement.ElapsedMs + temporalElapsed
        measurement.PeakMiB <- Math.Max(measurement.PeakMiB, temporalPeak)

        let safetyExit, safetyOutput, safetyError, safetyElapsed, safetyPeak =
            runMeasured scratch quint
                [ "run"; q2Qnt; "--main"; main; "--init"; init; "--step"; invalid
                  "--invariant"; invariantName; "--max-steps"; "2"; "--max-samples"; "1"
                  "--seed"; "1"; "--verbosity"; "0" ] []
        if safetyExit = 0 || not ((safetyOutput + "\n" + safetyError).Contains("Invariant violated", StringComparison.Ordinal)) then
            fail "QUINT-FORMAL-SAFETY-INVALID" ($"%s{formalId}: exit=%d{safetyExit}; stdout=%s{safetyOutput}; stderr=%s{safetyError}")
        measurement.ElapsedMs <- measurement.ElapsedMs + safetyElapsed
        measurement.PeakMiB <- Math.Max(measurement.PeakMiB, safetyPeak)

        let runCounterexample ordinal =
            let temporalExitCode, temporalOutput, temporalError, temporalElapsedMs, temporalPeakMiB =
                runMeasured scratch quint
                    [ "verify"; q2Qnt; "--main"; main; "--init"; init; "--step"; removedStep
                      "--temporal"; violatedTemporal; "--max-steps"; string depth
                      "--backend"; "tlc"; "--verbosity"; "5" ] environment
            let temporalDiagnostic = temporalOutput + "\n" + temporalError
            if temporalExitCode = 0 || not (temporalDiagnostic.Contains("Temporal properties were violated", StringComparison.Ordinal)) then
                fail
                    "QUINT-FORMAL-TEMPORAL-COUNTEREXAMPLE"
                    ($"%s{formalId}: transition-removal did not violate %s{violatedTemporal}; exit=%d{temporalExitCode}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")
            let diagnosticStart = temporalDiagnostic.IndexOf("Error: The following behavior constitutes a counter-example:", StringComparison.Ordinal)
            if diagnosticStart < 0 then
                fail
                    "QUINT-FORMAL-TEMPORAL-DIAGNOSTIC"
                    ($"%s{formalId}: counterexample start marker missing; exit=%d{temporalExitCode}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")
            let diagnosticEnd = temporalDiagnostic.IndexOf("Finished checking temporal properties", diagnosticStart, StringComparison.Ordinal)
            if diagnosticEnd <= diagnosticStart then
                fail
                    "QUINT-FORMAL-TEMPORAL-DIAGNOSTIC"
                    ($"%s{formalId}: counterexample end marker missing; exit=%d{temporalExitCode}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")
            let normalizedDiagnostic = temporalDiagnostic.Substring(diagnosticStart, diagnosticEnd - diagnosticStart).Trim()

            let path, projection, projectionElapsedMs, projectionPeakMiB =
                if ordinal = 1 then projectionPreflights[formalId]
                else runProjection formalId main init removedStep blockedInvariant depth ordinal
            path, projection, normalizedDiagnostic,
            temporalElapsedMs + projectionElapsedMs, Math.Max(temporalPeakMiB, projectionPeakMiB)

        let firstPath, first, firstDiagnostic, firstElapsed, firstPeak = runCounterexample 1
        let _, second, secondDiagnostic, secondElapsed, secondPeak = runCounterexample 2
        if first <> second || firstDiagnostic <> secondDiagnostic then
            fail
                "QUINT-FORMAL-COUNTEREXAMPLE-NONDETERMINISTIC"
                ($"%s{formalId}: firstProjection=%s{sha256Text first}; secondProjection=%s{sha256Text second}; firstDiagnostic=%s{sha256Text firstDiagnostic}; secondDiagnostic=%s{sha256Text secondDiagnostic}")
        File.WriteAllText(firstPath, first, UTF8Encoding(false))
        let itfSha256 = sha256 firstPath
        let itfNode = JsonNode.Parse(first).AsObject()
        let traceNode = JsonObject()
        traceNode["schema"] <- JsonValue.Create("fsgg.coordination.quint-counterexample-trace/1")
        traceNode["temporalDiagnostic"] <- JsonValue.Create(firstDiagnostic)
        traceNode["states"] <- itfNode["states"].DeepClone()
        let traceText = traceNode.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        let tracePath = Path.Combine(formalArtifactDirectory, $"%s{formalId}.quint-trace.json")
        File.WriteAllText(tracePath, traceText, UTF8Encoding(false))
        let traceSha256 = sha256 tracePath
        let orderedStatesSha256 = sha256Text (itfNode["states"].ToJsonString(JsonSerializerOptions(WriteIndented = false)))
        let manifestNode = JsonObject()
        manifestNode["schema"] <- JsonValue.Create("fsgg.coordination.quint-counterexample-manifest/1")
        manifestNode["id"] <- JsonValue.Create(formalId)
        manifestNode["sourceSha256"] <- JsonValue.Create(expectedSource)
        manifestNode["assembledQuintSha256"] <- JsonValue.Create(sha256 q2Qnt)
        manifestNode["main"] <- JsonValue.Create(main)
        manifestNode["init"] <- JsonValue.Create(init)
        manifestNode["removedStep"] <- JsonValue.Create(removedStep)
        manifestNode["violatedTemporal"] <- JsonValue.Create(violatedTemporal)
        manifestNode["blockedInvariant"] <- JsonValue.Create(blockedInvariant)
        let boundsNode = JsonObject()
        boundsNode["maxSteps"] <- JsonValue.Create(depth)
        manifestNode["bounds"] <- boundsNode
        manifestNode["backend"] <- JsonValue.Create("tlc")
        manifestNode["toolchainSha256"] <- JsonValue.Create(expectedToolchain)
        manifestNode["normalization"] <- JsonValue.Create("itf-volatile-meta-v1")
        manifestNode["outcome"] <- JsonValue.Create("temporal-violation")
        manifestNode["orderedStatesSha256"] <- JsonValue.Create(orderedStatesSha256)
        manifestNode["traceSha256"] <- JsonValue.Create(traceSha256)
        manifestNode["itfSha256"] <- JsonValue.Create(itfSha256)
        let manifestText = manifestNode.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        let manifestPath = Path.Combine(formalArtifactDirectory, $"%s{formalId}.manifest.json")
        File.WriteAllText(manifestPath, manifestText, UTF8Encoding(false))

        for code, retainedPath, actualText in
            [ "QUINT-FORMAL-COUNTEREXAMPLE-STALE", retainedCounterexample, first
              "QUINT-FORMAL-TRACE-STALE", retainedTrace, traceText
              "QUINT-FORMAL-MANIFEST-STALE", retainedManifest, manifestText ] do
            let fullPath = Path.Combine(root, retainedPath)
            if refreshFormalEvidence then
                Directory.CreateDirectory(Path.GetDirectoryName fullPath) |> ignore
                File.WriteAllText(fullPath, actualText, UTF8Encoding(false))
            else
                requireFile code fullPath
                if File.ReadAllText(fullPath, Encoding.UTF8) <> actualText then fail code formalId

        let totalBytes = FileInfo(firstPath).Length + FileInfo(tracePath).Length + FileInfo(manifestPath).Length + measurement.ArtifactBytes
        measurement.ArtifactBytes <- totalBytes
        measurement.ElapsedMs <- measurement.ElapsedMs + firstElapsed + secondElapsed
        measurement.PeakMiB <- List.max [ measurement.PeakMiB; firstPeak; secondPeak ]
        if distinctStates > stateBudget || transitions > transitionBudget
           || measurement.ElapsedMs > int64 elapsedBudget || measurement.PeakMiB > peakBudget
           || totalBytes > int64 artifactBudget then
            fail "QUINT-FORMAL-MEASUREMENT-BUDGET" ($"%s{formalId}: states=%d{distinctStates}; transitions=%d{transitions}; elapsedMs=%d{measurement.ElapsedMs}; peakMiB=%d{measurement.PeakMiB}; bytes=%d{totalBytes}")
        formalCounterexampleReceipts.Add(formalId, sha256 manifestPath, traceSha256, itfSha256)
        printfn "QUINT_FORMAL_TEST id=%s backend=tlc temporal=%s removedStep=%s violatedTemporal=%s states=%d transitions=%d samples=%d elapsedMs=%d peakMiB=%d artifactBytes=%d manifestSha256=%s traceSha256=%s itfSha256=%s" formalId temporalName removedStep violatedTemporal distinctStates transitions measurement.SampleCount measurement.ElapsedMs measurement.PeakMiB totalBytes (sha256 manifestPath) traceSha256 itfSha256

    use formalBaselineDocument = JsonDocument.Parse(File.ReadAllBytes qualificationBaseline)
    if formalBaselineDocument.RootElement.GetProperty("formalMeasurementMethod").GetString()
       <> "canonical-runner-observed-tlc-and-rust-v1" then
        fail "QUINT-FORMAL-BASELINE-METHOD" "unsupported"
    let retainedFormalMeasurements =
        formalBaselineDocument.RootElement.GetProperty("formalMeasurements").EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("id").GetString(), item.Clone())
        |> Map.ofSeq
    for KeyValue(formalId, observed) in formalMeasurements do
        if not (Map.containsKey formalId retainedFormalMeasurements) then fail "QUINT-FORMAL-BASELINE-MISSING" formalId
        let retainedMeasurement = retainedFormalMeasurements[formalId]
        let expectedStable =
            retainedMeasurement.GetProperty("stateCount").GetInt32(),
            retainedMeasurement.GetProperty("transitionCount").GetInt32(),
            retainedMeasurement.GetProperty("sampleCount").GetInt32(),
            retainedMeasurement.GetProperty("artifactBytes").GetInt64()
        let observedStable = observed.StateCount, observed.TransitionCount, observed.SampleCount, observed.ArtifactBytes
        if expectedStable <> observedStable && not refreshFormalEvidence then
            fail "QUINT-FORMAL-BASELINE-DRIFT" ($"%s{formalId}: expected=%A{expectedStable}; observed=%A{observedStable}")
        printfn "QUINT_FORMAL_MEASUREMENT id=%s stateCount=%d transitionCount=%d sampleCount=%d elapsedMs=%d peakMiB=%d artifactBytes=%d" formalId observed.StateCount observed.TransitionCount observed.SampleCount observed.ElapsedMs observed.PeakMiB observed.ArtifactBytes

    requireGreen
        "QUINT-POSITIVE-INVARIANTS-VERIFY"
        scratch
        quint
        ([ "verify"
           qnt
           "--main"
           "CoordinationProtocol"
           "--init"
           "init"
           "--step"
           "step"
           "--invariants" ]
         @ positiveInvariants
         @ [ "--max-steps"
             "4"
             "--verbosity"
             "1" ])
        environment
    |> ignore

    verifiedPositiveInvariantCount <- positiveInvariants.Length

    let mutatedQnt = Path.Combine(scratch, "protocol-missing-evidence-guard.qnt")
    let originalQnt = File.ReadAllText q2Qnt
    let rustMutationChecks = ResizeArray<unit -> unit>()

    let enqueueRustMutation code filePrefix testMatch (name: string) (fixture: string) (replacement: string) =
        if not (originalQnt.Contains(fixture, StringComparison.Ordinal)) then
            fail code ($"%s{name}: fixture absent")

        let mutant = Path.Combine(scratch, $"%s{filePrefix}-%s{name}.qnt")
        File.WriteAllText(mutant, originalQnt.Replace(fixture, replacement))

        rustMutationChecks.Add(fun () ->
            let exitCode, output, error =
                run
                    scratch
                    quint
                    [ "test"; mutant; "--main"; "CoordinationProtocolTests"; "--backend"; "rust"
                      "--match"; testMatch; "--verbosity"; "0" ]
                    []

            if exitCode = 0 then
                fail code ($"%s{name}: mutant passed")

            if not ((output + "\n" + error).Contains("failed", StringComparison.OrdinalIgnoreCase)) then
                fail code ($"%s{name}: no failed witness; %s{output}; %s{error}"))

    // Every independent oracle owns a focused mutation of the canonical subject it observes.
    // The mutation is run only against that oracle, so a broad generated test cannot mask a
    // disconnected or self-confirming oracle implementation.
    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleClaimExclusion$"
        "claim-exclusion"
        "    left != right,\n    left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey,"
        "    true,\n    left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleStaleProjection$"
        "lifecycle-claim-status"
        "else if (facts.claimPresent) \"claimed\""
        "else if (facts.claimPresent) \"ready\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleDependencyConcurrency$"
        "dependency-concurrency"
        "if (expectedRevision == observedRevision) \"MOUT-Applied\" else \"MOUT-RevisionConflict\""
        "\"MOUT-Applied\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oraclePartialOperation$"
        "partial-operation"
        "else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-ReceiptReread\""
        "else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-Advance\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleOldClientFencing$"
        "old-client-fencing"
        "versions == supportedDeterministicVersions"
        "true"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleLedgerTamper$"
        "ledger-tamper"
        "pure def retainedProtocolEnvelopeHasPredecessor(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =\n    if (envelope.sequence == 1)"
        "pure def retainedProtocolEnvelopeHasPredecessor(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =\n    if (true)"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleExactHeadReview$"
        "exact-head-review"
        "    manifest.reviewCandidateSha == manifest.candidateSha,"
        "    true,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oraclePostMergeVerification$"
        "post-merge-verification"
        "    manifest.resultCandidateSha == manifest.candidateSha,"
        "    true,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleDualFeedRecovery$"
        "dual-feed-recovery"
        "if (desired.contentDigest == observed.contentDigest) \"DSPLAN-NoChange\" else \"DSPLAN-Ready\""
        "\"DSPLAN-NoChange\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleAbstractionEquivalence$"
        "abstraction-equivalence"
        "    left.behavioralSha256 == right.behavioralSha256,"
        "    true,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleScaleEnvelope$"
        "scale-envelope"
        "{ id: \"BOUND-TraceSteps\", kind: \"bound\", minimum: 0, maximum: 4 }"
        "{ id: \"BOUND-TraceSteps\", kind: \"bound\", minimum: 5, maximum: 4 }"

    let enqueueInvalidParameterRed (rootId: string) (rootModule: string) =
        rustMutationChecks.Add(fun () ->
            let exitCode, output, error =
                run scratch quint
                    [ "run"; q2Qnt; "--main"; rootModule; "--init"; "init"; "--step"; "invalidStep"
                      "--invariant"; "qualificationInvariant"; "--max-steps"; "8"; "--max-samples"; "100"
                      "--seed"; "1"; "--verbosity"; "0" ] []
            if exitCode = 0 then
                fail "ANTI-VACUITY-NEGATIVE-CONTROL" ($"%s{rootId}: invalid parameterization passed")
            if not ((output + "\n" + error).Contains("Invariant", StringComparison.OrdinalIgnoreCase)) then
                fail "ANTI-VACUITY-NEGATIVE-CONTROL" ($"%s{rootId}: no invariant violation; %s{output}; %s{error}"))

    enqueueInvalidParameterRed "authority" "QualificationAuthorityRoot"
    enqueueInvalidParameterRed "lifecycle" "QualificationLifecycleRoot"
    enqueueInvalidParameterRed "relations" "QualificationRelationsRoot"
    enqueueInvalidParameterRed "protocol-streams" "QualificationProtocolStreamsRoot"

    let requireMutationRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation "MUTATION-NEGATIVE-CONTROL" "protocol-mutation" "^testMutation" name fixture replacement

    requireMutationRed
        "idempotency-key-binding"
        "left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey"
        "left.operationId == right.operationId"

    requireMutationRed
        "operation-binding"
        "left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey"
        "left.idempotencyKey == right.idempotencyKey"

    requireMutationRed
        "target-kind-binding"
        "      mutationKind.targetKind == intent.targetKind,\n      mutationKind.payloadKind == intent.payloadKind,"
        "      mutationKind.payloadKind == intent.payloadKind,"

    requireMutationRed
        "payload-kind-binding"
        "      mutationKind.targetKind == intent.targetKind,\n      mutationKind.payloadKind == intent.payloadKind,"
        "      mutationKind.targetKind == intent.targetKind,"

    requireMutationRed
        "remove-edge-payload-classification"
        "targetKind: \"relation\", payloadKind: \"edge\", revisionRequirement: \"exact\" },\n    { id: \"MUT-Set\""
        "targetKind: \"relation\", payloadKind: \"scalar\", revisionRequirement: \"exact\" },\n    { id: \"MUT-Set\""

    requireMutationRed
        "rate-limit-uncertainty-classification"
        "id: \"MOUT-RateLimited\", kind: \"mutationOutcome\", finality: \"uncertain\", effectClass: \"unknown\""
        "id: \"MOUT-RateLimited\", kind: \"mutationOutcome\", finality: \"terminal\", effectClass: \"applied\""

    requireMutationRed
        "exact-replay-idempotent-outcome"
        "        current.outcomeId == \"MOUT-Idempotent\","
        "        current.outcomeId == \"MOUT-Applied\","

    requireMutationRed
        "stale-revision"
        "if (expectedRevision == observedRevision) \"MOUT-Applied\" else \"MOUT-RevisionConflict\""
        "\"MOUT-Applied\""

    requireMutationRed
        "compensation-outcome-binding"
        "    original.outcomeId == \"MOUT-Applied\",\n    original.resultingRevision == intent.expectedRevision,"
        "    original.resultingRevision == intent.expectedRevision,"

    requireMutationRed
        "compensation-predecessor-shape-binding"
        "    mutationIntentShapeIsValid(original.intent),\n    mutationResultOutcomeIsValid(original),"
        "    mutationResultOutcomeIsValid(original),"

    requireMutationRed
        "compensation-uniqueness-binding"
        "      existing != intent,"
        "      existing == intent,"

    let requireDurablePlanRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation "DURABLE-PLAN-NEGATIVE-CONTROL" "protocol-durable-plan" "^testDurablePlan" name fixture replacement

    requireDurablePlanRed
        "predecessor-binding"
        "    current.sequence == previous.sequence + 1, current.predecessorStepId == previous.stepId,"
        "    current.sequence == previous.sequence + 1, true,"

    requireDurablePlanRed
        "causation-binding"
        "    current.causationId == previous.intent.operationId, current.stepId != previous.stepId,"
        "    true, current.stepId != previous.stepId,"

    requireDurablePlanRed
        "correlation-binding"
        "    previous.planId == current.planId, previous.correlationId == current.correlationId,"
        "    previous.planId == current.planId, true,"

    requireDurablePlanRed
        "receipt-intent-binding"
        "    checkpoint.receipt.intent == checkpoint.step.intent, mutationResultOutcomeIsValid(checkpoint.receipt),"
        "    true, mutationResultOutcomeIsValid(checkpoint.receipt),"

    requireDurablePlanRed
        "uncertain-receipt-reread"
        "    else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-ReceiptReread\""
        "    else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-Advance\""

    requireDurablePlanRed
        "compensation-boundary"
        "    compensation.compensationBoundaryId == original.compensationBoundaryId,"
        "    true,"

    requireDurablePlanRed
        "compensation-ordered-follow-relation"
        "    durablePlanStepMayFollow(original, compensation),"
        "    true,"

    requireDurablePlanRed
        "compensation-reverse-order"
        "      applied.sequence <= original.sequence,"
        "      true,"

    requireDurablePlanRed
        "disposition-classification"
        "    if (receipt.outcomeId == \"MOUT-Applied\" or receipt.outcomeId == \"MOUT-Idempotent\") \"PDISP-Advance\""
        "    if (receipt.outcomeId == \"MOUT-Applied\" or receipt.outcomeId == \"MOUT-Idempotent\") \"PDISP-Replan\""

    requireDurablePlanRed
        "disposition-boundary-history"
        "    applied.step.compensationBoundaryId == current.compensationBoundaryId,"
        "    true,"

    let requireDesiredStateRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation "DESIRED-STATE-NEGATIVE-CONTROL" "protocol-desired-state" "^testDesiredState" name fixture replacement

    requireDesiredStateRed
        "family-completeness"
        "    facts.map(fact => fact.familyId) == desiredStateFamilyCatalogue.map(family => family.id),"
        "    true,"

    requireDesiredStateRed
        "subject-binding"
        "    desired.subjectId == observed.subjectId, desired.profileId == observed.profileId,"
        "    true, desired.profileId == observed.profileId,"

    requireDesiredStateRed
        "profile-binding"
        "    desired.subjectId == observed.subjectId, desired.profileId == observed.profileId,"
        "    desired.subjectId == observed.subjectId, true,"

    requireDesiredStateRed
        "surface-binding"
        "      family.requiredPermission == fact.requiredPermission, family.surfaceIds == fact.surfaceIds,"
        "      family.requiredPermission == fact.requiredPermission, true,"

    requireDesiredStateRed
        "unsupported-classification"
        "    else if (not(observed.supported) or observed.outcomeId == \"OBS-Unsupported\") \"DSPLAN-Unsupported\""
        "    else if (observed.outcomeId == \"OBS-Unsupported\") \"DSPLAN-Ready\""

    requireDesiredStateRed
        "permission-classification"
        "    else if (not(observed.permissionGranted) or observed.outcomeId == \"OBS-Unauthorized\") \"DSPLAN-Unauthorized\""
        "    else if (observed.outcomeId == \"OBS-Unauthorized\") \"DSPLAN-Ready\""

    requireDesiredStateRed
        "stale-classification"
        "    else if (observed.outcomeId == \"OBS-Stale\") \"DSPLAN-Stale\""
        "    else if (observed.outcomeId == \"OBS-Stale\") \"DSPLAN-Ready\""

    requireDesiredStateRed
        "policy-content-binding"
        "    desired.contentDigest == observed.contentDigest,\n  }\n\n  pure def desiredStateSpecificationIsComplete"
        "    true,\n  }\n\n  pure def desiredStateSpecificationIsComplete"

    requireDesiredStateRed
        "phase-authorization"
        "      desiredStateMayApply(desired, observed),\n    },\n    and {\n      fromPhaseId == \"DSPH-Plan\", toPhaseId == \"DSPH-Verify\""
        "      true,\n    },\n    and {\n      fromPhaseId == \"DSPH-Plan\", toPhaseId == \"DSPH-Verify\""

    requireDesiredStateRed
        "desired-qualification"
        "    desired.complete, desired.supported, desired.permissionGranted, desired.outcomeId == \"OBS-Observed\","
        "    true,"

    requireDesiredStateRed
        "apply-receipt"
        "      Set(\"MOUT-Applied\", \"MOUT-Idempotent\").contains(authority.applyReceiptOutcomeId),"
        "      true,"

    requireDesiredStateRed
        "phase-authority-binding"
        "      desiredStatePhaseAuthorityMatches(authority, desired, observed),"
        "      true,"

    let requireCompiledOutputRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation "COMPILED-OUTPUT-NEGATIVE-CONTROL" "protocol-compiled-output" "^testCompiledOutput" name fixture replacement

    requireCompiledOutputRed
        "duplicate-family"
        "    outputs.size() == compiledOutputFamilyCatalogue.size(),"
        "    true,"

    requireCompiledOutputRed
        "family-completeness"
        ("    outputs.size() == compiledOutputFamilyCatalogue.size(),\n"
         + "    outputs.map(output => output.familyId) == compiledOutputFamilyCatalogue.map(family => family.id),\n"
         + "    outputs.map(output => output.ordinal) == Set(1, 2, 3, 4, 5, 6, 7, 8, 9),")
        "    true,\n    true,\n    true,"

    requireCompiledOutputRed
        "family-order"
        "      family.id == output.familyId, family.ordinal == output.ordinal,"
        "      family.id == output.familyId, true,"

    requireCompiledOutputRed
        "source-identity"
        "    output.sourceIdentity == \"literate-quint-authority\","
        "    true,"

    requireCompiledOutputRed
        "support-qualification"
        "    output.contentDigest != \"\", output.supported, output.complete, output.fresh,"
        "    output.contentDigest != \"\", true, output.complete, output.fresh,"

    requireCompiledOutputRed
        "completeness-qualification"
        "    output.contentDigest != \"\", output.supported, output.complete, output.fresh,"
        "    output.contentDigest != \"\", output.supported, true, output.fresh,"

    requireCompiledOutputRed
        "freshness-qualification"
        "    output.contentDigest != \"\", output.supported, output.complete, output.fresh,"
        "    output.contentDigest != \"\", output.supported, output.complete, true,"

    requireCompiledOutputRed
        "projection-formats"
        "      family.contentContract == output.contentContract, family.formats == output.formats,"
        "      family.contentContract == output.contentContract, true,"

    if rustMutationChecks.Count <> 56 then
        fail "RUST-MUTATION-INVENTORY" ($"expected=56; actual=%d{rustMutationChecks.Count}")

    let parallelOptions = ParallelOptions(MaxDegreeOfParallelism = 2)
    Parallel.ForEach(rustMutationChecks, parallelOptions, fun check -> check ()) |> ignore

    let guard = "    evidenceObserved,\n    evidenceObserved' = evidenceObserved,"

    if not (originalQnt.Contains(guard, StringComparison.Ordinal)) then
        fail "NEGATIVE-CONTROL" "guard fixture absent"

    File.WriteAllText(mutatedQnt, originalQnt.Replace(guard, "    evidenceObserved' = evidenceObserved,"))

    let counterexample = Path.Combine(scratch, "counterexample.itf.json")

    let redExit, redOutput, redError =
        run
            scratch
            quint
            [ "verify"
              mutatedQnt
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "acceptedVocabularyIsQualified"
              "--max-steps"
              "1"
              "--out-itf"
              counterexample
              "--verbosity"
              "1" ]
            environment

    if redExit = 0 then
        fail "NEGATIVE-CONTROL" "missing evidence guard passed"

    if not (File.Exists counterexample) then
        fail "NEGATIVE-CONTROL" ($"missing ITF; {redOutput}; {redError}")

    let lifecycleMutant = Path.Combine(scratch, "protocol-intent-follows-claim.qnt")

    let preservedIntent =
        "    humanIntentId' = humanIntentId,\n    authorizedHumanIntentId' = authorizedHumanIntentId,\n    lifecycleFacts' = facts,"

    let collapsedIntent =
        "    humanIntentId' = if (facts.claimPresent) \"INTENT-Ready\" else humanIntentId,\n    authorizedHumanIntentId' = authorizedHumanIntentId,\n    lifecycleFacts' = facts,"

    if not (originalQnt.Contains(preservedIntent, StringComparison.Ordinal)) then
        fail "LIFECYCLE-NEGATIVE-CONTROL" "intent-preservation fixture absent"

    File.WriteAllText(lifecycleMutant, originalQnt.Replace(preservedIntent, collapsedIntent))

    let lifecycleCounterexample =
        Path.Combine(scratch, "counterexample-intent-follows-claim.itf.json")

    let lifecycleRedExit, lifecycleRedOutput, lifecycleRedError =
        run
            scratch
            quint
            [ "verify"
              lifecycleMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "humanIntentIsObservationIndependent"
              "--max-steps"
              "1"
              "--out-itf"
              lifecycleCounterexample
              "--verbosity"
              "1" ]
            environment

    if lifecycleRedExit = 0 then
        fail "LIFECYCLE-NEGATIVE-CONTROL" "claim-to-intent collapse passed"

    if not (File.Exists lifecycleCounterexample) then
        fail "LIFECYCLE-NEGATIVE-CONTROL" ($"missing ITF; {lifecycleRedOutput}; {lifecycleRedError}")

    let replacementMutant =
        Path.Combine(scratch, "protocol-relation-whole-set-replacement.qnt")

    let edgeLocalAdd =
        "    nativeRelationEdges' = nativeRelationEdges.union(Set(edge)),"

    let wholeSetReplacement = "    nativeRelationEdges' = Set(edge),"

    if not (originalQnt.Contains(edgeLocalAdd, StringComparison.Ordinal)) then
        fail "RELATION-NEGATIVE-CONTROL" "edge-local add fixture absent"

    File.WriteAllText(replacementMutant, originalQnt.Replace(edgeLocalAdd, wholeSetReplacement))

    let replacementCounterexample =
        Path.Combine(scratch, "counterexample-relation-whole-set-replacement.itf.json")

    let replacementRedExit, replacementRedOutput, replacementRedError =
        run
            scratch
            quint
            [ "verify"
              replacementMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "relationChangesPreserveUnrelatedEdges"
              "--max-steps"
              "2"
              "--out-itf"
              replacementCounterexample
              "--verbosity"
              "1" ]
            environment

    if replacementRedExit = 0 then
        fail "RELATION-NEGATIVE-CONTROL" "whole-set replacement passed"

    if not (File.Exists replacementCounterexample) then
        fail
            "RELATION-NEGATIVE-CONTROL"
            ($"whole-set replacement missing ITF; {replacementRedOutput}; {replacementRedError}")

    let selfEdgeMutant = Path.Combine(scratch, "protocol-relation-self-edge.qnt")
    let validRelationStep = "    addNativeRelation(parentChildEdge),"

    let invalidRelationStep =
        "    addNativeRelation({ ...parentChildEdge, targetId: parentChildEdge.sourceId }),"

    let relationGuard =
        "    nativeRelationEdgeIsValid(edge),\n    evidenceObserved' = evidenceObserved,"

    if not (originalQnt.Contains(validRelationStep, StringComparison.Ordinal)) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "relation step fixture absent"

    if not (originalQnt.Contains(relationGuard, StringComparison.Ordinal)) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "relation guard fixture absent"

    let withoutRelationGuard =
        originalQnt.Replace(relationGuard, "    evidenceObserved' = evidenceObserved,")

    File.WriteAllText(selfEdgeMutant, withoutRelationGuard.Replace(validRelationStep, invalidRelationStep))

    let selfEdgeCounterexample =
        Path.Combine(scratch, "counterexample-relation-self-edge.itf.json")

    let selfEdgeRedExit, selfEdgeRedOutput, selfEdgeRedError =
        run
            scratch
            quint
            [ "verify"
              selfEdgeMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "nativeRelationEdgesAreValid"
              "--max-steps"
              "1"
              "--out-itf"
              selfEdgeCounterexample
              "--verbosity"
              "1" ]
            environment

    if selfEdgeRedExit = 0 then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "self edge passed"

    if not (File.Exists selfEdgeCounterexample) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" ($"self edge missing ITF; {selfEdgeRedOutput}; {selfEdgeRedError}")

    let orderingMutant = Path.Combine(scratch, "protocol-stream-ordering-gap.qnt")
    let orderedAppendGuard = "      protocolAppendHasPredecessor(envelope, events),"
    let gapStep = "    appendProtocolEnvelope(leaseEnvelope),"

    let invalidGapStep =
        "    appendProtocolEnvelope({ ...leaseEnvelope, sequence: 3 }),"

    if not (originalQnt.Contains(orderedAppendGuard, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL" "append ordering guard absent"

    if not (originalQnt.Contains(gapStep, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL" "gap step fixture absent"

    File.WriteAllText(
        orderingMutant,
        originalQnt.Replace(orderedAppendGuard, "      true,").Replace(gapStep, invalidGapStep)
    )

    let orderingCounterexample =
        Path.Combine(scratch, "counterexample-protocol-stream-ordering-gap.itf.json")

    let orderingRedExit, orderingRedOutput, orderingRedError =
        run
            scratch
            quint
            [ "verify"
              orderingMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "protocolEnvelopesAreValidAndOrdered"
              "--max-steps"
              "2"
              "--out-itf"
              orderingCounterexample
              "--verbosity"
              "1" ]
            environment

    if orderingRedExit = 0 then
        fail "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL" "ordering gap passed"

    if not (File.Exists orderingCounterexample) then
        fail
            "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL"
            ($"ordering gap missing ITF; {orderingRedOutput}; {orderingRedError}")

    let retentionMutant = Path.Combine(scratch, "protocol-stream-retention-relabel.qnt")

    let appendAdmissionGuard =
        "    protocolAppendIsValid(envelope, protocolStreamEvents),"

    let validClaimRetention =
        "    payloadKindId: \"PAYLOAD-Claim\", retentionClass: \"ephemeral\", durableCheckpoint: false,"

    let relabeledClaimRetention =
        "    payloadKindId: \"PAYLOAD-Claim\", retentionClass: \"durable\", durableCheckpoint: true,"

    if not (originalQnt.Contains(appendAdmissionGuard, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL" "append admission guard absent"

    if not (originalQnt.Contains(validClaimRetention, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL" "claim retention fixture absent"

    File.WriteAllText(
        retentionMutant,
        originalQnt.Replace(appendAdmissionGuard, "    true,").Replace(validClaimRetention, relabeledClaimRetention)
    )

    let retentionCounterexample =
        Path.Combine(scratch, "counterexample-protocol-stream-retention-relabel.itf.json")

    let retentionRedExit, retentionRedOutput, retentionRedError =
        run
            scratch
            quint
            [ "verify"
              retentionMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "protocolEnvelopesAreValidAndOrdered"
              "--max-steps"
              "1"
              "--out-itf"
              retentionCounterexample
              "--verbosity"
              "1" ]
            environment

    if retentionRedExit = 0 then
        fail "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL" "retention relabel passed"

    if not (File.Exists retentionCounterexample) then
        fail
            "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL"
            ($"retention relabel missing ITF; {retentionRedOutput}; {retentionRedError}")

    let checkpointMutant =
        Path.Combine(scratch, "protocol-stream-durable-compaction.qnt")

    let compactionGuard =
        "    ephemeralEnvelopeMayBeCompacted(envelope, protocolStreamEvents),"

    let compactEphemeralStep =
        "    compactEphemeralProtocolEnvelope(operationLockEnvelope),"

    let compactDurableStep =
        "    compactEphemeralProtocolEnvelope(reviewCheckpointEnvelope),"

    if not (originalQnt.Contains(compactionGuard, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-CHECKPOINT-NEGATIVE-CONTROL" "compaction guard absent"

    File.WriteAllText(
        checkpointMutant,
        originalQnt.Replace(compactionGuard, "    true,").Replace(compactEphemeralStep, compactDurableStep)
    )

    let checkpointCounterexample =
        Path.Combine(scratch, "counterexample-protocol-stream-durable-compaction.itf.json")

    let checkpointRedExit, checkpointRedOutput, checkpointRedError =
        run
            scratch
            quint
            [ "verify"
              checkpointMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "durableProtocolCheckpointsArePreserved"
              "--max-steps"
              "2"
              "--out-itf"
              checkpointCounterexample
              "--verbosity"
              "1" ]
            environment

    if checkpointRedExit = 0 then
        fail "PROTOCOL-STREAM-CHECKPOINT-NEGATIVE-CONTROL" "durable checkpoint compaction passed"

    if not (File.Exists checkpointCounterexample) then
        fail
            "PROTOCOL-STREAM-CHECKPOINT-NEGATIVE-CONTROL"
            ($"durable checkpoint compaction missing ITF; {checkpointRedOutput}; {checkpointRedError}")

    let unrelatedCheckpointMutant =
        Path.Combine(scratch, "protocol-stream-unrelated-checkpoint.qnt")

    let streamBoundCompaction =
        "      checkpoint.streamKindId == envelope.streamKindId,\n"
        + "      checkpoint.streamId == envelope.streamId,\n"
        + "      checkpoint.subjectId == envelope.subjectId,\n"
        + "      checkpoint.generation == envelope.generation,\n"
        + "      checkpoint.sequence > envelope.sequence,\n"
        + "      checkpoint.durableCheckpoint,\n"
        + "      checkpoint.retentionClass == \"durable\","

    let subjectOnlyCompaction =
        "      checkpoint.subjectId == envelope.subjectId,\n"
        + "      checkpoint.durableCheckpoint,\n"
        + "      checkpoint.retentionClass == \"durable\","

    if not (originalQnt.Contains(streamBoundCompaction, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-CAUSAL-COMPACTION-NEGATIVE-CONTROL" "stream-bound compaction fixture absent"

    File.WriteAllText(
        unrelatedCheckpointMutant,
        originalQnt.Replace(streamBoundCompaction, subjectOnlyCompaction)
    )

    let unrelatedRedExit, unrelatedRedOutput, unrelatedRedError =
        run
            scratch
            quint
            [ "test"
              unrelatedCheckpointMutant
              "--main"
              "CoordinationProtocolTests"
              "--backend"
              "rust"
              "--match"
              "^testUnrelatedCheckpointCannotCompactEphemeralHistory$"
              "--verbosity"
              "0" ]
            []

    if unrelatedRedExit = 0 then
        fail "PROTOCOL-STREAM-CAUSAL-COMPACTION-NEGATIVE-CONTROL" "unrelated checkpoint authorized compaction"

    if
        not (
            (unrelatedRedOutput + "\n" + unrelatedRedError)
                .Contains("failed", StringComparison.OrdinalIgnoreCase)
        )
    then
        fail
            "PROTOCOL-STREAM-CAUSAL-COMPACTION-NEGATIVE-CONTROL"
            ($"unrelated checkpoint mutant did not produce a failed test; {unrelatedRedOutput}; {unrelatedRedError}")

    let unrelatedPredecessorMutant =
        Path.Combine(scratch, "protocol-stream-unrelated-predecessor.qnt")

    let streamBoundPredecessor =
        "        checkpoint.streamKindId == envelope.streamKindId,\n"
        + "        checkpoint.streamId == envelope.streamId,\n"
        + "        checkpoint.subjectId == envelope.subjectId,\n"
        + "        checkpoint.generation == envelope.generation,\n"
        + "        checkpoint.sequence > envelope.sequence,\n"
        + "        checkpoint.durableCheckpoint,"

    let subjectOnlyPredecessor =
        "        checkpoint.subjectId == envelope.subjectId,\n"
        + "        checkpoint.durableCheckpoint,"

    if not (originalQnt.Contains(streamBoundPredecessor, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-RETAINED-ORDERING-NEGATIVE-CONTROL" "stream-bound predecessor fixture absent"

    File.WriteAllText(
        unrelatedPredecessorMutant,
        originalQnt.Replace(streamBoundPredecessor, subjectOnlyPredecessor)
    )

    let predecessorRedExit, predecessorRedOutput, predecessorRedError =
        run
            scratch
            quint
            [ "test"
              unrelatedPredecessorMutant
              "--main"
              "CoordinationProtocolTests"
              "--backend"
              "rust"
              "--match"
              "^testUnrelatedCheckpointCannotExcuseMissingPredecessor$"
              "--verbosity"
              "0" ]
            []

    if predecessorRedExit = 0 then
        fail "PROTOCOL-STREAM-RETAINED-ORDERING-NEGATIVE-CONTROL" "unrelated checkpoint excused a missing predecessor"

    if
        not (
            (predecessorRedOutput + "\n" + predecessorRedError)
                .Contains("failed", StringComparison.OrdinalIgnoreCase)
        )
    then
        fail
            "PROTOCOL-STREAM-RETAINED-ORDERING-NEGATIVE-CONTROL"
            ($"unrelated predecessor mutant did not produce a failed test; {predecessorRedOutput}; {predecessorRedError}")

    let requireAuthorityRed name (observationMutation: string -> string) (sourceMutation: string -> string) =
        let mutated = Path.Combine(scratch, $"protocol-%s{name}.qnt")

        let withoutQualificationGuard =
            originalQnt.Replace(
                "    authorityObservationIsQualified(authorityObservation),\n    evidenceObserved' = evidenceObserved,",
                "    evidenceObserved' = evidenceObserved,"
            )

        if withoutQualificationGuard = originalQnt then
            fail "AUTHORITY-NEGATIVE-CONTROL" "qualification guard fixture absent"

        let changedObservation = observationMutation withoutQualificationGuard
        let changedSource = sourceMutation changedObservation
        File.WriteAllText(mutated, changedSource)
        let outItf = Path.Combine(scratch, $"counterexample-%s{name}.itf.json")

        let exitCode, output, error =
            run
                scratch
                quint
                [ "verify"
                  mutated
                  "--main"
                  "CoordinationProtocol"
                  "--init"
                  "init"
                  "--step"
                  "step"
                  "--invariant"
                  "acceptedAuthoritiesAreQualified"
                  "--max-steps"
                  "2"
                  "--out-itf"
                  outItf
                  "--verbosity"
                  "1" ]
                environment

        if exitCode = 0 then
            fail "AUTHORITY-NEGATIVE-CONTROL" ($"%s{name} passed")

        if not (File.Exists outItf) then
            fail "AUTHORITY-NEGATIVE-CONTROL" ($"%s{name} missing ITF; %s{output}; %s{error}")

    let mutateStep replacement (text: string) =
        text.Replace("    observeAuthority(nativeGitHubObservation),", $"    observeAuthority(%s{replacement}),")

    let unchanged (text: string) = text
    requireAuthorityRed "incomplete" (mutateStep "{ ...nativeGitHubObservation, complete: false }") unchanged

    requireAuthorityRed
        "stale-revision"
        (mutateStep "{ ...nativeGitHubObservation, revisionValue: \"stale-revision\" }")
        unchanged

    requireAuthorityRed
        "wrong-revision-kind"
        (mutateStep "{ ...nativeGitHubObservation, revisionKind: \"wrong-kind\" }")
        unchanged

    requireAuthorityRed
        "wrong-authority"
        (mutateStep "{ ...nativeGitHubObservation, authorityId: \"AUTH-PackageFeed\" }")
        unchanged

    requireAuthorityRed "contradictory" (mutateStep "{ ...nativeGitHubObservation, contradictory: true }") unchanged

    let omittedFamilyRow =
        "    { id: \"AUTH-ClassifiedExternal\", kind: \"authorityBinding\", family: \"classified-external\", revisionKind: \"classified-external-revision\", revisionValue: \"declared-source-revision\", completenessContract: \"complete-required-fields\", evidenceRelationship: \"REL-AUTH-ClassifiedExternal-Evidence\" }"

    requireAuthorityRed "omitted-family" (mutateStep "nativeGitHubObservation") (fun text ->
        text.Replace(omittedFamilyRow, ""))

    if verifiedPositiveInvariantCount <> 8 then
        fail "POSITIVE-INVARIANT-COVERAGE" ($"expected=8; actual=%d{verifiedPositiveInvariantCount}")

    if quintRejectedProcessCount <> expectedRejectedProcessCount then
        fail "NEGATIVE-CONTROL-COVERAGE" ($"expected=%d{expectedRejectedProcessCount}; actual=%d{quintRejectedProcessCount}")

    requireCompletedProcessInventory ()

    q2Outcome <- "passed"
    writeQualificationReceipt None
    let totalDurationMs = qualificationClock.ElapsedMilliseconds

    printfn
        "CANONICAL_QUINT_PROTOCOL_OK contract=%s source=%s profile=%s receipt=%s durationMs=%d"
        expectedContract
        expectedSource
        expectedProfile
        qualificationOutput
        totalDurationMs
finally
    if Directory.Exists scratch then
        Directory.Delete(scratch, true)
