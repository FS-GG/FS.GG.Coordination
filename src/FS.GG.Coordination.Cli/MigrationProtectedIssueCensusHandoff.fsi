namespace FS.GG.Coordination.Cli

open System

type ProtectedIssueCensusHandoffPins =
    { HandoffResourceId: string
      HandoffArtifactSha256: string
      VaultResourceId: string
      VaultArtifactSha256: string
      NativeAttemptNamespaceId: string
      AppId: int64
      InstallationId: int64
      RepositoryId: int64
      PermissionSha256: string }

type ProtectedIssueCensusHandoffDescription =
    { HandoffResourceId: string
      HandoffArtifactSha256: string
      VaultResourceId: string
      VaultArtifactSha256: string
      NativeAttemptNamespaceId: string
      ReleaseResourceId: string
      ReleaseArtifactSha256: string
      ClockResourceId: string
      ClockArtifactSha256: string
      AppId: int64
      InstallationId: int64
      RepositoryId: int64
      PermissionSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      AtomicReservationConsumeAndMark: bool
      AtomicExpiryCompare: bool }

type ProtectedIssueCensusHandoffRequest =
    { NativeAttemptId: string
      ReservationId: string
      ClaimId: string
      Selection: ProtectedIssueCensusSelection
      AppId: int64
      InstallationId: int64
      RepositoryId: int64
      PermissionSha256: string
      VaultResourceId: string
      ExpectedStoreHeadSha256: string
      ExpectedJournalHeadSha256: string
      ClockResourceId: string
      ClockArtifactSha256: string
      SignedExpiresAtUtc: DateTimeOffset }

type ProtectedIssueCensusHandoffOutcome =
    | HandoffMarked
    | HandoffDuplicate
    | HandoffConflict
    | HandoffUnknown

/// Source-only marker before any native token exposure. Candidate access flags
/// cover both the handoff journal and vault. A protected installation must
/// consume the reservation and mark the shared native attempt atomically.
type IProtectedIssueCensusHandoffPort =
    abstract Describe: unit -> ProtectedIssueCensusHandoffDescription
    abstract MarkOnce: ProtectedIssueCensusHandoffRequest -> ProtectedIssueCensusHandoffOutcome
    abstract ReadMarker: string -> ProtectedIssueCensusHandoffRequest option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusHandoff =
    /// Fake-port marker only. No token is minted, returned, logged or dispatched.
    val mark:
        attestationPins:ProtectedIssueCensusAttestationPins ->
        storePins:ProtectedIssueCensusStoreHeadPins ->
        claimPins:ProtectedIssueCensusClaimPins ->
        releasePins:ProtectedIssueCensusReleasePins ->
        handoffPins:ProtectedIssueCensusHandoffPins ->
        selection:ProtectedIssueCensusSelection ->
        proof:ProtectedIssueCensusProof ->
        seal:ProtectedIssueCensusSealAttestation option ->
        clock:IProtectedIssueCensusClockPort option ->
        expectedReservation:ProtectedIssueCensusReleaseRequest ->
        releasePort:IProtectedIssueCensusReleasePort option ->
        handoffPort:IProtectedIssueCensusHandoffPort option -> Result<unit, string>
