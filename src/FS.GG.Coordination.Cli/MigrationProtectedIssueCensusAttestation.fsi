namespace FS.GG.Coordination.Cli

open System

type ProtectedIssueCensusAttestationPins =
    { SignerPublicKeySpkiBase64: string
      SignerPublicKeySha256: string
      SignerArtifactSha256: string
      ClockResourceId: string
      ClockArtifactSha256: string
      MaximumAgeSeconds: int }

type ProtectedIssueCensusClockDescription =
    { ClockResourceId: string
      ClockArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      MonotonicUtc: bool }

type ProtectedIssueCensusSealAttestation =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      CorpusSha256: string
      StoreGeneration: int64
      IssuedAtUtc: DateTimeOffset
      ExpiresAtUtc: DateTimeOffset
      SignatureBase64: string }

type IProtectedIssueCensusClockPort =
    /// The installed description must be pinned and candidate-inaccessible;
    /// source-only fake descriptions do not prove host custody or monotonicity.
    abstract Describe: unit -> ProtectedIssueCensusClockDescription
    abstract ReadNow: unit -> DateTimeOffset option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusAttestation =
    /// Exact public payload for a future protected signer; source-only and non-authorizing.
    val signingPayload:
        pins:ProtectedIssueCensusAttestationPins ->
        attestation:ProtectedIssueCensusSealAttestation -> byte[]

    /// Fake-port verifier only. The key, clock, generation and signer custody need protected installation.
    val verify:
        pins:ProtectedIssueCensusAttestationPins ->
        selection:ProtectedIssueCensusSelection ->
        storeResourceId:string ->
        proof:ProtectedIssueCensusProof ->
        expectedStoreGeneration:int64 ->
        attestation:ProtectedIssueCensusSealAttestation option ->
        clock:IProtectedIssueCensusClockPort option -> Result<unit, string>
