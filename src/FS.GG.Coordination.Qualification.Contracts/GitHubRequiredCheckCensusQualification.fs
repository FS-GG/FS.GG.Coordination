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
    let requiredControls =
        [ PrerequisiteReceipt; ProfileBinding; SourceBinding; CompleteAuthorities; StableOrdering
          ExactIdentity; AuthorityUnion; ProvenanceRetention; ProducerCompleteness; PullRequestProduction
          MergeGroupProduction; EventFilters; JobConditions; DependencyClosure; RepositoryBoundary
          Freshness; StableAggregates; ExactSeal; ExactReplay; QuintUnchanged; NoPlanSurface; NoApplySurface ]

    let controlId = function
        | PrerequisiteReceipt -> "prerequisite-receipt"
        | ProfileBinding -> "profile-binding"
        | SourceBinding -> "source-binding"
        | CompleteAuthorities -> "complete-authorities"
        | StableOrdering -> "stable-ordering"
        | ExactIdentity -> "exact-identity"
        | AuthorityUnion -> "authority-union"
        | ProvenanceRetention -> "provenance-retention"
        | ProducerCompleteness -> "producer-completeness"
        | PullRequestProduction -> "pull-request-production"
        | MergeGroupProduction -> "merge-group-production"
        | EventFilters -> "event-filters"
        | JobConditions -> "job-conditions"
        | DependencyClosure -> "dependency-closure"
        | RepositoryBoundary -> "repository-boundary"
        | Freshness -> "freshness"
        | StableAggregates -> "stable-aggregates"
        | ExactSeal -> "exact-seal"
        | ExactReplay -> "exact-replay"
        | QuintUnchanged -> "quint-unchanged"
        | NoPlanSurface -> "no-plan-surface"
        | NoApplySurface -> "no-apply-surface"

    let validate generated independent =
        let expected = requiredControls |> List.map controlId |> Set.ofList
        let findingsFor source values =
            let grouped = values |> List.groupBy (fun value -> controlId value.Control)
            [ for missing in Set.difference expected (grouped |> List.map fst |> Set.ofList) do
                  { Code = "RC-CONTROL-MISSING"; ControlId = missing; Message = $"{source} omitted the required control" }
              for control, results in grouped do
                  if results.Length <> 1 then
                      { Code = "RC-CONTROL-DUPLICATE"; ControlId = control; Message = $"{source} supplied the control more than once" }
                  else
                      let result = results.Head
                      if not result.BaselineGreen then
                          { Code = "RC-BASELINE-RED"; ControlId = control; Message = $"{source} baseline is not green" }
                      if not result.MutationRed then
                          { Code = "RC-MUTATION-SURVIVED"; ControlId = control; Message = $"{source} mutation did not fail" } ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings
