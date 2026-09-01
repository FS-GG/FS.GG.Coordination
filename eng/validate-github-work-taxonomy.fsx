#load "../src/FS.GG.Coordination.Core/WorkTaxonomy.fs"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.Core

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args[0]
let atRoot path = Path.Combine(root, path)
let corpusPath = atRoot "evidence/github-substrate-v2/gs2-05-1/corpus.json"
let expectationsPath = atRoot "evidence/github-substrate-v2/gs2-05-1/independent-expectations.json"
let modelPath = atRoot "evidence/github-substrate-v2/gs2-05-1/workTaxonomy.qnt"
let testModelPath = atRoot "evidence/github-substrate-v2/gs2-05-1/workTaxonomy_test.qnt"
let qualificationPath = atRoot "evidence/github-substrate-v2/gs2-05-1/qualification.json"

let sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let readBound path expected code =
    if not (File.Exists path) then fail $"{code}-MISSING" path
    let bytes = File.ReadAllBytes path
    let actual = sha256 bytes
    if actual <> expected then fail $"{code}-DIGEST" $"expected={expected} actual={actual}"
    bytes

let corpusBytes = readBound corpusPath "c89d6acbbb8b47ad928eb3ed29e8044d2e33ee66d165dd5d6243c8e42d597ae8" "WTX-CORPUS"
let expectationBytes = readBound expectationsPath "0ead0c765ec062ed3f30bd7ddb43287176b3614e83eda5cb8410d2a95a5fea37" "WTX-EXPECTATIONS"
let corpusDocument = JsonDocument.Parse corpusBytes
let expectationDocument = JsonDocument.Parse expectationBytes
let corpus = corpusDocument.RootElement
let expectations = expectationDocument.RootElement
if corpus.GetProperty("schema").GetString() <> "fsgg.github-substrate-v2.work-taxonomy-corpus/1" then fail "WTX-CORPUS-SCHEMA" corpusPath
if expectations.GetProperty("schema").GetString() <> "fsgg.github-substrate-v2.work-taxonomy-expectations/1" then fail "WTX-EXPECTATIONS-SCHEMA" expectationsPath

let strings (value: JsonElement) =
    value.EnumerateArray()
    |> Seq.map (fun item -> if item.ValueKind = JsonValueKind.Null then "<null>" else item.GetString())
    |> Seq.toList

let expectedClassInventory = [ "<null>"; "capability"; "hardening"; "defect"; "decision" ]
let expectedKindInventory = [ "<null>"; "work"; "anchor"; "register"; "directive" ]
let expectedNativeInventory = [ "Epic"; "Feature"; "Task"; "Bug"; "Decision"; "Register"; "Directive" ]
if strings (expectations.GetProperty("requiredLegacyClasses")) <> expectedClassInventory then fail "WTX-EXPECTATIONS-CLASS" "independent class inventory drifted"
if strings (expectations.GetProperty("requiredLegacyKinds")) <> expectedKindInventory then fail "WTX-EXPECTATIONS-KIND" "independent kind inventory drifted"
if strings (expectations.GetProperty("requiredNativeTypes")) <> expectedNativeInventory then fail "WTX-EXPECTATIONS-NATIVE" "independent native inventory drifted"
let expectedMapping =
    [ "kind:anchor", "Epic", "work"
      "class:capability", "Feature", "work"
      "class:hardening", "Task", "work"
      "class:defect", "Bug", "work"
      "class:decision", "Decision", "work"
      "kind:register", "Register", "standing-exempt"
      "kind:directive", "Directive", "standing-exempt" ]
let actualMapping =
    expectations.GetProperty("mapping").EnumerateArray()
    |> Seq.map (fun item -> item.GetProperty("signal").GetString(), item.GetProperty("target").GetString(), item.GetProperty("lifecycle").GetString())
    |> Seq.toList
if actualMapping <> expectedMapping then fail "WTX-EXPECTATIONS-MAPPING" $"actual={actualMapping}"

let optionalString (item: JsonElement) (name: string) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if not (item.TryGetProperty(name, &value)) || value.ValueKind = JsonValueKind.Null then None
    elif value.ValueKind = JsonValueKind.String then Some(value.GetString())
    else fail "WTX-CORPUS-TYPE" name

let requiredString (item: JsonElement) (name: string) =
    optionalString item name |> Option.defaultWith (fun () -> fail "WTX-CORPUS-FIELD" name)

let expectedLegacyIds =
    let token = Option.defaultValue "none"
    [ None; Some "capability"; Some "hardening"; Some "defect"; Some "decision" ]
    |> List.collect (fun legacyClass ->
        [ None; Some "work"; Some "anchor"; Some "register"; Some "directive" ]
        |> List.map (fun legacyKind -> $"legacy-{token legacyClass}-{token legacyKind}"))

let expectedNativeIds =
    [ "epic"; "feature"; "task"; "bug"; "decision"; "register"; "directive" ]
    |> List.map (sprintf "native-%s")

let requiredIds = expectedLegacyIds @ expectedNativeIds
let cases = corpus.GetProperty("cases").EnumerateArray() |> Seq.toList
let actualIds = cases |> List.map (fun item -> requiredString item "id")
if actualIds <> requiredIds then
    let requiredText = String.concat "," requiredIds
    let actualText = String.concat "," actualIds
    fail "WTX-CORPUS-TOTALITY" $"required={requiredText} actual={actualText}"
if (actualIds |> Set.ofList |> Set.count) <> actualIds.Length then fail "WTX-CORPUS-DUPLICATE" "case ids are not unique"

// Independent omission oracle: it derives the required cross product without consulting corpus rows.
let omissionCandidate = actualIds |> List.tail
if Set.ofList omissionCandidate = Set.ofList requiredIds then fail "WTX-OMISSION-INVERSION" "removing one case stayed green"

let observation (item: JsonElement) =
    let id = requiredString item "id"
    { StableRowId = id
      RepositoryScope = "FS-GG/frozen-corpus"
      Revision = $"frozen:{id}"
      NativeIssueType = optionalString item "nativeType"
      LegacyClass = optionalString item "class"
      LegacyKind = optionalString item "kind"
      HierarchyPresent = optionalString item "kind" = Some "anchor"
      HierarchyPreservable = true
      RepositoryScopePreservable = true
      Complete = true
      Current = true
      Readable = true }

let mutable accepted = []
let mutable refused = 0
for item in cases do
    let id = requiredString item "id"
    let input = observation item
    let expectedTarget = optionalString item "expectedTarget"
    let expectedDiagnostic = optionalString item "expectedDiagnostic"
    match WorkTaxonomy.classify input, expectedTarget, expectedDiagnostic with
    | Ok classification, Some target, None when WorkTaxonomy.nativeIssueTypeName classification.TargetType = target ->
        accepted <- input :: accepted
    | Error diagnostics, None, Some code when diagnostics |> List.map WorkTaxonomy.diagnosticCode |> List.contains code ->
        refused <- refused + 1
    | actual, _, _ -> fail "WTX-CORPUS-EXPECTATION" $"case={id} actual={actual}"

match WorkTaxonomy.plan accepted with
| Error failures -> fail "WTX-PLAN" $"accepted corpus refused: {failures}"
| Ok dispositions ->
    let stableIds = dispositions |> List.map _.StableRowId
    if stableIds <> List.sort stableIds then fail "WTX-PLAN-ORDER" "dispositions are not canonical"
    if (dispositions |> List.map _.PrestateFingerprint |> Set.ofList |> Set.count) <> dispositions.Length then
        fail "WTX-PRESTATE-FINGERPRINT" "prestate fingerprints are not unique"
    let reversed = accepted |> List.rev |> WorkTaxonomy.plan |> Result.defaultWith (fail "WTX-PLAN-SHUFFLE" << string)
    if WorkTaxonomy.canonicalPlanBytes dispositions <> WorkTaxonomy.canonicalPlanBytes reversed then
        fail "WTX-PLAN-BYTES" "input order changed canonical plan bytes"
    if dispositions |> List.exists (fun disposition ->
        disposition.StableRowId.StartsWith("native-", StringComparison.Ordinal)
        && not disposition.NoOp) then fail "WTX-NOOP" "already-native case was not a no-op"

let baseObservation =
    { StableRowId = "independent"
      RepositoryScope = "FS-GG/independent"
      Revision = "independent-revision"
      NativeIssueType = None
      LegacyClass = Some "hardening"
      LegacyKind = None
      HierarchyPresent = false
      HierarchyPreservable = true
      RepositoryScopePreservable = true
      Complete = true
      Current = true
      Readable = true }

let independentRefusals =
    [ { baseObservation with Readable = false }, "WTX-UNREADABLE"
      { baseObservation with Complete = false }, "WTX-INCOMPLETE"
      { baseObservation with Current = false }, "WTX-STALE"
      { baseObservation with StableRowId = "" }, "WTX-MISSING-STABLE-ID"
      { baseObservation with RepositoryScope = "" }, "WTX-MISSING-REPOSITORY-SCOPE"
      { baseObservation with Revision = "" }, "WTX-MISSING-REVISION"
      { baseObservation with LegacyClass = None }, "WTX-MISSING-CLASSIFICATION"
      { baseObservation with LegacyClass = Some "unknown" }, "WTX-UNKNOWN-CLASS:unknown"
      { baseObservation with LegacyKind = Some "unknown" }, "WTX-UNKNOWN-KIND:unknown"
      { baseObservation with NativeIssueType = Some "Incident"; LegacyClass = None }, "WTX-UNSUPPORTED-NATIVE:incident"
      { baseObservation with NativeIssueType = Some "Feature"; LegacyClass = Some "defect" }, "WTX-CONTRADICTORY"
      { baseObservation with LegacyClass = Some "defect"; LegacyKind = Some "anchor" }, "WTX-AMBIGUOUS"
      { baseObservation with HierarchyPresent = true; HierarchyPreservable = false }, "WTX-LOSSY-HIERARCHY"
      { baseObservation with RepositoryScopePreservable = false }, "WTX-LOSSY-REPOSITORY-SCOPE" ]

let requiredRefusalFamilies = strings (expectations.GetProperty("requiredRefusalCodes"))
let observedRefusalCodes = (independentRefusals |> List.map snd) @ [ "WTX-DUPLICATE-STABLE-ID" ]
for family in requiredRefusalFamilies do
    if observedRefusalCodes |> List.exists (_.StartsWith(family, StringComparison.Ordinal)) |> not then
        fail "WTX-EXPECTATIONS-REFUSAL" family

for item, expected in independentRefusals do
    match WorkTaxonomy.classify item with
    | Error diagnostics when diagnostics |> List.map WorkTaxonomy.diagnosticCode |> List.contains expected -> ()
    | actual -> fail "WTX-INDEPENDENT-REFUSAL" $"expected={expected} actual={actual}"

match WorkTaxonomy.plan [ baseObservation; baseObservation ] with
| Error failures when failures |> List.forall (fun refusal -> refusal.Diagnostics |> List.contains WorkTaxonomyDiagnostic.DuplicateStableRowId) -> ()
| actual -> fail "WTX-INDEPENDENT-DUPLICATE" $"actual={actual}"

let run executable arguments code =
    let start = ProcessStartInfo(executable)
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    arguments |> List.iter start.ArgumentList.Add
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then fail code $"exit={child.ExitCode}\n{output}\n{error}"
    output

run "quint" [ "typecheck"; modelPath ] "WTX-QUINT-TYPECHECK" |> ignore
run "quint" [ "typecheck"; testModelPath ] "WTX-QUINT-TEST-TYPECHECK" |> ignore
run "quint" [ "test"; testModelPath; "--main=workTaxonomy_test" ] "WTX-QUINT-TEST" |> ignore
let simulation =
    run "quint"
        [ "run"; modelPath; "--main=workTaxonomy"
          "--invariants"; "soleNativeAuthority"; "refusedHasNoDisposition"; "uniqueDisposition"; "preservation"; "standingExemptionExact"; "deterministicOutcome"
          "--witnesses"; "plannedWitness"; "refusedWitness"; "--max-steps=2"; "--max-samples=200"; "--seed=0x8c07c89db6e926ff" ]
        "WTX-QUINT-RUN"
if not (simulation.Contains("plannedWitness was witnessed", StringComparison.Ordinal)
        && simulation.Contains("refusedWitness was witnessed", StringComparison.Ordinal)) then
    fail "WTX-QUINT-WITNESS" simulation

if not (File.Exists qualificationPath) then fail "WTX-QUALIFICATION-MISSING" qualificationPath
let qualificationDocument = JsonDocument.Parse(File.ReadAllBytes qualificationPath)
let qualification = qualificationDocument.RootElement
if qualification.GetProperty("schema").GetString() <> "fsgg.github-substrate-v2.work-taxonomy-qualification/1" then fail "WTX-QUALIFICATION-SCHEMA" qualificationPath
if qualification.GetProperty("unitContractSha256").GetString() <> "ed9ae9d198d6eaaf89030f85d214a0a359333598be7ceb3597c2c4aeb629ef28" then fail "WTX-QUALIFICATION-CONTRACT" "registration drift"
if qualification.GetProperty("gateCommandSha256").GetString() <> "c10ddf0ee6bb9328e09fceae4d0deebcc688478c1b2ae80c3fc3b4fbc766ef7f" then fail "WTX-QUALIFICATION-COMMAND" "registered command drift"
let expectedArtifactPaths =
    [ "src/FS.GG.Coordination.Core/WorkTaxonomy.fsi"
      "src/FS.GG.Coordination.Core/WorkTaxonomy.fs"
      "evidence/github-substrate-v2/gs2-05-1/corpus.json"
      "evidence/github-substrate-v2/gs2-05-1/independent-expectations.json"
      "evidence/github-substrate-v2/gs2-05-1/workTaxonomy.qnt"
      "evidence/github-substrate-v2/gs2-05-1/workTaxonomy_test.qnt"
      "eng/validate-github-work-taxonomy.fsx" ]
let artifactRows = qualification.GetProperty("artifacts").EnumerateArray() |> Seq.toList
let artifactPaths = artifactRows |> List.map (fun item -> item.GetProperty("path").GetString())
if artifactPaths <> expectedArtifactPaths then fail "WTX-QUALIFICATION-ARTIFACTS" $"actual={artifactPaths}"
for artifact in artifactRows do
    let relative = artifact.GetProperty("path").GetString()
    let expected = artifact.GetProperty("sha256").GetString()
    let actual = File.ReadAllBytes(atRoot relative) |> sha256
    if actual <> expected then fail "WTX-QUALIFICATION-DIGEST" $"path={relative} expected={expected} actual={actual}"

printfn "github-work-taxonomy-contract OK cases=%d accepted=%d refused=%d q=Q2 network=offline inversions=%d" cases.Length accepted.Length refused (independentRefusals.Length + 2)
