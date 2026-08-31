namespace FS.GG.Coordination.Qualification.Contracts

type GitHubIssueFieldControl =
    | Pagination
    | DuplicateIdentity
    | TypeDrift
    | OptionDrift
    | StaleRevision
    | IncompleteObservation
    | NoOpMutation

type GitHubIssueFieldControlResult =
    { Control: GitHubIssueFieldControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubIssueFieldFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubIssueFieldQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-issue-field-qualification/1"
    val requiredControls: GitHubIssueFieldControl list
    val controlId: GitHubIssueFieldControl -> string
    val validate: generated: GitHubIssueFieldControlResult list -> independent: GitHubIssueFieldControlResult list -> Result<unit, GitHubIssueFieldFinding list>
