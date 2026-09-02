namespace FS.GG.Coordination.Qualification.Contracts

type GitHubRequiredCheckCensusControl =
    | PrerequisiteReceipt | ProfileBinding | SourceBinding | CompleteAuthorities | StableOrdering
    | ExactIdentity | AuthorityUnion | ProvenanceRetention | ProducerCompleteness
    | PullRequestProduction | MergeGroupProduction | EventFilters | JobConditions
    | DependencyClosure | RepositoryBoundary | Freshness | StableAggregates | ExactSeal
    | ExactReplay | QuintUnchanged | NoPlanSurface | NoApplySurface

type GitHubRequiredCheckCensusControlResult =
    { Control: GitHubRequiredCheckCensusControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubRequiredCheckCensusFinding = { Code: string; ControlId: string; Message: string }

module GitHubRequiredCheckCensusQualification =
    val requiredControls: GitHubRequiredCheckCensusControl list
    val controlId: GitHubRequiredCheckCensusControl -> string
    val validate: generated: GitHubRequiredCheckCensusControlResult list -> independent: GitHubRequiredCheckCensusControlResult list -> Result<unit, GitHubRequiredCheckCensusFinding list>
