namespace FS.GG.Coordination.Qualification.Contracts

type GitHubRulesetPlanControl =
    | PrerequisiteReceipt | ProfileBinding | CensusBinding | CurrentPolicyBinding | CompleteObservation
    | RepositoryBoundary | StableOrdering | DefaultBranchTarget | ReleaseTagTarget | RequiredChecks
    | ReviewPolicy | ConversationResolution | MergeMethods | AutoMerge | MergeQueue | BranchDeletion
    | BypassAuthorization | ExceptionIdentity | ExceptionWindow | ExceptionScope | ObserveOnly
    | Freshness | ExactSeal | ExactReplay | QuintUnchanged | NoApplySurface

type GitHubRulesetPlanControlResult =
    { Control: GitHubRulesetPlanControl
      ControlPassed: bool
      BaselineGreen: bool }

type GitHubRulesetPlanFinding = { Code: string; ControlId: string; Message: string }

module GitHubRulesetPlanQualification =
    val requiredControls: GitHubRulesetPlanControl list
    val controlId: GitHubRulesetPlanControl -> string
    val validate: generated: GitHubRulesetPlanControlResult list -> independent: GitHubRulesetPlanControlResult list -> Result<unit, GitHubRulesetPlanFinding list>
