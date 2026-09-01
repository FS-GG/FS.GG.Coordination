namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubLifecycleProjectionControl = IntentAuthority | CompleteKnowledge | FormalPrecedence | HoldDependency | Claim | PullRequest | Review | Delivery | IssueState | StatusMapping | StatusNotIntent | HistoricalFact | ProtectedDelivery | ExactPlan | RevisionFence | ExactReplay | BoundedCost | QuintAndPrerequisite
type GitHubLifecycleProjectionControlResult = { Control: GitHubLifecycleProjectionControl; MutationRed: bool; BaselineGreen: bool }
type GitHubLifecycleProjectionFinding = { Code: string; ControlId: string; Message: string }
[<RequireQualifiedAccess>]
module GitHubLifecycleProjectionQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-lifecycle-projection-qualification/1"
    let requiredControls =
        [ GitHubLifecycleProjectionControl.IntentAuthority; GitHubLifecycleProjectionControl.CompleteKnowledge
          GitHubLifecycleProjectionControl.FormalPrecedence; GitHubLifecycleProjectionControl.HoldDependency
          GitHubLifecycleProjectionControl.Claim; GitHubLifecycleProjectionControl.PullRequest
          GitHubLifecycleProjectionControl.Review; GitHubLifecycleProjectionControl.Delivery
          GitHubLifecycleProjectionControl.IssueState; GitHubLifecycleProjectionControl.StatusMapping
          GitHubLifecycleProjectionControl.StatusNotIntent; GitHubLifecycleProjectionControl.HistoricalFact
          GitHubLifecycleProjectionControl.ProtectedDelivery; GitHubLifecycleProjectionControl.ExactPlan
          GitHubLifecycleProjectionControl.RevisionFence; GitHubLifecycleProjectionControl.ExactReplay
          GitHubLifecycleProjectionControl.BoundedCost; GitHubLifecycleProjectionControl.QuintAndPrerequisite ]
    let controlId = function
        | GitHubLifecycleProjectionControl.IntentAuthority -> "intent-authority"
        | GitHubLifecycleProjectionControl.CompleteKnowledge -> "complete-knowledge"
        | GitHubLifecycleProjectionControl.FormalPrecedence -> "formal-precedence"
        | GitHubLifecycleProjectionControl.HoldDependency -> "hold-dependency"
        | GitHubLifecycleProjectionControl.Claim -> "claim"
        | GitHubLifecycleProjectionControl.PullRequest -> "pull-request"
        | GitHubLifecycleProjectionControl.Review -> "review"
        | GitHubLifecycleProjectionControl.Delivery -> "delivery"
        | GitHubLifecycleProjectionControl.IssueState -> "issue-state"
        | GitHubLifecycleProjectionControl.StatusMapping -> "status-mapping"
        | GitHubLifecycleProjectionControl.StatusNotIntent -> "status-not-intent"
        | GitHubLifecycleProjectionControl.HistoricalFact -> "historical-fact"
        | GitHubLifecycleProjectionControl.ProtectedDelivery -> "protected-delivery"
        | GitHubLifecycleProjectionControl.ExactPlan -> "exact-plan"
        | GitHubLifecycleProjectionControl.RevisionFence -> "revision-fence"
        | GitHubLifecycleProjectionControl.ExactReplay -> "exact-replay"
        | GitHubLifecycleProjectionControl.BoundedCost -> "bounded-cost"
        | GitHubLifecycleProjectionControl.QuintAndPrerequisite -> "quint-and-prerequisite"
    let validate generated independent =
        let validateSet prefix values =
            let expected = requiredControls |> List.map controlId
            let observed = values |> List.map (fun value -> controlId value.Control)
            [ if observed <> expected then yield { Code = prefix + "-INVENTORY"; ControlId = "inventory"; Message = "control inventory is not exact" }
              for value in values do
                  if not value.BaselineGreen then yield { Code = prefix + "-BASELINE"; ControlId = controlId value.Control; Message = "baseline was not green" }
                  if not value.MutationRed then yield { Code = prefix + "-NOT-RED"; ControlId = controlId value.Control; Message = "mutation did not fail closed" } ]
        let findings = validateSet "GLPQ-GENERATED" generated @ validateSet "GLPQ-INDEPENDENT" independent
        if List.isEmpty findings then Ok() else Error findings
