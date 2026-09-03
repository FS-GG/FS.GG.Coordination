#load "github-workflow-selection-prelude.fsx"

open System
open System.IO
open System.Security.Cryptography
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

let sha256File path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let observationsPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-7/observed-workflow-runs.json")
if sha256File observationsPath <> snapshot.ObservationSha256 then failwith "observed workflow evidence digest differs"
let deletionLedgerPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-7/deletion-ledger.json")
if sha256File deletionLedgerPath <> snapshot.RemovalLedgerSha256 then failwith "deletion ledger digest differs"
let deletionLedger = JsonNode.Parse(File.ReadAllText deletionLedgerPath).AsObject()
exactProperties "deletion-ledger" [ "schema"; "complete"; "scope"; "removedWorkflows"; "removedObligations"; "consolidations"; "note" ] deletionLedger
if text "schema" deletionLedger <> "fsgg.coordination.workflow-deletion-ledger/1" || not (boolean "complete" deletionLedger)
   || text "scope" deletionLedger <> "FS-GG/FS.GG.Coordination" then failwith "deletion ledger contract differs"
if deletionLedger["removedWorkflows"].AsArray().Count <> snapshot.Removals.Length
   || deletionLedger["removedObligations"].AsArray().Count <> snapshot.Removals.Length then failwith "deletion ledger inventory differs"

let seconds (left: string) (right: string) =
    max 0 (int (DateTimeOffset.Parse(right).Subtract(DateTimeOffset.Parse(left)).TotalSeconds |> Math.Round))
let percentile p values =
    let sorted = values |> List.sort
    if sorted.IsEmpty then 0 else sorted[max 0 (int (Math.Ceiling(p * float sorted.Length)) - 1)]
let recomputeObservation (rootNode: JsonObject) =
    exactProperties "observations" [ "schema"; "observedAt"; "window"; "completeness"; "source"; "aggregation"; "repositories" ] rootNode
    if text "schema" rootNode <> "fsgg.coordination.workflow-observations/1" || text "completeness" rootNode <> "complete" then failwith "observation contract differs"
    let window = obj rootNode["window"]
    exactProperties "observation-window" [ "start"; "end" ] window
    let observedAt = DateTimeOffset.Parse(text "observedAt" rootNode)
    let startAt = DateTimeOffset.Parse(text "start" window)
    let endAt = DateTimeOffset.Parse(text "end" window)
    if startAt >= endAt || endAt > observedAt || observedAt - endAt > TimeSpan.FromHours 24.0 then failwith "observation window is stale or invalid"
    let aggregation = obj rootNode["aggregation"]
    exactProperties "aggregation" [ "id"; "triggerCorrelation"; "workflowFanOut"; "jobFanOut"; "billedMinutes"; "queueTimeSeconds"; "p50Seconds"; "p95Seconds" ] aggregation
    if text "id" aggregation <> "workflow-baseline-p95/1" then failwith "aggregation identity differs"
    let rows = rootNode["repositories"] |> arr
    let repositories = rows |> List.map (obj >> text "repository")
    let expectedRepositories = snapshot.Performance |> List.map _.Repository
    if repositories <> expectedRepositories || repositories.Length <> (repositories |> Set.ofList |> Set.count) then failwith "observed repository inventory differs"
    let allRuns = ResizeArray<int64>()
    let allJobs = ResizeArray<int64>()
    let computed =
        rows |> List.map (fun node ->
            let row = obj node
            exactProperties "observed-repository" [ "repository"; "query"; "complete"; "runs" ] row
            let repository = text "repository" row
            if not (boolean "complete" row) || not ((text "query" row).Contains(repository, StringComparison.Ordinal)) then failwith $"incomplete observation: {repository}"
            let runs = row["runs"] |> arr |> List.map obj
            if runs.Length < 5 then failwith $"insufficient run sample: {repository}"
            let groups = System.Collections.Generic.Dictionary<string, ResizeArray<JsonObject>>()
            let queues = ResizeArray<int>()
            let completions = ResizeArray<int>()
            for run in runs do
                exactProperties "observed-run" [ "id"; "workflowId"; "name"; "event"; "conclusion"; "headSha"; "attempt"; "createdAt"; "runStartedAt"; "updatedAt"; "jobs" ] run
                let runId = run["id"].GetValue<int64>()
                allRuns.Add runId
                let created = text "createdAt" run
                let started = text "runStartedAt" run
                let updated = text "updatedAt" run
                let createdAt = DateTimeOffset.Parse created
                if createdAt < startAt || createdAt > endAt then failwith $"run outside window: {runId}"
                completions.Add(seconds started updated)
                let key = text "headSha" run + "|" + created
                if not (groups.ContainsKey key) then groups[key] <- ResizeArray()
                groups[key].Add run
                for jobNode in run["jobs"] |> arr do
                    let job = obj jobNode
                    // Retain only the provider fields required to independently recompute queue
                    // and billed duration.  Names, runner labels, and job-created timestamps are
                    // deliberately omitted so this read-only observation remains below the Git
                    // evidence storage ceiling without weakening source identity or timing proof.
                    exactProperties "observed-job" [ "id"; "conclusion"; "startedAt"; "completedAt" ] job
                    allJobs.Add(job["id"].GetValue<int64>())
                    if not (isNull job["startedAt"]) then queues.Add(seconds created (text "startedAt" job))
            let groupRows = groups.Values |> Seq.map Seq.toList |> Seq.toList
            let workflowFanOut = groupRows |> List.map (fun group -> group |> List.map (fun run -> run["workflowId"].GetValue<int64>()) |> Set.ofList |> Set.count) |> List.max
            let jobFanOut = groupRows |> List.map (List.sumBy (fun run -> run["jobs"].AsArray().Count)) |> List.max
            let billed =
                groupRows |> List.map (List.sumBy (fun run ->
                    run["jobs"] |> arr |> List.sumBy (fun node ->
                        let job = obj node
                        if text "conclusion" job = "skipped" || isNull job["startedAt"] || isNull job["completedAt"] then 0
                        else max 1 (int (Math.Ceiling(float (seconds (text "startedAt" job) (text "completedAt" job)) / 60.0))))))
            repository,
                { WorkflowFanOut = workflowFanOut; JobFanOut = jobFanOut; BilledMinutes = percentile 0.95 billed
                  QueueTimeSeconds = percentile 0.95 (List.ofSeq queues); P50Seconds = percentile 0.50 (List.ofSeq completions)
                  P95Seconds = percentile 0.95 (List.ofSeq completions) }, runs.Length, runs |> List.sumBy (fun run -> run["jobs"].AsArray().Count))
    if allRuns.Count <> (allRuns |> Set.ofSeq |> Set.count) then failwith "duplicate run provenance"
    if allJobs.Count <> (allJobs |> Set.ofSeq |> Set.count) then failwith "duplicate job provenance"
    for repository, metrics, runCount, jobCount in computed do
        let retained = snapshot.Performance |> List.find (fun value -> value.Repository = repository)
        if metrics <> retained.Baseline || runCount <> retained.Provenance.RunSampleCount || jobCount <> retained.Provenance.JobSampleCount then
            failwith $"independent baseline recomputation differs: {repository}"
        let sourceRow = rows |> List.map obj |> List.find (fun value -> text "repository" value = repository)
        let sourceRunIds = sourceRow["runs"] |> arr |> List.map (fun value -> ((obj value)["id"]).GetValue<int64>())
        if sourceRunIds <> retained.Provenance.RunIds then failwith $"run provenance substitution: {repository}"
    computed

let observationNode = JsonNode.Parse(File.ReadAllText observationsPath).AsObject()
let recomputed = recomputeObservation observationNode
if recomputed |> List.map (fun (_, metrics, _, _) -> metrics) |> Set.ofList |> Set.count < 2 then failwith "uniform fabricated observation rows"
let expectObservationRefusal change =
    let mutant = observationNode.DeepClone().AsObject()
    change mutant
    try recomputeObservation mutant |> ignore; false with _ -> true
if not (expectObservationRefusal (fun value -> value["repositories"].AsArray().RemoveAt(0))) then failwith "missing-observation control stayed green"
if not (expectObservationRefusal (fun value -> value["observedAt"] <- JsonValue.Create("2026-09-05T03:14:51Z"))) then failwith "stale-observation control stayed green"
if not (expectObservationRefusal (fun value ->
    let rows = value["repositories"].AsArray()
    let firstRuns = (rows[0]["runs"]).AsArray()
    let secondRuns = (rows[1]["runs"]).AsArray()
    let firstRun = (firstRuns[0]["id"]).GetValue<int64>()
    secondRuns[0]["id"] <- JsonValue.Create(firstRun))) then failwith "duplicate-provenance control stayed green"
if not (expectObservationRefusal (fun value ->
    let firstRepository = (value["repositories"]).AsArray()[0]
    for run in (firstRepository["runs"]).AsArray() do
        for job in (run["jobs"]).AsArray() do
            if not (isNull job["startedAt"]) then job["completedAt"] <- job["startedAt"].DeepClone())) then failwith "forged-observation control stayed green"

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
    | JobFanOutTarget -> expectCompileError (updateFirst (fun item -> selectedMetric (fun value -> { value with JobFanOut = item.Target.JobFanOut + 1 }) item) snapshot)
    | BilledMinuteTarget -> expectCompileError (updateFirst (selectedMetric (fun value -> { value with BilledMinutes = 100 })) snapshot)
    | QueueTimeTarget -> expectCompileError (updateFirst (fun item -> selectedMetric (fun value -> { value with QueueTimeSeconds = item.Target.QueueTimeSeconds + 1 }) item) snapshot)
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
    | RemovalLedger, "removed-obligation-unrecorded" -> expectCompileError { snapshot with RemovalLedgerComplete = false }
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
