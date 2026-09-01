#load "../src/FS.GG.Coordination.Core/OrganizationIssueFields.fs"

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
let corpusPath = atRoot "evidence/github-substrate-v2/gs2-05-2/corpus.json"
let expectationsPath = atRoot "evidence/github-substrate-v2/gs2-05-2/independent-expectations.json"
let modelPath = atRoot "evidence/github-substrate-v2/gs2-05-2/organizationIssueFields.quint"
let testModelPath = atRoot "evidence/github-substrate-v2/gs2-05-2/organizationIssueFields_test.quint"
let qualificationPath = atRoot "evidence/github-substrate-v2/gs2-05-2/qualification.json"

let sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let readBound path expected code =
    if not (File.Exists path) then fail $"{code}-MISSING" path
    let bytes = File.ReadAllBytes path
    let actual = sha256 bytes
    if actual <> expected then fail $"{code}-DIGEST" $"expected={expected} actual={actual}"
    bytes

let corpusBytes = readBound corpusPath "4cf18d3c38f92d58b52bd6244c322555695b1b08cef4c79a2678d0cb529c6a0b" "OIF-CORPUS"
let expectationBytes = readBound expectationsPath "6fd672f33f0bd117be8a217a0e70675edce0f192f681ce4678c4b6c2a0e6e2c4" "OIF-EXPECTATIONS"
let corpusDocument = JsonDocument.Parse corpusBytes
let expectationDocument = JsonDocument.Parse expectationBytes
let corpus = corpusDocument.RootElement
let expectations = expectationDocument.RootElement
if corpus.GetProperty("schema").GetString() <> "fsgg.github-substrate-v2.organization-issue-fields-corpus/1" then fail "OIF-CORPUS-SCHEMA" corpusPath
if expectations.GetProperty("schema").GetString() <> "fsgg.github-substrate-v2.organization-issue-fields-expectations/1" then fail "OIF-EXPECTATIONS-SCHEMA" expectationsPath

let strings (value: JsonElement) = value.EnumerateArray() |> Seq.map (_.GetString()) |> Seq.toList
let requiredString (item: JsonElement) (name: string) =
    let value = item.GetProperty(name)
    if value.ValueKind <> JsonValueKind.String then fail "OIF-CORPUS-FIELD" name
    value.GetString()
let optionalString (item: JsonElement) (name: string) =
    let value = item.GetProperty(name)
    if value.ValueKind = JsonValueKind.Null then None
    elif value.ValueKind = JsonValueKind.String then Some(value.GetString())
    else fail "OIF-CORPUS-TYPE" name

let expectedFields = [ "Blocked by"; "Class"; "Contract"; "Effort"; "Kind"; "Phase"; "Repo Scope"; "Severity"; "Start"; "Status"; "Target"; "Workstream" ]
if strings (expectations.GetProperty("requiredFields")) <> expectedFields then fail "OIF-EXPECTATIONS-FIELDS" "independent inventory drifted"
let expectedDefinitions =
    [ "Blocked by", "TEXT", []
      "Class", "SINGLE_SELECT", [ "decision"; "defect"; "hardening" ]
      "Contract", "TEXT", []
      "Effort", "SINGLE_SELECT", [ "L"; "M"; "S"; "XL" ]
      "Kind", "SINGLE_SELECT", [ "anchor"; "directive"; "register"; "work" ]
      "Phase", "SINGLE_SELECT", [ "P0 Decisions"; "P1 Rendering"; "P2 SDD"; "P3 Governance"; "P4 Templates"; "P5 Versioning"; "P6 Game"; "P7 Audio"; "P8 Net" ]
      "Repo Scope", "SINGLE_SELECT", [ ".github"; "audio"; "coordination"; "cross-repo"; "game"; "governance"; "net"; "rendering"; "sdd"; "sir"; "templates" ]
      "Severity", "SINGLE_SELECT", [ "Critical"; "High"; "Low"; "Medium"; "Unset" ]
      "Start", "DATE", []
      "Status", "SINGLE_SELECT", [ "Backlog"; "Blocked"; "Done"; "In progress"; "In review"; "Ready" ]
      "Target", "DATE", []
      "Workstream", "SINGLE_SELECT", [ "Composition"; "Coordination"; "Docs"; "Governance"; "Lifecycle"; "Versioning" ] ]
let definitions =
    corpus.GetProperty("fieldDefinitions").EnumerateArray()
    |> Seq.map (fun item -> requiredString item "name", requiredString item "type", strings (item.GetProperty("options")))
    |> Seq.toList
if definitions <> expectedDefinitions then fail "OIF-CORPUS-FIELDS" $"actual={definitions}"
let fieldNames = definitions |> List.map (fun (name, _, _) -> name)
if fieldNames <> expectedFields || Set.count (Set.ofList fieldNames) <> expectedFields.Length then fail "OIF-CORPUS-TOTALITY" "field definitions are missing, duplicated, or reordered"
if Set.ofList (List.tail fieldNames) = Set.ofList expectedFields then fail "OIF-FIELD-OMISSION-INVERSION" "removing one field stayed green"

let expectedVocabularies =
    [ "schedulingIntents", [ "Backlog"; "Ready"; "Paused"; "Cancelled" ]
      "holdReasons", [ "not-yet-actionable"; "dependency"; "decision"; "external"; "operator" ]
      "priorities", [ "Critical"; "High"; "Normal"; "Low" ]
      "efforts", [ "S"; "M"; "L"; "XL" ]
      "severities", [ "Critical"; "High"; "Medium"; "Low"; "Unset" ]
      "phases", [ "Planning"; "Execution"; "Verification"; "Operations" ]
      "workstreams", [ "Composition"; "Coordination"; "Docs"; "Governance"; "Lifecycle"; "Versioning" ] ]
for property, expected in expectedVocabularies do
    if strings (expectations.GetProperty(property)) <> expected then fail "OIF-EXPECTATIONS-VOCABULARY" property

let cases = corpus.GetProperty("cases").EnumerateArray() |> Seq.toList
let requiredCaseIds = [ "intent-backlog"; "intent-ready"; "intent-paused"; "intent-cancelled" ]
let caseIds = cases |> List.map (fun item -> requiredString item "id")
if caseIds <> requiredCaseIds || Set.count (Set.ofList caseIds) <> requiredCaseIds.Length then fail "OIF-CORPUS-CASES" $"actual={caseIds}"
if Set.ofList (List.tail caseIds) = Set.ofList requiredCaseIds then fail "OIF-CASE-OMISSION-INVERSION" "removing one case stayed green"

let observation (item: JsonElement) =
    { StableRowId = requiredString item "id"; Revision = "frozen:2026-09-01"; RepositoryScope = "FS-GG/frozen-corpus"; NativeIssueType = "Task"
      SchedulingIntent = Some(requiredString item "intent"); LifecycleStatus = Some(requiredString item "status"); HoldReason = optionalString item "hold"
      Priority = Some(requiredString item "priority"); Effort = Some(requiredString item "effort"); StartDate = Some "2026-09-01"; TargetDate = Some "2026-09-02"
      Severity = Some(requiredString item "severity"); Phase = Some(requiredString item "phase"); Workstream = Some(requiredString item "workstream")
      ContractReference = None; ContractAuthorityDigest = None; TouchSet = []; TouchSetAuthorityDigest = None
      HierarchyPresent = false; HierarchyPreservable = true; Dependencies = []; DependenciesPreservable = true; RepositoryScopePreservable = true; LifecycleExempt = false
      Complete = true; Current = true; Readable = true }

let observations = cases |> List.map observation
for item in observations do
    match OrganizationIssueFields.validate item with
    | Ok fields ->
        if OrganizationIssueFields.lifecycleStatusName fields.LifecycleStatus <> item.LifecycleStatus.Value then fail "OIF-STATUS-DERIVATION" item.StableRowId
    | Error diagnostics -> fail "OIF-CORPUS-EXPECTATION" $"case={item.StableRowId} diagnostics={diagnostics}"
match OrganizationIssueFields.plan observations with
| Error refusals -> fail "OIF-PLAN" (string refusals)
| Ok dispositions ->
    if dispositions |> List.map _.StableRowId <> List.sort caseIds then fail "OIF-PLAN-ORDER" "noncanonical order"
    if dispositions |> List.map _.PrestateFingerprint |> Set.ofList |> Set.count <> dispositions.Length then fail "OIF-PRESTATE-FINGERPRINT" "not unique"
    let reversed = observations |> List.rev |> OrganizationIssueFields.plan |> Result.defaultWith (fail "OIF-PLAN-SHUFFLE" << string)
    if OrganizationIssueFields.canonicalPlanBytes dispositions <> OrganizationIssueFields.canonicalPlanBytes reversed then fail "OIF-PLAN-BYTES" "input order changed bytes"
    if dispositions |> List.exists (fun item -> not item.NoOp) then fail "OIF-NOOP" "canonical corpus was not stable"

let baseObservation = observations.Head
let digest = String.replicate 64 "a"
let touchSet = [ "eng/**"; "src/**" ]
let touchDigest = OrganizationIssueFields.touchSetDigest touchSet
let independentRefusals =
    [ { baseObservation with Readable = false }, "OIF-UNREADABLE"
      { baseObservation with Complete = false }, "OIF-INCOMPLETE"
      { baseObservation with Current = false }, "OIF-STALE"
      { baseObservation with StableRowId = "" }, "OIF-MISSING-STABLE-ID"
      { baseObservation with Revision = "" }, "OIF-MISSING-REVISION"
      { baseObservation with RepositoryScope = "" }, "OIF-MISSING-REPOSITORY-SCOPE"
      { baseObservation with NativeIssueType = "" }, "OIF-MISSING-NATIVE-TYPE"
      { baseObservation with SchedulingIntent = None }, "OIF-MISSING-INTENT"
      { baseObservation with SchedulingIntent = Some "Later" }, "OIF-UNKNOWN-INTENT:later"
      { baseObservation with LifecycleStatus = None }, "OIF-MISSING-STATUS"
      { baseObservation with LifecycleStatus = Some "Running" }, "OIF-UNKNOWN-STATUS:running"
      { baseObservation with LifecycleStatus = Some "Done" }, "OIF-INTENT-STATUS-AUTHORITY"
      { baseObservation with HoldReason = None }, "OIF-MISSING-HOLD"
      { baseObservation with SchedulingIntent = Some "Ready"; LifecycleStatus = Some "Ready" }, "OIF-UNEXPECTED-HOLD"
      { baseObservation with HoldReason = Some "mystery" }, "OIF-UNKNOWN-HOLD:mystery"
      { baseObservation with Priority = None }, "OIF-MISSING-PRIORITY"
      { baseObservation with Priority = Some "Urgent" }, "OIF-UNKNOWN-PRIORITY:urgent"
      { baseObservation with Effort = None }, "OIF-MISSING-EFFORT"
      { baseObservation with Effort = Some "XXL" }, "OIF-UNKNOWN-EFFORT:xxl"
      { baseObservation with StartDate = Some "09/01/2026" }, "OIF-INVALID-START-DATE"
      { baseObservation with TargetDate = Some "tomorrow" }, "OIF-INVALID-TARGET-DATE"
      { baseObservation with TargetDate = Some "2026-08-31" }, "OIF-REVERSED-DATE-RANGE"
      { baseObservation with Severity = None }, "OIF-MISSING-SEVERITY"
      { baseObservation with Severity = Some "Urgent" }, "OIF-UNKNOWN-SEVERITY:urgent"
      { baseObservation with Phase = None }, "OIF-MISSING-PHASE"
      { baseObservation with Phase = Some "Build" }, "OIF-UNKNOWN-PHASE:build"
      { baseObservation with Workstream = None }, "OIF-MISSING-WORKSTREAM"
      { baseObservation with Workstream = Some "Other" }, "OIF-UNKNOWN-WORKSTREAM:other"
      { baseObservation with ContractReference = Some "bad"; ContractAuthorityDigest = Some "bad" }, "OIF-INVALID-CONTRACT"
      { baseObservation with ContractReference = Some digest }, "OIF-UNBOUND-CONTRACT"
      { baseObservation with TouchSet = List.rev touchSet; TouchSetAuthorityDigest = Some touchDigest }, "OIF-NONCANONICAL-TOUCH-SET"
      { baseObservation with TouchSet = touchSet }, "OIF-UNBOUND-TOUCH-SET"
      { baseObservation with HierarchyPresent = true; HierarchyPreservable = false }, "OIF-LOSSY-HIERARCHY"
      { baseObservation with DependenciesPreservable = false }, "OIF-LOSSY-DEPENDENCIES"
      { baseObservation with RepositoryScopePreservable = false }, "OIF-LOSSY-REPOSITORY-SCOPE" ]
let requiredRefusals = strings (expectations.GetProperty("requiredRefusalCodes"))
let observedRefusals = (independentRefusals |> List.map snd) @ [ "OIF-DUPLICATE-STABLE-ID" ]
for family in requiredRefusals do
    if observedRefusals |> List.exists (_.StartsWith(family, StringComparison.Ordinal)) |> not then fail "OIF-EXPECTATIONS-REFUSAL" family
for item, expected in independentRefusals do
    match OrganizationIssueFields.validate item with
    | Error diagnostics when diagnostics |> List.map OrganizationIssueFields.diagnosticCode |> List.contains expected -> ()
    | actual -> fail "OIF-INDEPENDENT-REFUSAL" $"expected={expected} actual={actual}"
match OrganizationIssueFields.plan [ baseObservation; baseObservation ] with
| Error refusals when refusals |> List.forall (fun item -> item.Diagnostics |> List.contains OrganizationIssueFieldDiagnostic.DuplicateStableRowId) -> ()
| actual -> fail "OIF-INDEPENDENT-DUPLICATE" (string actual)

let run executable arguments code =
    let start = ProcessStartInfo(executable)
    start.WorkingDirectory <- root; start.UseShellExecute <- false; start.RedirectStandardOutput <- true; start.RedirectStandardError <- true
    arguments |> List.iter start.ArgumentList.Add
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then fail code $"exit={child.ExitCode}\n{output}\n{error}"
    output

let scratch = Path.Combine(Path.GetTempPath(), $"fsgg-gs2-05-2-{Guid.NewGuid():N}")
Directory.CreateDirectory scratch |> ignore
let stagedModel = Path.Combine(scratch, "organizationIssueFields.qnt")
let stagedTest = Path.Combine(scratch, "organizationIssueFields_test.qnt")
File.Copy(modelPath, stagedModel); File.Copy(testModelPath, stagedTest)
run "quint" [ "typecheck"; stagedModel ] "OIF-QUINT-TYPECHECK" |> ignore
run "quint" [ "typecheck"; stagedTest ] "OIF-QUINT-TEST-TYPECHECK" |> ignore
run "quint" [ "test"; stagedTest; "--main=organizationIssueFields_test" ] "OIF-QUINT-TEST" |> ignore
let simulation = run "quint" [ "run"; stagedModel; "--main=organizationIssueFields"; "--invariants"; "intentIsAuthority"; "statusIsDerived"; "refusedHasNoDisposition"; "uniqueDisposition"; "authorityProjectionBound"; "preservation"; "deterministicOutcome"; "--witnesses"; "plannedWitness"; "refusedWitness"; "--max-steps=2"; "--max-samples=200"; "--seed=0xe594a7f43d8bc0a1" ] "OIF-QUINT-RUN"
if not (simulation.Contains("plannedWitness was witnessed", StringComparison.Ordinal) && simulation.Contains("refusedWitness was witnessed", StringComparison.Ordinal)) then fail "OIF-QUINT-WITNESS" simulation
Directory.Delete(scratch, true)

if not (File.Exists qualificationPath) then fail "OIF-QUALIFICATION-MISSING" qualificationPath
let qualificationDocument = JsonDocument.Parse(File.ReadAllBytes qualificationPath)
let qualification = qualificationDocument.RootElement
if qualification.GetProperty("schema").GetString() <> "fsgg.github-substrate-v2.organization-issue-fields-qualification/1" then fail "OIF-QUALIFICATION-SCHEMA" qualificationPath
if qualification.GetProperty("unitContractSha256").GetString() <> "054ee50545c55b314447b7636ee35faf866adba40f6ff9b5ef07effb2009b41f" then fail "OIF-QUALIFICATION-CONTRACT" "registration drift"
if qualification.GetProperty("gateCommandSha256").GetString() <> "6311e9ca2c92315c48981983efcb93f2717b6b5273aa6fafa75c3fb8496ebcbd" then fail "OIF-QUALIFICATION-COMMAND" "registered command drift"
let expectedArtifacts =
    [ "src/FS.GG.Coordination.Core/OrganizationIssueFields.fsi"
      "src/FS.GG.Coordination.Core/OrganizationIssueFields.fs"
      "evidence/github-substrate-v2/gs2-05-2/corpus.json"
      "evidence/github-substrate-v2/gs2-05-2/independent-expectations.json"
      "evidence/github-substrate-v2/gs2-05-2/organizationIssueFields.quint"
      "evidence/github-substrate-v2/gs2-05-2/organizationIssueFields_test.quint"
      "eng/validate-github-organization-issue-fields.fsx" ]
let artifacts = qualification.GetProperty("artifacts").EnumerateArray() |> Seq.toList
if artifacts |> List.map (fun item -> item.GetProperty("path").GetString()) <> expectedArtifacts then fail "OIF-QUALIFICATION-ARTIFACTS" "artifact inventory drift"
for artifact in artifacts do
    let path = artifact.GetProperty("path").GetString()
    let expected = artifact.GetProperty("sha256").GetString()
    let actual = File.ReadAllBytes(atRoot path) |> sha256
    if actual <> expected then fail "OIF-QUALIFICATION-DIGEST" $"path={path} expected={expected} actual={actual}"

printfn "github-organization-issue-fields-contract OK fields=%d cases=%d accepted=%d q=Q2 network=offline inversions=%d" definitions.Length cases.Length observations.Length (independentRefusals.Length + 3)
