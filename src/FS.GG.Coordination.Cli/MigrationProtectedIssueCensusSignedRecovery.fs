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
        elif expectedMarker.ClockResourceId <> attestationPins.ClockResourceId
             || expectedMarker.ClockArtifactSha256 <> attestationPins.ClockArtifactSha256 then
            Error "protected-native-attestation-binding"
        else
            match MigrationProtectedIssueCensusAttemptRecovery.inspectWithSnapshot
                      handoffPins nativePins expectedMarker handoffPort nativePort with
            | Error reason -> Error reason
            | Ok (snapshot, hold) ->
                match MigrationProtectedIssueCensusNativeAttestation.verify
                          attestationPins handoffPins expectedMarker snapshot attestation clock with
                | Error reason -> Error reason
                | Ok () ->
                    // The handoff marker or signed snapshot can be superseded while
                    // the protected clock and signature are checked. Return no
                    // classification unless both authorities still match; this read
                    // is still hold-only.
                    match handoffPort, nativePort with
                    | Some handoff, Some native ->
                        let markerAfter =
                            try handoff.ReadMarker expectedMarker.NativeAttemptId
                            with _ -> None
                        match markerAfter with
                        | None -> Error "protected-census-attempt-unknown"
                        | Some current when current <> expectedMarker ->
                            Error "protected-census-attempt-binding"
                        | Some _ ->
                            try
                                match native.ReadHead() with
                                | Some current when current = snapshot.Head -> Ok hold
                                | Some _ -> Error "protected-census-attempt-head"
                                | None -> Error "protected-census-attempt-unknown"
                            with _ -> Error "protected-census-attempt-unknown"
                    | _ -> Error "protected-census-attempt-unknown"
