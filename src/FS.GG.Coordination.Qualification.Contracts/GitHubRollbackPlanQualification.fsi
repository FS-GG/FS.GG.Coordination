namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubRollbackDomain =
    | Settings
    | ReceiverPin
    | V1Projection
    | Schedule
    | AuthoritySnapshot

type GitHubRollbackStep =
    { Order: int
      StepId: string
      Domain: GitHubRollbackDomain
      TargetIdentity: string
      CapturedStateSha256: string
      RestorePayloadSha256: string }

type GitHubRollbackReceipt =
    { Order: int
      StepId: string
      PlanSeal: string
      PreviousReceiptSha256: string option
      ResultSha256: string
      ReceiptSha256: string }

type GitHubRollbackPlan =
    { Identity: string
      RoadmapRevision: string
      RoadmapSha256: string
      UnitContractSha256: string
      PredecessorReceiptDigest: string
      ManifestNormalizedDigest: string
      ManifestSeal: string
      HistoryNormalizedDigest: string
      HistorySeal: string
      StartEpoch: string
      TerminalEpoch: string
      Steps: GitHubRollbackStep list
      NormalizedDigest: string
      Seal: string
      CreatedAt: DateTimeOffset }

type GitHubRollbackPlanFinding =
    | InvalidIdentity
    | InvalidAuthority
    | InvalidEpochBoundary
    | InvalidStepPopulation
    | InvalidStep of string
    | MissingDomain of GitHubRollbackDomain
    | AlteredNormalizedDigest
    | AlteredSeal
    | ReceiptPopulationMismatch
    | InvalidReceipt of string
    | ReceiptChainMismatch of string

type GitHubRollbackPlanControlResult =
    { Control: string
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubRollbackPlanQualification =
    val requiredDomains: GitHubRollbackDomain list
    val requiredControls: string list
    val qualify:
        identity:string -> roadmapRevision:string -> roadmapSha256:string -> unitContractSha256:string ->
        predecessorReceiptDigest:string -> manifestNormalizedDigest:string -> manifestSeal:string ->
        historyNormalizedDigest:string -> historySeal:string -> startEpoch:string ->
        steps:GitHubRollbackStep list -> createdAt:DateTimeOffset -> Result<GitHubRollbackPlan, GitHubRollbackPlanFinding list>
    val verify: expectedSeal:string -> plan:GitHubRollbackPlan -> Result<GitHubRollbackPlan, GitHubRollbackPlanFinding list>
    val createReceipt:
        plan:GitHubRollbackPlan -> previous:GitHubRollbackReceipt option -> step:GitHubRollbackStep -> resultSha256:string -> GitHubRollbackReceipt
    val resume:
        plan:GitHubRollbackPlan -> receipts:GitHubRollbackReceipt list -> Result<GitHubRollbackStep option, GitHubRollbackPlanFinding list>
    val validateControls:
        primary:GitHubRollbackPlanControlResult list -> recovery:GitHubRollbackPlanControlResult list -> Result<unit, string list>
