namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubActionsReleaseFeedControl =
    | RunAttempt | Rerun | CheckSuite | MergeGroup | Pagination | ImmutableRelease | AssetDeletion
    | AttestationSubject | PackageVersion | AuthenticatedFeed | PublicDownload | Redirect | ByteDigest
    | UploadResponse | Unauthorized | Unavailable | Incomplete | Stale

type GitHubActionsReleaseFeedControlResult =
    { Control: GitHubActionsReleaseFeedControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubActionsReleaseFeedFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubActionsReleaseFeedQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-actions-release-feed-qualification/1"
    val requiredControls: GitHubActionsReleaseFeedControl list
    val controlId: GitHubActionsReleaseFeedControl -> string
    val validate: generated: GitHubActionsReleaseFeedControlResult list -> independent: GitHubActionsReleaseFeedControlResult list -> Result<unit, GitHubActionsReleaseFeedFinding list>
