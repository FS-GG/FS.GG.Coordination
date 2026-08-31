namespace FS.GG.Coordination.Qualification.Contracts

open System

[<RequireQualifiedAccess>]
type GitHubRepositorySettingsControl = IdentityDrift | MissingSurface | PaginationIncomplete | UnauthorizedSurface | UnavailableSurface | UnreadableSurface | ContradictorySetting | SecretValue | ObservationDigest | DesiredDigest | StaleObservation | UnsupportedDesired | MinimalPlan | StableOrder | LeastPermission | NoOp | AmbiguousResponse | PartialRollback | PartialRepair | UnrelatedPreserved
type GitHubRepositorySettingsControlResult = { Control: GitHubRepositorySettingsControl; MutationRed: bool; BaselineGreen: bool }
type GitHubRepositorySettingsFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubRepositorySettingsQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-repository-settings-qualification/1"
    let requiredControls = [ GitHubRepositorySettingsControl.IdentityDrift; GitHubRepositorySettingsControl.MissingSurface; GitHubRepositorySettingsControl.PaginationIncomplete; GitHubRepositorySettingsControl.UnauthorizedSurface; GitHubRepositorySettingsControl.UnavailableSurface; GitHubRepositorySettingsControl.UnreadableSurface; GitHubRepositorySettingsControl.ContradictorySetting; GitHubRepositorySettingsControl.SecretValue; GitHubRepositorySettingsControl.ObservationDigest; GitHubRepositorySettingsControl.DesiredDigest; GitHubRepositorySettingsControl.StaleObservation; GitHubRepositorySettingsControl.UnsupportedDesired; GitHubRepositorySettingsControl.MinimalPlan; GitHubRepositorySettingsControl.StableOrder; GitHubRepositorySettingsControl.LeastPermission; GitHubRepositorySettingsControl.NoOp; GitHubRepositorySettingsControl.AmbiguousResponse; GitHubRepositorySettingsControl.PartialRollback; GitHubRepositorySettingsControl.PartialRepair; GitHubRepositorySettingsControl.UnrelatedPreserved ]
    let controlId = function
        | GitHubRepositorySettingsControl.IdentityDrift -> "identity-drift" | GitHubRepositorySettingsControl.MissingSurface -> "missing-surface" | GitHubRepositorySettingsControl.PaginationIncomplete -> "pagination-incomplete"
        | GitHubRepositorySettingsControl.UnauthorizedSurface -> "unauthorized-surface" | GitHubRepositorySettingsControl.UnavailableSurface -> "unavailable-surface" | GitHubRepositorySettingsControl.UnreadableSurface -> "unreadable-surface"
        | GitHubRepositorySettingsControl.ContradictorySetting -> "contradictory-setting" | GitHubRepositorySettingsControl.SecretValue -> "secret-value" | GitHubRepositorySettingsControl.ObservationDigest -> "observation-digest"
        | GitHubRepositorySettingsControl.DesiredDigest -> "desired-digest" | GitHubRepositorySettingsControl.StaleObservation -> "stale-observation" | GitHubRepositorySettingsControl.UnsupportedDesired -> "unsupported-desired"
        | GitHubRepositorySettingsControl.MinimalPlan -> "minimal-plan" | GitHubRepositorySettingsControl.StableOrder -> "stable-order" | GitHubRepositorySettingsControl.LeastPermission -> "least-permission" | GitHubRepositorySettingsControl.NoOp -> "no-op"
        | GitHubRepositorySettingsControl.AmbiguousResponse -> "ambiguous-response" | GitHubRepositorySettingsControl.PartialRollback -> "partial-rollback" | GitHubRepositorySettingsControl.PartialRepair -> "partial-repair" | GitHubRepositorySettingsControl.UnrelatedPreserved -> "unrelated-preserved"
    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let inventory (producer: string) (results: GitHubRepositorySettingsControlResult list) =
            let observed = results |> List.map (fun result -> controlId result.Control)
            if observed = expected then []
            else
                let expectedText = String.concat "," expected
                let observedText = String.concat "," observed
                [ { Code = "GRSQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" } ]
        let outcomes (producer: string) (results: GitHubRepositorySettingsControlResult list) =
            [ for result in results do
                if not result.MutationRed then yield { Code = $"GRSQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = controlId result.Control; Message = "mutation did not turn red" }
                if not result.BaselineGreen then yield { Code = $"GRSQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = controlId result.Control; Message = "baseline did not remain green" } ]
        let findings = inventory "generated" generated @ inventory "independent" independent @ outcomes "generated" generated @ outcomes "independent" independent
        if List.isEmpty findings then Ok () else Error findings
