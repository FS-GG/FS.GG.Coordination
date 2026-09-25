namespace FS.GG.Coordination.Cli

open System

type ProtectedIssueCensusNativeAttestationPins =
    { SignerPublicKeySpkiBase64: string
      SignerPublicKeySha256: string
      SignerArtifactSha256: string
      ClockResourceId: string
      ClockArtifactSha256: string
      NativeAttemptResourceId: string
      NativeAttemptArtifactSha256: string
      MaximumAgeSeconds: int }

type ProtectedIssueCensusNativeSnapshotAttestation =
    { AttemptId: string
      NativeAttemptResourceId: string
      HeadGeneration: int64
      SnapshotSealSha256: string
      IssuedAtUtc: DateTimeOffset
      ExpiresAtUtc: DateTimeOffset
      SignatureBase64: string }

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusNativeAttestation =
    /// Public canonical bytes for a future candidate-inaccessible protected signer.
    val signingPayload:
        pins:ProtectedIssueCensusNativeAttestationPins ->
        attestation:ProtectedIssueCensusNativeSnapshotAttestation -> byte[]

    /// Fake-key source verifier only; it does not install a signer or native reader.
    val verify:
        pins:ProtectedIssueCensusNativeAttestationPins ->
        handoffPins:ProtectedIssueCensusHandoffPins ->
        expectedMarker:ProtectedIssueCensusHandoffRequest ->
        snapshot:ProtectedIssueCensusNativeAttemptSnapshot ->
        attestation:ProtectedIssueCensusNativeSnapshotAttestation option ->
        clock:IProtectedIssueCensusClockPort option -> Result<unit, string>
