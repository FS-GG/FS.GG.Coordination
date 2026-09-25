namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

type ProtectedIssueCensusClaimPins =
    { JournalResourceId: string
      JournalArtifactSha256: string }

type ProtectedIssueCensusClaimDescription =
    { JournalResourceId: string
      JournalArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      ImmutableJournal: bool }

type ProtectedIssueCensusClaimRequest =
    { ClaimId: string
      AttestationPayloadSha256: string
      Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      StoreGeneration: int64
      JournalResourceId: string }

type ProtectedIssueCensusClaimRecord =
    { Request: ProtectedIssueCensusClaimRequest
      CommitGeneration: int64 }

type ProtectedIssueCensusClaimOutcome =
    | ClaimCommitted
    | ClaimDuplicate
    | ClaimUnknown

type IProtectedIssueCensusClaimPort =
    abstract Describe: unit -> ProtectedIssueCensusClaimDescription
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

    let private preflight (pins: ProtectedIssueCensusClaimPins)
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

    let verifyAndClaim (attestationPins: ProtectedIssueCensusAttestationPins)
                       (claimPins: ProtectedIssueCensusClaimPins)
                       (selection: ProtectedIssueCensusSelection)
                       (storeResourceId: string)
                       (proof: ProtectedIssueCensusProof)
                       (storeGeneration: int64)
                       (attestation: ProtectedIssueCensusSealAttestation option)
                       (clock: IProtectedIssueCensusClockPort option)
                       (claimPort: IProtectedIssueCensusClaimPort option) : Result<unit, string> =
        match preflight claimPins claimPort with
        | Error reason -> Error reason
        | Ok journal ->
            match MigrationProtectedIssueCensusAttestation.verify
                      attestationPins selection storeResourceId proof storeGeneration attestation clock with
            | Error reason -> Error reason
            | Ok () ->
                match attestation with
                | None -> Error "protected-census-attestation-unavailable"
                | Some claim ->
                    let request =
                        { ClaimId=claimId selection storeResourceId storeGeneration
                          AttestationPayloadSha256=
                            MigrationProtectedIssueCensusAttestation.signingPayload
                                attestationPins claim |> sha
                          Selection=selection; CustodyStoreResourceId=storeResourceId
                          StoreGeneration=storeGeneration
                          JournalResourceId=claimPins.JournalResourceId }
                    let outcome =
                        try journal.ClaimOnce request
                        with _ -> ClaimUnknown
                    match outcome with
                    | ClaimDuplicate -> Error "protected-census-claim-duplicate"
                    | ClaimUnknown -> Error "protected-census-claim-unknown"
                    | ClaimCommitted ->
                        let readback =
                            try journal.ReadClaim request.ClaimId
                            with _ -> None
                        match readback with
                        | None -> Error "protected-census-claim-unknown"
                        | Some committed when committed.Request = request
                                              && committed.CommitGeneration > 0L -> Ok ()
                        | Some _ -> Error "protected-census-claim-binding"
