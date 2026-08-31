namespace FS.GG.Coordination.Qualification.Contracts

type GitHubCommentProjectionControl =
    | Pagination
    | DuplicateIdentity
    | ReorderedPage
    | EditedProjection
    | DeletedProjection
    | TamperedMarker
    | MalformedJson
    | AuthorityDigestMismatch
    | IncompleteObservation
    | StaleRevision
    | ConcurrentChange
    | NoOpMutation

type GitHubCommentProjectionControlResult =
    { Control: GitHubCommentProjectionControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubCommentProjectionFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubCommentProjectionQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-comment-projection-qualification/1"
    val requiredControls: GitHubCommentProjectionControl list
    val controlId: GitHubCommentProjectionControl -> string
    val validate: generated: GitHubCommentProjectionControlResult list -> independent: GitHubCommentProjectionControlResult list -> Result<unit, GitHubCommentProjectionFinding list>
