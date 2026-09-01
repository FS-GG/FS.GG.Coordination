namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubRoadmapIntakeControl =
    | CanonicalPlan | CreateOrReuse | Hierarchy | Dependencies | Dates | Fields
    | IdentityCollision | DuplicateTarget | StaleObservation | AlteredPlan
    | CardinalityInvariant | ProjectionNotLedger | OwnedDrift | Replay
    | PartialApply | Unauthorized | Unsupported | Indeterminate

type GitHubRoadmapIntakeControlResult =
    { Control: GitHubRoadmapIntakeControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubRoadmapIntakeFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubRoadmapIntakeQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-roadmap-intake-qualification/1"
    val requiredControls: GitHubRoadmapIntakeControl list
    val controlId: GitHubRoadmapIntakeControl -> string
    val validate: generated: GitHubRoadmapIntakeControlResult list -> independent: GitHubRoadmapIntakeControlResult list -> Result<unit, GitHubRoadmapIntakeFinding list>
