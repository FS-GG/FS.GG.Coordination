namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

/// One declared protected journal object. The caller must independently establish
/// that this list is exhaustive for the isolated copy.
type MigrationDeliveryJournalDeclaration =
    { Repository: string
      RefName: string
      Path: string
      PullRequestNumber: int
      OperationId: string }

type MigrationReviewDeliveryPage =
    { RequestUri: string
      RawBody: string
      RawSha256: string
      NextUri: string option }

type MigrationReviewDeliveryStream =
    { Kind: string
      Subject: string
      Pages: MigrationReviewDeliveryPage list
      RecordIds: string list }

type MigrationDeliveryJournalEvidence =
    { Declaration: MigrationDeliveryJournalDeclaration
      RefHead: string
      RequestUri: string
      RawBody: string
      RawSha256: string
      ContentSha256: string
      MergeCommit: string option }

/// Source evidence only. This is not a nine-authority inspect result or Q5/Q6 receipt.
type MigrationReviewDeliveryPass =
    { RepositoryId: int64
      PullRequests: MigrationPullRequestPopulation
      Reviews: MigrationPullRequestReviewPopulation list
      InlineComments: MigrationPullRequestReviewCommentPopulation list
      Streams: MigrationReviewDeliveryStream list
      Journals: MigrationDeliveryJournalEvidence list
      SnapshotSha256: string }

type MigrationReviewDeliveryTwoPass =
    { First: MigrationReviewDeliveryPass
      Second: MigrationReviewDeliveryPass }

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryCapture =
    /// Reads one complete repository census and every declared review, check,
    /// delivery, tag, release and journal stream twice. All dispatches are GET.
    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        expectedPullRequests:(int * string * string) list ->
        journals:MigrationDeliveryJournalDeclaration list ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReviewDeliveryTwoPass, string>
