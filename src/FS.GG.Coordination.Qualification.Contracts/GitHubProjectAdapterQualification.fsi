namespace FS.GG.Coordination.Qualification.Contracts

type GitHubProjectAdapterControl =
    | Pagination
    | ArchivedItem
    | DuplicateItem
    | ExternalItem
    | DraftItem
    | MissingItem
    | UnreadableObservation
    | StaleRevision
    | ConcurrentChange
    | NoOpMutation

type GitHubProjectAdapterControlResult =
    { Control: GitHubProjectAdapterControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubProjectAdapterFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubProjectAdapterQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-project-adapter-qualification/1"
    val requiredControls: GitHubProjectAdapterControl list
    val controlId: GitHubProjectAdapterControl -> string
    val validate: generated: GitHubProjectAdapterControlResult list -> independent: GitHubProjectAdapterControlResult list -> Result<unit, GitHubProjectAdapterFinding list>
