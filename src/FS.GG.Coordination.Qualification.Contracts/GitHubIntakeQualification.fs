namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubIntakeControl = CompleteObservation | DuplicateSurface | IncompletePagination | TypedOutcome | CanonicalPlan | FullFence | EffectOrder | PostState | Replay | Resume | Compensation | IntentBoundary
type GitHubIntakeControlResult = { Control: GitHubIntakeControl; MutationRed: bool; BaselineGreen: bool }
type GitHubIntakeFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubIntakeQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-intake-qualification/1"
    let requiredControls = [ CompleteObservation; DuplicateSurface; IncompletePagination; TypedOutcome; CanonicalPlan; FullFence; EffectOrder; PostState; Replay; Resume; Compensation; IntentBoundary ]
    let controlId = function CompleteObservation -> "complete-observation" | DuplicateSurface -> "duplicate-surface" | IncompletePagination -> "incomplete-pagination" | TypedOutcome -> "typed-outcome" | CanonicalPlan -> "canonical-plan" | FullFence -> "full-fence" | EffectOrder -> "effect-order" | PostState -> "post-state" | Replay -> "replay" | Resume -> "resume" | Compensation -> "compensation" | IntentBoundary -> "intent-boundary"
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
