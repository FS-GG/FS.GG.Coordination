namespace FS.GG.Coordination.GitHub

open System

type RulesetPlanProfileClass = AuthorityPlan | FrameworkPlan | HostedNonParticipantPlan | ObserveOnlyPlan
type RulesetBypassActorKind = TeamActor | IntegrationActor
type RulesetMergeMethod = Squash | MergeCommit | Rebase

type RulesetRequiredCheck =
    { Context: string
      IntegrationId: int64 option }

type ApprovedRulesetBypassPrincipal =
    { ActorId: int64
      Kind: RulesetBypassActorKind
      AllowedProfiles: RulesetPlanProfileClass list }

type RequestedRulesetBypassPrincipal =
    { ActorId: int64
      Kind: RulesetBypassActorKind }

type RulesetExceptionScope =
    | RequiredReviewCount of int
    | ConversationResolution of bool
    | MergeQueue of bool
    | BypassPrincipal of RequestedRulesetBypassPrincipal

type RulesetPlanException =
    { Id: string
      Owner: string
      Rationale: string
      Scope: RulesetExceptionScope
      ApprovedAt: DateTimeOffset
      StartsAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

type DefaultBranchRulesetTarget =
    { Include: string list
      ProtectDeletion: bool
      BlockNonFastForward: bool
      RequirePullRequest: bool
      DismissStaleReviews: bool
      RequiredApprovals: int
      RequireConversationResolution: bool
      StrictChecks: bool
      RequiredChecks: RulesetRequiredCheck list
      Bypass: RequestedRulesetBypassPrincipal list
      MergeQueueEnabled: bool
      MergeQueueDisabledReason: string option }

type ReleaseTagRulesetTarget =
    { Include: string list
      ProtectDeletion: bool
      BlockNonFastForward: bool
      BlockUpdate: bool
      RequireSignatures: bool
      Bypass: RequestedRulesetBypassPrincipal list }

type RepositoryMergePolicyTarget =
    { AllowedMergeMethods: RulesetMergeMethod list
      AllowAutoMerge: bool
      DeleteBranchOnMerge: bool }

type RulesetPlanSnapshot =
    { SchemaVersion: int
      Repository: string
      PrerequisiteReceiptDigest: string
      ProfileSnapshot: RepositoryRosterSnapshot
      ExpectedProfileSeal: string
      CensusSnapshot: RequiredCheckCensusSnapshot option
      ExpectedCensusSeal: string option
      CurrentPolicyRevision: string
      CurrentPolicyEvidenceSha256: string
      ObservedAt: DateTimeOffset
      Complete: bool
      ApprovedBypass: ApprovedRulesetBypassPrincipal list
      RequestedBypass: RequestedRulesetBypassPrincipal list
      Exceptions: RulesetPlanException list }

type RulesetPlanReport =
    { Repository: string
      ProfileClass: RulesetPlanProfileClass
      MutationPermitted: bool
      PrerequisiteReceiptDigest: string
      ProfileReportSeal: string
      CensusSeal: string option
      CurrentPolicyRevision: string
      CurrentPolicyEvidenceSha256: string
      DefaultBranch: DefaultBranchRulesetTarget option
      ReleaseTags: ReleaseTagRulesetTarget option
      RepositoryPolicy: RepositoryMergePolicyTarget option
      ActiveExceptions: RulesetPlanException list
      Seal: string }

type RulesetPlanFinding =
    | UnsupportedRulesetPlanSchema of int
    | InvalidRulesetPlanRepository of string
    | IncompleteRulesetPlanObservation
    | StaleRulesetPlanObservation
    | InvalidRulesetPlanBinding of string
    | CrossRepositoryRulesetProfile of string
    | InconsistentRulesetProfileAdministration of string
    | MissingRequiredCheckCensus of string
    | UnexpectedRequiredCheckCensus of string
    | CrossRepositoryRequiredCheckCensus of string
    | AlteredRulesetProfileBinding
    | InvalidRulesetBypassPrincipal of int64
    | DuplicateRulesetBypassPrincipal of int64
    | UnauthorizedRulesetBypassPrincipal of int64
    | InvalidRulesetException of string
    | DuplicateRulesetException of string
    | InactiveRulesetException of string
    | OverlongRulesetException of string
    | AlteredRulesetPlanSeal

module RulesetPlanAdapter =
    val compile: asOf: DateTimeOffset -> maxAge: TimeSpan -> RulesetPlanSnapshot -> Result<RulesetPlanReport, RulesetPlanFinding list>
    val verify: expectedSeal: string -> asOf: DateTimeOffset -> maxAge: TimeSpan -> RulesetPlanSnapshot -> Result<RulesetPlanReport, RulesetPlanFinding list>
