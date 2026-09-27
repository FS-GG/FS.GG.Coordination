namespace FS.GG.Coordination.GitHub

/// Bounded native activity only. Protected journals and custom receipts remain separate authorities.
type MigrationNativeActivityInput =
    { Issues: MigrationIssuePopulation
      PullRequests: MigrationPullRequestPopulation
      IssueComments: MigrationIssueCommentPopulation list
      IssueEvents: MigrationIssueEventPopulation list
      IssueTimelines: MigrationTimelinePopulation list
      PullRequestComments: MigrationIssueCommentPopulation list
      PullRequestEvents: MigrationIssueEventPopulation list
      PullRequestTimelines: MigrationTimelinePopulation list
      PullRequestReviews: MigrationPullRequestReviewPopulation list
      PullRequestInlineComments: MigrationPullRequestReviewCommentPopulation list }

type MigrationNativeActivitySnapshot =
    { RepositoryId: int64
      IssueCount: int
      PullRequestCount: int
      IssueCommentCount: int
      IssueEventCount: int
      IssueTimelineCount: int
      PullRequestCommentCount: int
      PullRequestEventCount: int
      PullRequestTimelineCount: int
      ReviewCount: int
      InlineCommentCount: int
      NormalizedSha256: string }

type MigrationNativeActivityCapture =
    { Input: MigrationNativeActivityInput
      Snapshot: MigrationNativeActivitySnapshot }

[<RequireQualifiedAccess>]
module MigrationNativeActivity =
    val reconcile:
        options:MigrationGitHubReadOptions ->
        input:MigrationNativeActivityInput ->
            Result<MigrationNativeActivitySnapshot, MigrationReadFailure>

    /// Reads every censused issue and PR stream, then repeats both censuses before returning.
    val capture:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationNativeActivityCapture, MigrationReadFailure>

    /// Two complete independent captures must agree on every bound input and digest.
    val captureStable:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationNativeActivityCapture, MigrationReadFailure>
