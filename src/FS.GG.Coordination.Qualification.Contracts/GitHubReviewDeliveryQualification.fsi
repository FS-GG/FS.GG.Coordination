namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubReviewDeliveryControl = StableChain | ImmutableEpoch | CompleteSnapshot | FreshEpochSeat | SameEpochSuccession | AccountableAuthority | HistoricalPass | CurrentPass | ReviewFence | MergeDistinct | ProtectedMain | ExactMergeRun | DeliveryReceipt | DoneReceipt | ExactReplay | DivergentReplay | BoundedCost | QuintAndPrerequisite
type GitHubReviewDeliveryControlResult = { Control: GitHubReviewDeliveryControl; MutationRed: bool; BaselineGreen: bool }
type GitHubReviewDeliveryFinding = { Code: string; ControlId: string; Message: string }
[<RequireQualifiedAccess>]
module GitHubReviewDeliveryQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-review-delivery-qualification/1"
    val requiredControls: GitHubReviewDeliveryControl list
    val controlId: GitHubReviewDeliveryControl -> string
    val validate: generated: GitHubReviewDeliveryControlResult list -> independent: GitHubReviewDeliveryControlResult list -> Result<unit, GitHubReviewDeliveryFinding list>
