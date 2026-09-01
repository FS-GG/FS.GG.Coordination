namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubIntakeControl = MissingPage | RepeatedPage | CursorCycle | MissingField | UnknownType | DuplicateMembership | HierarchyCycle | DependencyCycle | StaleRevision | AlteredPlan | ReorderedOperation | PreconditionDrift | PostconditionMismatch | PartialApply | Replay | Compensation | Unauthorized | Unsupported | Indeterminate
type GitHubIntakeControlResult = { Control: GitHubIntakeControl; MutationRed: bool; BaselineGreen: bool }
type GitHubIntakeFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubIntakeQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-intake-qualification/1"
    val requiredControls: GitHubIntakeControl list
    val controlId: GitHubIntakeControl -> string
    val validate: generated: GitHubIntakeControlResult list -> independent: GitHubIntakeControlResult list -> Result<unit, GitHubIntakeFinding list>
