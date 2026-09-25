namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

type ProtectedIssueCensusHandoffPins =
    { HandoffResourceId: string
      HandoffArtifactSha256: string
      VaultResourceId: string
      VaultArtifactSha256: string
      NativeAttemptNamespaceId: string
      AppId: int64
      InstallationId: int64
      RepositoryId: int64
      PermissionSha256: string }

type ProtectedIssueCensusHandoffDescription =
    { HandoffResourceId: string
      HandoffArtifactSha256: string
      VaultResourceId: string
      VaultArtifactSha256: string
      NativeAttemptNamespaceId: string
      ReleaseResourceId: string
      ReleaseArtifactSha256: string
      ClockResourceId: string
      ClockArtifactSha256: string
      AppId: int64
      InstallationId: int64
      RepositoryId: int64
      PermissionSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      AtomicReservationConsumeAndMark: bool
      AtomicExpiryCompare: bool }

type ProtectedIssueCensusHandoffRequest =
    { NativeAttemptId: string
      ReservationId: string
      ClaimId: string
      Selection: ProtectedIssueCensusSelection
      AppId: int64
      InstallationId: int64
      RepositoryId: int64
      PermissionSha256: string
      VaultResourceId: string
      ExpectedStoreHeadSha256: string
      ExpectedJournalHeadSha256: string
      ClockResourceId: string
      ClockArtifactSha256: string
      SignedExpiresAtUtc: DateTimeOffset }

type ProtectedIssueCensusHandoffOutcome =
    | HandoffMarked
    | HandoffDuplicate
    | HandoffConflict
    | HandoffUnknown

type IProtectedIssueCensusHandoffPort =
    abstract Describe: unit -> ProtectedIssueCensusHandoffDescription
    abstract MarkOnce: ProtectedIssueCensusHandoffRequest -> ProtectedIssueCensusHandoffOutcome
    abstract ReadMarker: string -> ProtectedIssueCensusHandoffRequest option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusHandoff =
    let private sha (bytes: byte[]) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private exactSha (value: string) =
        not (isNull value) && value.Length = 64
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private attemptId (reservationId: string) (pins: ProtectedIssueCensusHandoffPins) =
        [ "fsgg.gs2-09.7.protected-census-native-attempt/v1"
          pins.NativeAttemptNamespaceId; reservationId
          string pins.AppId; string pins.InstallationId; string pins.RepositoryId
          pins.PermissionSha256; pins.VaultResourceId ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> sha

    let mark (attestationPins: ProtectedIssueCensusAttestationPins)
             (storePins: ProtectedIssueCensusStoreHeadPins)
             (claimPins: ProtectedIssueCensusClaimPins)
             (releasePins: ProtectedIssueCensusReleasePins)
             (handoffPins: ProtectedIssueCensusHandoffPins)
             (selection: ProtectedIssueCensusSelection)
             (proof: ProtectedIssueCensusProof)
             (seal: ProtectedIssueCensusSealAttestation option)
             (clock: IProtectedIssueCensusClockPort option)
             (expectedReservation: ProtectedIssueCensusReleaseRequest)
             (releasePort: IProtectedIssueCensusReleasePort option)
             (handoffPort: IProtectedIssueCensusHandoffPort option) : Result<unit, string> =
        if String.IsNullOrWhiteSpace releasePins.ReleaseResourceId
           || not (exactSha releasePins.ReleaseArtifactSha256)
           || String.IsNullOrWhiteSpace storePins.StoreResourceId
           || not (exactSha storePins.StoreArtifactSha256)
           || String.IsNullOrWhiteSpace claimPins.JournalResourceId
           || not (exactSha claimPins.JournalArtifactSha256)
           || String.IsNullOrWhiteSpace handoffPins.HandoffResourceId
           || not (exactSha handoffPins.HandoffArtifactSha256)
           || String.IsNullOrWhiteSpace handoffPins.VaultResourceId
           || not (exactSha handoffPins.VaultArtifactSha256)
           || String.IsNullOrWhiteSpace handoffPins.NativeAttemptNamespaceId
           || handoffPins.AppId < 1L || handoffPins.InstallationId < 1L
           || handoffPins.RepositoryId <> selection.RepositoryId
           || not (exactSha handoffPins.PermissionSha256) then
            Error "protected-census-handoff-pins"
        else
            match seal, releasePort, handoffPort with
            | Some signed, Some release, Some handoff ->
                let installed =
                    try Some (release.Describe(), handoff.Describe())
                    with _ -> None
                match installed with
                | None -> Error "protected-census-handoff-unavailable"
                | Some (releaseDescription, handoffDescription) when
                    releaseDescription.ReleaseResourceId <> releasePins.ReleaseResourceId
                    || releaseDescription.ReleaseArtifactSha256 <> releasePins.ReleaseArtifactSha256
                    || releaseDescription.StoreResourceId <> storePins.StoreResourceId
                    || releaseDescription.StoreArtifactSha256 <> storePins.StoreArtifactSha256
                    || releaseDescription.JournalResourceId <> claimPins.JournalResourceId
                    || releaseDescription.JournalArtifactSha256 <> claimPins.JournalArtifactSha256
                    || releaseDescription.ClockResourceId <> attestationPins.ClockResourceId
                    || releaseDescription.ClockArtifactSha256 <> attestationPins.ClockArtifactSha256
                    || releaseDescription.SignerPublicKeySha256
                       <> attestationPins.SignerPublicKeySha256
                    || releaseDescription.SignerArtifactSha256
                       <> attestationPins.SignerArtifactSha256
                    || releaseDescription.CandidateMayRead || releaseDescription.CandidateMayWrite
                    || not releaseDescription.AtomicCompareAndConsume
                    || not releaseDescription.AtomicExpiryCompare
                    || handoffDescription.HandoffResourceId <> handoffPins.HandoffResourceId
                    || handoffDescription.HandoffArtifactSha256 <> handoffPins.HandoffArtifactSha256
                    || handoffDescription.VaultResourceId <> handoffPins.VaultResourceId
                    || handoffDescription.VaultArtifactSha256 <> handoffPins.VaultArtifactSha256
                    || handoffDescription.NativeAttemptNamespaceId
                       <> handoffPins.NativeAttemptNamespaceId
                    || handoffDescription.ReleaseResourceId <> releasePins.ReleaseResourceId
                    || handoffDescription.ReleaseArtifactSha256
                       <> releasePins.ReleaseArtifactSha256
                    || handoffDescription.ClockResourceId <> attestationPins.ClockResourceId
                    || handoffDescription.ClockArtifactSha256
                       <> attestationPins.ClockArtifactSha256
                    || handoffDescription.AppId <> handoffPins.AppId
                    || handoffDescription.InstallationId <> handoffPins.InstallationId
                    || handoffDescription.RepositoryId <> handoffPins.RepositoryId
                    || handoffDescription.PermissionSha256 <> handoffPins.PermissionSha256
                    || handoffDescription.CandidateMayRead || handoffDescription.CandidateMayWrite
                    || not handoffDescription.AtomicReservationConsumeAndMark
                    || not handoffDescription.AtomicExpiryCompare ->
                    Error "protected-census-handoff-installation"
                | Some _ ->
                    match MigrationProtectedIssueCensusAttestation.verify
                              attestationPins selection expectedReservation.StoreResourceId proof
                              expectedReservation.StoreGeneration seal clock with
                    | Error reason -> Error reason
                    | Ok () ->
                        let claimedId = MigrationProtectedIssueCensusClaim.claimId
                                            selection expectedReservation.StoreResourceId
                                            expectedReservation.StoreGeneration
                        let journalHead =
                            { JournalResourceId=expectedReservation.JournalResourceId
                              Generation=expectedReservation.ExpectedJournalGeneration
                              SealSha256=expectedReservation.ExpectedJournalHeadSha256 }
                        let reservationId =
                            MigrationProtectedIssueCensusRelease.reservationId claimedId journalHead
                        let payloadSha =
                            MigrationProtectedIssueCensusAttestation.signingPayload
                                attestationPins signed |> sha
                        if expectedReservation.Selection <> selection
                           || expectedReservation.StoreResourceId <> storePins.StoreResourceId
                           || expectedReservation.JournalResourceId <> claimPins.JournalResourceId
                           || expectedReservation.ClaimId <> claimedId
                           || expectedReservation.ReservationId <> reservationId
                           || expectedReservation.AttestationPayloadSha256 <> payloadSha
                           || expectedReservation.StoreCorpusSha256 <> proof.CorpusSha256
                           || not (exactSha expectedReservation.ExpectedStoreHeadSha256)
                           || not (exactSha expectedReservation.ExpectedJournalHeadSha256)
                           || expectedReservation.ClockResourceId <> attestationPins.ClockResourceId
                           || expectedReservation.ClockArtifactSha256
                              <> attestationPins.ClockArtifactSha256
                           || expectedReservation.SignerPublicKeySha256
                              <> attestationPins.SignerPublicKeySha256
                           || expectedReservation.SignerArtifactSha256
                              <> attestationPins.SignerArtifactSha256
                           || expectedReservation.SignedIssuedAtUtc <> signed.IssuedAtUtc
                           || expectedReservation.SignedExpiresAtUtc <> signed.ExpiresAtUtc then
                            Error "protected-census-handoff-binding"
                        else
                            let observed =
                                try release.ReadReservation reservationId
                                with _ -> None
                            match observed with
                            | None -> Error "protected-census-handoff-unknown"
                            | Some readback when readback <> expectedReservation ->
                                Error "protected-census-handoff-binding"
                            | Some _ ->
                                let request =
                                    { NativeAttemptId=attemptId reservationId handoffPins
                                      ReservationId=reservationId; ClaimId=claimedId
                                      Selection=selection; AppId=handoffPins.AppId
                                      InstallationId=handoffPins.InstallationId
                                      RepositoryId=handoffPins.RepositoryId
                                      PermissionSha256=handoffPins.PermissionSha256
                                      VaultResourceId=handoffPins.VaultResourceId
                                      ExpectedStoreHeadSha256=
                                        expectedReservation.ExpectedStoreHeadSha256
                                      ExpectedJournalHeadSha256=
                                        expectedReservation.ExpectedJournalHeadSha256
                                      ClockResourceId=attestationPins.ClockResourceId
                                      ClockArtifactSha256=attestationPins.ClockArtifactSha256
                                      SignedExpiresAtUtc=signed.ExpiresAtUtc }
                                let outcome =
                                    try handoff.MarkOnce request
                                    with _ -> HandoffUnknown
                                match outcome with
                                | HandoffDuplicate -> Error "protected-census-handoff-duplicate"
                                | HandoffConflict -> Error "protected-census-handoff-conflict"
                                | HandoffUnknown -> Error "protected-census-handoff-unknown"
                                | HandoffMarked ->
                                    let marker =
                                        try handoff.ReadMarker request.NativeAttemptId
                                        with _ -> None
                                    match marker with
                                    | Some exact when exact = request -> Ok ()
                                    | Some _ -> Error "protected-census-handoff-binding"
                                    | None -> Error "protected-census-handoff-unknown"
            | _ -> Error "protected-census-handoff-unavailable"
