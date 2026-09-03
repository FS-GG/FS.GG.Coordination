namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubFleetDisposition =
    | Supported | Unsupported | Unauthorized | Unavailable | Incomplete | Unreadable
    | Stale | Indeterminate | ExternalObserveOnly | NoOp

type GitHubFleetPaginationProof =
    { Kind: string; Pages: int; ItemCount: int; Terminal: bool; Next: string option }

type GitHubFleetEndpointObservation =
    { Endpoint: string; StatusCode: int; Permission: string
      Pagination: GitHubFleetPaginationProof; PayloadSha256: string
      RelevantFingerprint: string; Disposition: GitHubFleetDisposition }

type GitHubFleetRepositoryObservation =
    { Repository: string; DefaultBranch: string; ObservedAt: System.DateTimeOffset
      Complete: bool; Endpoints: GitHubFleetEndpointObservation list }

type GitHubFleetDesiredSetting =
    { Setting: string; DesiredSha256: string; RequiredPermission: string
      RollbackOrForwardRepair: string }

type GitHubFleetRepositoryTarget =
    { Repository: string; ExternalOwner: bool; Settings: GitHubFleetDesiredSetting list }

type GitHubFleetOperation =
    { Id: string; Repository: string; Setting: string; Action: string
      PreStateSha256: string; DesiredSha256: string; RequiredPermission: string
      RollbackOrForwardRepair: string }

type GitHubFleetRepositoryPlan =
    { Repository: string; DefaultBranch: string; ObservedAt: System.DateTimeOffset
      PreStateSha256: string; DesiredStateSha256: string; Disposition: GitHubFleetDisposition
      Operations: GitHubFleetOperation list; PreservesUnrelatedSettings: bool }

type GitHubFleetDryPlan =
    { SchemaVersion: int; RoadmapRevision: string; RoadmapSha256: string
      UnitContractSha256: string; SourceRevision: string; ReceiptDigests: string list
      Roster: string list; Plans: GitHubFleetRepositoryPlan list; Seal: string }

type GitHubFleetPlanReview =
    { Reviewer: string; ReviewedAt: System.DateTimeOffset; PlanSha256: string
      Independent: bool; Accepted: bool; EvidenceSha256: string }

type GitHubFleetReinspection =
    { Repository: string; ObservedAt: System.DateTimeOffset
      RelevantFingerprint: string; Complete: bool; Authoritative: bool }

type GitHubFleetReinspectionResult = Confirmed | PlanStale of repositories: string list

[<RequireQualifiedAccess>]
type GitHubFleetDryPlanFinding =
    | InvalidFleetField of string | InvalidFleetAuthority of string | InvalidFleetRoster
    | IncompleteFleetObservation of string | InvalidFleetPagination of string
    | InvalidFleetDisposition of string | InvalidFleetTarget of string
    | InvalidFleetOperation of string | InvalidFleetReview | InvalidFleetReinspection of string
    | AlteredFleetSeal | InvalidFleetSerialization of string

type GitHubFleetControl =
    | FleetPrerequisites | FleetRoadmap | FleetRoster | FleetCompleteness | FleetPagination
    | FleetRepositoryIdentity | FleetDefaultBranch | FleetObservationTime | FleetPreState
    | FleetDesiredState | FleetOperationIdentity | FleetOrdering | FleetLeastPermission
    | FleetSupported | FleetUnsupported | FleetUnauthorized | FleetUnavailable | FleetIncomplete
    | FleetUnreadable | FleetStale | FleetIndeterminate | FleetExternalOwner | FleetNoOp
    | FleetUnrelatedSetting | FleetReview | FleetReinspection | FleetSerialization | FleetReplay
    | FleetComprehensiveGate | FleetOmission | FleetQuintPreservation | FleetNoApply | FleetNoMutation

type GitHubFleetControlResult =
    { Control: GitHubFleetControl; ControlPassed: bool; BaselineGreen: bool }

type GitHubFleetQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubFleetDryPlanQualification =
    val expectedRepositories: string list
    val expectedEndpoints: string list
    val expectedReceiptDigests: string list
    val requiredControls: GitHubFleetControl list
    val dispositionId: GitHubFleetDisposition -> string
    val controlId: GitHubFleetControl -> string
    val compile:
        roadmapRevision:string -> roadmapSha256:string -> unitContractSha256:string ->
        sourceRevision:string -> receiptDigests:string list -> roster:string list ->
        observations:GitHubFleetRepositoryObservation list -> targets:GitHubFleetRepositoryTarget list ->
        Result<GitHubFleetDryPlan, GitHubFleetDryPlanFinding list>
    val review: reviewer:string -> reviewedAt:System.DateTimeOffset -> planBytes:string -> GitHubFleetPlanReview
    val reinspect:
        plan:GitHubFleetDryPlan -> review:GitHubFleetPlanReview ->
        observations:GitHubFleetReinspection list -> Result<GitHubFleetReinspectionResult, GitHubFleetDryPlanFinding list>
    val serialize: GitHubFleetDryPlan -> string
    val parse: string -> Result<GitHubFleetDryPlan, GitHubFleetDryPlanFinding list>
    val verify: expectedSeal:string -> GitHubFleetDryPlan -> Result<GitHubFleetDryPlan, GitHubFleetDryPlanFinding list>
    val validateControls:
        generated:GitHubFleetControlResult list -> independent:GitHubFleetControlResult list ->
        Result<unit, GitHubFleetQualificationFinding list>
