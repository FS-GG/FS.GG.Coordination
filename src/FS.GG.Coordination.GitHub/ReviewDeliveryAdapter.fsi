namespace FS.GG.Coordination.GitHub

type ReviewSnapshot =
    { Complete: bool
      Subject: string
      BaseCommit: string
      HeadCommit: string
      ChangedFiles: string list
      RequiredChecks: string list }

type ReviewVerdict = ReviewPending | ReviewPass | ReviewChangesRequired

type ReviewAuthorityRecord =
    { SchemaVersion: int
      ChainId: string
      EpochKey: string
      SnapshotDigest: string
      AccountableAuthority: string
      PhaseSeat: string
      SeatOrdinal: int64
      Verdict: ReviewVerdict
      OperationId: string }

type ReviewAuthorityObservation =
    { Complete: bool
      Journal: JournalObservation
      Current: ReviewAuthorityRecord }

type ReviewCommitMaterial = { CommitOid: string; TreeOid: string }
type ReviewDeliveryCost = { AuthorityReads: int; MaximumEffects: int }

type ReviewGrant =
    { Address: AggregateAddress
      ChainId: string
      EpochKey: string
      SnapshotDigest: string
      AccountableAuthority: string
      PhaseSeat: string
      JournalCommit: string
      Generation: int64 }

type ReviewPlan =
    { ProposedAuthority: ReviewAuthorityRecord
      Proposal: CasProposal
      Grant: ReviewGrant
      Seal: string
      Cost: ReviewDeliveryCost }

type ReviewRefusal =
    | InvalidReviewSubject
    | IncompleteReviewSnapshot
    | InvalidReviewSnapshot of string
    | InvalidAccountableAuthority
    | InvalidSeatOrdinal
    | ReusedPhaseSeat
    | ReviewObservationIncomplete
    | ReviewJournalFailure of JournalFailure
    | ReviewPayloadMismatch
    | WrongReviewChain
    | WrongReviewEpoch
    | WrongReviewSnapshot
    | WrongReviewSeat
    | ReviewNotPassed
    | ReviewEffectRefused of EffectRefusal
    | InvalidReviewCommitMaterial

type DeliveryState =
    | NotMerged
    | Merged of mergeCommit: string
    | ProtectedVerified of mergeCommit: string * runId: int64 * runCommit: string * conclusion: string

type DeliveryReceiptKind = DeliveryReceipt | DoneReceipt

type DeliveryAuthorityRecord =
    { SchemaVersion: int
      Subject: string
      Kind: DeliveryReceiptKind
      ReviewChainId: string
      ReviewEpochKey: string
      ReviewSeat: string
      MergeCommit: string
      ProtectedRunId: int64 option
      OperationId: string }

type DeliveryAuthorityObservation =
    { Complete: bool
      Journal: JournalObservation
      Current: DeliveryAuthorityRecord }

type DeliveryReceipt =
    { Address: AggregateAddress
      Record: DeliveryAuthorityRecord
      JournalCommit: string
      Generation: int64
      Digest: string }

type DeliveryPlan =
    { ProposedAuthority: DeliveryAuthorityRecord
      Proposal: CasProposal
      Receipt: DeliveryReceipt
      Seal: string
      Cost: ReviewDeliveryCost }

type DeliveryPlanResult = DeliveryPlanned of DeliveryPlan | DeliveryReplayed of DeliveryReceipt

type DeliveryRefusal =
    | ReviewAuthorizationRefused of ReviewRefusal
    | DeliveryObservationIncomplete
    | DeliveryJournalFailure of JournalFailure
    | DeliveryPayloadMismatch
    | MergeRequired
    | ProtectedVerificationRequired
    | InvalidMergeCommit
    | InvalidProtectedRun
    | ProtectedRunCommitMismatch
    | ProtectedRunNotSuccessful
    | DivergentDeliveryReplay
    | InvalidDeliveryCommitMaterial

[<RequireQualifiedAccess>]
module ReviewDeliveryAdapter =
    val chainId: subject: string -> Result<string, ReviewRefusal>
    val snapshotBytes: ReviewSnapshot -> Result<byte array, ReviewRefusal list>
    val epochKey: chainValue: string -> ReviewSnapshot -> Result<string, ReviewRefusal list>
    val phaseSeat: epochValue: string -> ordinal: int64 -> Result<string, ReviewRefusal>
    val reviewAddress: chainValue: string -> Result<AggregateAddress, ReviewRefusal>
    val reviewAuthorityBytes: ReviewAuthorityRecord -> Result<byte array, ReviewRefusal list>
    val planReview: subject: string -> accountableAuthority: string -> seatOrdinal: int64 -> verdict: ReviewVerdict -> snapshot: ReviewSnapshot -> ReviewAuthorityObservation -> ReviewCommitMaterial -> Result<ReviewPlan, ReviewRefusal list>
    val authorizeReview: ReviewGrant -> ReviewSnapshot -> ReviewAuthorityObservation -> Result<JournalCommit, ReviewRefusal>
    val deliveryAddress: subject: string -> Result<AggregateAddress, DeliveryRefusal>
    val deliveryAuthorityBytes: DeliveryAuthorityRecord -> Result<byte array, DeliveryRefusal list>
    val planDelivery: kind: DeliveryReceiptKind -> ReviewGrant -> ReviewSnapshot -> ReviewAuthorityObservation -> DeliveryState -> DeliveryAuthorityObservation -> ReviewCommitMaterial -> Result<DeliveryPlanResult, DeliveryRefusal list>
