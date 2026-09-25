namespace FS.GG.Coordination.Cli

open System

type ProtectedIssueCensusNativeAttemptPins =
    { AttemptResourceId: string
      AttemptArtifactSha256: string }

type ProtectedIssueCensusNativeAttemptDescription =
    { AttemptResourceId: string
      AttemptArtifactSha256: string
      NativeAttemptNamespaceId: string
      VaultResourceId: string
      VaultArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      AuthoritativeCompleteReadback: bool }

type ProtectedIssueCensusNativeAttemptPhase =
    | InvocationUnknown
    | TokenVaulted
    | NativeRevoked

type ProtectedIssueCensusNativeAttemptRecord =
    { Request: ProtectedIssueCensusHandoffRequest
      ProviderAttemptId: string
      VaultResourceId: string
      Phase: ProtectedIssueCensusNativeAttemptPhase
      TokenFingerprintSha256: string option
      RevocationReceiptSha256: string option }

type ProtectedIssueCensusRecoveryHold =
    | NativeResultUnknown
    | NativeRevocationRequired
    | ProtectedReceiptRequired

type IProtectedIssueCensusNativeAttemptPort =
    abstract Describe: unit -> ProtectedIssueCensusNativeAttemptDescription
    abstract ReadAttempts: string -> ProtectedIssueCensusNativeAttemptRecord list option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusAttemptRecovery =
    let private exactSha (value: string) =
        not (isNull value) && value.Length = 64
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let inspect (handoffPins: ProtectedIssueCensusHandoffPins)
                (nativePins: ProtectedIssueCensusNativeAttemptPins)
                (expectedMarker: ProtectedIssueCensusHandoffRequest)
                (handoffPort: IProtectedIssueCensusHandoffPort option)
                (nativePort: IProtectedIssueCensusNativeAttemptPort option)
                : Result<ProtectedIssueCensusRecoveryHold, string> =
        if String.IsNullOrWhiteSpace handoffPins.HandoffResourceId
           || not (exactSha handoffPins.HandoffArtifactSha256)
           || String.IsNullOrWhiteSpace handoffPins.VaultResourceId
           || not (exactSha handoffPins.VaultArtifactSha256)
           || String.IsNullOrWhiteSpace handoffPins.NativeAttemptNamespaceId
           || handoffPins.AppId < 1L || handoffPins.InstallationId < 1L
           || handoffPins.RepositoryId < 1L
           || not (exactSha handoffPins.PermissionSha256)
           || String.IsNullOrWhiteSpace nativePins.AttemptResourceId
           || not (exactSha nativePins.AttemptArtifactSha256) then
            Error "protected-census-attempt-pins"
        else
            match handoffPort, nativePort with
            | Some handoff, Some native ->
                let installed =
                    try Some (handoff.Describe(), native.Describe())
                    with _ -> None
                match installed with
                | None -> Error "protected-census-attempt-unavailable"
                | Some (markerDescription, nativeDescription) when
                    markerDescription.HandoffResourceId <> handoffPins.HandoffResourceId
                    || markerDescription.HandoffArtifactSha256 <> handoffPins.HandoffArtifactSha256
                    || markerDescription.VaultResourceId <> handoffPins.VaultResourceId
                    || markerDescription.VaultArtifactSha256 <> handoffPins.VaultArtifactSha256
                    || markerDescription.NativeAttemptNamespaceId
                       <> handoffPins.NativeAttemptNamespaceId
                    || markerDescription.AppId <> handoffPins.AppId
                    || markerDescription.InstallationId <> handoffPins.InstallationId
                    || markerDescription.RepositoryId <> handoffPins.RepositoryId
                    || markerDescription.PermissionSha256 <> handoffPins.PermissionSha256
                    || markerDescription.CandidateMayRead || markerDescription.CandidateMayWrite
                    || not markerDescription.AtomicReservationConsumeAndMark
                    || not markerDescription.AtomicExpiryCompare
                    || nativeDescription.AttemptResourceId <> nativePins.AttemptResourceId
                    || nativeDescription.AttemptArtifactSha256 <> nativePins.AttemptArtifactSha256
                    || nativeDescription.NativeAttemptNamespaceId
                       <> handoffPins.NativeAttemptNamespaceId
                    || nativeDescription.VaultResourceId <> handoffPins.VaultResourceId
                    || nativeDescription.VaultArtifactSha256 <> handoffPins.VaultArtifactSha256
                    || nativeDescription.CandidateMayRead || nativeDescription.CandidateMayWrite
                    || not nativeDescription.AuthoritativeCompleteReadback ->
                    Error "protected-census-attempt-installation"
                | Some _ ->
                    if expectedMarker.NativeAttemptId
                       <> MigrationProtectedIssueCensusHandoff.attemptId
                              expectedMarker.ReservationId handoffPins
                       || expectedMarker.AppId <> handoffPins.AppId
                       || expectedMarker.InstallationId <> handoffPins.InstallationId
                       || expectedMarker.RepositoryId <> handoffPins.RepositoryId
                       || expectedMarker.Selection.RepositoryId <> handoffPins.RepositoryId
                       || expectedMarker.PermissionSha256 <> handoffPins.PermissionSha256
                       || expectedMarker.VaultResourceId <> handoffPins.VaultResourceId
                       || not (exactSha expectedMarker.ReservationId)
                       || not (exactSha expectedMarker.ClaimId)
                       || not (exactSha expectedMarker.ExpectedStoreHeadSha256)
                       || not (exactSha expectedMarker.ExpectedJournalHeadSha256) then
                        Error "protected-census-attempt-binding"
                    else
                        let readback =
                            try handoff.ReadMarker expectedMarker.NativeAttemptId
                            with _ -> None
                        match readback with
                        | None -> Error "protected-census-attempt-unknown"
                        | Some marker when marker <> expectedMarker ->
                            Error "protected-census-attempt-binding"
                        | Some _ ->
                            let nativeReadback =
                                try native.ReadAttempts expectedMarker.NativeAttemptId
                                with _ -> None
                            match nativeReadback with
                            | None | Some [] -> Error "protected-census-attempt-unknown"
                            | Some [attempt] when attempt.Request <> expectedMarker
                                                  || attempt.ProviderAttemptId
                                                     <> expectedMarker.NativeAttemptId
                                                  || attempt.VaultResourceId
                                                     <> handoffPins.VaultResourceId ->
                                Error "protected-census-attempt-binding"
                            | Some [attempt] ->
                                match attempt.Phase, attempt.TokenFingerprintSha256,
                                      attempt.RevocationReceiptSha256 with
                                | InvocationUnknown, None, None -> Ok NativeResultUnknown
                                | TokenVaulted, Some tokenHash, None when exactSha tokenHash ->
                                    Ok NativeRevocationRequired
                                | NativeRevoked, Some tokenHash, Some receiptHash
                                    when exactSha tokenHash && exactSha receiptHash ->
                                    Ok ProtectedReceiptRequired
                                | _ -> Error "protected-census-attempt-phase"
                            | Some _ -> Error "protected-census-attempt-duplicate"
            | _ -> Error "protected-census-attempt-unavailable"
