namespace FS.GG.Coordination.Cli

type ProtectedIssueCensusReleasePins =
    { ReleaseResourceId: string
      ReleaseArtifactSha256: string }

type ProtectedIssueCensusReleaseDescription =
    { ReleaseResourceId: string
      ReleaseArtifactSha256: string
      StoreResourceId: string
      StoreArtifactSha256: string
      JournalResourceId: string
      JournalArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      AtomicCompareAndConsume: bool }

type ProtectedIssueCensusReleaseRequest =
    { ReservationId: string
      Selection: ProtectedIssueCensusSelection
      ClaimId: string
      AttestationPayloadSha256: string
      StoreResourceId: string
      StoreGeneration: int64
      StoreCorpusSha256: string
      ExpectedStoreHeadSha256: string
      JournalResourceId: string
      ExpectedJournalGeneration: int64
      ExpectedJournalHeadSha256: string }

type ProtectedIssueCensusReleaseOutcome =
    | ReleaseReserved
    | ReleaseDuplicate
    | ReleaseConflict
    | ReleaseUnknown

/// A protected installation must compare both heads and consume the claim in one
/// durable transaction. This source contract contains no token or provider adapter.
type IProtectedIssueCensusReleasePort =
    abstract Describe: unit -> ProtectedIssueCensusReleaseDescription
    abstract ReserveOnce: ProtectedIssueCensusReleaseRequest -> ProtectedIssueCensusReleaseOutcome
    abstract ReadReservation: string -> ProtectedIssueCensusReleaseRequest option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusRelease =
    /// Fake-port reservation only. A successful result does not release a token.
    val reserve:
        attestationPins:ProtectedIssueCensusAttestationPins ->
        claimPins:ProtectedIssueCensusClaimPins ->
        storePins:ProtectedIssueCensusStoreHeadPins ->
        releasePins:ProtectedIssueCensusReleasePins ->
        selection:ProtectedIssueCensusSelection ->
        proof:ProtectedIssueCensusProof ->
        attestation:ProtectedIssueCensusSealAttestation option ->
        clock:IProtectedIssueCensusClockPort option ->
        storePort:IProtectedIssueCensusStoreHeadPort option ->
        claimPort:IProtectedIssueCensusClaimPort option ->
        releasePort:IProtectedIssueCensusReleasePort option -> Result<unit, string>
