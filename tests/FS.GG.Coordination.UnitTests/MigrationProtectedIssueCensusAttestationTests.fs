module FS.GG.Coordination.MigrationProtectedIssueCensusAttestationTests

open System
open System.Security.Cryptography
open Xunit
open FS.GG.Coordination.Cli

let private digest (bytes: byte[]) =
    bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private selection =
    { RunId=101L; RunAttempt=2; RunNonce="protected-run-nonce"
      CandidateSha=String.replicate 40 "a"; WorkflowSha=String.replicate 40 "b"
      ApiOrigin="https://api.github.test"; Owner="FS-GG"; Repository="copy"
      RepositoryId=42L }

let private clock resource now =
    { new IProtectedIssueCensusClockPort with
        member _.Describe() = resource
        member _.ReadNow() = Some now }

let private fixture () =
    use signer = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let publicKey = signer.ExportSubjectPublicKeyInfo()
    let pins =
        { SignerPublicKeySpkiBase64=Convert.ToBase64String publicKey
          SignerPublicKeySha256=digest publicKey
          SignerArtifactSha256=String.replicate 64 "c"
          ClockResourceId="protected-clock:fixture"; MaximumAgeSeconds=60 }
    let issued = DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)
    let proof =
        { Inspect=Unchecked.defaultof<_>; CustodyObjectIds=[]
          CorpusSha256=String.replicate 64 "d" }
    let unsigned =
        { Selection=selection; CustodyStoreResourceId="protected-store:fixture"
          CorpusSha256=proof.CorpusSha256; StoreGeneration=7L
          IssuedAtUtc=issued; ExpiresAtUtc=issued.AddSeconds 60
          SignatureBase64="" }
    let payload = MigrationProtectedIssueCensusAttestation.signingPayload pins unsigned
    let signature =
        signer.SignData(payload, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    pins, proof, { unsigned with SignatureBase64=Convert.ToBase64String signature }, issued.AddSeconds 10

let private verify pins proof attestation observedClock =
    MigrationProtectedIssueCensusAttestation.verify
        pins selection "protected-store:fixture" proof 7L attestation observedClock

[<Fact>]
let ``protected census seal verifier accepts exact fake signed selection`` () =
    let pins, proof, attestation, now = fixture ()
    Assert.Equal(Ok (), verify pins proof (Some attestation)
                            (Some (clock pins.ClockResourceId now)))

[<Fact>]
let ``protected census seal verifier refuses unsigned foreign or stale claims`` () =
    let pins, proof, attestation, now = fixture ()
    let nativeClock = Some (clock pins.ClockResourceId now)
    Assert.Equal(Error "protected-census-attestation-unavailable",
                 verify pins proof None nativeClock)
    Assert.Equal(Error "protected-census-attestation-unavailable",
                 verify pins proof (Some attestation) None)
    Assert.Equal(Error "protected-census-attestation-binding",
                 verify pins proof
                     (Some { attestation with Selection={ selection with RunNonce="stale" } }) nativeClock)
    Assert.Equal(Error "protected-census-attestation-binding",
                 verify pins proof
                     (Some { attestation with StoreGeneration=8L }) nativeClock)
    Assert.Equal(Error "protected-census-attestation-binding",
                 verify pins proof
                     (Some { attestation with CorpusSha256=String.replicate 64 "e" }) nativeClock)
    Assert.Equal(Error "protected-census-attestation-stale",
                 verify pins proof (Some attestation)
                     (Some (clock pins.ClockResourceId (now.AddSeconds 120))))
    Assert.Equal(Error "protected-census-attestation-stale",
                 verify pins proof (Some attestation)
                     (Some (clock pins.ClockResourceId (now.AddSeconds -20))))
    Assert.Equal(Error "protected-census-attestation-stale",
                 verify pins proof
                     (Some { attestation with ExpiresAtUtc=attestation.IssuedAtUtc.AddSeconds 301 })
                     nativeClock)
    Assert.Equal(Error "protected-census-clock-installation",
                 verify pins proof (Some attestation)
                     (Some (clock "candidate-clock" now)))
    Assert.Equal(Error "protected-census-attestation-signature",
                 verify pins proof
                     (Some { attestation with SignatureBase64=Convert.ToBase64String(Array.zeroCreate<byte> 64) })
                     nativeClock)
    Assert.Equal(Error "protected-census-attestation-signature",
                 verify pins proof
                     (Some { attestation with SignatureBase64="" }) nativeClock)
    Assert.Equal(Error "protected-census-attestation-signature",
                 verify { pins with SignerArtifactSha256=String.replicate 64 "e" }
                     proof (Some attestation) nativeClock)
    Assert.Equal(Error "protected-census-attestation-pins",
                 verify { pins with SignerPublicKeySha256=String.replicate 64 "0" }
                     proof (Some attestation) nativeClock)

[<Fact>]
let ``protected census seal verifier refuses unknown clock once`` () =
    let pins, proof, attestation, _ = fixture ()
    let mutable calls = 0
    let unknown =
        { new IProtectedIssueCensusClockPort with
            member _.Describe() = pins.ClockResourceId
            member _.ReadNow() =
                calls <- calls + 1
                raise (InvalidOperationException "unknown protected clock") }
    Assert.Equal(Error "protected-census-attestation-unavailable",
                 verify pins proof (Some attestation) (Some unknown))
    Assert.Equal(1, calls)

let private claimPins =
    { JournalResourceId="protected-claim-journal:fixture"
      JournalArtifactSha256=String.replicate 64 "e" }

let private storeHeadPins =
    { StoreResourceId="protected-store:fixture"
      StoreArtifactSha256=String.replicate 64 "f" }

let private storeHeadDescription =
    { StoreResourceId=storeHeadPins.StoreResourceId
      StoreArtifactSha256=storeHeadPins.StoreArtifactSha256
      CandidateMayRead=false; CandidateMayWrite=false; ImmutableHead=true }

let private storeHead =
    { Selection=selection; StoreResourceId=storeHeadPins.StoreResourceId
      Generation=7L; CorpusSha256=String.replicate 64 "d"
      HeadSha256=String.replicate 64 "a" }

let private storePort head =
    { new IProtectedIssueCensusStoreHeadPort with
        member _.Describe() = storeHeadDescription
        member _.ReadHead _ = Some head }

let private claimDescription =
    { JournalResourceId=claimPins.JournalResourceId
      JournalArtifactSha256=claimPins.JournalArtifactSha256
      StoreHeadResourceId=storeHeadPins.StoreResourceId
      StoreHeadArtifactSha256=storeHeadPins.StoreArtifactSha256
      AtomicStoreHeadCompare=true
      CandidateMayRead=false; CandidateMayWrite=false; ImmutableJournal=true }

let private initialHead =
    { JournalResourceId=claimPins.JournalResourceId
      Generation=0L; SealSha256=String.replicate 64 "a" }

let private committedRecord request =
    { Request=request; CommitGeneration=1L
      CommitHeadSha256=MigrationProtectedIssueCensusClaim.expectedCommitHeadSha256
                           initialHead request }

let private verifyAndClaim pins proof attestation now port =
    MigrationProtectedIssueCensusClaim.verifyAndClaim
        pins claimPins storeHeadPins selection "protected-store:fixture" proof 7L
        (Some attestation) (Some (clock pins.ClockResourceId now))
        (Some (storePort storeHead)) port

[<Fact>]
let ``protected census claim refuses signed stale store head before journal CAS`` () =
    let pins, proof, attestation, now = fixture ()
    let mutable attempts = 0
    let journal =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce _ = attempts <- attempts + 1; ClaimUnknown
            member _.ReadClaim _ = None }
    let result =
        MigrationProtectedIssueCensusClaim.verifyAndClaim
            pins claimPins storeHeadPins selection "protected-store:fixture" proof 7L
            (Some attestation) (Some (clock pins.ClockResourceId now))
            (Some (storePort { storeHead with Generation=8L })) (Some journal)
    Assert.Equal(Error "protected-census-store-head", result)
    Assert.Equal(0, attempts)

[<Fact>]
let ``protected census claim refuses foreign missing and candidate-readable store head`` () =
    let pins, proof, attestation, now = fixture ()
    let mutable attempts = 0
    let journal =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce _ = attempts <- attempts + 1; ClaimUnknown
            member _.ReadClaim _ = None }
    let invoke store =
        MigrationProtectedIssueCensusClaim.verifyAndClaim
            pins claimPins storeHeadPins selection "protected-store:fixture" proof 7L
            (Some attestation) (Some (clock pins.ClockResourceId now)) store (Some journal)
    Assert.Equal(Error "protected-census-store-unavailable", invoke None)
    let foreign = storePort { storeHead with Selection={ selection with RunNonce="other" } }
    Assert.Equal(Error "protected-census-store-head", invoke (Some foreign))
    let otherCorpus = storePort { storeHead with CorpusSha256=String.replicate 64 "0" }
    Assert.Equal(Error "protected-census-store-head", invoke (Some otherCorpus))
    let missing =
        { new IProtectedIssueCensusStoreHeadPort with
            member _.Describe() = storeHeadDescription
            member _.ReadHead _ = None }
    Assert.Equal(Error "protected-census-store-unknown", invoke (Some missing))
    let crashed =
        { new IProtectedIssueCensusStoreHeadPort with
            member _.Describe() = storeHeadDescription
            member _.ReadHead _ = failwith "lost protected readback" }
    Assert.Equal(Error "protected-census-store-unknown", invoke (Some crashed))
    let candidateReadable =
        { new IProtectedIssueCensusStoreHeadPort with
            member _.Describe() = { storeHeadDescription with CandidateMayRead=true }
            member _.ReadHead _ = failwith "must refuse before read" }
    Assert.Equal(Error "protected-census-store-installation", invoke (Some candidateReadable))
    Assert.Equal(0, attempts)

[<Fact>]
let ``protected census claim refuses store head drift after durable journal claim`` () =
    let pins, proof, attestation, now = fixture ()
    let mutable request: ProtectedIssueCensusClaimRequest option = None
    let mutable storeReads = 0
    let store =
        { new IProtectedIssueCensusStoreHeadPort with
            member _.Describe() = storeHeadDescription
            member _.ReadHead _ =
                storeReads <- storeReads + 1
                if storeReads = 1 then Some storeHead
                else Some { storeHead with HeadSha256=String.replicate 64 "b" } }
    let journal =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() =
                match request with
                | None -> Some initialHead
                | Some value ->
                    Some { initialHead with Generation=1L
                                            SealSha256=(committedRecord value).CommitHeadSha256 }
            member _.ClaimOnce value = request <- Some value; ClaimCommitted
            member _.ReadClaim _ = request |> Option.map committedRecord }
    let result =
        MigrationProtectedIssueCensusClaim.verifyAndClaim
            pins claimPins storeHeadPins selection "protected-store:fixture" proof 7L
            (Some attestation) (Some (clock pins.ClockResourceId now))
            (Some store) (Some journal)
    Assert.Equal(Error "protected-census-store-head", result)
    Assert.Equal(2, storeReads)
    Assert.True(request.IsSome)

[<Fact>]
let ``protected census claim requires atomic store head compare at journal CAS`` () =
    let pins, proof, attestation, now = fixture ()
    let mutable request: ProtectedIssueCensusClaimRequest option = None
    let journal =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() =
                match request with
                | None -> Some initialHead
                | Some value ->
                    Some { initialHead with Generation=1L
                                            SealSha256=(committedRecord value).CommitHeadSha256 }
            member _.ClaimOnce value =
                // The fake protected store changed during the CAS. A coupled
                // adapter must compare the exact head supplied in the request.
                if value.ExpectedStoreHeadSha256 = storeHead.HeadSha256
                   && value.ExpectedStoreCorpusSha256 = proof.CorpusSha256 then
                    ClaimConflict
                else
                    request <- Some value
                    ClaimCommitted
            member _.ReadClaim _ = request |> Option.map committedRecord }
    Assert.Equal(Error "protected-census-claim-conflict",
                 verifyAndClaim pins proof attestation now (Some journal))
    Assert.True(request.IsNone)

[<Fact>]
let ``protected census claim refuses journal without atomic store head authority`` () =
    let pins, proof, attestation, now = fixture ()
    let journal =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = { claimDescription with AtomicStoreHeadCompare=false }
            member _.ReadHead() = failwith "must refuse before journal read"
            member _.ClaimOnce _ = failwith "must refuse before CAS"
            member _.ReadClaim _ = None }
    Assert.Equal(Error "protected-census-claim-installation",
                 verifyAndClaim pins proof attestation now (Some journal))
    let foreignStore =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() =
                { claimDescription with StoreHeadResourceId="foreign-store" }
            member _.ReadHead() = failwith "must refuse before journal read"
            member _.ClaimOnce _ = failwith "must refuse before CAS"
            member _.ReadClaim _ = None }
    Assert.Equal(Error "protected-census-claim-installation",
                 verifyAndClaim pins proof attestation now (Some foreignStore))

[<Fact>]
let ``protected census attestation claim refuses duplicate signed handoff`` () =
    let pins, proof, attestation, now = fixture ()
    let mutable attempts = 0
    let mutable stored: ProtectedIssueCensusClaimRequest option = None
    let journal =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() =
                match stored with
                | None -> Some initialHead
                | Some request ->
                    Some { initialHead with Generation=1L
                                            SealSha256=(committedRecord request).CommitHeadSha256 }
            member _.ClaimOnce request =
                attempts <- attempts + 1
                match stored with
                | Some _ -> ClaimDuplicate
                | None -> stored <- Some request; ClaimCommitted
            member _.ReadClaim _ =
                stored |> Option.map committedRecord }
    Assert.Equal(Ok (), verifyAndClaim pins proof attestation now (Some journal))
    Assert.Equal(Error "protected-census-claim-duplicate",
                 verifyAndClaim pins proof attestation now (Some journal))
    Assert.Equal(2, attempts)

[<Fact>]
let ``protected census attestation claim refuses absent unknown and forged journal`` () =
    let pins, proof, attestation, now = fixture ()
    Assert.Equal(Error "protected-census-claim-unavailable",
                 verifyAndClaim pins proof attestation now None)
    let mutable attempts = 0
    let unknown =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce _ =
                attempts <- attempts + 1
                ClaimUnknown
            member _.ReadClaim _ = failwith "unknown CAS must not be retried or read" }
    Assert.Equal(Error "protected-census-claim-unknown",
                 verifyAndClaim pins proof attestation now (Some unknown))
    Assert.Equal(1, attempts)
    let mutable readbacks = 0
    let lost =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce _ = ClaimCommitted
            member _.ReadClaim _ =
                readbacks <- readbacks + 1
                None }
    Assert.Equal(Error "protected-census-claim-unknown",
                 verifyAndClaim pins proof attestation now (Some lost))
    Assert.Equal(1, readbacks)
    let candidateWritable =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = { claimDescription with CandidateMayWrite=true }
            member _.ReadHead() = failwith "installation must refuse before head"
            member _.ClaimOnce _ = failwith "installation must refuse before claim"
            member _.ReadClaim _ = None }
    Assert.Equal(Error "protected-census-claim-installation",
                 verifyAndClaim pins proof attestation now (Some candidateWritable))
    let forgedReadback =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce _ = ClaimCommitted
            member _.ReadClaim _ =
                Some { Request={ ClaimId=String.replicate 64 "f"
                                 AttestationPayloadSha256=proof.CorpusSha256
                                 Selection=selection; CustodyStoreResourceId="protected-store:fixture"
                                 StoreGeneration=7L; JournalResourceId=claimPins.JournalResourceId
                                 ExpectedStoreCorpusSha256=proof.CorpusSha256
                                 ExpectedStoreHeadSha256=storeHead.HeadSha256
                                 ExpectedHeadGeneration=0L; ExpectedHeadSha256=initialHead.SealSha256 }
                       CommitGeneration=1L; CommitHeadSha256=String.replicate 64 "f" } }
    Assert.Equal(Error "protected-census-claim-binding",
                 verifyAndClaim pins proof attestation now (Some forgedReadback))

[<Fact>]
let ``protected census attestation claim refuses head jump and lost CAS`` () =
    let pins, proof, attestation, now = fixture ()
    let mutable request: ProtectedIssueCensusClaimRequest option = None
    let jump =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce value = request <- Some value; ClaimCommitted
            member _.ReadClaim _ =
                request |> Option.map (fun value ->
                    { (committedRecord value) with CommitGeneration=99L }) }
    Assert.Equal(Error "protected-census-claim-head",
                 verifyAndClaim pins proof attestation now (Some jump))
    request <- None
    let changedPostHead =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce value = request <- Some value; ClaimCommitted
            member _.ReadClaim _ = request |> Option.map committedRecord }
    Assert.Equal(Error "protected-census-claim-head",
                 verifyAndClaim pins proof attestation now (Some changedPostHead))
    let mutable attempts = 0
    let unknownPrehead =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = None
            member _.ClaimOnce _ = attempts <- attempts + 1; ClaimCommitted
            member _.ReadClaim _ = None }
    Assert.Equal(Error "protected-census-claim-unknown",
                 verifyAndClaim pins proof attestation now (Some unknownPrehead))
    Assert.Equal(0, attempts)
    let conflict =
        { new IProtectedIssueCensusClaimPort with
            member _.Describe() = claimDescription
            member _.ReadHead() = Some initialHead
            member _.ClaimOnce _ = attempts <- attempts + 1; ClaimConflict
            member _.ReadClaim _ = failwith "conflicted CAS must not read a receipt" }
    Assert.Equal(Error "protected-census-claim-conflict",
                 verifyAndClaim pins proof attestation now (Some conflict))
    Assert.Equal(1, attempts)
