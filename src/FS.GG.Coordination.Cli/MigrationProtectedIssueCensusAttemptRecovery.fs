namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

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

type ProtectedIssueCensusNativeAttemptHead =
    { AttemptResourceId: string
      Generation: int64
      SealSha256: string }

type ProtectedIssueCensusNativeAttemptSnapshot =
    { Head: ProtectedIssueCensusNativeAttemptHead
      AttemptId: string
      Complete: bool
      Records: ProtectedIssueCensusNativeAttemptRecord list }

type ProtectedIssueCensusRecoveryHold =
    | NativeResultUnknown
    | NativeRevocationRequired
    | ProtectedReceiptRequired

type IProtectedIssueCensusNativeAttemptPort =
    abstract Describe: unit -> ProtectedIssueCensusNativeAttemptDescription
    abstract ReadHead: unit -> ProtectedIssueCensusNativeAttemptHead option
    abstract ReadAttempts: string -> ProtectedIssueCensusNativeAttemptSnapshot option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusAttemptRecovery =
    let private exactSha (value: string) =
        not (isNull value) && value.Length = 64
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let validUtf8Atom (value: string) =
        if String.IsNullOrWhiteSpace value then false
        else
            try
                UTF8Encoding(false, true).GetBytes value |> ignore
                true
            with _ -> false

    let validSelectionShape (selection: ProtectedIssueCensusSelection) =
        let exactCommitSha (value: string) =
            not (isNull value) && value.Length = 40
            && (value |> Seq.forall (fun ch ->
                (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
        let canonicalOrigin =
            try
                let origin = Uri(selection.ApiOrigin, UriKind.Absolute)
                origin.Scheme = Uri.UriSchemeHttps
                && String.IsNullOrEmpty origin.UserInfo
                && origin.GetLeftPart(UriPartial.Authority) = selection.ApiOrigin
            with _ -> false
        selection.RunId > 0L && selection.RunAttempt > 0
        && validUtf8Atom selection.RunNonce
        && exactCommitSha selection.CandidateSha
        && exactCommitSha selection.WorkflowSha
        && canonicalOrigin && validUtf8Atom selection.ApiOrigin
        && validUtf8Atom selection.Owner
        && validUtf8Atom selection.Repository
        && selection.RepositoryId > 0L

    let validMarkerChain (marker: ProtectedIssueCensusHandoffRequest) =
        if not (validSelectionShape marker.Selection)
           || not (validUtf8Atom marker.StoreResourceId)
           || marker.StoreGeneration < 1L
           || not (exactSha marker.ExpectedStoreHeadSha256)
           || not (validUtf8Atom marker.JournalResourceId)
           || marker.ExpectedJournalGeneration < 1L
           || not (exactSha marker.ExpectedJournalHeadSha256)
           || not (validUtf8Atom marker.VaultResourceId)
           || not (validUtf8Atom marker.ClockResourceId)
           || marker.SignedExpiresAtUtc.Offset <> TimeSpan.Zero then false
        else
            let claimId =
                MigrationProtectedIssueCensusClaim.claimId
                    marker.Selection marker.StoreResourceId marker.StoreGeneration
            let journalHead =
                { JournalResourceId=marker.JournalResourceId
                  Generation=marker.ExpectedJournalGeneration
                  SealSha256=marker.ExpectedJournalHeadSha256 }
            marker.ClaimId = claimId
            && marker.ReservationId =
                MigrationProtectedIssueCensusRelease.reservationId claimId journalHead

    let validAttemptPhase (record: ProtectedIssueCensusNativeAttemptRecord) =
        match record.Phase, record.TokenFingerprintSha256,
              record.RevocationReceiptSha256 with
        | InvocationUnknown, None, None -> true
        | TokenVaulted, Some tokenHash, None -> exactSha tokenHash
        | NativeRevoked, Some tokenHash, Some receiptHash ->
            exactSha tokenHash && exactSha receiptHash
        | _ -> false

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let expectedSnapshotSealSha256 (snapshot: ProtectedIssueCensusNativeAttemptSnapshot) =
        let phase = function
            | InvocationUnknown -> "invocation-unknown"
            | TokenVaulted -> "token-vaulted"
            | NativeRevoked -> "native-revoked"
        let optional = function
            | None -> [ "none" ]
            | Some value -> [ "some"; value ]
        let recordValues (record: ProtectedIssueCensusNativeAttemptRecord) =
            let request = record.Request
            let selection = request.Selection
            [ request.NativeAttemptId; request.ReservationId; request.ClaimId
              string selection.RunId; string selection.RunAttempt; selection.RunNonce
              selection.CandidateSha; selection.WorkflowSha; selection.ApiOrigin
              selection.Owner; selection.Repository; string selection.RepositoryId
              string request.AppId; string request.InstallationId; string request.RepositoryId
              request.PermissionSha256; request.VaultResourceId
              request.StoreResourceId; string request.StoreGeneration
              request.ExpectedStoreHeadSha256
              request.JournalResourceId; string request.ExpectedJournalGeneration
              request.ExpectedJournalHeadSha256
              request.ClockResourceId; request.ClockArtifactSha256
              request.SignedExpiresAtUtc.ToUniversalTime().ToString("O")
              record.ProviderAttemptId; record.VaultResourceId; phase record.Phase ]
            @ optional record.TokenFingerprintSha256
            @ optional record.RevocationReceiptSha256
        [ "fsgg.gs2-09.7.protected-native-attempt-snapshot/v2"
          snapshot.Head.AttemptResourceId; string snapshot.Head.Generation
          snapshot.AttemptId; string snapshot.Complete; string snapshot.Records.Length ]
        @ (snapshot.Records |> List.collect recordValues)
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private readStableSnapshot (native: IProtectedIssueCensusNativeAttemptPort)
                                   (pins: ProtectedIssueCensusNativeAttemptPins)
                                   (attemptId: string) =
        let before =
            try native.ReadHead()
            with _ -> None
        match before with
        | None -> Error "protected-census-attempt-unknown"
        | Some head when head.AttemptResourceId <> pins.AttemptResourceId
                         || head.Generation < 1L
                         || not (exactSha head.SealSha256) ->
            Error "protected-census-attempt-head"
        | Some head ->
            let snapshot =
                try native.ReadAttempts attemptId
                with _ -> None
            match snapshot with
            | None -> Error "protected-census-attempt-unknown"
            | Some observed when not observed.Complete ->
                Error "protected-census-attempt-incomplete"
            | Some observed when observed.AttemptId <> attemptId
                                 || observed.Head <> head ->
                Error "protected-census-attempt-head"
            | Some observed when observed.Head.SealSha256
                                 <> expectedSnapshotSealSha256 observed ->
                Error "protected-census-attempt-seal"
            | Some observed ->
                let after =
                    try native.ReadHead()
                    with _ -> None
                match after with
                | None -> Error "protected-census-attempt-unknown"
                | Some current when current = head -> Ok observed
                | Some _ -> Error "protected-census-attempt-head"

    let inspectWithSnapshot (handoffPins: ProtectedIssueCensusHandoffPins)
                (nativePins: ProtectedIssueCensusNativeAttemptPins)
                (expectedMarker: ProtectedIssueCensusHandoffRequest)
                (handoffPort: IProtectedIssueCensusHandoffPort option)
                (nativePort: IProtectedIssueCensusNativeAttemptPort option)
                : Result<ProtectedIssueCensusNativeAttemptSnapshot * ProtectedIssueCensusRecoveryHold, string> =
        if not (validUtf8Atom handoffPins.HandoffResourceId)
           || not (exactSha handoffPins.HandoffArtifactSha256)
           || not (validUtf8Atom handoffPins.VaultResourceId)
           || not (exactSha handoffPins.VaultArtifactSha256)
           || not (validUtf8Atom handoffPins.NativeAttemptNamespaceId)
           || handoffPins.AppId < 1L || handoffPins.InstallationId < 1L
           || handoffPins.RepositoryId < 1L
           || not (exactSha handoffPins.PermissionSha256)
           || not (validUtf8Atom nativePins.AttemptResourceId)
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
                    || markerDescription.ClockResourceId <> expectedMarker.ClockResourceId
                    || markerDescription.ClockArtifactSha256
                       <> expectedMarker.ClockArtifactSha256
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
                       || not (validMarkerChain expectedMarker)
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
                        | Some marker when not (validMarkerChain marker)
                                           || marker <> expectedMarker ->
                            Error "protected-census-attempt-binding"
                        | Some _ ->
                            match readStableSnapshot native nativePins
                                                     expectedMarker.NativeAttemptId with
                            | Error reason -> Error reason
                            | Ok snapshot ->
                                let markerAfter =
                                    try handoff.ReadMarker expectedMarker.NativeAttemptId
                                    with _ -> None
                                match markerAfter with
                                | None -> Error "protected-census-attempt-unknown"
                                | Some current when not (validMarkerChain current)
                                                   || current <> expectedMarker ->
                                    Error "protected-census-attempt-binding"
                                | Some _ ->
                                    match snapshot.Records with
                                    | [] -> Error "protected-census-attempt-unknown"
                                    | [attempt] when not (validMarkerChain attempt.Request)
                                                     || attempt.Request <> expectedMarker
                                                     || attempt.ProviderAttemptId
                                                        <> expectedMarker.NativeAttemptId
                                                     || attempt.VaultResourceId
                                                        <> handoffPins.VaultResourceId ->
                                        Error "protected-census-attempt-binding"
                                    | [attempt] ->
                                        if not (validAttemptPhase attempt) then
                                            Error "protected-census-attempt-phase"
                                        else
                                            match attempt.Phase with
                                            | InvocationUnknown -> Ok (snapshot, NativeResultUnknown)
                                            | TokenVaulted -> Ok (snapshot, NativeRevocationRequired)
                                            | NativeRevoked -> Ok (snapshot, ProtectedReceiptRequired)
                                    | _ -> Error "protected-census-attempt-duplicate"
            | _ -> Error "protected-census-attempt-unavailable"

    let inspect handoffPins nativePins expectedMarker handoffPort nativePort =
        inspectWithSnapshot handoffPins nativePins expectedMarker handoffPort nativePort
        |> Result.map snd
