namespace FS.GG.Coordination.GitHub

type JournalKind = Claim | Review | Operation | Cutover

type AggregateAddress =
    { CanonicalId: string
      Digest: string
      Shard: string
      Kind: JournalKind
      Ref: string }

type JournalHead =
    { SchemaVersion: int
      Address: AggregateAddress
      Generation: int64
      EventDigest: string
      SnapshotDigest: string option
      Terminal: bool
      PriorHeadDigest: string option
      HeadDigest: string }

type CanonicalBlob = { Bytes: byte array; Digest: string }

type JournalCheckpoint =
    { Blob: CanonicalBlob
      HighWaterGeneration: int64
      EventDigests: string list
      AggregateDigest: string
      ReplayAggregateDigest: string }

type JournalCommit =
    { CommitOid: string
      ParentOid: string option
      TreeOid: string
      OperationId: string
      Head: JournalHead
      HeadBytes: byte array
      Event: CanonicalBlob
      Checkpoint: JournalCheckpoint option }

type JournalObservation =
    | JournalComplete of revision: string * commits: JournalCommit list
    | JournalIncomplete of reason: string
    | JournalUnsupported of reason: string
    | JournalUnauthorized of reason: string
    | JournalUnreadable of reason: string
    | JournalDeleted
    | JournalDivergent of reason: string

type JournalFailure =
    | InvalidAggregateId
    | InvalidJournalRef
    | IncompleteJournal of string
    | UnsupportedJournal of string
    | UnauthorizedJournal of string
    | UnreadableJournal of string
    | DeletedJournal
    | DivergentJournal of string
    | UnknownJournalSchema of int
    | WrongJournalShard
    | InvalidDigest of string
    | MissingJournalParent
    | DuplicateJournalGeneration of int64
    | NonMonotonicJournalGeneration
    | TerminalJournalAppend
    | InvalidJournalCommit

type JournalSnapshot = { Revision: string; Current: JournalCommit; Commits: JournalCommit list }

type CasProposal =
    { Address: AggregateAddress
      ObservedObjectId: string
      ProposedCommit: JournalCommit
      OperationId: string
      Refspec: string
      ForceWithLease: string }

type ReceivePackOutcome = ReceiveAccepted | ReceiveParentConflict | ReceiveDefiniteRefusal of string | ReceiveResponseUnknown
type ReconcileOutcome = Accepted | ParentConflict | DefiniteRefusal of string | ResponseUnknownRequiresReread

type FencedGrant = { Address: AggregateAddress; JournalCommit: string; Generation: int64 }
type EffectRefusal = StaleFence | TerminalAuthority | EffectAuthorityUnavailable of JournalFailure

type SagaTouch = { Address: AggregateAddress; ExpectedGeneration: int64 }
type SagaPlan = { OperationId: string; PersistBeforeEffects: SagaTouch list; AcquisitionOrder: SagaTouch list }
type Compensation = { OperationId: string; Address: AggregateAddress; Generation: int64; OriginalResultRetained: bool }
type SagaConflictPlan = { ReleaseUnconsumed: SagaTouch list; CompensateApplied: Compensation list }

type EffectiveBranchRule = CreationRestricted | UpdateRestricted | DeletionRejected | NonFastForwardRejected

type Ruleset =
    { Id: int64
      Name: string
      Active: bool
      Target: string
      BypassAppIds: int64 list
      RestrictsCreationAndUpdate: bool
      RejectsDeletionAndNonFastForward: bool }

type ProtectionObservation =
    { Complete: bool
      RepositoryId: int64
      Writer: Ruleset
      Integrity: Ruleset
      EffectiveRulesComplete: bool
      EffectiveRules: EffectiveBranchRule list }

type ProtectionFailure = IncompleteProtectionObservation | AuthorityRepositoryDrift | WriterRulesetDrift | IntegrityRulesetDrift | TargetPatternDrift | BypassDrift | EffectiveRulesDrift

[<RequireQualifiedAccess>]
module ShardedJournalAdapter =
    val journalKind: JournalKind -> string
    val address: JournalKind -> aggregateId: string -> Result<AggregateAddress, JournalFailure>
    val canonicalJson: string -> Result<byte array, string>
    val sha256: byte array -> string
    val journalHeadBytes: JournalHead -> byte array
    val validate: AggregateAddress -> JournalObservation -> Result<JournalSnapshot, JournalFailure>
    val planCas: operationId: string -> JournalSnapshot -> proposed: JournalCommit -> Result<CasProposal, JournalFailure>
    val reconcile: CasProposal -> ReceivePackOutcome -> JournalObservation -> ReconcileOutcome
    val authorizeEffect: FencedGrant -> JournalObservation -> Result<JournalCommit, EffectRefusal>
    val planSaga: operationId: string -> SagaTouch list -> Result<SagaPlan, string>
    val planConflict: SagaPlan -> acquired: SagaTouch list -> applied: SagaTouch list -> Result<SagaConflictPlan, string>
    val validateProtection: ProtectionObservation -> Result<unit, ProtectionFailure>
