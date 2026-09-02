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
    let requiredControls =
        [ RosterSourceBinding; CompleteRoster; StableOrdering; IdentityUniqueness; RoleVocabulary
          CapabilityVocabulary; RichAuthorityRetention; OrganizationPropertyProjection; ExternalObserveOnly
          PropertyBounds; Freshness; ExactSeal; ExactReplay; PrerequisiteReceipts; QuintUnchanged; NoApplySurface ]

    let controlId = function
        | RosterSourceBinding -> "roster-source-binding"
        | CompleteRoster -> "complete-roster"
        | StableOrdering -> "stable-ordering"
        | IdentityUniqueness -> "identity-uniqueness"
        | RoleVocabulary -> "role-vocabulary"
        | CapabilityVocabulary -> "capability-vocabulary"
        | RichAuthorityRetention -> "rich-authority-retention"
        | OrganizationPropertyProjection -> "organization-property-projection"
        | ExternalObserveOnly -> "external-observe-only"
        | PropertyBounds -> "property-bounds"
        | Freshness -> "freshness"
        | ExactSeal -> "exact-seal"
        | ExactReplay -> "exact-replay"
        | PrerequisiteReceipts -> "prerequisite-receipts"
        | QuintUnchanged -> "quint-unchanged"
        | NoApplySurface -> "no-apply-surface"

    let validate generated independent =
        let expected = requiredControls |> List.map controlId |> Set.ofList
        let findingsFor source values =
            let grouped = values |> List.groupBy (fun value -> controlId value.Control)
            [ for missing in Set.difference expected (grouped |> List.map fst |> Set.ofList) do
                  { Code = "RP-CONTROL-MISSING"; ControlId = missing; Message = $"{source} omitted the required control" }
              for control, results in grouped do
                  if results.Length <> 1 then
                      { Code = "RP-CONTROL-DUPLICATE"; ControlId = control; Message = $"{source} supplied the control more than once" }
                  else
                      let result = results.Head
                      if not result.BaselineGreen then
                          { Code = "RP-BASELINE-RED"; ControlId = control; Message = $"{source} baseline is not green" }
                      if not result.MutationRed then
                          { Code = "RP-MUTATION-SURVIVED"; ControlId = control; Message = $"{source} mutation did not fail" } ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings
