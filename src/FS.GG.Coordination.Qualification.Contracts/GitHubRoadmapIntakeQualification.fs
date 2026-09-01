namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubRoadmapIntakeControl =
    | CanonicalPlan | CreateOrReuse | Hierarchy | Dependencies | Dates | Fields
    | IdentityCollision | DuplicateTarget | StaleObservation | AlteredPlan
    | CardinalityInvariant | ProjectionNotLedger | OwnedDrift | Replay
    | PartialApply | Unauthorized | Unsupported | Indeterminate

type GitHubRoadmapIntakeControlResult = { Control: GitHubRoadmapIntakeControl; MutationRed: bool; BaselineGreen: bool }
type GitHubRoadmapIntakeFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubRoadmapIntakeQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-roadmap-intake-qualification/1"
    let requiredControls =
        [ GitHubRoadmapIntakeControl.CanonicalPlan; GitHubRoadmapIntakeControl.CreateOrReuse
          GitHubRoadmapIntakeControl.Hierarchy; GitHubRoadmapIntakeControl.Dependencies
          GitHubRoadmapIntakeControl.Dates; GitHubRoadmapIntakeControl.Fields
          GitHubRoadmapIntakeControl.IdentityCollision; GitHubRoadmapIntakeControl.DuplicateTarget
          GitHubRoadmapIntakeControl.StaleObservation; GitHubRoadmapIntakeControl.AlteredPlan
          GitHubRoadmapIntakeControl.CardinalityInvariant; GitHubRoadmapIntakeControl.ProjectionNotLedger
          GitHubRoadmapIntakeControl.OwnedDrift; GitHubRoadmapIntakeControl.Replay
          GitHubRoadmapIntakeControl.PartialApply; GitHubRoadmapIntakeControl.Unauthorized
          GitHubRoadmapIntakeControl.Unsupported; GitHubRoadmapIntakeControl.Indeterminate ]
    let controlId = function
        | GitHubRoadmapIntakeControl.CanonicalPlan -> "canonical-plan" | GitHubRoadmapIntakeControl.CreateOrReuse -> "create-or-reuse"
        | GitHubRoadmapIntakeControl.Hierarchy -> "hierarchy" | GitHubRoadmapIntakeControl.Dependencies -> "dependencies"
        | GitHubRoadmapIntakeControl.Dates -> "dates" | GitHubRoadmapIntakeControl.Fields -> "fields"
        | GitHubRoadmapIntakeControl.IdentityCollision -> "identity-collision" | GitHubRoadmapIntakeControl.DuplicateTarget -> "duplicate-target"
        | GitHubRoadmapIntakeControl.StaleObservation -> "stale-observation" | GitHubRoadmapIntakeControl.AlteredPlan -> "altered-plan"
        | GitHubRoadmapIntakeControl.CardinalityInvariant -> "cardinality-invariant" | GitHubRoadmapIntakeControl.ProjectionNotLedger -> "projection-not-ledger"
        | GitHubRoadmapIntakeControl.OwnedDrift -> "owned-drift" | GitHubRoadmapIntakeControl.Replay -> "replay"
        | GitHubRoadmapIntakeControl.PartialApply -> "partial-apply" | GitHubRoadmapIntakeControl.Unauthorized -> "unauthorized"
        | GitHubRoadmapIntakeControl.Unsupported -> "unsupported" | GitHubRoadmapIntakeControl.Indeterminate -> "indeterminate"
    let validate generated independent =
        let inspect producer results =
            let expected = requiredControls |> List.map controlId
            let observed = results |> List.map (fun value -> controlId value.Control)
            [ if observed <> expected then yield { Code = "GRIQ-INVENTORY"; ControlId = producer; Message = "control inventory is not exact" }
              for value in results do
                  if not value.BaselineGreen then yield { Code = "GRIQ-BASELINE"; ControlId = controlId value.Control; Message = producer + " baseline failed" }
                  if not value.MutationRed then yield { Code = "GRIQ-MUTATION"; ControlId = controlId value.Control; Message = producer + " inversion stayed green" } ]
        match inspect "generated" generated @ inspect "independent" independent with
        | [] -> Ok ()
        | findings -> Error findings
