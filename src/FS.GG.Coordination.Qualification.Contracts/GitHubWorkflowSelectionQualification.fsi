namespace FS.GG.Coordination.Qualification.Contracts

type GitHubWorkflowObligation =
    | Build | Test | Policy | Coordination | Packaging | Release

type GitHubWorkflowInventoryRow =
    { Workflow: string
      PolicyJobs: string list
      CompositeSteps: string list
      ReusableJobContracts: string list
      AggregateOutputs: string list }

type GitHubWorkflowDependencyEdge = { Source: GitHubWorkflowObligation; Target: GitHubWorkflowObligation }

type GitHubMergeGroupImpact =
    { QueuedHead: string
      CurrentBase: string
      CurrentSettings: string
      ObservedBase: string
      ObservedSettings: string
      Recomputed: bool }

type GitHubWorkflowImpactCase =
    { Id: string
      ChangedSubjects: string list
      NonFileInputs: string list
      Roots: GitHubWorkflowObligation list
      ExpectedClosure: GitHubWorkflowObligation list
      Unknown: bool
      Ambiguous: bool
      Fresh: bool
      Complete: bool
      MergeGroup: GitHubMergeGroupImpact option }

type GitHubWorkflowChildDisposition = Selected | NotApplicable of reason: string
type GitHubWorkflowChildOutcome =
    { Obligation: GitHubWorkflowObligation
      Disposition: GitHubWorkflowChildDisposition
      ExpensiveJobProvisioned: bool }

type GitHubWorkflowMetrics =
    { WorkflowFanOut: int
      JobFanOut: int
      BilledMinutes: int
      QueueTimeSeconds: int
      P50Seconds: int
      P95Seconds: int }

type GitHubWorkflowRepositoryPerformance =
    { Repository: string
      Baseline: GitHubWorkflowMetrics
      Target: GitHubWorkflowMetrics
      Selected: GitHubWorkflowMetrics }

type GitHubWorkflowSentinel =
    { Scheduled: bool
      SelectedClosure: GitHubWorkflowObligation list
      ActualFailures: GitHubWorkflowObligation list }

type GitHubWorkflowRemoval = { Workflow: string; Obligation: string; Reason: string }

type GitHubWorkflowSelectionSnapshot =
    { SchemaVersion: int
      Repository: string
      SourceRevision: string
      RoadmapRevision: string
      RoadmapSha256: string
      PrerequisiteReceiptDigest: string
      Complete: bool
      InventoryComplete: bool
      NonFileInputInventoryComplete: bool
      GraphVersion: string
      Workflows: GitHubWorkflowInventoryRow list
      Obligations: GitHubWorkflowObligation list
      DependencyEdges: GitHubWorkflowDependencyEdge list
      UnconditionalObligations: GitHubWorkflowObligation list
      ImpactCases: GitHubWorkflowImpactCase list
      ChildOutcomes: GitHubWorkflowChildOutcome list
      RequiredAggregates: string list
      UnconditionalCore: GitHubWorkflowObligation list
      Performance: GitHubWorkflowRepositoryPerformance list
      Sentinel: GitHubWorkflowSentinel
      FleetSelectionEnabled: bool
      RemovalLedgerComplete: bool
      Removals: GitHubWorkflowRemoval list }

type GitHubWorkflowSelectionReport =
    { Repository: string
      SourceRevision: string
      WorkflowCount: int
      ObligationCount: int
      ImpactCaseCount: int
      RepositoryMetricCount: int
      NotApplicableCount: int
      FleetSelectionEnabled: bool
      MissedObligations: GitHubWorkflowObligation list
      Seal: string }

type GitHubWorkflowSelectionFinding =
    | InvalidWorkflowSelectionField of string
    | IncompleteWorkflowSelectionInventory
    | InvalidWorkflowInventory
    | InvalidDependencyGraph
    | InvalidImpactCase of string
    | InvalidTransitiveClosure of string
    | InvalidAggregateOutcome
    | InvalidPerformanceEvidence of string
    | InvalidSentinelEvidence
    | InvalidFleetDisableDecision
    | InvalidRemovalLedger
    | AlteredWorkflowSelectionSeal

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
    { Control: 'control
      ControlPassed: bool
      BaselineGreen: bool }

type GitHubWorkflowQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubWorkflowSelectionQualification =
    val requiredObligations: GitHubWorkflowObligation list
    val requiredSelectionControls: GitHubWorkflowSelectionControl list
    val requiredSupplyChainControls: GitHubWorkflowSupplyChainControl list
    val obligationId: GitHubWorkflowObligation -> string
    val selectionControlId: GitHubWorkflowSelectionControl -> string
    val supplyChainControlId: GitHubWorkflowSupplyChainControl -> string
    val compile: GitHubWorkflowSelectionSnapshot -> Result<GitHubWorkflowSelectionReport, GitHubWorkflowSelectionFinding list>
    val verify: string -> GitHubWorkflowSelectionSnapshot -> Result<GitHubWorkflowSelectionReport, GitHubWorkflowSelectionFinding list>
    val validateSelection: GitHubWorkflowControlResult<GitHubWorkflowSelectionControl> list -> GitHubWorkflowControlResult<GitHubWorkflowSelectionControl> list -> Result<unit, GitHubWorkflowQualificationFinding list>
    val validateSupplyChain: GitHubWorkflowControlResult<GitHubWorkflowSupplyChainControl> list -> GitHubWorkflowControlResult<GitHubWorkflowSupplyChainControl> list -> Result<unit, GitHubWorkflowQualificationFinding list>
