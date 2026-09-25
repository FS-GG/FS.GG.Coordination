namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

type ProtectedIssueCensusReleasePins =
    { ReleaseResourceId: string
      ReleaseArtifactSha256: string }

type ProtectedIssueCensusReleaseDescription =
    { ReleaseResourceId: string
      ReleaseArtifactSha256: string
      StoreResourceId: string
      StoreArtifactSha256: string
      JournalResourceId: string
      JournalArtifactSha256: string
      ClockResourceId: string
      SignerPublicKeySha256: string
      SignerArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      AtomicCompareAndConsume: bool
      AtomicExpiryCompare: bool }

type ProtectedIssueCensusReleaseRequest =
    { ReservationId: string
      Selection: ProtectedIssueCensusSelection
      ClaimId: string
      AttestationPayloadSha256: string
      ClockResourceId: string
      SignerPublicKeySha256: string
      SignerArtifactSha256: string
      SignedIssuedAtUtc: DateTimeOffset
      SignedExpiresAtUtc: DateTimeOffset
      StoreResourceId: string
      StoreGeneration: int64
      StoreCorpusSha256: string
      ExpectedStoreHeadSha256: string
      JournalResourceId: string
      ExpectedJournalGeneration: int64
      ExpectedJournalHeadSha256: string }

type ProtectedIssueCensusReleaseOutcome =
    | ReleaseReserved
    | ReleaseDuplicate
    | ReleaseConflict
    | ReleaseUnknown

type IProtectedIssueCensusReleasePort =
    abstract Describe: unit -> ProtectedIssueCensusReleaseDescription
    abstract ReserveOnce: ProtectedIssueCensusReleaseRequest -> ProtectedIssueCensusReleaseOutcome
    abstract ReadReservation: string -> ProtectedIssueCensusReleaseRequest option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusRelease =
    let private sha (bytes: byte[]) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private exactSha (value: string) =
        not (isNull value) && value.Length = 64
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private reservationId (claimId: string) (journalHead: ProtectedIssueCensusClaimHead) =
        [ "fsgg.gs2-09.7.protected-census-release-reservation/v1"
          claimId; journalHead.JournalResourceId; string journalHead.Generation
          journalHead.SealSha256 ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> sha

    let reserve (attestationPins: ProtectedIssueCensusAttestationPins)
                (claimPins: ProtectedIssueCensusClaimPins)
                (storePins: ProtectedIssueCensusStoreHeadPins)
                (releasePins: ProtectedIssueCensusReleasePins)
                (selection: ProtectedIssueCensusSelection)
                (proof: ProtectedIssueCensusProof)
                (attestation: ProtectedIssueCensusSealAttestation option)
                (clock: IProtectedIssueCensusClockPort option)
                (storePort: IProtectedIssueCensusStoreHeadPort option)
                (claimPort: IProtectedIssueCensusClaimPort option)
                (releasePort: IProtectedIssueCensusReleasePort option) : Result<unit, string> =
        if String.IsNullOrWhiteSpace releasePins.ReleaseResourceId
           || not (exactSha releasePins.ReleaseArtifactSha256)
           || String.IsNullOrWhiteSpace storePins.StoreResourceId
           || not (exactSha storePins.StoreArtifactSha256)
           || String.IsNullOrWhiteSpace claimPins.JournalResourceId
           || not (exactSha claimPins.JournalArtifactSha256) then
            Error "protected-census-release-pins"
        else
            match attestation, storePort, claimPort, releasePort with
            | Some seal, Some store, Some journal, Some release ->
                let installed =
                    try Some (store.Describe(), journal.Describe(), release.Describe())
                    with _ -> None
                match installed with
                | None -> Error "protected-census-release-unavailable"
                | Some (storeDescription, journalDescription, releaseDescription) when
                    storeDescription.StoreResourceId <> storePins.StoreResourceId
                    || storeDescription.StoreArtifactSha256 <> storePins.StoreArtifactSha256
                    || storeDescription.CandidateMayRead || storeDescription.CandidateMayWrite
                    || not storeDescription.ImmutableHead
                    || journalDescription.JournalResourceId <> claimPins.JournalResourceId
                    || journalDescription.JournalArtifactSha256 <> claimPins.JournalArtifactSha256
                    || journalDescription.StoreHeadResourceId <> storePins.StoreResourceId
                    || journalDescription.StoreHeadArtifactSha256 <> storePins.StoreArtifactSha256
                    || journalDescription.CandidateMayRead || journalDescription.CandidateMayWrite
                    || not journalDescription.AtomicStoreHeadCompare
                    || not journalDescription.ImmutableJournal
                    || releaseDescription.ReleaseResourceId <> releasePins.ReleaseResourceId
                    || releaseDescription.ReleaseArtifactSha256 <> releasePins.ReleaseArtifactSha256
                    || releaseDescription.StoreResourceId <> storePins.StoreResourceId
                    || releaseDescription.StoreArtifactSha256 <> storePins.StoreArtifactSha256
                    || releaseDescription.JournalResourceId <> claimPins.JournalResourceId
                    || releaseDescription.JournalArtifactSha256 <> claimPins.JournalArtifactSha256
                    || releaseDescription.ClockResourceId <> attestationPins.ClockResourceId
                    || releaseDescription.SignerPublicKeySha256
                       <> attestationPins.SignerPublicKeySha256
                    || releaseDescription.SignerArtifactSha256
                       <> attestationPins.SignerArtifactSha256
                    || releaseDescription.CandidateMayRead || releaseDescription.CandidateMayWrite
                    || not releaseDescription.AtomicCompareAndConsume
                    || not releaseDescription.AtomicExpiryCompare ->
                    Error "protected-census-release-installation"
                | Some _ ->
                    let storeHead, claimRecord, journalHead =
                        try
                            let claimId = MigrationProtectedIssueCensusClaim.claimId
                                              selection storePins.StoreResourceId seal.StoreGeneration
                            store.ReadHead selection, journal.ReadClaim claimId, journal.ReadHead()
                        with _ -> None, None, None
                    match storeHead, claimRecord, journalHead with
                    | Some head, Some record, Some current ->
                        if head.Selection <> selection
                           || head.StoreResourceId <> storePins.StoreResourceId
                           || head.Generation <> seal.StoreGeneration
                           || head.CorpusSha256 <> proof.CorpusSha256
                           || not (exactSha head.HeadSha256)
                           || current.JournalResourceId <> claimPins.JournalResourceId
                           || current.Generation <> record.CommitGeneration
                           || current.SealSha256 <> record.CommitHeadSha256 then
                            Error "protected-census-release-head"
                        else
                            match MigrationProtectedIssueCensusAttestation.verify
                                      attestationPins selection storePins.StoreResourceId proof
                                      head.Generation attestation clock with
                            | Error reason -> Error reason
                            | Ok () ->
                                let claimed = record.Request
                                let predecessor =
                                    { JournalResourceId=claimPins.JournalResourceId
                                      Generation=claimed.ExpectedHeadGeneration
                                      SealSha256=claimed.ExpectedHeadSha256 }
                                let payloadSha =
                                    MigrationProtectedIssueCensusAttestation.signingPayload
                                        attestationPins seal |> sha
                                let expectedId = MigrationProtectedIssueCensusClaim.claimId
                                                     selection storePins.StoreResourceId head.Generation
                                if claimed.ClaimId <> expectedId
                                   || claimed.AttestationPayloadSha256 <> payloadSha
                                   || claimed.Selection <> selection
                                   || claimed.CustodyStoreResourceId <> storePins.StoreResourceId
                                   || claimed.StoreGeneration <> head.Generation
                                   || claimed.ExpectedStoreCorpusSha256 <> proof.CorpusSha256
                                   || claimed.ExpectedStoreHeadSha256 <> head.HeadSha256
                                   || claimed.JournalResourceId <> claimPins.JournalResourceId
                                   || predecessor.Generation < 0L
                                   || predecessor.Generation = Int64.MaxValue
                                   || not (exactSha predecessor.SealSha256)
                                   || record.CommitGeneration <> predecessor.Generation + 1L
                                   || record.CommitHeadSha256 <>
                                      MigrationProtectedIssueCensusClaim.expectedCommitHeadSha256
                                          predecessor claimed then
                                    Error "protected-census-release-claim"
                                else
                                    let request =
                                        { ReservationId=reservationId expectedId current
                                          Selection=selection; ClaimId=expectedId
                                          AttestationPayloadSha256=payloadSha
                                          ClockResourceId=attestationPins.ClockResourceId
                                          SignerPublicKeySha256=attestationPins.SignerPublicKeySha256
                                          SignerArtifactSha256=attestationPins.SignerArtifactSha256
                                          SignedIssuedAtUtc=seal.IssuedAtUtc
                                          SignedExpiresAtUtc=seal.ExpiresAtUtc
                                          StoreResourceId=storePins.StoreResourceId
                                          StoreGeneration=head.Generation
                                          StoreCorpusSha256=proof.CorpusSha256
                                          ExpectedStoreHeadSha256=head.HeadSha256
                                          JournalResourceId=claimPins.JournalResourceId
                                          ExpectedJournalGeneration=current.Generation
                                          ExpectedJournalHeadSha256=current.SealSha256 }
                                    let outcome =
                                        try release.ReserveOnce request
                                        with _ -> ReleaseUnknown
                                    match outcome with
                                    | ReleaseDuplicate -> Error "protected-census-release-duplicate"
                                    | ReleaseConflict -> Error "protected-census-release-conflict"
                                    | ReleaseUnknown -> Error "protected-census-release-unknown"
                                    | ReleaseReserved ->
                                        let readback =
                                            try release.ReadReservation request.ReservationId
                                            with _ -> None
                                        match readback with
                                        | Some exact when exact = request -> Ok ()
                                        | Some _ -> Error "protected-census-release-binding"
                                        | None -> Error "protected-census-release-unknown"
                    | _ -> Error "protected-census-release-unknown"
            | _ -> Error "protected-census-release-unavailable"
