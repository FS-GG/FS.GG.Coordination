namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type MergeGroupCheckConclusion = Success | Pending | Failure

type MergeGroupCheckFact =
    { Name: string
      EventName: string
      HeadSha: string
      Conclusion: MergeGroupCheckConclusion }

type GitHubMergeGroupFacts =
    { EventName: string
      Repository: string
      MergeGroupId: string
      MergeGroupHeadSha: string
      BaseRepository: string
      BaseRef: string
      ObservedBaseSha: string
      CurrentBaseSha: string
      BaseObservationRevision: int64
      CurrentBaseObservationRevision: int64
      ObservedAtUnixSeconds: int64
      FreshUntilUnixSeconds: int64
      EvaluatedAtUnixSeconds: int64
      RequiredChecks: string list
      CheckResults: MergeGroupCheckFact list
      ObservedClaimGeneration: int64
      CurrentClaimGeneration: int64
      ObservedReviewDigest: string
      CurrentReviewDigest: string
      ObservedCandidateHeadSha: string
      CurrentCandidateHeadSha: string
      ObservedDependencyDigest: string
      CurrentDependencyDigest: string
      ObservedReleaseObligationsMet: bool
      CurrentReleaseObligationsMet: bool
      ObservedSettingsDigest: string
      CurrentSettingsDigest: string
      HasConflictingGroup: bool
      AttemptsDirectMerge: bool }

type GitHubMergeGroupPlan =
    { SchemaVersion: int
      EventName: string
      Repository: string
      MergeGroupId: string
      MergeGroupHeadSha: string
      BaseRepository: string
      BaseRef: string
      BaseSha: string
      BaseObservationRevision: int64
      ObservedAtUnixSeconds: int64
      FreshUntilUnixSeconds: int64
      EvaluatedAtUnixSeconds: int64
      RequiredChecks: string list
      ClaimGeneration: int64
      ReviewDigest: string
      CandidateHeadSha: string
      DependencyDigest: string
      ReleaseObligationsMet: bool
      SettingsDigest: string
      Disposition: string
      Seal: string }

[<RequireQualifiedAccess>]
type GitHubMergeGroupFinding =
    | MissingField of string
    | MalformedField of string
    | UnknownEvent of string
    | BaseObservationStale of int64
    | BaseChanged of string * string
    | BaseRevisionChanged of int64 * int64
    | NonCanonicalCheckInventory
    | IncompleteCheckInventory
    | CheckDidNotRunForMergeGroup of string
    | CheckHeadMismatch of string
    | CheckPending of string
    | CheckFailed of string
    | ClaimChanged of int64 * int64
    | ReviewChanged
    | CandidateHeadChanged
    | DependencyChanged
    | ReleaseObligationsUnmet
    | ReleaseAuthorityChanged
    | SettingsChanged
    | ConflictingGroup
    | DirectMergeAttempt
    | AlteredSeal
    | ReplayConflict
    | InvalidSerialization of string

type GitHubMergeGroupControl =
    | MergeGroupPrerequisite | MergeGroupRoadmap | MergeGroupPositive | MergeGroupEvent
    | MergeGroupIdentity | MergeGroupHead | BaseRepositoryIdentity | BaseRef | BaseSha
    | BaseRevision | BaseFreshness | BaseChanged | RequiredCheckInventory | RequiredCheckEvent
    | RequiredCheckHead | RequiredCheckPending | RequiredCheckFailure | ClaimFreshness
    | ReviewFreshness | CandidateHeadFreshness | DependencyFreshness | ReleaseFreshness
    | SettingsFreshness | ConflictingGroup | DirectMerge | MergeGroupOrdering | MergeGroupSeal
    | MergeGroupReplay | MergeGroupQuintPreservation | MergeGroupNoNetwork
    | MergeGroupNoProductionMutation

type GitHubMergeGroupControlResult =
    { Control: GitHubMergeGroupControl
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubMergeGroupQualification =
    val eventName: string
    val disposition: string
    val requiredControls: GitHubMergeGroupControl list
    val controlId: GitHubMergeGroupControl -> string
    val compile: GitHubMergeGroupFacts -> Result<GitHubMergeGroupPlan, GitHubMergeGroupFinding list>
    val serialize: GitHubMergeGroupPlan -> string
    val parse: string -> Result<GitHubMergeGroupPlan, GitHubMergeGroupFinding list>
    val verify: expectedSeal: string -> GitHubMergeGroupPlan -> Result<GitHubMergeGroupPlan, GitHubMergeGroupFinding list>
    val replay: prior: GitHubMergeGroupPlan -> facts: GitHubMergeGroupFacts -> Result<GitHubMergeGroupPlan, GitHubMergeGroupFinding list>
    val validateControls: generated: GitHubMergeGroupControlResult list -> independent: GitHubMergeGroupControlResult list -> Result<unit, string list>
