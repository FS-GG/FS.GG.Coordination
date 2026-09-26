namespace FS.GG.Coordination.Cli

type ProtectedIssueCensusSignedRecoveryReadDescription =
    { HandoffResourceId: string
      HandoffArtifactSha256: string
      NativeAttemptResourceId: string
      NativeAttemptArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      AtomicMarkerAndNativeHeadReadback: bool }

type ProtectedIssueCensusSignedRecoveryReadback =
    { Marker: ProtectedIssueCensusHandoffRequest
      NativeHead: ProtectedIssueCensusNativeAttemptHead }

/// Protected linearizable observation of both authorities used for the final
/// hold classification. No effect is permitted through this port.
type IProtectedIssueCensusSignedRecoveryReadPort =
    abstract Describe: unit -> ProtectedIssueCensusSignedRecoveryReadDescription
    abstract ReadAuthority:
        attemptId:string -> ProtectedIssueCensusSignedRecoveryReadback option

/// Source-only signed snapshot qualification over the exact native recovery read.
[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusSignedRecovery =
    val inspectSigned:
        attestationPins:ProtectedIssueCensusNativeAttestationPins ->
        attestation:ProtectedIssueCensusNativeSnapshotAttestation option ->
        clock:IProtectedIssueCensusClockPort option ->
        handoffPins:ProtectedIssueCensusHandoffPins ->
        nativePins:ProtectedIssueCensusNativeAttemptPins ->
        expectedMarker:ProtectedIssueCensusHandoffRequest ->
        handoffPort:IProtectedIssueCensusHandoffPort option ->
        nativePort:IProtectedIssueCensusNativeAttemptPort option ->
        recoveryReadPort:IProtectedIssueCensusSignedRecoveryReadPort option ->
        Result<ProtectedIssueCensusRecoveryHold, string>
