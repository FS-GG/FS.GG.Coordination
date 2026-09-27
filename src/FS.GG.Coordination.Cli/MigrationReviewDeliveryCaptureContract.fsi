namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

type MigrationReviewDeliveryRequest =
    { Kind: string
      Method: string
      Uri: string
      Headers: Map<string, string>
      Body: string option
      Variables: Map<string, string>
      ApiVersion: string
      Idempotency: string }

type MigrationReviewDeliveryRead =
    { Request: MigrationReviewDeliveryRequest
      RequestSha256: string
      StatusCode: int
      ResponseHeaders: Map<string, string>
      RawBody: string
      RawSha256: string
      NextRequestUri: string option }

type MigrationReviewDeliveryRepository =
    { RepositoryId: int64
      NodeId: string
      FullName: string
      Read: MigrationReviewDeliveryRead }

type MigrationReviewDeliveryPullRequest =
    { Number: int
      NodeId: string
      HeadSha: string }

type MigrationReviewDeliveryRecord =
    | Review of pullRequestNumber:int * id:int64
    | InlineComment of pullRequestNumber:int * id:int64
    | CheckRun of pullRequestNumber:int * commitSha:string * id:int64 * name:string * status:string * conclusion:string option
    | CommitStatus of pullRequestNumber:int * commitSha:string * id:int64 * context:string * state:string
    | PullDelivery of pullRequestNumber:int * mergeCommit:string option
    | MergeObject of pullRequestNumber:int * mergeCommit:string
    | Tag of name:string * commitSha:string
    | Release of id:int64 * tagName:string * draft:bool

type MigrationReviewDeliveryNativeStream =
    { Kind: string
      Subject: string
      Reads: MigrationReviewDeliveryRead list
      Records: MigrationReviewDeliveryRecord list }

type MigrationReviewDeliveryNativePass =
    { Repository: MigrationReviewDeliveryRepository
      PullRequestCensus: MigrationReviewDeliveryRead list
      PullRequests: MigrationReviewDeliveryPullRequest list
      Streams: MigrationReviewDeliveryNativeStream list
      Fingerprint: string }

type MigrationReviewDeliveryNativeTwoPass =
    { First: MigrationReviewDeliveryNativePass
      Second: MigrationReviewDeliveryNativePass }

type MigrationJournalRef =
    { RefName: string
      HeadSha: string }

type MigrationJournalDecodedRecord =
    { Schema: string
      Kind: string
      Subject: string
      OperationId: string
      Generation: int64
      MergeCommit: string option
      ProtectedRunId: int64 option
      ProtectedRunCommit: string option
      ProtectedRunConclusion: string option }

type MigrationJournalHistoryEntry =
    { RefName: string
      CommitSha: string
      ParentSha: string option
      TreeSha: string
      HeadPath: string
      EventPath: string
      Reads: MigrationReviewDeliveryRead list
      Record: MigrationJournalDecodedRecord }

type MigrationJournalNamespaceCensus =
    { Prefix: string
      Reads: MigrationReviewDeliveryRead list
      Refs: MigrationJournalRef list }

type MigrationJournalPass =
    { Repository: MigrationReviewDeliveryRepository
      Namespaces: MigrationJournalNamespaceCensus list
      Histories: MigrationJournalHistoryEntry list
      Fingerprint: string }

type MigrationJournalTwoPass =
    { First: MigrationJournalPass
      Second: MigrationJournalPass }

type MigrationReviewDeliveryCorrespondence =
    { PullRequestNumber: int
      HeadSha: string
      MergeCommit: string option
      CheckRunIds: int64 list
      StatusIds: int64 list
      ReviewJournalRefs: string list
      DeliveryJournalRefs: string list
      TagNames: string list
      ReleaseIds: int64 list }

type MigrationReviewDeliveryCompletePass =
    { Native: MigrationReviewDeliveryNativePass
      Journals: MigrationJournalPass
      Correspondence: MigrationReviewDeliveryCorrespondence list
      Fingerprint: string }

type MigrationReviewDeliveryCompleteTwoPass =
    { First: MigrationReviewDeliveryCompletePass
      Second: MigrationReviewDeliveryCompletePass }

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryCaptureContract =
    val journalRefPrefixes: string list
    val checkRunsPath: repositoryPath:string -> commitSha:string -> string
    val statusesPath: repositoryPath:string -> commitSha:string -> string
    /// Produces exact request evidence with authorization removed before retention.
    val captureRequest: GitHubRequest -> MigrationReviewDeliveryRequest
    val requestSha256: MigrationReviewDeliveryRequest -> string
    val nativeFingerprint: MigrationReviewDeliveryNativePass -> string
    val journalFingerprint: MigrationJournalPass -> string
    val validateNativeTwoPass: MigrationReviewDeliveryNativeTwoPass -> Result<MigrationReviewDeliveryNativeTwoPass, string>
    val validateJournalTwoPass: MigrationJournalTwoPass -> Result<MigrationJournalTwoPass, string>
