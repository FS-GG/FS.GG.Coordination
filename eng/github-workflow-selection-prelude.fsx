module WorkflowSelectionPrelude

#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

let obj (node: JsonNode) = node.AsObject()
let arr (node: JsonNode) = node.AsArray() |> Seq.toList
let text (name: string) (value: JsonObject) = value[name].GetValue<string>()
let boolean (name: string) (value: JsonObject) = value[name].GetValue<bool>()
let integer (name: string) (value: JsonObject) = value[name].GetValue<int>()
let texts (name: string) (value: JsonObject) = value[name] |> arr |> List.map _.GetValue<string>()

let exactProperties context expected (value: JsonObject) =
    let actual = value |> Seq.map _.Key |> Set.ofSeq
    let expectedSet = Set.ofList expected
    if actual <> expectedSet then
        let extra = Set.difference actual expectedSet |> String.concat ","
        let missing = Set.difference expectedSet actual |> String.concat ","
        failwith $"{context} property inventory differs; extra=[{extra}] missing=[{missing}]"

let obligation : string -> GitHubWorkflowObligation = function
    | "build" -> GitHubWorkflowObligation.Build
    | "test" -> GitHubWorkflowObligation.Test
    | "policy" -> GitHubWorkflowObligation.Policy
    | "coordination" -> GitHubWorkflowObligation.Coordination
    | "packaging" -> GitHubWorkflowObligation.Packaging
    | "release" -> GitHubWorkflowObligation.Release
    | value -> failwith $"unknown obligation: {value}"
let obligations name value = texts name value |> List.map obligation

let metrics (node: JsonNode) =
    let value = obj node
    exactProperties "metrics" [ "workflowFanOut"; "jobFanOut"; "billedMinutes"; "queueTimeSeconds"; "p50Seconds"; "p95Seconds" ] value
    { WorkflowFanOut = integer "workflowFanOut" value; JobFanOut = integer "jobFanOut" value
      BilledMinutes = integer "billedMinutes" value; QueueTimeSeconds = integer "queueTimeSeconds" value
      P50Seconds = integer "p50Seconds" value; P95Seconds = integer "p95Seconds" value }

let parseSnapshot (path: string) =
    let root = JsonNode.Parse(File.ReadAllText path) |> obj
    exactProperties "corpus"
        [ "schemaVersion"; "repository"; "sourceRevision"; "roadmapRevision"; "roadmapSha256"
          "prerequisiteReceiptDigest"; "complete"; "inventoryComplete"; "nonFileInputInventoryComplete"
          "graphVersion"; "workflows"; "obligations"; "dependencyEdges"; "unconditionalObligations"
          "impactCases"; "childOutcomes"; "requiredAggregates"; "unconditionalCore"; "performance"
          "sentinel"; "fleetSelectionEnabled"; "removalLedgerComplete"; "removals" ] root
    let workflows =
        root["workflows"] |> arr |> List.map (fun node ->
            let value = obj node
            exactProperties "workflow" [ "workflow"; "policyJobs"; "compositeSteps"; "reusableJobContracts"; "aggregateOutputs" ] value
            { Workflow = text "workflow" value; PolicyJobs = texts "policyJobs" value
              CompositeSteps = texts "compositeSteps" value; ReusableJobContracts = texts "reusableJobContracts" value
              AggregateOutputs = texts "aggregateOutputs" value })
    let edges =
        root["dependencyEdges"] |> arr |> List.map (fun node ->
            let value = obj node
            exactProperties "dependency-edge" [ "source"; "target" ] value
            { Source = obligation (text "source" value); Target = obligation (text "target" value) })
    let impactCases =
        root["impactCases"] |> arr |> List.map (fun node ->
            let value = obj node
            exactProperties "impact-case" [ "id"; "changedSubjects"; "nonFileInputs"; "roots"; "expectedClosure"; "unknown"; "ambiguous"; "fresh"; "complete"; "mergeGroup" ] value
            let mergeGroup =
                if isNull (value["mergeGroup"]) then None else
                let merge = obj value["mergeGroup"]
                exactProperties "merge-group" [ "queuedHead"; "currentBase"; "currentSettings"; "observedBase"; "observedSettings"; "recomputed" ] merge
                Some { QueuedHead = text "queuedHead" merge; CurrentBase = text "currentBase" merge
                       CurrentSettings = text "currentSettings" merge; ObservedBase = text "observedBase" merge
                       ObservedSettings = text "observedSettings" merge; Recomputed = boolean "recomputed" merge }
            { Id = text "id" value; ChangedSubjects = texts "changedSubjects" value
              NonFileInputs = texts "nonFileInputs" value; Roots = obligations "roots" value
              ExpectedClosure = obligations "expectedClosure" value; Unknown = boolean "unknown" value
              Ambiguous = boolean "ambiguous" value; Fresh = boolean "fresh" value
              Complete = boolean "complete" value; MergeGroup = mergeGroup })
    let childOutcomes =
        root["childOutcomes"] |> arr |> List.map (fun node ->
            let value = obj node
            exactProperties "child-outcome" [ "obligation"; "disposition"; "reason"; "expensiveJobProvisioned" ] value
            let disposition =
                match text "disposition" value with
                | "selected" ->
                    if not (isNull value["reason"]) then failwith "selected child carries a reason"
                    Selected
                | "not-applicable" -> NotApplicable(text "reason" value)
                | other -> failwith $"unknown child disposition: {other}"
            { Obligation = obligation (text "obligation" value); Disposition = disposition
              ExpensiveJobProvisioned = boolean "expensiveJobProvisioned" value })
    let performance =
        root["performance"] |> arr |> List.map (fun node ->
            let value = obj node
            exactProperties "performance" [ "repository"; "baseline"; "target"; "selected" ] value
            { Repository = text "repository" value; Baseline = metrics value["baseline"]
              Target = metrics value["target"]; Selected = metrics value["selected"] })
    let sentinelNode = obj root["sentinel"]
    exactProperties "sentinel" [ "scheduled"; "selectedClosure"; "actualFailures" ] sentinelNode
    let removals =
        root["removals"] |> arr |> List.map (fun node ->
            let value = obj node
            exactProperties "removal" [ "workflow"; "obligation"; "reason" ] value
            { Workflow = text "workflow" value; Obligation = text "obligation" value; Reason = text "reason" value })
    { SchemaVersion = integer "schemaVersion" root; Repository = text "repository" root
      SourceRevision = text "sourceRevision" root; RoadmapRevision = text "roadmapRevision" root
      RoadmapSha256 = text "roadmapSha256" root; PrerequisiteReceiptDigest = text "prerequisiteReceiptDigest" root
      Complete = boolean "complete" root; InventoryComplete = boolean "inventoryComplete" root
      NonFileInputInventoryComplete = boolean "nonFileInputInventoryComplete" root
      GraphVersion = text "graphVersion" root; Workflows = workflows; Obligations = obligations "obligations" root
      DependencyEdges = edges; UnconditionalObligations = obligations "unconditionalObligations" root
      ImpactCases = impactCases; ChildOutcomes = childOutcomes; RequiredAggregates = texts "requiredAggregates" root
      UnconditionalCore = obligations "unconditionalCore" root; Performance = performance
      Sentinel = { Scheduled = boolean "scheduled" sentinelNode; SelectedClosure = obligations "selectedClosure" sentinelNode; ActualFailures = obligations "actualFailures" sentinelNode }
      FleetSelectionEnabled = boolean "fleetSelectionEnabled" root
      RemovalLedgerComplete = boolean "removalLedgerComplete" root; Removals = removals }

let parseExpectations path = JsonNode.Parse(File.ReadAllText path) |> obj
let expectCompileError snapshot = GitHubWorkflowSelectionQualification.compile snapshot |> Result.isError
let expectVerifyError seal snapshot = GitHubWorkflowSelectionQualification.verify seal snapshot |> Result.isError
