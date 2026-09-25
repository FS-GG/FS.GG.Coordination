namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

type ProtectedIssueCensusClaimPins =
    { JournalResourceId: string
      JournalArtifactSha256: string }

type ProtectedIssueCensusStoreHeadPins =
    { StoreResourceId: string
      StoreArtifactSha256: string }

type ProtectedIssueCensusStoreHeadDescription =
    { StoreResourceId: string
      StoreArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      ImmutableHead: bool }

type ProtectedIssueCensusStoreHead =
    { Selection: ProtectedIssueCensusSelection
      StoreResourceId: string
      Generation: int64
      CorpusSha256: string
      HeadSha256: string }

type IProtectedIssueCensusStoreHeadPort =
    abstract Describe: unit -> ProtectedIssueCensusStoreHeadDescription
    abstract ReadHead: ProtectedIssueCensusSelection -> ProtectedIssueCensusStoreHead option

type ProtectedIssueCensusClaimDescription =
    { JournalResourceId: string
      JournalArtifactSha256: string
      StoreHeadResourceId: string
      StoreHeadArtifactSha256: string
      AtomicStoreHeadCompare: bool
      CandidateMayRead: bool
      CandidateMayWrite: bool
      ImmutableJournal: bool }

type ProtectedIssueCensusClaimHead =
    { JournalResourceId: string
      Generation: int64
      SealSha256: string }

type ProtectedIssueCensusClaimRequest =
    { ClaimId: string
      AttestationPayloadSha256: string
      Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      StoreGeneration: int64
      ExpectedStoreCorpusSha256: string
      ExpectedStoreHeadSha256: string
      JournalResourceId: string
      ExpectedHeadGeneration: int64
      ExpectedHeadSha256: string }

type ProtectedIssueCensusClaimRecord =
    { Request: ProtectedIssueCensusClaimRequest
      CommitGeneration: int64
      CommitHeadSha256: string }

type ProtectedIssueCensusClaimOutcome =
    | ClaimCommitted
    | ClaimDuplicate
    | ClaimConflict
    | ClaimUnknown

type IProtectedIssueCensusClaimPort =
    abstract Describe: unit -> ProtectedIssueCensusClaimDescription
    abstract ReadHead: unit -> ProtectedIssueCensusClaimHead option
    abstract ClaimOnce: ProtectedIssueCensusClaimRequest -> ProtectedIssueCensusClaimOutcome
    abstract ReadClaim: string -> ProtectedIssueCensusClaimRecord option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusClaim =
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private sha (bytes: byte[]) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private exactSha (value: string) =
        not (isNull value) && value.Length = 64
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let expectedCommitHeadSha256 (previous: ProtectedIssueCensusClaimHead)
                                     (request: ProtectedIssueCensusClaimRequest) =
        [ "fsgg.gs2-09.7.protected-census-claim-head/v2"
          previous.JournalResourceId; string previous.Generation; previous.SealSha256
          request.ClaimId; request.AttestationPayloadSha256
          request.ExpectedStoreCorpusSha256; request.ExpectedStoreHeadSha256
          request.JournalResourceId; string request.ExpectedHeadGeneration
          request.ExpectedHeadSha256 ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> sha

    let private preflight (pins: ProtectedIssueCensusClaimPins)
                          (storePins: ProtectedIssueCensusStoreHeadPins)
                          (port: IProtectedIssueCensusClaimPort option) =
        if String.IsNullOrWhiteSpace pins.JournalResourceId
           || not (exactSha pins.JournalArtifactSha256) then
            Error "protected-census-claim-pins"
        else
            match port with
            | None -> Error "protected-census-claim-unavailable"
            | Some journal ->
                try
                    let description = journal.Describe()
                    if description.JournalResourceId <> pins.JournalResourceId
                       || description.JournalArtifactSha256 <> pins.JournalArtifactSha256
                       || description.StoreHeadResourceId <> storePins.StoreResourceId
                       || description.StoreHeadArtifactSha256 <> storePins.StoreArtifactSha256
                       || not description.AtomicStoreHeadCompare
                       || description.CandidateMayRead || description.CandidateMayWrite
                       || not description.ImmutableJournal then
                        Error "protected-census-claim-installation"
                    else Ok journal
                with _ -> Error "protected-census-claim-unavailable"

    let claimId (selection: ProtectedIssueCensusSelection) (storeResourceId: string)
                (storeGeneration: int64) =
        [ "fsgg.gs2-09.7.protected-census-attestation-claim/v1"
          string selection.RunId; string selection.RunAttempt; selection.RunNonce
          selection.CandidateSha; selection.WorkflowSha; selection.ApiOrigin
          selection.Owner; selection.Repository; string selection.RepositoryId
          storeResourceId; string storeGeneration ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> sha

    let private verifyAndClaimJournal (attestationPins: ProtectedIssueCensusAttestationPins)
                                      (claimPins: ProtectedIssueCensusClaimPins)
                                      (storeHeadPins: ProtectedIssueCensusStoreHeadPins)
                                      (beforeStoreHead: ProtectedIssueCensusStoreHead)
                                      (selection: ProtectedIssueCensusSelection)
                                      (storeResourceId: string)
                                      (proof: ProtectedIssueCensusProof)
                                      (storeGeneration: int64)
                                      (attestation: ProtectedIssueCensusSealAttestation option)
                                      (clock: IProtectedIssueCensusClockPort option)
                                      (claimPort: IProtectedIssueCensusClaimPort option) : Result<unit, string> =
        match preflight claimPins storeHeadPins claimPort with
        | Error reason -> Error reason
        | Ok journal ->
            match MigrationProtectedIssueCensusAttestation.verify
                      attestationPins selection storeResourceId proof storeGeneration attestation clock with
            | Error reason -> Error reason
            | Ok () ->
                match attestation with
                | None -> Error "protected-census-attestation-unavailable"
                | Some claim ->
                    let before =
                        try journal.ReadHead()
                        with _ -> None
                    match before with
                    | None -> Error "protected-census-claim-unknown"
                    | Some head when head.JournalResourceId <> claimPins.JournalResourceId
                                     || head.Generation < 0L
                                     || head.Generation = Int64.MaxValue
                                     || not (exactSha head.SealSha256) ->
                        Error "protected-census-claim-head"
                    | Some head ->
                        let request =
                            { ClaimId=claimId selection storeResourceId storeGeneration
                              AttestationPayloadSha256=
                                MigrationProtectedIssueCensusAttestation.signingPayload
                                    attestationPins claim |> sha
                              Selection=selection; CustodyStoreResourceId=storeResourceId
                              StoreGeneration=storeGeneration
                              ExpectedStoreCorpusSha256=beforeStoreHead.CorpusSha256
                              ExpectedStoreHeadSha256=beforeStoreHead.HeadSha256
                              JournalResourceId=claimPins.JournalResourceId
                              ExpectedHeadGeneration=head.Generation
                              ExpectedHeadSha256=head.SealSha256 }
                        let outcome =
                            try journal.ClaimOnce request
                            with _ -> ClaimUnknown
                        match outcome with
                        | ClaimDuplicate -> Error "protected-census-claim-duplicate"
                        | ClaimConflict -> Error "protected-census-claim-conflict"
                        | ClaimUnknown -> Error "protected-census-claim-unknown"
                        | ClaimCommitted ->
                            let readback =
                                try journal.ReadClaim request.ClaimId
                                with _ -> None
                            match readback with
                            | None -> Error "protected-census-claim-unknown"
                            | Some committed when committed.Request <> request ->
                                Error "protected-census-claim-binding"
                            | Some committed when committed.CommitGeneration <> head.Generation + 1L
                                                  || committed.CommitHeadSha256
                                                     <> expectedCommitHeadSha256 head request ->
                                Error "protected-census-claim-head"
                            | Some committed ->
                                let after =
                                    try journal.ReadHead()
                                    with _ -> None
                                match after with
                                | None -> Error "protected-census-claim-unknown"
                                | Some current when current.JournalResourceId = claimPins.JournalResourceId
                                                    && current.Generation = committed.CommitGeneration
                                                    && current.SealSha256 = committed.CommitHeadSha256 -> Ok ()
                                | Some _ -> Error "protected-census-claim-head"

    let private readStoreHead (port: IProtectedIssueCensusStoreHeadPort)
                              (selection: ProtectedIssueCensusSelection)
                              (storeResourceId: string)
                              (proof: ProtectedIssueCensusProof)
                              (storeGeneration: int64) =
        let observed =
            try port.ReadHead selection
            with _ -> None
        match observed with
        | None -> Error "protected-census-store-unknown"
        | Some head when head.Selection <> selection
                         || head.StoreResourceId <> storeResourceId
                         || head.Generation <> storeGeneration
                         || head.CorpusSha256 <> proof.CorpusSha256
                         || not (exactSha head.HeadSha256) ->
            Error "protected-census-store-head"
        | Some head -> Ok head

    let verifyAndClaim (attestationPins: ProtectedIssueCensusAttestationPins)
                       (claimPins: ProtectedIssueCensusClaimPins)
                       (storeHeadPins: ProtectedIssueCensusStoreHeadPins)
                       (selection: ProtectedIssueCensusSelection)
                       (storeResourceId: string)
                       (proof: ProtectedIssueCensusProof)
                       (storeGeneration: int64)
                       (attestation: ProtectedIssueCensusSealAttestation option)
                       (clock: IProtectedIssueCensusClockPort option)
                       (storeHeadPort: IProtectedIssueCensusStoreHeadPort option)
                       (claimPort: IProtectedIssueCensusClaimPort option) : Result<unit, string> =
        if String.IsNullOrWhiteSpace storeHeadPins.StoreResourceId
           || storeHeadPins.StoreResourceId <> storeResourceId
           || not (exactSha storeHeadPins.StoreArtifactSha256) then
            Error "protected-census-store-pins"
        else
            match storeHeadPort with
            | None -> Error "protected-census-store-unavailable"
            | Some store ->
                let description =
                    try Some (store.Describe())
                    with _ -> None
                match description with
                | None -> Error "protected-census-store-unavailable"
                | Some installed when installed.StoreResourceId <> storeHeadPins.StoreResourceId
                                      || installed.StoreArtifactSha256 <> storeHeadPins.StoreArtifactSha256
                                      || installed.CandidateMayRead || installed.CandidateMayWrite
                                      || not installed.ImmutableHead ->
                    Error "protected-census-store-installation"
                | Some _ ->
                    match readStoreHead store selection storeResourceId proof storeGeneration with
                    | Error reason -> Error reason
                    | Ok before ->
                        match verifyAndClaimJournal attestationPins claimPins storeHeadPins before
                                                    selection storeResourceId
                                                    proof storeGeneration attestation clock claimPort with
                        | Error reason -> Error reason
                        | Ok () ->
                            match readStoreHead store selection storeResourceId proof storeGeneration with
                            | Error reason -> Error reason
                            | Ok after when after = before -> Ok ()
                            | Ok _ -> Error "protected-census-store-head"
