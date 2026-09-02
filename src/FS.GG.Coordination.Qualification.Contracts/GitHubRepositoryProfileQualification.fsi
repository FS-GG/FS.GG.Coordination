namespace FS.GG.Coordination.Qualification.Contracts

type GitHubRepositoryProfileControl =
    | RosterSourceBinding | CompleteRoster | StableOrdering | IdentityUniqueness
    | RoleVocabulary | CapabilityVocabulary | RichAuthorityRetention
    | OrganizationPropertyProjection | ExternalObserveOnly | PropertyBounds
    | Freshness | ExactSeal | ExactReplay | PrerequisiteReceipts | QuintUnchanged | NoApplySurface

type GitHubRepositoryProfileControlResult =
    { Control: GitHubRepositoryProfileControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubRepositoryProfileFinding = { Code: string; ControlId: string; Message: string }

module GitHubRepositoryProfileQualification =
    val requiredControls: GitHubRepositoryProfileControl list
    val controlId: GitHubRepositoryProfileControl -> string
    val validate: generated: GitHubRepositoryProfileControlResult list -> independent: GitHubRepositoryProfileControlResult list -> Result<unit, GitHubRepositoryProfileFinding list>
