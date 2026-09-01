namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubReviewDeliveryControl = StableChain | ImmutableEpoch | CompleteSnapshot | FreshEpochSeat | SameEpochSuccession | AccountableAuthority | HistoricalPass | CurrentPass | ReviewFence | MergeDistinct | ProtectedMain | ExactMergeRun | DeliveryReceipt | DoneReceipt | ExactReplay | DivergentReplay | BoundedCost | QuintAndPrerequisite
type GitHubReviewDeliveryControlResult = { Control: GitHubReviewDeliveryControl; MutationRed: bool; BaselineGreen: bool }
type GitHubReviewDeliveryFinding = { Code: string; ControlId: string; Message: string }
[<RequireQualifiedAccess>]
module GitHubReviewDeliveryQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-review-delivery-qualification/1"
    let requiredControls = [ GitHubReviewDeliveryControl.StableChain; GitHubReviewDeliveryControl.ImmutableEpoch; GitHubReviewDeliveryControl.CompleteSnapshot; GitHubReviewDeliveryControl.FreshEpochSeat; GitHubReviewDeliveryControl.SameEpochSuccession; GitHubReviewDeliveryControl.AccountableAuthority; GitHubReviewDeliveryControl.HistoricalPass; GitHubReviewDeliveryControl.CurrentPass; GitHubReviewDeliveryControl.ReviewFence; GitHubReviewDeliveryControl.MergeDistinct; GitHubReviewDeliveryControl.ProtectedMain; GitHubReviewDeliveryControl.ExactMergeRun; GitHubReviewDeliveryControl.DeliveryReceipt; GitHubReviewDeliveryControl.DoneReceipt; GitHubReviewDeliveryControl.ExactReplay; GitHubReviewDeliveryControl.DivergentReplay; GitHubReviewDeliveryControl.BoundedCost; GitHubReviewDeliveryControl.QuintAndPrerequisite ]
    let controlId = function
        | GitHubReviewDeliveryControl.StableChain -> "stable-chain"
        | GitHubReviewDeliveryControl.ImmutableEpoch -> "immutable-epoch"
        | GitHubReviewDeliveryControl.CompleteSnapshot -> "complete-snapshot"
        | GitHubReviewDeliveryControl.FreshEpochSeat -> "fresh-epoch-seat"
        | GitHubReviewDeliveryControl.SameEpochSuccession -> "same-epoch-succession"
        | GitHubReviewDeliveryControl.AccountableAuthority -> "accountable-authority"
        | GitHubReviewDeliveryControl.HistoricalPass -> "historical-pass"
        | GitHubReviewDeliveryControl.CurrentPass -> "current-pass"
        | GitHubReviewDeliveryControl.ReviewFence -> "review-fence"
        | GitHubReviewDeliveryControl.MergeDistinct -> "merge-distinct"
        | GitHubReviewDeliveryControl.ProtectedMain -> "protected-main"
        | GitHubReviewDeliveryControl.ExactMergeRun -> "exact-merge-run"
        | GitHubReviewDeliveryControl.DeliveryReceipt -> "delivery-receipt"
        | GitHubReviewDeliveryControl.DoneReceipt -> "done-receipt"
        | GitHubReviewDeliveryControl.ExactReplay -> "exact-replay"
        | GitHubReviewDeliveryControl.DivergentReplay -> "divergent-replay"
        | GitHubReviewDeliveryControl.BoundedCost -> "bounded-cost"
        | GitHubReviewDeliveryControl.QuintAndPrerequisite -> "quint-and-prerequisite"
    let validate generated independent =
        let validateSet prefix values =
            let expected = requiredControls |> List.map controlId
            let observed = values |> List.map (fun value -> controlId value.Control)
            [ if observed <> expected then yield { Code = prefix + "-INVENTORY"; ControlId = "inventory"; Message = "control inventory is not exact" }
              for value in values do
                  if not value.BaselineGreen then yield { Code = prefix + "-BASELINE"; ControlId = controlId value.Control; Message = "baseline was not green" }
                  if not value.MutationRed then yield { Code = prefix + "-NOT-RED"; ControlId = controlId value.Control; Message = "mutation did not fail closed" } ]
        let findings = validateSet "GRDQ-GENERATED" generated @ validateSet "GRDQ-INDEPENDENT" independent
        if List.isEmpty findings then Ok() else Error findings
