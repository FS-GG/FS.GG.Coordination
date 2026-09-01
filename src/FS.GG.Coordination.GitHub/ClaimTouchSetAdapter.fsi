namespace FS.GG.Coordination.GitHub

type ClaimTouch = { Repository: string; Path: string }

type ClaimAuthorityRecord =
    { SchemaVersion: int
      Subject: string
      Owner: string
      Touches: ClaimTouch list
      LeaseExpiresAt: int64
      OperationId: string }

type ClaimAuthorityObservation =
    { Complete: bool
      Journal: JournalObservation
      Current: ClaimAuthorityRecord }

type ClaimProjectionHints =
    { FieldOwner: string option
      CommentOwner: string option
      LeaseLooksActive: bool
      WebhookSequence: int64 option }

type ClaimAcquireIntent =
    { Subject: string
      Owner: string
      Touches: ClaimTouch list
      Now: int64
      LeaseExpiresAt: int64 }

type ClaimCommitMaterial = { CommitOid: string; TreeOid: string }

type ClaimCost = { AuthorityReads: int; MaximumEffects: int }

type ClaimGrant =
    { Address: AggregateAddress
      Subject: string
      Owner: string
      Touches: ClaimTouch list
      JournalCommit: string
      Generation: int64 }

type ClaimAcquirePlan =
    { OperationId: string
      ProposedAuthority: ClaimAuthorityRecord
      Proposal: CasProposal
      Grant: ClaimGrant
      Seal: string
      Cost: ClaimCost }

type SuccessorEligibility = CurrentOwner | EligibleAfterExpiry | BlockedUntil of int64

type ClaimRefusal =
    | InvalidSubject
    | InvalidOwner
    | InvalidTouch of string
    | DuplicateTouch of string
    | IncompleteClaimObservation
    | ClaimJournalFailure of JournalFailure
    | ClaimPayloadMismatch
    | ActiveForeignClaim of owner: string * leaseExpiresAt: int64
    | InvalidLease
    | InvalidCommitMaterial
    | AlteredClaimPlan
    | PersistedPlanMissing
    | WrongClaimOwner
    | WrongClaimTouches
    | ClaimEffectRefused of EffectRefusal

type ClaimAcquireResult =
    | ClaimAcquired of ClaimGrant
    | ClaimParentConflict
    | ClaimDefiniteRefusal of string
    | ClaimResponseUnknownRequiresReread
    | ClaimAcquireRefused of ClaimRefusal

type ClaimDomainObservation =
    { Touch: ClaimTouch
      ExpectedGeneration: int64
      ActiveGrant: ClaimGrant option }

type ClaimMultiTouchPlan =
    { OperationId: string
      Owner: string
      Touches: ClaimTouch list
      Saga: SagaPlan
      Seal: string
      Cost: ClaimCost }

type ClaimPersistedPlan =
    { OperationId: string
      PlanSeal: string
      Touches: ClaimTouch list
      ExpectedGenerations: int64 list }

[<RequireQualifiedAccess>]
module ClaimTouchSetAdapter =
    val normalizeTouches: ClaimTouch list -> Result<ClaimTouch list, ClaimRefusal list>
    val touchesConflict: ClaimTouch list -> ClaimTouch list -> bool
    val claimAddress: subject: string -> Result<AggregateAddress, ClaimRefusal>
    val conflictAddress: ClaimTouch -> Result<AggregateAddress, ClaimRefusal>
    val authorityBytes: ClaimAuthorityRecord -> Result<byte array, ClaimRefusal list>
    val successorEligibility: now: int64 -> candidateOwner: string -> ClaimAuthorityObservation -> Result<SuccessorEligibility, ClaimRefusal>
    val planAcquire: ClaimAcquireIntent -> ClaimAuthorityObservation -> ClaimCommitMaterial -> Result<ClaimAcquirePlan, ClaimRefusal list>
    val confirmAcquire: ClaimAcquirePlan -> ReceivePackOutcome -> ClaimAuthorityObservation -> ClaimAcquireResult
    val authorizeEffect: ClaimGrant -> ClaimAuthorityObservation -> Result<JournalCommit, ClaimRefusal>
    val planMultiTouch: operationId: string -> owner: string -> ClaimTouch list -> ClaimDomainObservation list -> Result<ClaimMultiTouchPlan, ClaimRefusal list>
    val persistPlan: ClaimMultiTouchPlan -> ClaimPersistedPlan
    val authorizeMultiTouchEffects: ClaimMultiTouchPlan -> ClaimPersistedPlan option -> (ClaimGrant * ClaimAuthorityObservation) list -> Result<JournalCommit list, ClaimRefusal list>
    val planConflict: ClaimMultiTouchPlan -> acquired: SagaTouch list -> applied: SagaTouch list -> Result<SagaConflictPlan, string>
