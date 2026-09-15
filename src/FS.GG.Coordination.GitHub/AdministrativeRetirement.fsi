namespace FS.GG.Coordination.GitHub

open System

type RetirementPullRequestDisposition = Open | ClosedUnmerged | MergedExactBeforeRetirement | MergedOther
type RetirementIssueDisposition = IssueOpen | ClosedNotPlanned | ClosedCompleted

type RetirementIdentity =
    { Repository: string
      RepositoryId: int64
      IssueNumber: int
      PullRequestNumber: int
      BranchRef: string
      CandidateHead: string
      CandidateTree: string
      CandidateParent: string
      AcceptedClientCommit: string
      AcceptedClientArtifactDigest: string
      OperationAuthorityDigest: string }

type RetirementObservation =
    { ObservedAt: DateTimeOffset
      CompleteNativeCensus: bool
      PullRequestDisposition: RetirementPullRequestDisposition
      PullRequestHead: string
      MergeCommit: string option
      MergedCandidateHead: string option
      MergedCandidateTree: string option
      MergedCandidateParent: string option
      ProtectedBaseRef: string option
      DeliveredPathDigest: string option
      AutoMergeEnabled: bool
      MergeQueueEntry: string option
      CandidateArchiveDigest: string
      CandidateArchiveLocation: string
      ArchivedHead: string
      ArchivedTree: string
      ArchivedParent: string
      CandidateArchiveIndependent: bool
      NativeCensusDigest: string
      NativeCensusLocation: string
      BranchFenceRuleId: int64 option
      BranchFenceDigest: string option
      BranchFenceActive: bool
      BranchFenceHasBypass: bool
      TemporaryMainRuleId: int64 option
      TemporaryMainRuleDigest: string option
      TemporaryMainRuleActive: bool
      SubjectExcluded: bool
      IssueDisposition: RetirementIssueDisposition }

type AdministrativeRetirementReceipt =
    { Schema: string
      IdentityDigest: string
      NativeCensusDigest: string
      Disposition: RetirementPullRequestDisposition
      CandidateHead: string
      CandidateArchiveDigest: string
      BranchFenceRuleId: int64
      SubjectExcluded: bool
      OriginalJournalAvailable: bool
      OriginalCompletionRecorded: bool
      OriginalUsageKnown: bool
      RetiredAt: DateTimeOffset
      ReceiptDigest: string }

type AdministrativeRetirementResult =
    | RetirementPending
    | AdministrativelyRetiredWithLostHistory of AdministrativeRetirementReceipt

type AdministrativeRetirementFailure =
    | InvalidIdentity of string
    | StaleOrFutureObservation
    | IncompleteNativeCensus
    | CandidateIdentityMismatch
    | CandidateNotIndependentlyPreserved
    | NativeCensusEvidenceMissing
    | PullRequestOutcomeUnresolved
    | UnexpectedMergeOutcome
    | PullRequestMutationStillEnabled
    | BranchFenceMissing
    | BranchFenceHasBypass
    | SubjectExclusionMissing
    | IssueDispositionMissing
    | TemporaryMainHoldStillActive
    | FabricatedOriginalHistory
    | ReceiptDigestMismatch

[<RequireQualifiedAccess>]
module AdministrativeRetirement =
    [<Literal>]
    val ReceiptSchema: string = "fsgg.coordination.administrative-retirement/1"
    val identityDigest: RetirementIdentity -> string
    val observationDigest: RetirementIdentity -> RetirementObservation -> string
    val receiptDigest: AdministrativeRetirementReceipt -> string
    val validatePreview: now: DateTimeOffset -> maxAge: TimeSpan -> RetirementIdentity -> RetirementObservation -> Result<RetirementObservation, AdministrativeRetirementFailure list>
    val settle: now: DateTimeOffset -> maxAge: TimeSpan -> RetirementIdentity -> RetirementObservation -> Result<AdministrativeRetirementResult, AdministrativeRetirementFailure list>
    val verify: now: DateTimeOffset -> maxAge: TimeSpan -> RetirementIdentity -> RetirementObservation -> AdministrativeRetirementReceipt -> Result<AdministrativeRetirementResult, AdministrativeRetirementFailure list>
