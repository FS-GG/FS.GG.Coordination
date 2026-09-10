namespace FS.GG.Coordination.Core

open System
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration

module OrchestrationPersistence =
    type EffectChange = NoEffect | IntentAdded of EffectIntent | Settled of OperationId
    type SerializedEvent =
        { PersistenceId: string; Sequence: int64; EventId: Guid; SchemaVersion: int
          SerializerVersion: string; Payload: byte array; PayloadSha256: string
          EffectChange: EffectChange; RecordedAt: DateTimeOffset }
    type InboxCommand =
        { PersistenceId: string; CommandId: CommandId; BodySha256: string; ReceivedAt: DateTimeOffset }
    type Snapshot =
        { PersistenceId: string; Sequence: int64; SchemaVersion: int; Payload: byte array; PayloadSha256: string }
    type AppendRequest = { Inbox: InboxCommand; ExpectedSequence: int64; Events: SerializedEvent list }
    type AppendOutcome =
        | Appended of lastSequence:int64
        /// Returns the terminal sequence stored for the original identical command.
        | Duplicate of originalLastSequence:int64
        | Conflict | WrongExpectedSequence of actual:int64 | InvalidAppend of reason:string
    type ReadinessFailure =
        | StoreUnavailable of string | ReadOnlyStore | CapacityUnavailable
        | CorruptRecord of string * int64 | UnknownEventVersion of string * int64 * int
        | UnknownSerializerVersion of string | SnapshotAheadOfJournal of string
        | MigrationInterrupted of string | IncompatibleDowngrade of databaseVersion:int * runtimeVersion:int
        | BackupRequiresReconciliation of string
    type RecoveryResult =
        { Events: SerializedEvent list; Snapshot: Snapshot option; UnsettledEffects: EffectIntent list
          RequiresExternalReconciliation: bool }
    type ProjectionCheckpoint =
        { ProjectionId: string; PersistenceId: string; Sequence: int64; ProjectionVersion: int }
    type CandidatePut = { Candidate: CandidateArtifact; Bytes: byte array }
    type CandidatePutOutcome = Existing of CandidateStorageReceipt | IdentityConflict | DigestConflict | InvalidArchive | CapacityRefused
    type IJournalStore =
        abstract CheckReadiness: CancellationToken -> Task<Result<unit,ReadinessFailure list>>
        /// One transaction admits/deduplicates the inbox command and appends its events. A new
        /// command with no events is invalid; an identical duplicate may supply no events and
        /// returns the original terminal sequence. EffectChange is part of the authoritative
        /// event row, so pending outbox work is derived from event rows rather than a dual write.
        abstract Append: AppendRequest * CancellationToken -> Task<AppendOutcome>
        abstract Recover: persistenceId:string * CancellationToken -> Task<Result<RecoveryResult,ReadinessFailure list>>
        abstract SaveSnapshot: Snapshot * CancellationToken -> Task<Result<unit,ReadinessFailure>>
        abstract SaveProjectionCheckpoint: ProjectionCheckpoint * CancellationToken -> Task<Result<unit,ReadinessFailure>>
    type ICandidateStore =
        /// Stored means owner-controlled bytes and metadata are recoverable; partial uploads are not acknowledged.
        abstract Put: CandidatePut * CancellationToken -> Task<Result<CandidateStorageReceipt,CandidatePutOutcome>>
        abstract Read: CandidateId * CancellationToken -> Task<Result<CandidatePut,string>>
        abstract Quarantine: CandidateId * reason:string * CancellationToken -> Task<Result<unit,string>>
        abstract CleanupUnreferenced: olderThan:DateTimeOffset * maximum:int * CancellationToken -> Task<int>
    type IBackupReconciler =
        abstract BackupIdentity: string
        abstract ReconcileGenerationsRevocationsAndEffects: CancellationToken -> Task<Result<unit,ReadinessFailure list>>
