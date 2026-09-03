namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type GitHubWorkflowObligation = Build | Test | Policy | Coordination | Packaging | Release
type GitHubWorkflowInventoryRow =
    { Workflow: string; PolicyJobs: string list; CompositeSteps: string list
      ReusableJobContracts: string list; AggregateOutputs: string list }
type GitHubWorkflowDependencyEdge = { Source: GitHubWorkflowObligation; Target: GitHubWorkflowObligation }
type GitHubMergeGroupImpact =
    { QueuedHead: string; CurrentBase: string; CurrentSettings: string
      ObservedBase: string; ObservedSettings: string; Recomputed: bool }
type GitHubWorkflowImpactCase =
    { Id: string; ChangedSubjects: string list; NonFileInputs: string list
      Roots: GitHubWorkflowObligation list; ExpectedClosure: GitHubWorkflowObligation list
      Unknown: bool; Ambiguous: bool; Fresh: bool; Complete: bool
      MergeGroup: GitHubMergeGroupImpact option }
type GitHubWorkflowChildDisposition = Selected | NotApplicable of reason: string
type GitHubWorkflowChildOutcome =
    { Obligation: GitHubWorkflowObligation; Disposition: GitHubWorkflowChildDisposition
      ExpensiveJobProvisioned: bool }
type GitHubWorkflowMetrics =
    { WorkflowFanOut: int; JobFanOut: int; BilledMinutes: int
      QueueTimeSeconds: int; P50Seconds: int; P95Seconds: int }
type GitHubWorkflowObservationProvenance =
    { ObservationId: string; Query: string; RunIds: int64 list; Revisions: string list
      ObservedAt: DateTimeOffset; WindowStart: DateTimeOffset; WindowEnd: DateTimeOffset
      RunSampleCount: int; JobSampleCount: int; Aggregation: string; Complete: bool
      IndependentRecomputed: bool; ReviewedBy: string; ReviewedAt: DateTimeOffset
      TargetRationale: string }
type GitHubWorkflowRepositoryPerformance =
    { Repository: string; Baseline: GitHubWorkflowMetrics
      Target: GitHubWorkflowMetrics; Selected: GitHubWorkflowMetrics; SelectedKind: string
      Provenance: GitHubWorkflowObservationProvenance }
type GitHubWorkflowSentinel =
    { Scheduled: bool; SelectedClosure: GitHubWorkflowObligation list
      ActualFailures: GitHubWorkflowObligation list }
type GitHubWorkflowRemoval = { Workflow: string; Obligation: string; Reason: string }
type GitHubWorkflowSelectionSnapshot =
    { SchemaVersion: int; Repository: string; SourceRevision: string
      RoadmapRevision: string; RoadmapSha256: string; PrerequisiteReceiptDigest: string
      Complete: bool; InventoryComplete: bool; NonFileInputInventoryComplete: bool
      GraphVersion: string; Workflows: GitHubWorkflowInventoryRow list
      Obligations: GitHubWorkflowObligation list; DependencyEdges: GitHubWorkflowDependencyEdge list
      UnconditionalObligations: GitHubWorkflowObligation list; ImpactCases: GitHubWorkflowImpactCase list
      ChildOutcomes: GitHubWorkflowChildOutcome list; RequiredAggregates: string list
      UnconditionalCore: GitHubWorkflowObligation list; ObservationSha256: string
      Performance: GitHubWorkflowRepositoryPerformance list
      Sentinel: GitHubWorkflowSentinel; FleetSelectionEnabled: bool
      RemovalLedgerComplete: bool; RemovalLedgerSha256: string; Removals: GitHubWorkflowRemoval list }
type GitHubWorkflowSelectionReport =
    { Repository: string; SourceRevision: string; WorkflowCount: int; ObligationCount: int
      ImpactCaseCount: int; RepositoryMetricCount: int; NotApplicableCount: int
      FleetSelectionEnabled: bool; MissedObligations: GitHubWorkflowObligation list; Seal: string }
type GitHubWorkflowSelectionFinding =
    | InvalidWorkflowSelectionField of string | IncompleteWorkflowSelectionInventory
    | InvalidWorkflowInventory | InvalidDependencyGraph | InvalidImpactCase of string
    | InvalidTransitiveClosure of string | InvalidAggregateOutcome
    | InvalidPerformanceEvidence of string | InvalidSentinelEvidence
    | InvalidFleetDisableDecision | InvalidRemovalLedger | AlteredWorkflowSelectionSeal
type GitHubWorkflowSelectionControl =
    | WorkflowPrerequisite | WorkflowRoadmap | WorkflowCompleteness | TypedWorkflowInventory
    | WorkflowGraphVersion | ChangedSubjectSelection | NonFileInputSelection | TransitiveClosure
    | UnconditionalObligations | StableAggregates | TypedNotApplicable | NoExpensiveProvisioning
    | AmbiguousImpactRefusal | StaleImpactRefusal | MergeGroupRecomputation
    | RepresentativeChanges | MixedChanges | UnknownChanges | WorkflowOrdering | ExactWorkflowSeal
    | ExactWorkflowReplay | QuintWorkflowUnchanged | NoWorkflowMutationSurface
type GitHubWorkflowSupplyChainControl =
    | FleetBaselines | AcceptedTargets | WorkflowFanOutTarget | JobFanOutTarget
    | BilledMinuteTarget | QueueTimeTarget | P50Target | P95Target | ScheduledSentinel
    | MissedObligationDetection | FleetDisable | RemovalLedger
type GitHubWorkflowControlResult<'control> =
    { Control: 'control; ControlPassed: bool; BaselineGreen: bool }
type GitHubWorkflowQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubWorkflowSelectionQualification =
    let requiredObligations = [ Build; Test; Policy; Coordination; Packaging; Release ]
    let requiredSelectionControls =
        [ WorkflowPrerequisite; WorkflowRoadmap; WorkflowCompleteness; TypedWorkflowInventory
          WorkflowGraphVersion; ChangedSubjectSelection; NonFileInputSelection; TransitiveClosure
          UnconditionalObligations; StableAggregates; TypedNotApplicable; NoExpensiveProvisioning
          AmbiguousImpactRefusal; StaleImpactRefusal; MergeGroupRecomputation
          RepresentativeChanges; MixedChanges; UnknownChanges; WorkflowOrdering; ExactWorkflowSeal
          ExactWorkflowReplay; QuintWorkflowUnchanged; NoWorkflowMutationSurface ]
    let requiredSupplyChainControls =
        [ FleetBaselines; AcceptedTargets; WorkflowFanOutTarget; JobFanOutTarget
          BilledMinuteTarget; QueueTimeTarget; P50Target; P95Target; ScheduledSentinel
          MissedObligationDetection; FleetDisable; RemovalLedger ]

    let obligationId = function
        | Build -> "build" | Test -> "test" | Policy -> "policy" | Coordination -> "coordination"
        | Packaging -> "packaging" | Release -> "release"
    let selectionControlId = function
        | WorkflowPrerequisite -> "workflow-prerequisite" | WorkflowRoadmap -> "workflow-roadmap"
        | WorkflowCompleteness -> "workflow-completeness" | TypedWorkflowInventory -> "typed-workflow-inventory"
        | WorkflowGraphVersion -> "workflow-graph-version" | ChangedSubjectSelection -> "changed-subject-selection"
        | NonFileInputSelection -> "non-file-input-selection" | TransitiveClosure -> "transitive-closure"
        | UnconditionalObligations -> "unconditional-obligations" | StableAggregates -> "stable-aggregates"
        | TypedNotApplicable -> "typed-not-applicable" | NoExpensiveProvisioning -> "no-expensive-provisioning"
        | AmbiguousImpactRefusal -> "ambiguous-impact-refusal" | StaleImpactRefusal -> "stale-impact-refusal"
        | MergeGroupRecomputation -> "merge-group-recomputation" | RepresentativeChanges -> "representative-changes"
        | MixedChanges -> "mixed-changes" | UnknownChanges -> "unknown-changes"
        | WorkflowOrdering -> "workflow-ordering" | ExactWorkflowSeal -> "exact-workflow-seal"
        | ExactWorkflowReplay -> "exact-workflow-replay" | QuintWorkflowUnchanged -> "quint-workflow-unchanged"
        | NoWorkflowMutationSurface -> "no-workflow-mutation-surface"
    let supplyChainControlId = function
        | FleetBaselines -> "fleet-baselines" | AcceptedTargets -> "accepted-targets"
        | WorkflowFanOutTarget -> "workflow-fan-out-target" | JobFanOutTarget -> "job-fan-out-target"
        | BilledMinuteTarget -> "billed-minute-target" | QueueTimeTarget -> "queue-time-target"
        | P50Target -> "p50-target" | P95Target -> "p95-target" | ScheduledSentinel -> "scheduled-sentinel"
        | MissedObligationDetection -> "missed-obligation-detection" | FleetDisable -> "fleet-disable"
        | RemovalLedger -> "removal-ledger"

    let private expectedRepositories =
        [ "EHotwagner/S.I.R."; "FS-GG/.github"; "FS-GG/FS.GG.Audio"; "FS-GG/FS.GG.Coordination"
          "FS-GG/FS.GG.Game"; "FS-GG/FS.GG.Governance"; "FS-GG/FS.GG.Net"
          "FS-GG/FS.GG.Rendering"; "FS-GG/FS.GG.SDD"; "FS-GG/FS.GG.Templates" ]
    let private expectedCases =
        [ "dependency"; "documentation"; "generated-output"; "merge-group"; "mixed"; "non-file-input"
          "policy"; "release"; "source"; "test"; "workflow" ]
    let private expectedWorkflows =
        [ ".github/workflows/bootstrap-qualification.yml"; ".github/workflows/candidate-supply-chain.yml"
          ".github/workflows/reusable-obligation-selection.yml"; ".github/workflows/workflow-selection-sentinel.yml" ]
    let private expectedGraph =
        [ { Source = Build; Target = Test }; { Source = Test; Target = Policy }
          { Source = Policy; Target = Coordination }; { Source = Packaging; Target = Release } ]
    let private expectedImpactBindings =
        [ "dependency", [ "Directory.Packages.props" ], [], [ Build; Packaging ]
          "documentation", [ "docs/architecture.md" ], [], [ Coordination ]
          "generated-output", [ "src/Generated.fs" ], [], [ Build ]
          "merge-group", [ "src/Merge.fs" ], [ "merge-group-settings" ], [ Build ]
          "mixed", [ "tests/Mixed.fs"; "eng/release.json" ], [], [ Test; Packaging ]
          "non-file-input", [], [ "renovate-policy-revision" ], [ Packaging ]
          "policy", [ "eng/policy.json" ], [], [ Policy ]
          "release", [ "eng/release-plan.json" ], [], [ Release ]
          "source", [ "src/Core.fs" ], [], [ Build ]
          "test", [ "tests/CoreTests.fs" ], [], [ Test ]
          "workflow", [ ".github/workflows/bootstrap-qualification.yml" ], [], [ Policy ] ]
    let private expectedChildOutcomes =
        [ { Obligation = Build; Disposition = Selected; ExpensiveJobProvisioned = false }
          { Obligation = Test; Disposition = Selected; ExpensiveJobProvisioned = false }
          { Obligation = Policy; Disposition = Selected; ExpensiveJobProvisioned = false }
          { Obligation = Coordination; Disposition = Selected; ExpensiveJobProvisioned = false }
          { Obligation = Packaging; Disposition = NotApplicable "source change does not reach packaging"; ExpensiveJobProvisioned = false }
          { Obligation = Release; Disposition = NotApplicable "source change does not reach release"; ExpensiveJobProvisioned = false } ]
    let private expectedRemovals: GitHubWorkflowRemoval list = []

    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)
    let private isRevision (value: string) = validText value && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
    let private isSha (value: string) = validText value && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount(value)}:{value}"
    let private boolText value = if value then "true" else "false"
    let private ordered values = values = (values |> List.sort)
    let private unique (values: 'a list) = values.Length = (values |> Set.ofList |> Set.count)
    let private obligationOrder value = requiredObligations |> List.findIndex ((=) value)
    let private orderObligations values = values |> List.distinct |> List.sortBy obligationOrder
    let private obligationList values = values |> List.map obligationId |> String.concat ","

    let private closure edges unconditional roots =
        let rec visit seen =
            let added =
                edges
                |> List.choose (fun edge -> if Set.contains edge.Source seen then Some edge.Target else None)
                |> Set.ofList
                |> Set.difference <| seen
            if Set.isEmpty added then seen else visit (Set.union seen added)
        Set.ofList (roots @ unconditional) |> visit |> Set.toList |> orderObligations

    let private dispositionId = function Selected -> "selected" | NotApplicable reason -> $"not-applicable:{reason}"
    let private metricsText value =
        [ value.WorkflowFanOut; value.JobFanOut; value.BilledMinutes; value.QueueTimeSeconds; value.P50Seconds; value.P95Seconds ]
        |> List.map string |> String.concat ","
    let private provenanceText value =
        [ value.ObservationId; value.Query; value.RunIds |> List.map string |> String.concat ","
          String.concat "," value.Revisions; value.ObservedAt.ToString("O"); value.WindowStart.ToString("O")
          value.WindowEnd.ToString("O"); string value.RunSampleCount; string value.JobSampleCount
          value.Aggregation; boolText value.Complete; boolText value.IndependentRecomputed
          value.ReviewedBy; value.ReviewedAt.ToString("O"); value.TargetRationale ] |> String.concat "|"
    let private mergeGroupText = function
        | None -> ""
        | Some value ->
            [ value.QueuedHead; value.CurrentBase; value.CurrentSettings; value.ObservedBase
              value.ObservedSettings; boolText value.Recomputed ] |> String.concat ","

    let private seal (snapshot: GitHubWorkflowSelectionSnapshot) =
        [ string snapshot.SchemaVersion; snapshot.Repository; snapshot.SourceRevision; snapshot.RoadmapRevision
          snapshot.RoadmapSha256; snapshot.PrerequisiteReceiptDigest; boolText snapshot.Complete
          boolText snapshot.InventoryComplete; boolText snapshot.NonFileInputInventoryComplete; snapshot.GraphVersion
          snapshot.Workflows |> List.map (fun row ->
              [ row.Workflow; String.concat "," row.PolicyJobs; String.concat "," row.CompositeSteps
                String.concat "," row.ReusableJobContracts; String.concat "," row.AggregateOutputs ] |> String.concat "|") |> String.concat ";"
          obligationList snapshot.Obligations
          snapshot.DependencyEdges |> List.map (fun edge -> $"{obligationId edge.Source}>{obligationId edge.Target}") |> String.concat ","
          obligationList snapshot.UnconditionalObligations
          snapshot.ImpactCases |> List.map (fun item ->
              [ item.Id; String.concat "," item.ChangedSubjects; String.concat "," item.NonFileInputs
                obligationList item.Roots; obligationList item.ExpectedClosure; boolText item.Unknown
                boolText item.Ambiguous; boolText item.Fresh; boolText item.Complete; mergeGroupText item.MergeGroup ] |> String.concat "|") |> String.concat ";"
          snapshot.ChildOutcomes |> List.map (fun item -> $"{obligationId item.Obligation}:{dispositionId item.Disposition}:{boolText item.ExpensiveJobProvisioned}") |> String.concat ","
          String.concat "," snapshot.RequiredAggregates; obligationList snapshot.UnconditionalCore; snapshot.ObservationSha256
          snapshot.Performance |> List.map (fun item -> $"{item.Repository}|{metricsText item.Baseline}|{metricsText item.Target}|{metricsText item.Selected}|{item.SelectedKind}|{provenanceText item.Provenance}") |> String.concat ";"
          boolText snapshot.Sentinel.Scheduled; obligationList snapshot.Sentinel.SelectedClosure
          obligationList snapshot.Sentinel.ActualFailures; boolText snapshot.FleetSelectionEnabled
          boolText snapshot.RemovalLedgerComplete; snapshot.RemovalLedgerSha256
          snapshot.Removals |> List.map (fun item -> $"{item.Workflow}|{item.Obligation}|{item.Reason}") |> String.concat ";" ]
        |> List.map frame |> String.concat "" |> sha

    let private metricsPositive value =
        [ value.WorkflowFanOut; value.JobFanOut; value.BilledMinutes; value.QueueTimeSeconds; value.P50Seconds; value.P95Seconds ]
        |> List.forall (fun count -> count > 0)
    let private metricsAtMost left right =
        left.WorkflowFanOut <= right.WorkflowFanOut && left.JobFanOut <= right.JobFanOut
        && left.BilledMinutes <= right.BilledMinutes && left.QueueTimeSeconds <= right.QueueTimeSeconds
        && left.P50Seconds <= right.P50Seconds && left.P95Seconds <= right.P95Seconds

    let compile (snapshot: GitHubWorkflowSelectionSnapshot) =
        let findings = ResizeArray<GitHubWorkflowSelectionFinding>()
        let invalid name value = if not (validText value) then findings.Add(InvalidWorkflowSelectionField name)
        invalid "repository" snapshot.Repository; invalid "sourceRevision" snapshot.SourceRevision
        invalid "roadmapRevision" snapshot.RoadmapRevision; invalid "graphVersion" snapshot.GraphVersion
        if snapshot.SchemaVersion <> 1 then findings.Add(InvalidWorkflowSelectionField "schemaVersion")
        if not (isRevision snapshot.SourceRevision) then findings.Add(InvalidWorkflowSelectionField "sourceRevision")
        if not (isRevision snapshot.RoadmapRevision) then findings.Add(InvalidWorkflowSelectionField "roadmapRevision")
        if not (isSha snapshot.RoadmapSha256) then findings.Add(InvalidWorkflowSelectionField "roadmapSha256")
        if not (isSha snapshot.PrerequisiteReceiptDigest) then findings.Add(InvalidWorkflowSelectionField "prerequisiteReceiptDigest")
        if not (isSha snapshot.ObservationSha256) then findings.Add(InvalidPerformanceEvidence "observation-digest")
        if not (isSha snapshot.RemovalLedgerSha256) then findings.Add(InvalidRemovalLedger)
        if snapshot.Repository <> "FS-GG/FS.GG.Coordination" then findings.Add(InvalidWorkflowSelectionField "repositoryBinding")
        if snapshot.SourceRevision <> "57305e540267f3f4696ba5a6cdfc84361de577d3" then findings.Add(InvalidWorkflowSelectionField "sourceRevisionBinding")
        if snapshot.RoadmapRevision <> "b6d4b60493d1f0b99daf73b98f4e8ad9bbbc0ed9"
           || snapshot.RoadmapSha256 <> "590d019dba1f7ce72338d8ca940e66e89d2e9f47d0454495938256c912a35b57" then
            findings.Add(InvalidWorkflowSelectionField "roadmapBinding")
        if snapshot.PrerequisiteReceiptDigest <> "517172e0eb31d3fd2eefb5844ed426d67d128f795c16195010eb772b7fcd2a5f" then
            findings.Add(InvalidWorkflowSelectionField "prerequisiteBinding")
        if not snapshot.Complete || not snapshot.InventoryComplete || not snapshot.NonFileInputInventoryComplete then
            findings.Add(IncompleteWorkflowSelectionInventory)
        let workflowNames = snapshot.Workflows |> List.map _.Workflow
        let inventoryListsValid row =
            [ row.PolicyJobs; row.CompositeSteps; row.ReusableJobContracts; row.AggregateOutputs ]
            |> List.forall (fun values -> unique values && ordered values && List.forall validText values)
        let compositeInventory = snapshot.Workflows |> List.collect _.CompositeSteps |> Set.ofList
        let reusableInventory = snapshot.Workflows |> List.collect _.ReusableJobContracts |> Set.ofList
        let aggregateInventory = snapshot.Workflows |> List.collect _.AggregateOutputs |> Set.ofList
        if workflowNames <> expectedWorkflows || not (unique workflowNames) || not (List.forall inventoryListsValid snapshot.Workflows)
           || compositeInventory <> Set [ "coordination-setup" ]
           || reusableInventory <> Set [ "obligation-selection" ]
           || aggregateInventory <> Set [ "required"; "sentinel"; "supply-chain" ] then
            findings.Add(InvalidWorkflowInventory)
        if snapshot.Obligations <> requiredObligations || snapshot.GraphVersion <> "fsgg.workflow-impact/1"
           || snapshot.DependencyEdges <> expectedGraph || not (unique snapshot.DependencyEdges)
           || snapshot.DependencyEdges |> List.exists (fun edge -> edge.Source = edge.Target) then findings.Add(InvalidDependencyGraph)
        if snapshot.UnconditionalObligations <> [ Policy ] || snapshot.UnconditionalCore <> [ Policy ] then
            findings.Add(InvalidDependencyGraph)
        let caseIds = snapshot.ImpactCases |> List.map _.Id
        if caseIds <> expectedCases || not (unique caseIds) then findings.Add(InvalidImpactCase "inventory")
        for item in snapshot.ImpactCases do
            if not (validText item.Id) || (List.isEmpty item.ChangedSubjects && List.isEmpty item.NonFileInputs)
               || not (item.ChangedSubjects |> List.forall validText) || not (item.NonFileInputs |> List.forall validText)
               || item.Unknown || item.Ambiguous || not item.Fresh || not item.Complete then findings.Add(InvalidImpactCase item.Id)
            match expectedImpactBindings |> List.tryFind (fun (id, _, _, _) -> id = item.Id) with
            | Some(_, changedSubjects, nonFileInputs, roots)
                when item.ChangedSubjects = changedSubjects && item.NonFileInputs = nonFileInputs && item.Roots = roots -> ()
            | _ -> findings.Add(InvalidImpactCase item.Id)
            let actual = closure snapshot.DependencyEdges (snapshot.UnconditionalObligations @ snapshot.UnconditionalCore) item.Roots
            if item.ExpectedClosure <> actual then findings.Add(InvalidTransitiveClosure item.Id)
            match item.Id, item.MergeGroup with
            | "merge-group", Some mergeGroup when isRevision mergeGroup.QueuedHead && isRevision mergeGroup.CurrentBase
                                                   && isSha mergeGroup.CurrentSettings && mergeGroup.CurrentBase = mergeGroup.ObservedBase
                                                   && mergeGroup.CurrentSettings = mergeGroup.ObservedSettings && mergeGroup.Recomputed -> ()
            | "merge-group", _ -> findings.Add(InvalidImpactCase "merge-group")
            | _, Some _ -> findings.Add(InvalidImpactCase item.Id)
            | _ -> ()
        let outcomes = snapshot.ChildOutcomes |> List.map _.Obligation
        if outcomes <> requiredObligations || snapshot.ChildOutcomes <> expectedChildOutcomes
           || snapshot.RequiredAggregates <> [ "required"; "supply-chain" ] then
            findings.Add(InvalidAggregateOutcome)
        for item in snapshot.ChildOutcomes do
            match item.Disposition with
            | NotApplicable reason when not (validText reason) || item.ExpensiveJobProvisioned -> findings.Add(InvalidAggregateOutcome)
            | Selected when (item.Obligation = Packaging || item.Obligation = Release) && not item.ExpensiveJobProvisioned -> findings.Add(InvalidAggregateOutcome)
            | _ -> ()
        if snapshot.ChildOutcomes |> List.exists (fun item -> match item.Disposition with NotApplicable _ -> true | _ -> false) |> not then
            findings.Add(InvalidAggregateOutcome)
        let performanceRepositories = snapshot.Performance |> List.map _.Repository
        if performanceRepositories <> expectedRepositories || not (unique performanceRepositories) then
            findings.Add(InvalidPerformanceEvidence "repository-inventory")
        let allRunIds = snapshot.Performance |> List.collect (fun item -> item.Provenance.RunIds)
        if allRunIds.Length <> (allRunIds |> Set.ofList |> Set.count) then findings.Add(InvalidPerformanceEvidence "duplicate-run-id")
        let baselineRows = snapshot.Performance |> List.map _.Baseline |> Set.ofList
        if baselineRows.Count < 2 then findings.Add(InvalidPerformanceEvidence "fabricated-uniform-baselines")
        for item in snapshot.Performance do
            let provenance = item.Provenance
            let validWindow = provenance.WindowStart < provenance.WindowEnd && provenance.WindowEnd <= provenance.ObservedAt
                              && provenance.ObservedAt - provenance.WindowEnd <= TimeSpan.FromHours 24.0
                              && provenance.WindowEnd - provenance.WindowStart <= TimeSpan.FromDays 15.0
            let validProvenance =
                validText provenance.ObservationId && validText provenance.Query
                && provenance.Query.Contains(item.Repository, StringComparison.Ordinal)
                && provenance.RunSampleCount = provenance.RunIds.Length && provenance.RunSampleCount >= 5
                && provenance.JobSampleCount >= provenance.RunSampleCount
                && unique provenance.RunIds && not provenance.Revisions.IsEmpty && unique provenance.Revisions
                && provenance.Revisions |> List.forall isRevision
                && provenance.Aggregation = "workflow-baseline-p95/1" && provenance.Complete
                && provenance.IndependentRecomputed && validText provenance.ReviewedBy
                && provenance.ReviewedAt >= provenance.ObservedAt && validText provenance.TargetRationale
                && validWindow
            if not (metricsPositive item.Baseline) || not (metricsPositive item.Target) || not (metricsPositive item.Selected)
               || not (metricsAtMost item.Target item.Baseline) || not (metricsAtMost item.Selected item.Target) then
                findings.Add(InvalidPerformanceEvidence item.Repository)
            if item.SelectedKind <> "reviewed-target-projection" || not validProvenance then
                findings.Add(InvalidPerformanceEvidence $"{item.Repository}:provenance")
        let selected = Set.ofList snapshot.Sentinel.SelectedClosure
        let missed = snapshot.Sentinel.ActualFailures |> List.filter (fun value -> not (Set.contains value selected)) |> orderObligations
        if not snapshot.Sentinel.Scheduled || snapshot.Sentinel.SelectedClosure <> orderObligations snapshot.Sentinel.SelectedClosure
           || snapshot.Sentinel.ActualFailures <> orderObligations snapshot.Sentinel.ActualFailures then findings.Add(InvalidSentinelEvidence)
        if snapshot.FleetSelectionEnabled <> List.isEmpty missed then findings.Add(InvalidFleetDisableDecision)
        let removalKeys = snapshot.Removals |> List.map (fun value -> value.Workflow, value.Obligation)
        if not snapshot.RemovalLedgerComplete || snapshot.Removals <> expectedRemovals || not (unique removalKeys)
           || snapshot.Removals |> List.exists (fun value -> not (validText value.Workflow && validText value.Obligation && validText value.Reason)) then
            findings.Add(InvalidRemovalLedger)
        if findings.Count > 0 then Error(List.ofSeq findings)
        else
            let notApplicable = snapshot.ChildOutcomes |> List.filter (fun item -> match item.Disposition with NotApplicable _ -> true | _ -> false) |> List.length
            Ok { Repository = snapshot.Repository; SourceRevision = snapshot.SourceRevision
                 WorkflowCount = snapshot.Workflows.Length; ObligationCount = snapshot.Obligations.Length
                 ImpactCaseCount = snapshot.ImpactCases.Length; RepositoryMetricCount = snapshot.Performance.Length
                 NotApplicableCount = notApplicable; FleetSelectionEnabled = snapshot.FleetSelectionEnabled
                 MissedObligations = missed; Seal = seal snapshot }

    let verify expectedSeal snapshot =
        match compile snapshot with
        | Error findings -> Error findings
        | Ok report when report.Seal = expectedSeal -> Ok report
        | Ok _ -> Error [ AlteredWorkflowSelectionSeal ]

    let private validateInventory idOf required generated independent =
        let findings = ResizeArray<GitHubWorkflowQualificationFinding>()
        let validateSide side (values: GitHubWorkflowControlResult<'control> list) =
            let ids = values |> List.map (fun value -> idOf value.Control)
            let expected = required |> List.map idOf
            if ids <> expected then
                findings.Add { Code = "Q3-INVENTORY"; ControlId = side; Message = $"{side} control inventory differs" }
            values |> List.iter (fun value ->
                if not value.BaselineGreen then findings.Add { Code = "Q3-BASELINE"; ControlId = idOf value.Control; Message = $"{side} baseline is not green" }
                if not value.ControlPassed then findings.Add { Code = "Q3-CONTROL"; ControlId = idOf value.Control; Message = $"{side} control did not turn red" })
        validateSide "generated" generated; validateSide "independent" independent
        if findings.Count = 0 then Ok () else Error(List.ofSeq findings)

    let validateSelection generated independent =
        validateInventory selectionControlId requiredSelectionControls generated independent
    let validateSupplyChain generated independent =
        validateInventory supplyChainControlId requiredSupplyChainControls generated independent
