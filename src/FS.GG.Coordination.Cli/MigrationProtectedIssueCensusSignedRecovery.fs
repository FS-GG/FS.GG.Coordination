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

type IProtectedIssueCensusSignedRecoveryReadPort =
    abstract Describe: unit -> ProtectedIssueCensusSignedRecoveryReadDescription
    abstract ReadAuthority:
        attemptId:string -> ProtectedIssueCensusSignedRecoveryReadback option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusSignedRecovery =
    let inspectSigned (attestationPins: ProtectedIssueCensusNativeAttestationPins)
                      (attestation: ProtectedIssueCensusNativeSnapshotAttestation option)
                      (clock: IProtectedIssueCensusClockPort option)
                      (handoffPins: ProtectedIssueCensusHandoffPins)
                      (nativePins: ProtectedIssueCensusNativeAttemptPins)
                      (expectedMarker: ProtectedIssueCensusHandoffRequest)
                      (handoffPort: IProtectedIssueCensusHandoffPort option)
                      (nativePort: IProtectedIssueCensusNativeAttemptPort option)
                      (recoveryReadPort: IProtectedIssueCensusSignedRecoveryReadPort option) =
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
                    // Sequential final reads only move the race between authorities.
                    // The protected adapter must expose one linearizable observation
                    // of the marker and native head. This remains a hold-only read.
                    match recoveryReadPort with
                    | None -> Error "protected-census-attempt-unknown"
                    | Some finalRead ->
                        let installed =
                            try Some (finalRead.Describe())
                            with _ -> None
                        match installed with
                        | None -> Error "protected-census-attempt-unknown"
                        | Some description when
                            description.HandoffResourceId <> handoffPins.HandoffResourceId
                            || description.HandoffArtifactSha256
                               <> handoffPins.HandoffArtifactSha256
                            || description.NativeAttemptResourceId
                               <> nativePins.AttemptResourceId
                            || description.NativeAttemptArtifactSha256
                               <> nativePins.AttemptArtifactSha256
                            || description.CandidateMayRead
                            || description.CandidateMayWrite
                            || not description.AtomicMarkerAndNativeHeadReadback ->
                            Error "protected-census-attempt-installation"
                        | Some _ ->
                            let readback =
                                try finalRead.ReadAuthority expectedMarker.NativeAttemptId
                                with _ -> None
                            match readback with
                            | None -> Error "protected-census-attempt-unknown"
                            | Some current when
                                not (MigrationProtectedIssueCensusAttemptRecovery.validMarkerChain
                                         current.Marker)
                                || current.Marker <> expectedMarker ->
                                Error "protected-census-attempt-binding"
                            | Some current when current.NativeHead <> snapshot.Head ->
                                Error "protected-census-attempt-head"
                            | Some _ -> Ok hold
