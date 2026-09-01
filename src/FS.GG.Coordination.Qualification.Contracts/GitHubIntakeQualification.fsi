namespace FS.GG.Coordination.Qualification.Contracts

type GitHubIntakeControl = CompleteObservation | DuplicateSurface | IncompletePagination | TypedOutcome | CanonicalPlan | FullFence | EffectOrder | PostState | Replay | Resume | Compensation | IntentBoundary
type GitHubIntakeControlResult = { Control: GitHubIntakeControl; MutationRed: bool; BaselineGreen: bool }
type GitHubIntakeFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubIntakeQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-intake-qualification/1"
    val requiredControls: GitHubIntakeControl list
    val controlId: GitHubIntakeControl -> string
    val validate: generated: GitHubIntakeControlResult list -> independent: GitHubIntakeControlResult list -> Result<unit, GitHubIntakeFinding list>

