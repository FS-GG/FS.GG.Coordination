namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

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
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private exactSha (value: string) =
        not (isNull value) && value.Length = 64
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let private canonicalBase64 (value: string) =
        try
            let bytes = Convert.FromBase64String value
            if Convert.ToBase64String bytes = value then Some bytes else None
        with _ -> None

    let private publicKey (pins: ProtectedIssueCensusNativeAttestationPins) =
        if not (exactSha pins.SignerPublicKeySha256)
           || not (exactSha pins.SignerArtifactSha256)
           || not (exactSha pins.ClockArtifactSha256)
           || not (exactSha pins.NativeAttemptArtifactSha256)
           || String.IsNullOrWhiteSpace pins.ClockResourceId
           || String.IsNullOrWhiteSpace pins.NativeAttemptResourceId
           || pins.MaximumAgeSeconds < 1 || pins.MaximumAgeSeconds > 300 then None
        else
            match canonicalBase64 pins.SignerPublicKeySpkiBase64 with
            | None -> None
            | Some bytes ->
                let digest = bytes |> SHA256.HashData |> Convert.ToHexString
                                   |> _.ToLowerInvariant()
                if digest = pins.SignerPublicKeySha256 then Some bytes else None

    let signingPayload (pins: ProtectedIssueCensusNativeAttestationPins)
                       (attestation: ProtectedIssueCensusNativeSnapshotAttestation) =
        [ "fsgg.gs2-09.7.protected-native-snapshot-attestation/v1"
          pins.SignerPublicKeySha256; pins.SignerArtifactSha256
          pins.ClockResourceId; pins.ClockArtifactSha256
          pins.NativeAttemptResourceId; pins.NativeAttemptArtifactSha256
          string pins.MaximumAgeSeconds
          attestation.AttemptId; attestation.NativeAttemptResourceId
          string attestation.HeadGeneration; attestation.SnapshotSealSha256
          attestation.IssuedAtUtc.ToUniversalTime().ToString("O")
          attestation.ExpiresAtUtc.ToUniversalTime().ToString("O") ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes

    let private validHandoffIdentity (pins: ProtectedIssueCensusHandoffPins)
                                     (marker: ProtectedIssueCensusHandoffRequest) =
        not (String.IsNullOrWhiteSpace pins.HandoffResourceId)
        && exactSha pins.HandoffArtifactSha256
        && not (String.IsNullOrWhiteSpace pins.VaultResourceId)
        && exactSha pins.VaultArtifactSha256
        && not (String.IsNullOrWhiteSpace pins.NativeAttemptNamespaceId)
        && pins.AppId > 0L && pins.InstallationId > 0L && pins.RepositoryId > 0L
        && exactSha pins.PermissionSha256
        && MigrationProtectedIssueCensusAttemptRecovery.validMarkerChain marker
        && marker.NativeAttemptId =
            MigrationProtectedIssueCensusHandoff.attemptId marker.ReservationId pins
        && marker.AppId = pins.AppId
        && marker.InstallationId = pins.InstallationId
        && marker.RepositoryId = pins.RepositoryId
        && marker.Selection.RepositoryId = pins.RepositoryId
        && marker.PermissionSha256 = pins.PermissionSha256
        && marker.VaultResourceId = pins.VaultResourceId

    let verify (pins: ProtectedIssueCensusNativeAttestationPins)
               (handoffPins: ProtectedIssueCensusHandoffPins)
               (expectedMarker: ProtectedIssueCensusHandoffRequest)
               (snapshot: ProtectedIssueCensusNativeAttemptSnapshot)
               (attestation: ProtectedIssueCensusNativeSnapshotAttestation option)
               (clock: IProtectedIssueCensusClockPort option) : Result<unit, string> =
        match publicKey pins with
        | None -> Error "protected-native-attestation-pins"
        | Some publicBytes ->
            if not (validHandoffIdentity handoffPins expectedMarker)
               || expectedMarker.ClockResourceId <> pins.ClockResourceId
               || expectedMarker.ClockArtifactSha256 <> pins.ClockArtifactSha256 then
                Error "protected-native-attestation-binding"
            elif snapshot.AttemptId <> expectedMarker.NativeAttemptId
               || snapshot.Head.AttemptResourceId <> pins.NativeAttemptResourceId
               || snapshot.Head.Generation < 1L
               || not snapshot.Complete
               || not (exactSha snapshot.Head.SealSha256)
               || snapshot.Head.SealSha256
                  <> MigrationProtectedIssueCensusAttemptRecovery.expectedSnapshotSealSha256
                         snapshot
               || not (exactSha expectedMarker.NativeAttemptId)
               || (match snapshot.Records with
                   | [record] -> not (MigrationProtectedIssueCensusAttemptRecovery.validAttemptPhase
                                         record)
                                 || not (MigrationProtectedIssueCensusAttemptRecovery.validMarkerChain
                                             record.Request)
                                 || record.Request <> expectedMarker
                                 || record.ProviderAttemptId <> expectedMarker.NativeAttemptId
                                 || record.VaultResourceId <> expectedMarker.VaultResourceId
                   | _ -> true) then
                Error "protected-native-attestation-snapshot"
            else
                match attestation, clock with
                | None, _ | _, None -> Error "protected-native-attestation-unavailable"
                | Some signed, Some trustedClock ->
                    if signed.AttemptId <> snapshot.AttemptId
                       || signed.NativeAttemptResourceId <> snapshot.Head.AttemptResourceId
                       || signed.HeadGeneration <> snapshot.Head.Generation
                       || signed.SnapshotSealSha256 <> snapshot.Head.SealSha256 then
                        Error "protected-native-attestation-binding"
                    else
                        let observed =
                            try
                                let installed = trustedClock.Describe()
                                if installed.ClockResourceId <> pins.ClockResourceId
                                   || installed.ClockArtifactSha256 <> pins.ClockArtifactSha256
                                   || installed.CandidateMayRead || installed.CandidateMayWrite
                                   || not installed.MonotonicUtc then
                                    Error "protected-native-clock-installation"
                                else
                                    match trustedClock.ReadNow() with
                                    | Some now -> Ok (now.ToUniversalTime())
                                    | None -> Error "protected-native-attestation-unavailable"
                            with _ -> Error "protected-native-attestation-unavailable"
                        match observed with
                        | Error reason -> Error reason
                        | Ok now ->
                            let lifetime = signed.ExpiresAtUtc - signed.IssuedAtUtc
                            if signed.IssuedAtUtc.Offset <> TimeSpan.Zero
                               || signed.ExpiresAtUtc.Offset <> TimeSpan.Zero
                               || lifetime <= TimeSpan.Zero
                               || lifetime > TimeSpan.FromSeconds(float pins.MaximumAgeSeconds)
                               || now < signed.IssuedAtUtc || now > signed.ExpiresAtUtc then
                                Error "protected-native-attestation-stale"
                            else
                                match canonicalBase64 signed.SignatureBase64 with
                                | None -> Error "protected-native-attestation-signature"
                                | Some signature when signature.Length <> 64 ->
                                    Error "protected-native-attestation-signature"
                                | Some signature ->
                                    try
                                        use key = ECDsa.Create()
                                        let mutable bytesRead = 0
                                        key.ImportSubjectPublicKeyInfo(publicBytes, &bytesRead)
                                        if bytesRead <> publicBytes.Length || key.KeySize <> 256
                                           || key.ExportParameters(false).Curve.Oid.Value
                                              <> "1.2.840.10045.3.1.7" then
                                            Error "protected-native-attestation-pins"
                                        elif key.VerifyData(signingPayload pins signed, signature,
                                                            HashAlgorithmName.SHA256,
                                                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation) then
                                            Ok ()
                                        else Error "protected-native-attestation-signature"
                                    with _ -> Error "protected-native-attestation-pins"
