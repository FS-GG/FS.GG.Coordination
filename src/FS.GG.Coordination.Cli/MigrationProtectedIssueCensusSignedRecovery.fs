namespace FS.GG.Coordination.Cli

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusSignedRecovery =
    let inspectSigned (attestationPins: ProtectedIssueCensusNativeAttestationPins)
                      (attestation: ProtectedIssueCensusNativeSnapshotAttestation option)
                      (clock: IProtectedIssueCensusClockPort option)
                      (handoffPins: ProtectedIssueCensusHandoffPins)
                      (nativePins: ProtectedIssueCensusNativeAttemptPins)
                      (expectedMarker: ProtectedIssueCensusHandoffRequest)
                      (handoffPort: IProtectedIssueCensusHandoffPort option)
                      (nativePort: IProtectedIssueCensusNativeAttemptPort option) =
        if attestationPins.NativeAttemptResourceId <> nativePins.AttemptResourceId
           || attestationPins.NativeAttemptArtifactSha256 <> nativePins.AttemptArtifactSha256 then
            Error "protected-native-attestation-pins"
        else
            match MigrationProtectedIssueCensusAttemptRecovery.inspectWithSnapshot
                      handoffPins nativePins expectedMarker handoffPort nativePort with
            | Error reason -> Error reason
            | Ok (snapshot, hold) ->
                match MigrationProtectedIssueCensusNativeAttestation.verify
                          attestationPins expectedMarker snapshot attestation clock with
                | Error reason -> Error reason
                | Ok () -> Ok hold
