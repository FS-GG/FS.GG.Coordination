#load "github-workflow-selection-prelude.fsx"

open System.IO
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts
open WorkflowSelectionPrelude
open type FS.GG.Coordination.Qualification.Contracts.GitHubWorkflowSupplyChainControl

let root = if fsi.CommandLineArgs.Length > 1 then Path.GetFullPath fsi.CommandLineArgs[1] else Path.GetFullPath "."
let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-7/corpus.json")
let expectationsPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-7/independent-expectations.json")
let snapshot = parseSnapshot corpusPath
let expectations = parseExpectations expectationsPath
exactProperties "expectations" [ "schemaVersion"; "expectedSeal"; "selectionControls"; "selectionIndependentCases"; "supplyChainControls"; "supplyChainIndependentCases"; "shapeCases" ] expectations
for name in [ "selectionIndependentCases"; "supplyChainIndependentCases" ] do
    expectations[name] |> arr |> List.iter (fun node -> exactProperties name [ "control"; "fixture" ] (obj node))
let expectedSeal = text "expectedSeal" expectations
let report =
    match GitHubWorkflowSelectionQualification.verify expectedSeal snapshot with
    | Ok value -> value
    | Error findings -> failwith $"workflow selection supply-chain baseline failed: {findings}"

let expectedControls = texts "supplyChainControls" expectations
let actualControls = GitHubWorkflowSelectionQualification.requiredSupplyChainControls |> List.map GitHubWorkflowSelectionQualification.supplyChainControlId
if expectedControls <> actualControls then failwith "supply-chain control inventory differs"
let independentCases =
    expectations["supplyChainIndependentCases"] |> arr |> List.map (fun node ->
        let value = obj node
        text "control" value, text "fixture" value)
if independentCases |> List.map fst <> expectedControls then failwith "supply-chain independent case binding differs"

let updateFirst change source = { source with Performance = change source.Performance.Head :: source.Performance.Tail }
let targetMetric change (item: GitHubWorkflowRepositoryPerformance) = { item with Target = change item.Target }
let selectedMetric change (item: GitHubWorkflowRepositoryPerformance) = { item with Selected = change item.Selected }
let missedSnapshot obligation enabled =
    { snapshot with Sentinel = { snapshot.Sentinel with SelectedClosure = snapshot.Sentinel.SelectedClosure |> List.filter ((<>) obligation); ActualFailures = [ obligation ] }
                    FleetSelectionEnabled = enabled }
let disabledReport obligation source =
    match GitHubWorkflowSelectionQualification.compile source with
    | Ok value -> value.MissedObligations = [ obligation ] && not value.FleetSelectionEnabled
    | Error _ -> false

let generatedMutation (control: GitHubWorkflowSupplyChainControl) =
    match control with
    | FleetBaselines -> expectCompileError { snapshot with Performance = snapshot.Performance.Tail }
    | AcceptedTargets -> expectCompileError (updateFirst (targetMetric (fun value -> { value with WorkflowFanOut = 99 })) snapshot)
    | WorkflowFanOutTarget -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with WorkflowFanOut = 4 })) snapshot)
    | JobFanOutTarget -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with JobFanOut = 10 })) snapshot)
    | BilledMinuteTarget -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with BilledMinutes = 100 })) snapshot)
    | QueueTimeTarget -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with QueueTimeSeconds = 250 })) snapshot)
    | P50Target -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with P50Seconds = 1100 })) snapshot)
    | P95Target -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with P95Seconds = 2200 })) snapshot)
    | ScheduledSentinel -> expectCompileError { snapshot with Sentinel = { snapshot.Sentinel with Scheduled = false } }
    | MissedObligationDetection -> disabledReport GitHubWorkflowObligation.Release (missedSnapshot GitHubWorkflowObligation.Release false)
    | FleetDisable -> expectCompileError (missedSnapshot GitHubWorkflowObligation.Packaging true)
    | RemovalLedger -> expectCompileError { snapshot with RemovalLedgerComplete = false }

let independentMutation (control: GitHubWorkflowSupplyChainControl) fixture =
    match control, fixture with
    | FleetBaselines, "fleet-repository-baseline-absent" -> expectCompileError { snapshot with Performance = snapshot.Performance |> List.filter (fun value -> value.Repository <> "FS-GG/FS.GG.Audio") }
    | AcceptedTargets, "target-worse-than-baseline" -> expectCompileError (updateFirst (targetMetric (fun value -> { value with P95Seconds = value.P95Seconds + 1000 })) snapshot)
    | WorkflowFanOutTarget, "workflow-fan-out-over-target" -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with WorkflowFanOut = value.WorkflowFanOut + 2 })) snapshot)
    | JobFanOutTarget, "job-fan-out-over-target" -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with JobFanOut = value.JobFanOut + 4 })) snapshot)
    | BilledMinuteTarget, "billed-minutes-over-target" -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with BilledMinutes = value.BilledMinutes + 40 })) snapshot)
    | QueueTimeTarget, "queue-time-over-target" -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with QueueTimeSeconds = value.QueueTimeSeconds + 100 })) snapshot)
    | P50Target, "p50-over-target" -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with P50Seconds = value.P50Seconds + 500 })) snapshot)
    | P95Target, "p95-over-target" -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with P95Seconds = value.P95Seconds + 500 })) snapshot)
    | ScheduledSentinel, "sentinel-schedule-absent" -> expectCompileError { snapshot with Sentinel = { snapshot.Sentinel with Scheduled = false; ActualFailures = [] } }
    | MissedObligationDetection, "actual-release-failure-unselected" -> disabledReport GitHubWorkflowObligation.Packaging (missedSnapshot GitHubWorkflowObligation.Packaging false)
    | FleetDisable, "missed-obligation-leaves-fleet-enabled" -> expectCompileError (missedSnapshot GitHubWorkflowObligation.Release true)
    | RemovalLedger, "removed-obligation-unrecorded" -> expectCompileError { snapshot with Removals = snapshot.Removals.Tail }
    | _ -> failwith $"unknown supply-chain fixture {GitHubWorkflowSelectionQualification.supplyChainControlId control}/{fixture}"

let result (control: GitHubWorkflowSupplyChainControl) passed : GitHubWorkflowControlResult<GitHubWorkflowSupplyChainControl> =
    { Control = control; ControlPassed = passed; BaselineGreen = true }
let generated = GitHubWorkflowSelectionQualification.requiredSupplyChainControls |> List.map (fun control -> result control (generatedMutation control))
let independent =
    List.zip GitHubWorkflowSelectionQualification.requiredSupplyChainControls independentCases
    |> List.map (fun (control, (caseControl, fixture)) ->
        if GitHubWorkflowSelectionQualification.supplyChainControlId control <> caseControl then failwith "supply-chain case/control mismatch"
        result control (independentMutation control fixture))
match GitHubWorkflowSelectionQualification.validateSupplyChain generated independent with
| Error findings -> failwith $"workflow selection supply-chain qualification failed: {findings}"
| Ok () -> printfn "GITHUB_WORKFLOW_SELECTION_SUPPLY_CHAIN_OK repositories=%d fleetEnabled=%b controls=%d seal=%s" report.RepositoryMetricCount report.FleetSelectionEnabled expectedControls.Length report.Seal
