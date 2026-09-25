namespace FS.GG.Coordination.Cli

open System

type ProtectedIssueCensusAttestationPins =
    { SignerPublicKeySpkiBase64: string
      SignerPublicKeySha256: string
      SignerArtifactSha256: string
      ClockResourceId: string
      MaximumAgeSeconds: int }

type ProtectedIssueCensusSealAttestation =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      CorpusSha256: string
      StoreGeneration: int64
      IssuedAtUtc: DateTimeOffset
      ExpiresAtUtc: DateTimeOffset
      SignatureBase64: string }

type IProtectedIssueCensusClockPort =
    abstract Describe: unit -> string
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
