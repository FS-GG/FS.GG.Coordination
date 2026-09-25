namespace FS.GG.Coordination.Cli

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
        Result<ProtectedIssueCensusRecoveryHold, string>
