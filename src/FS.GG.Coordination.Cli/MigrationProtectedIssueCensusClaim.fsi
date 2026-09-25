namespace FS.GG.Coordination.Cli

type ProtectedIssueCensusClaimPins =
    { JournalResourceId: string
      JournalArtifactSha256: string }

type ProtectedIssueCensusClaimDescription =
    { JournalResourceId: string
      JournalArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      ImmutableJournal: bool }

type ProtectedIssueCensusClaimHead =
    { JournalResourceId: string
      Generation: int64
      SealSha256: string }

type ProtectedIssueCensusClaimRequest =
    { ClaimId: string
      AttestationPayloadSha256: string
      Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      StoreGeneration: int64
      JournalResourceId: string
      ExpectedHeadGeneration: int64
      ExpectedHeadSha256: string }

type ProtectedIssueCensusClaimRecord =
    { Request: ProtectedIssueCensusClaimRequest
      CommitGeneration: int64
      CommitHeadSha256: string }

type ProtectedIssueCensusClaimOutcome =
    | ClaimCommitted
    | ClaimDuplicate
    | ClaimConflict
    | ClaimUnknown

type IProtectedIssueCensusClaimPort =
    abstract Describe: unit -> ProtectedIssueCensusClaimDescription
    abstract ReadHead: unit -> ProtectedIssueCensusClaimHead option
    abstract ClaimOnce: ProtectedIssueCensusClaimRequest -> ProtectedIssueCensusClaimOutcome
    abstract ReadClaim: string -> ProtectedIssueCensusClaimRecord option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusClaim =
    /// Canonical successor head; a protected store must attest CAS and durability.
    val expectedCommitHeadSha256:
        previous:ProtectedIssueCensusClaimHead ->
        request:ProtectedIssueCensusClaimRequest -> string

    /// Stable identity excludes signature bytes and issuance time; the protected journal must enforce atomicity.
    val claimId:
        selection:ProtectedIssueCensusSelection ->
        storeResourceId:string ->
        storeGeneration:int64 -> string

    /// Source-only fake-port contract; no protected durable journal is installed.
    val verifyAndClaim:
        attestationPins:ProtectedIssueCensusAttestationPins ->
        claimPins:ProtectedIssueCensusClaimPins ->
        selection:ProtectedIssueCensusSelection ->
        storeResourceId:string ->
        proof:ProtectedIssueCensusProof ->
        storeGeneration:int64 ->
        attestation:ProtectedIssueCensusSealAttestation option ->
        clock:IProtectedIssueCensusClockPort option ->
        claimPort:IProtectedIssueCensusClaimPort option -> Result<unit, string>
