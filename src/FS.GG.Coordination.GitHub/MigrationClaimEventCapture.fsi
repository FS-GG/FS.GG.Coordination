namespace FS.GG.Coordination.GitHub

/// One source-declared protected aggregate. The provider must independently prove this
/// inventory complete before it can contribute to nine-authority discovery.
type MigrationClaimJournalTarget =
    { Kind: JournalKind
      AggregateId: string }

/// Exact correspondence asserted by the protected event's canonical JSON, not by a
/// comment-order or lease inference.
type MigrationClaimEventLink =
    { CommentNodeId: string
      Target: MigrationClaimJournalTarget
      Generation: int64
      OperationId: string }

type MigrationClaimEventDeclaration =
    { RepositoryId: int64
      RepositoryFullName: string
      InventoryComplete: bool
      Targets: MigrationClaimJournalTarget list
      Links: MigrationClaimEventLink list }

type MigrationClaimEventMarker =
    { CommentNodeId: string
      SubjectNumber: int
      SubjectNodeId: string
      Kind: string
      BodySha256: string }

type MigrationClaimEventSnapshot =
    { RepositoryId: int64
      NativeSha256: string
      Markers: MigrationClaimEventMarker list
      JournalCount: int
      JournalEventCount: int
      NormalizedSha256: string }

type MigrationClaimEventFailure =
    | NativeFailure of MigrationReadFailure
    | MissingInventory
    | InvalidDeclaration of string
    | InvalidMarker of string
    | MissingCorrespondence of string
    | ForeignCorrespondence of string
    | JournalReadFailure of string
    | JournalInvalid of JournalFailure
    | ChangedPass

/// Read-only port. Its implementation must retain the complete protected ref history;
/// unauthorized, partial, and deleted reads must not be represented as absence.
type IMigrationClaimJournalRead =
    abstract Read: AggregateAddress -> JournalObservation

[<RequireQualifiedAccess>]
module MigrationClaimEventCapture =
    val reconcile:
        declaration:MigrationClaimEventDeclaration ->
        native:MigrationNativeActivityCapture ->
        journals:(MigrationClaimJournalTarget * JournalObservation) list ->
            Result<MigrationClaimEventSnapshot, MigrationClaimEventFailure>

    val captureStable:
        options:MigrationGitHubReadOptions ->
        nativeTransport:IMigrationGitHubReadTransport ->
        declaration:MigrationClaimEventDeclaration ->
        journalRead:IMigrationClaimJournalRead ->
            Result<MigrationClaimEventSnapshot, MigrationClaimEventFailure>
