namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubRepositorySettingsControl =
    | IdentityDrift | MissingSurface | PaginationIncomplete | UnauthorizedSurface | UnavailableSurface
    | UnreadableSurface | ContradictorySetting | SecretValue | ObservationDigest | DesiredDigest
    | StaleObservation | UnsupportedDesired | MinimalPlan | StableOrder | LeastPermission | NoOp
    | AmbiguousResponse | PartialRollback | PartialRepair | UnrelatedPreserved

type GitHubRepositorySettingsControlResult =
    { Control: GitHubRepositorySettingsControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubRepositorySettingsFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubRepositorySettingsQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-repository-settings-qualification/1"
    val requiredControls: GitHubRepositorySettingsControl list
    val controlId: GitHubRepositorySettingsControl -> string
    val validate: generated: GitHubRepositorySettingsControlResult list -> independent: GitHubRepositorySettingsControlResult list -> Result<unit, GitHubRepositorySettingsFinding list>
