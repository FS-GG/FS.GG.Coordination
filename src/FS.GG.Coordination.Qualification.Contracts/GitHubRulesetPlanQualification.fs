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
    let requiredControls =
        [ PrerequisiteReceipt; ProfileBinding; CensusBinding; CurrentPolicyBinding; CompleteObservation
          RepositoryBoundary; StableOrdering; DefaultBranchTarget; ReleaseTagTarget; RequiredChecks
          ReviewPolicy; ConversationResolution; MergeMethods; AutoMerge; MergeQueue; BranchDeletion
          BypassAuthorization; ExceptionIdentity; ExceptionWindow; ExceptionScope; ObserveOnly
          Freshness; ExactSeal; ExactReplay; QuintUnchanged; NoApplySurface ]

    let controlId = function
        | PrerequisiteReceipt -> "prerequisite-receipt"
        | ProfileBinding -> "profile-binding"
        | CensusBinding -> "census-binding"
        | CurrentPolicyBinding -> "current-policy-binding"
        | CompleteObservation -> "complete-observation"
        | RepositoryBoundary -> "repository-boundary"
        | StableOrdering -> "stable-ordering"
        | DefaultBranchTarget -> "default-branch-target"
        | ReleaseTagTarget -> "release-tag-target"
        | RequiredChecks -> "required-checks"
        | ReviewPolicy -> "review-policy"
        | ConversationResolution -> "conversation-resolution"
        | MergeMethods -> "merge-methods"
        | AutoMerge -> "auto-merge"
        | MergeQueue -> "merge-queue"
        | BranchDeletion -> "branch-deletion"
        | BypassAuthorization -> "bypass-authorization"
        | ExceptionIdentity -> "exception-identity"
        | ExceptionWindow -> "exception-window"
        | ExceptionScope -> "exception-scope"
        | ObserveOnly -> "observe-only"
        | Freshness -> "freshness"
        | ExactSeal -> "exact-seal"
        | ExactReplay -> "exact-replay"
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
                      if not result.ControlPassed then
                          { Code = "RP-CONTROL-FAILED"; ControlId = control; Message = $"{source} control did not pass" } ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings
