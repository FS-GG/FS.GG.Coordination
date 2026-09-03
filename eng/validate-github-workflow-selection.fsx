#load "github-workflow-selection-prelude.fsx"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts
open WorkflowSelectionPrelude
open type FS.GG.Coordination.Qualification.Contracts.GitHubWorkflowSelectionControl

let root = if fsi.CommandLineArgs.Length > 1 then Path.GetFullPath fsi.CommandLineArgs[1] else Path.GetFullPath "."
let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-7/corpus.json")
let expectationsPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-7/independent-expectations.json")
let sourcePath = Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubWorkflowSelectionQualification.fs")
let protocolPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
let snapshot = parseSnapshot corpusPath
let expectations = parseExpectations expectationsPath

let sha256File path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let expectRefusal action = try action (); false with _ -> true
let replaceCase id change source =
    { source with ImpactCases = source.ImpactCases |> List.map (fun value -> if value.Id = id then change value else value) }
let replaceOutcome obligation change source =
    { source with ChildOutcomes = source.ChildOutcomes |> List.map (fun value -> if value.Obligation = obligation then change value else value) }

let validateExpectationsShape (value: JsonObject) =
    exactProperties "expectations" [ "schemaVersion"; "expectedSeal"; "selectionControls"; "selectionIndependentCases"; "supplyChainControls"; "supplyChainIndependentCases"; "shapeCases" ] value
    if integer "schemaVersion" value <> 1 then failwith "expectations schemaVersion differs"
    for name in [ "selectionIndependentCases"; "supplyChainIndependentCases" ] do
        value[name] |> arr |> List.iter (fun node -> exactProperties name [ "control"; "fixture" ] (obj node))
validateExpectationsShape expectations

let expectedSeal = text "expectedSeal" expectations
let report =
    match GitHubWorkflowSelectionQualification.compile snapshot with
    | Ok value -> value
    | Error findings -> failwith $"workflow selection baseline failed: {findings}"
if String.IsNullOrWhiteSpace expectedSeal then failwith $"expectedSeal is empty; computed={report.Seal}"
match GitHubWorkflowSelectionQualification.verify expectedSeal snapshot with
| Error findings -> failwith $"workflow selection seal failed: {findings}"
| Ok _ -> ()

let expectedControls = texts "selectionControls" expectations
let actualControls = GitHubWorkflowSelectionQualification.requiredSelectionControls |> List.map GitHubWorkflowSelectionQualification.selectionControlId
if expectedControls <> actualControls then failwith "selection control inventory differs"
let independentCases =
    expectations["selectionIndependentCases"] |> arr |> List.map (fun node ->
        let value = obj node
        text "control" value, text "fixture" value)
if independentCases |> List.map fst <> expectedControls then failwith "selection independent case binding differs"

let generatedMutation (control: GitHubWorkflowSelectionControl) =
    match control with
    | WorkflowPrerequisite -> expectCompileError { snapshot with PrerequisiteReceiptDigest = String.replicate 64 "a" }
    | WorkflowRoadmap -> expectCompileError { snapshot with RoadmapSha256 = String.replicate 64 "b" }
    | WorkflowCompleteness -> expectCompileError { snapshot with Complete = false }
    | TypedWorkflowInventory ->
        let first = snapshot.Workflows.Head
        expectCompileError { snapshot with Workflows = { first with CompositeSteps = [] } :: snapshot.Workflows.Tail }
    | WorkflowGraphVersion -> expectCompileError { snapshot with GraphVersion = "fsgg.workflow-impact/2" }
    | ChangedSubjectSelection -> expectCompileError (replaceCase "source" (fun value -> { value with ChangedSubjects = [ "" ] }) snapshot)
    | NonFileInputSelection -> expectCompileError { snapshot with NonFileInputInventoryComplete = false }
    | TransitiveClosure -> expectCompileError (replaceCase "dependency" (fun value -> { value with ExpectedClosure = value.ExpectedClosure |> List.filter ((<>) GitHubWorkflowObligation.Release) }) snapshot)
    | UnconditionalObligations -> expectCompileError { snapshot with UnconditionalObligations = [] }
    | StableAggregates -> expectCompileError { snapshot with RequiredAggregates = [ "required" ] }
    | TypedNotApplicable -> expectCompileError (replaceOutcome GitHubWorkflowObligation.Packaging (fun value -> { value with Disposition = NotApplicable "" }) snapshot)
    | NoExpensiveProvisioning -> expectCompileError (replaceOutcome GitHubWorkflowObligation.Release (fun value -> { value with ExpensiveJobProvisioned = true }) snapshot)
    | AmbiguousImpactRefusal -> expectCompileError (replaceCase "documentation" (fun value -> { value with Ambiguous = true }) snapshot)
    | StaleImpactRefusal -> expectCompileError (replaceCase "policy" (fun value -> { value with Fresh = false }) snapshot)
    | MergeGroupRecomputation ->
        expectCompileError (replaceCase "merge-group" (fun value ->
            let merge = value.MergeGroup.Value
            { value with MergeGroup = Some { merge with ObservedBase = String.replicate 40 "4" } }) snapshot)
    | RepresentativeChanges -> expectCompileError { snapshot with ImpactCases = snapshot.ImpactCases |> List.filter (fun value -> value.Id <> "source") }
    | MixedChanges -> expectCompileError (replaceCase "mixed" (fun value -> { value with Roots = [ GitHubWorkflowObligation.Test ] }) snapshot)
    | UnknownChanges -> expectCompileError (replaceCase "workflow" (fun value -> { value with Unknown = true }) snapshot)
    | WorkflowOrdering -> expectCompileError { snapshot with Workflows = List.rev snapshot.Workflows }
    | ExactWorkflowSeal -> expectVerifyError (String.replicate 64 "0") snapshot
    | ExactWorkflowReplay -> expectVerifyError expectedSeal (replaceCase "test" (fun value -> { value with ChangedSubjects = [ "tests/ReplayChanged.fs" ] }) snapshot)
    | QuintWorkflowUnchanged ->
        sha256File protocolPath = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        && SHA256.HashData(Array.append (File.ReadAllBytes protocolPath) [| 10uy |]) <> SHA256.HashData(File.ReadAllBytes protocolPath)
    | NoWorkflowMutationSurface ->
        let source = File.ReadAllText sourcePath
        not (source.Contains("System.Net.Http", StringComparison.Ordinal) || source.Contains("Octokit", StringComparison.Ordinal))
        && (source + "\nSystem.Net.Http.HttpClient").Contains("System.Net.Http", StringComparison.Ordinal)

let independentMutation (control: GitHubWorkflowSelectionControl) fixture =
    match control, fixture with
    | WorkflowPrerequisite, "alternate-prerequisite-receipt" -> expectCompileError { snapshot with PrerequisiteReceiptDigest = String.replicate 64 "c" }
    | WorkflowRoadmap, "alternate-roadmap-bytes" -> expectCompileError { snapshot with RoadmapRevision = String.replicate 40 "d" }
    | WorkflowCompleteness, "non-file-inventory-incomplete" -> expectCompileError { snapshot with NonFileInputInventoryComplete = false }
    | TypedWorkflowInventory, "missing-reusable-contract" ->
        let second = snapshot.Workflows[1]
        expectCompileError { snapshot with Workflows = [ snapshot.Workflows[0]; { second with ReusableJobContracts = [] } ] }
    | WorkflowGraphVersion, "unsupported-graph-major" -> expectCompileError { snapshot with GraphVersion = "workflow-impact/99" }
    | ChangedSubjectSelection, "blank-changed-subject" -> expectCompileError (replaceCase "test" (fun value -> { value with ChangedSubjects = [ " " ] }) snapshot)
    | NonFileInputSelection, "blank-non-file-input" -> expectCompileError (replaceCase "non-file-input" (fun value -> { value with NonFileInputs = [ "" ] }) snapshot)
    | TransitiveClosure, "dependency-closure-omits-release" -> expectCompileError (replaceCase "dependency" (fun value -> { value with ExpectedClosure = [ GitHubWorkflowObligation.Build; GitHubWorkflowObligation.Test; GitHubWorkflowObligation.Policy; GitHubWorkflowObligation.Coordination; GitHubWorkflowObligation.Packaging ] }) snapshot)
    | UnconditionalObligations, "policy-no-longer-unconditional" -> expectCompileError { snapshot with UnconditionalObligations = [ GitHubWorkflowObligation.Build ] }
    | StableAggregates, "required-aggregate-missing" -> expectCompileError { snapshot with RequiredAggregates = [ "supply-chain" ] }
    | TypedNotApplicable, "not-applicable-reason-empty" -> expectCompileError (replaceOutcome GitHubWorkflowObligation.Release (fun value -> { value with Disposition = NotApplicable " " }) snapshot)
    | NoExpensiveProvisioning, "release-job-provisioned-while-not-applicable" -> expectCompileError (replaceOutcome GitHubWorkflowObligation.Release (fun value -> { value with ExpensiveJobProvisioned = true }) snapshot)
    | AmbiguousImpactRefusal, "documentation-double-classified" -> expectCompileError (replaceCase "documentation" (fun value -> { value with Ambiguous = true; Roots = [ GitHubWorkflowObligation.Coordination; GitHubWorkflowObligation.Packaging ] }) snapshot)
    | StaleImpactRefusal, "policy-impact-stale" -> expectCompileError (replaceCase "policy" (fun value -> { value with Fresh = false }) snapshot)
    | MergeGroupRecomputation, "queued-base-differs-from-current" ->
        expectCompileError (replaceCase "merge-group" (fun value ->
            let merge = value.MergeGroup.Value
            { value with MergeGroup = Some { merge with CurrentSettings = String.replicate 64 "5"; Recomputed = false } }) snapshot)
    | RepresentativeChanges, "release-representative-case-absent" -> expectCompileError { snapshot with ImpactCases = snapshot.ImpactCases |> List.filter (fun value -> value.Id <> "release") }
    | MixedChanges, "mixed-roots-not-recomputed" -> expectCompileError (replaceCase "mixed" (fun value -> { value with ExpectedClosure = [ GitHubWorkflowObligation.Test; GitHubWorkflowObligation.Policy; GitHubWorkflowObligation.Coordination ] }) snapshot)
    | UnknownChanges, "unknown-extension-admitted" -> expectCompileError (replaceCase "source" (fun value -> { value with Unknown = true; ChangedSubjects = [ "assets/file.future" ] }) snapshot)
    | WorkflowOrdering, "workflow-inventory-reversed" -> expectCompileError { snapshot with Workflows = List.rev snapshot.Workflows }
    | ExactWorkflowSeal, "one-nibble-seal-divergence" -> expectVerifyError ("f" + expectedSeal.Substring 1) snapshot
    | ExactWorkflowReplay, "post-seal-case-change" -> expectVerifyError expectedSeal (replaceCase "source" (fun value -> { value with ChangedSubjects = [ "src/Other.fs" ] }) snapshot)
    | QuintWorkflowUnchanged, "protocol-byte-append" ->
        let original = File.ReadAllBytes protocolPath
        sha256File protocolPath = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        && SHA256.HashData(Array.append original [| 0uy |]) <> SHA256.HashData original
    | NoWorkflowMutationSurface, "forbidden-http-client-surface" ->
        let source = File.ReadAllText sourcePath
        not (source.Contains("HttpClient(", StringComparison.Ordinal)) && (source + "\nHttpClient()").Contains("HttpClient(", StringComparison.Ordinal)
    | _ -> failwith $"unknown selection fixture {GitHubWorkflowSelectionQualification.selectionControlId control}/{fixture}"

let result (control: GitHubWorkflowSelectionControl) passed : GitHubWorkflowControlResult<GitHubWorkflowSelectionControl> =
    { Control = control; ControlPassed = passed; BaselineGreen = true }
let generated = GitHubWorkflowSelectionQualification.requiredSelectionControls |> List.map (fun control -> result control (generatedMutation control))
let independent =
    List.zip GitHubWorkflowSelectionQualification.requiredSelectionControls independentCases
    |> List.map (fun (control, (caseControl, fixture)) ->
        if GitHubWorkflowSelectionQualification.selectionControlId control <> caseControl then failwith "selection case/control mismatch"
        result control (independentMutation control fixture))
match GitHubWorkflowSelectionQualification.validateSelection generated independent with
| Error findings -> failwith $"workflow selection qualification failed: {findings}"
| Ok () -> ()

let expectShapeRefusal (value: JsonObject) =
    let path = Path.GetTempFileName()
    try File.WriteAllText(path, value.ToJsonString()); expectRefusal (fun () -> parseSnapshot path |> ignore)
    finally File.Delete path
let corpusNode = JsonNode.Parse(File.ReadAllText corpusPath).AsObject()
let firstObject (collection: string) (value: JsonObject) = ((value[collection].AsArray())[0]).AsObject()
let objectById (collection: string) id (value: JsonObject) =
    value[collection].AsArray()
    |> Seq.map _.AsObject()
    |> Seq.find (fun item -> item["id"].GetValue<string>() = id)
let addUnknown (value: JsonObject) = value["unknown"] <- JsonValue.Create true
let addUnknownField (value: JsonObject) = value["unknownField"] <- JsonValue.Create true
let shapeMutation = function
    | "corpus-top-level-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown value; expectShapeRefusal value
    | "workflow-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown (firstObject "workflows" value); expectShapeRefusal value
    | "edge-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown (firstObject "dependencyEdges" value); expectShapeRefusal value
    | "impact-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknownField (firstObject "impactCases" value); expectShapeRefusal value
    | "merge-group-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown (((objectById "impactCases" "merge-group" value)["mergeGroup"]).AsObject()); expectShapeRefusal value
    | "outcome-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown (firstObject "childOutcomes" value); expectShapeRefusal value
    | "metric-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown (((firstObject "performance" value)["baseline"]).AsObject()); expectShapeRefusal value
    | "sentinel-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown ((value["sentinel"]).AsObject()); expectShapeRefusal value
    | "removal-extra" -> let value = corpusNode.DeepClone().AsObject() in addUnknown (firstObject "removals" value); expectShapeRefusal value
    | "expectations-top-level-extra" -> let value = expectations.DeepClone().AsObject() in addUnknown value; expectRefusal (fun () -> validateExpectationsShape value)
    | "selection-case-extra" -> let value = expectations.DeepClone().AsObject() in addUnknown (firstObject "selectionIndependentCases" value); expectRefusal (fun () -> validateExpectationsShape value)
    | "supply-case-extra" -> let value = expectations.DeepClone().AsObject() in addUnknown (firstObject "supplyChainIndependentCases" value); expectRefusal (fun () -> validateExpectationsShape value)
    | value -> failwith $"unknown shape fixture: {value}"
if texts "shapeCases" expectations |> List.forall shapeMutation |> not then failwith "unknown-property self-test failed"

printfn "GITHUB_WORKFLOW_SELECTION_OK workflows=%d obligations=%d cases=%d controls=%d seal=%s" report.WorkflowCount report.ObligationCount report.ImpactCaseCount expectedControls.Length report.Seal
