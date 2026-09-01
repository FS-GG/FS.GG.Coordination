namespace FS.GG.Coordination.Qualification.Contracts

open System

[<RequireQualifiedAccess>]
type GitHubIntakeControl = MissingPage | RepeatedPage | CursorCycle | MissingField | UnknownType | DuplicateMembership | HierarchyCycle | DependencyCycle | StaleRevision | AlteredPlan | ReorderedOperation | PreconditionDrift | PostconditionMismatch | PartialApply | Replay | Compensation | Unauthorized | Unsupported | Indeterminate
type GitHubIntakeControlResult = { Control: GitHubIntakeControl; MutationRed: bool; BaselineGreen: bool }
type GitHubIntakeFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubIntakeQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-intake-qualification/1"
    let requiredControls = [ GitHubIntakeControl.MissingPage; GitHubIntakeControl.RepeatedPage; GitHubIntakeControl.CursorCycle; GitHubIntakeControl.MissingField; GitHubIntakeControl.UnknownType; GitHubIntakeControl.DuplicateMembership; GitHubIntakeControl.HierarchyCycle; GitHubIntakeControl.DependencyCycle; GitHubIntakeControl.StaleRevision; GitHubIntakeControl.AlteredPlan; GitHubIntakeControl.ReorderedOperation; GitHubIntakeControl.PreconditionDrift; GitHubIntakeControl.PostconditionMismatch; GitHubIntakeControl.PartialApply; GitHubIntakeControl.Replay; GitHubIntakeControl.Compensation; GitHubIntakeControl.Unauthorized; GitHubIntakeControl.Unsupported; GitHubIntakeControl.Indeterminate ]
    let controlId = function GitHubIntakeControl.MissingPage -> "missing-page" | GitHubIntakeControl.RepeatedPage -> "repeated-page" | GitHubIntakeControl.CursorCycle -> "cursor-cycle" | GitHubIntakeControl.MissingField -> "missing-field" | GitHubIntakeControl.UnknownType -> "unknown-type" | GitHubIntakeControl.DuplicateMembership -> "duplicate-membership" | GitHubIntakeControl.HierarchyCycle -> "hierarchy-cycle" | GitHubIntakeControl.DependencyCycle -> "dependency-cycle" | GitHubIntakeControl.StaleRevision -> "stale-revision" | GitHubIntakeControl.AlteredPlan -> "altered-plan" | GitHubIntakeControl.ReorderedOperation -> "reordered-operation" | GitHubIntakeControl.PreconditionDrift -> "precondition-drift" | GitHubIntakeControl.PostconditionMismatch -> "postcondition-mismatch" | GitHubIntakeControl.PartialApply -> "partial-apply" | GitHubIntakeControl.Replay -> "replay" | GitHubIntakeControl.Compensation -> "compensation" | GitHubIntakeControl.Unauthorized -> "unauthorized" | GitHubIntakeControl.Unsupported -> "unsupported" | GitHubIntakeControl.Indeterminate -> "indeterminate"
    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let inspect producer (results: GitHubIntakeControlResult list) =
            let observed = results |> List.map (fun result -> controlId result.Control)
            let expectedText = String.concat "," expected
            let observedText = String.concat "," observed
            [ if observed <> expected then yield { Code = "GIAQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" }
              for result in results do
                  if not result.BaselineGreen then yield { Code = "GIAQ-BASELINE"; ControlId = controlId result.Control; Message = producer + " baseline did not remain green" }
                  if not result.MutationRed then yield { Code = "GIAQ-MUTATION"; ControlId = controlId result.Control; Message = producer + " mutation did not turn red" } ]
        match inspect "generated" generated @ inspect "independent" independent with [] -> Ok () | findings -> Error findings
