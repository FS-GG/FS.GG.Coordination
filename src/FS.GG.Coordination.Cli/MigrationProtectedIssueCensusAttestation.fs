namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

type ProtectedIssueCensusAttestationPins =
    { SignerPublicKeySpkiBase64: string
      SignerPublicKeySha256: string
      SignerArtifactSha256: string
      ClockResourceId: string
      MaximumAgeSeconds: int }

type ProtectedIssueCensusSealAttestation =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      CorpusSha256: string
      StoreGeneration: int64
      IssuedAtUtc: DateTimeOffset
      ExpiresAtUtc: DateTimeOffset
      SignatureBase64: string }

type IProtectedIssueCensusClockPort =
    abstract Describe: unit -> string
    abstract ReadNow: unit -> DateTimeOffset option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusAttestation =
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private exactAtom (value: string) = not (String.IsNullOrWhiteSpace value)

    let private exactSha (value: string) =
        not (isNull value) && value.Length = 64
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let private canonicalBase64 (value: string) =
        try
            let bytes = Convert.FromBase64String value
            if Convert.ToBase64String bytes = value then Some bytes else None
        with _ -> None

    let private publicKey (pins: ProtectedIssueCensusAttestationPins) =
        if not (exactSha pins.SignerPublicKeySha256)
           || not (exactSha pins.SignerArtifactSha256)
           || not (exactAtom pins.ClockResourceId)
           || pins.MaximumAgeSeconds < 1 || pins.MaximumAgeSeconds > 300 then None
        else
            match canonicalBase64 pins.SignerPublicKeySpkiBase64 with
            | None -> None
            | Some bytes ->
                let digest = bytes |> SHA256.HashData |> Convert.ToHexString
                                   |> _.ToLowerInvariant()
                if digest = pins.SignerPublicKeySha256 then Some bytes else None

    let signingPayload (pins: ProtectedIssueCensusAttestationPins)
                       (attestation: ProtectedIssueCensusSealAttestation) =
        let selection = attestation.Selection
        [ "fsgg.gs2-09.7.protected-census-seal-attestation/v1"
          pins.SignerPublicKeySha256; pins.SignerArtifactSha256
          pins.ClockResourceId; string pins.MaximumAgeSeconds
          string selection.RunId; string selection.RunAttempt; selection.RunNonce
          selection.CandidateSha; selection.WorkflowSha; selection.ApiOrigin
          selection.Owner; selection.Repository; string selection.RepositoryId
          attestation.CustodyStoreResourceId; attestation.CorpusSha256
          string attestation.StoreGeneration
          attestation.IssuedAtUtc.ToUniversalTime().ToString("O")
          attestation.ExpiresAtUtc.ToUniversalTime().ToString("O") ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes

    let verify (pins: ProtectedIssueCensusAttestationPins)
               (selection: ProtectedIssueCensusSelection)
               (storeResourceId: string)
               (proof: ProtectedIssueCensusProof)
               (expectedStoreGeneration: int64)
               (attestation: ProtectedIssueCensusSealAttestation option)
               (clock: IProtectedIssueCensusClockPort option) : Result<unit, string> =
        match publicKey pins with
        | None -> Error "protected-census-attestation-pins"
        | Some publicBytes ->
            if not (exactAtom storeResourceId)
               || not (exactSha proof.CorpusSha256)
               || expectedStoreGeneration < 1L then
                Error "protected-census-attestation-binding"
            else
                match attestation, clock with
                | None, _ | _, None -> Error "protected-census-attestation-unavailable"
                | Some claim, Some trustedClock ->
                    if claim.Selection <> selection
                       || claim.CustodyStoreResourceId <> storeResourceId
                       || claim.CorpusSha256 <> proof.CorpusSha256
                       || claim.StoreGeneration <> expectedStoreGeneration then
                        Error "protected-census-attestation-binding"
                    else
                        let observed =
                            try
                                if trustedClock.Describe() <> pins.ClockResourceId then
                                    Error "protected-census-clock-installation"
                                else
                                    match trustedClock.ReadNow() with
                                    | None -> Error "protected-census-attestation-unavailable"
                                    | Some now -> Ok (now.ToUniversalTime())
                            with _ -> Error "protected-census-attestation-unavailable"
                        match observed with
                        | Error reason -> Error reason
                        | Ok now ->
                            let lifetime = claim.ExpiresAtUtc - claim.IssuedAtUtc
                            if claim.IssuedAtUtc.Offset <> TimeSpan.Zero
                               || claim.ExpiresAtUtc.Offset <> TimeSpan.Zero
                               || lifetime <= TimeSpan.Zero
                               || lifetime > TimeSpan.FromSeconds(float pins.MaximumAgeSeconds)
                               || now < claim.IssuedAtUtc || now > claim.ExpiresAtUtc then
                                Error "protected-census-attestation-stale"
                            else
                                match canonicalBase64 claim.SignatureBase64 with
                                | None -> Error "protected-census-attestation-signature"
                                | Some signature when signature.Length <> 64 ->
                                    Error "protected-census-attestation-signature"
                                | Some signature ->
                                    try
                                        use key = ECDsa.Create()
                                        let mutable bytesRead = 0
                                        key.ImportSubjectPublicKeyInfo(publicBytes, &bytesRead)
                                        if bytesRead <> publicBytes.Length || key.KeySize <> 256
                                           || key.ExportParameters(false).Curve.Oid.Value
                                              <> "1.2.840.10045.3.1.7" then
                                            Error "protected-census-attestation-pins"
                                        elif key.VerifyData(signingPayload pins claim, signature,
                                                            HashAlgorithmName.SHA256,
                                                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation) then
                                            Ok ()
                                        else Error "protected-census-attestation-signature"
                                    with _ -> Error "protected-census-attestation-pins"
