module FS.GG.Coordination.MigrationProtectedIssueCensusReleaseTests

open System
open System.Security.Cryptography
open Xunit
open FS.GG.Coordination.Cli

let private digest (bytes: byte[]) =
    bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private selection =
    { RunId=201L; RunAttempt=3; RunNonce="protected-release-nonce"
      CandidateSha=String.replicate 40 "a"; WorkflowSha=String.replicate 40 "b"
      ApiOrigin="https://api.github.test"; Owner="FS-GG"; Repository="copy"
      RepositoryId=42L }

let private storePins =
    { StoreResourceId="protected-store:release-test"
      StoreArtifactSha256=String.replicate 64 "c" }

let private claimPins =
    { JournalResourceId="protected-journal:release-test"
      JournalArtifactSha256=String.replicate 64 "d" }

let private releasePins =
    { ReleaseResourceId="protected-release:release-test"
      ReleaseArtifactSha256=String.replicate 64 "e" }

let private storeDescription =
    { StoreResourceId=storePins.StoreResourceId
      StoreArtifactSha256=storePins.StoreArtifactSha256
      CandidateMayRead=false; CandidateMayWrite=false; ImmutableHead=true }

let private claimDescription =
    { JournalResourceId=claimPins.JournalResourceId
      JournalArtifactSha256=claimPins.JournalArtifactSha256
      StoreHeadResourceId=storePins.StoreResourceId
      StoreHeadArtifactSha256=storePins.StoreArtifactSha256
      AtomicStoreHeadCompare=true
      CandidateMayRead=false; CandidateMayWrite=false; ImmutableJournal=true }

let private releaseDescription keySha =
    { ReleaseResourceId=releasePins.ReleaseResourceId
      ReleaseArtifactSha256=releasePins.ReleaseArtifactSha256
      StoreResourceId=storePins.StoreResourceId
      StoreArtifactSha256=storePins.StoreArtifactSha256
      JournalResourceId=claimPins.JournalResourceId
      JournalArtifactSha256=claimPins.JournalArtifactSha256
      ClockResourceId="protected-clock:release-test"
      SignerPublicKeySha256=keySha
      SignerArtifactSha256=String.replicate 64 "f"
      CandidateMayRead=false; CandidateMayWrite=false
      AtomicCompareAndConsume=true; AtomicExpiryCompare=true }

type private Fixture =
    { Pins: ProtectedIssueCensusAttestationPins
      Proof: ProtectedIssueCensusProof
      Seal: ProtectedIssueCensusSealAttestation
      Now: DateTimeOffset
      StoreHead: ProtectedIssueCensusStoreHead
      ClaimRecord: ProtectedIssueCensusClaimRecord
      JournalHead: ProtectedIssueCensusClaimHead }

let private fixture () =
    use signer = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let publicKey = signer.ExportSubjectPublicKeyInfo()
    let pins =
        { SignerPublicKeySpkiBase64=Convert.ToBase64String publicKey
          SignerPublicKeySha256=digest publicKey
          SignerArtifactSha256=String.replicate 64 "f"
          ClockResourceId="protected-clock:release-test"; MaximumAgeSeconds=60 }
    let issued = DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)
    let proof =
        { Inspect=Unchecked.defaultof<_>; CustodyObjectIds=[]
          CorpusSha256=String.replicate 64 "1" }
    let unsigned =
        { Selection=selection; CustodyStoreResourceId=storePins.StoreResourceId
          CorpusSha256=proof.CorpusSha256; StoreGeneration=7L
          IssuedAtUtc=issued; ExpiresAtUtc=issued.AddSeconds 60
          SignatureBase64="" }
    let signature =
        signer.SignData(MigrationProtectedIssueCensusAttestation.signingPayload pins unsigned,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    let seal = { unsigned with SignatureBase64=Convert.ToBase64String signature }
    let storeHead =
        { Selection=selection; StoreResourceId=storePins.StoreResourceId
          Generation=7L; CorpusSha256=proof.CorpusSha256
          HeadSha256=String.replicate 64 "2" }
    let previous =
        { JournalResourceId=claimPins.JournalResourceId
          Generation=0L; SealSha256=String.replicate 64 "3" }
    let claim =
        { ClaimId=MigrationProtectedIssueCensusClaim.claimId
                      selection storePins.StoreResourceId 7L
          AttestationPayloadSha256=
            MigrationProtectedIssueCensusAttestation.signingPayload pins seal |> digest
          Selection=selection; CustodyStoreResourceId=storePins.StoreResourceId
          StoreGeneration=7L; ExpectedStoreCorpusSha256=proof.CorpusSha256
          ExpectedStoreHeadSha256=storeHead.HeadSha256
          JournalResourceId=claimPins.JournalResourceId
          ExpectedHeadGeneration=previous.Generation
          ExpectedHeadSha256=previous.SealSha256 }
    let record =
        { Request=claim; CommitGeneration=1L
          CommitHeadSha256=MigrationProtectedIssueCensusClaim.expectedCommitHeadSha256
                               previous claim }
    let journalHead =
        { JournalResourceId=claimPins.JournalResourceId
          Generation=record.CommitGeneration; SealSha256=record.CommitHeadSha256 }
    { Pins=pins; Proof=proof; Seal=seal; Now=issued.AddSeconds 10
      StoreHead=storeHead; ClaimRecord=record; JournalHead=journalHead }

let private clock (f: Fixture) =
    { new IProtectedIssueCensusClockPort with
        member _.Describe() = f.Pins.ClockResourceId
        member _.ReadNow() = Some f.Now }

let private store (head: ProtectedIssueCensusStoreHead) =
    { new IProtectedIssueCensusStoreHeadPort with
        member _.Describe() = storeDescription
        member _.ReadHead _ = Some head }

let private journal (record: ProtectedIssueCensusClaimRecord)
                    (head: ProtectedIssueCensusClaimHead) =
    { new IProtectedIssueCensusClaimPort with
        member _.Describe() = claimDescription
        member _.ReadHead() = Some head
        member _.ClaimOnce _ = failwith "release must not claim again"
        member _.ReadClaim _ = Some record }

let private reserve (f: Fixture) storePort claimPort releasePort =
    MigrationProtectedIssueCensusRelease.reserve
        f.Pins claimPins storePins releasePins selection f.Proof
        (Some f.Seal) (Some (clock f)) storePort claimPort releasePort

[<Fact>]
let ``protected census release reservation is one use and exactly bound`` () =
    let f = fixture ()
    let mutable stored: ProtectedIssueCensusReleaseRequest option = None
    let mutable attempts = 0
    let release =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
            member _.ReserveOnce request =
                attempts <- attempts + 1
                match stored with
                | Some _ -> ReleaseDuplicate
                | None -> stored <- Some request; ReleaseReserved
            member _.ReadReservation _ = stored }
    let invoke () =
        reserve f (Some (store f.StoreHead))
                  (Some (journal f.ClaimRecord f.JournalHead)) (Some release)
    Assert.Equal(Ok (), invoke ())
    Assert.Equal(Error "protected-census-release-duplicate", invoke ())
    Assert.Equal(2, attempts)
    Assert.Equal(f.ClaimRecord.Request.ClaimId, stored.Value.ClaimId)
    Assert.Equal(f.StoreHead.HeadSha256, stored.Value.ExpectedStoreHeadSha256)
    Assert.Equal(f.JournalHead.SealSha256, stored.Value.ExpectedJournalHeadSha256)
    Assert.Equal(f.Seal.ExpiresAtUtc, stored.Value.SignedExpiresAtUtc)
    Assert.Equal(f.Pins.ClockResourceId, stored.Value.ClockResourceId)

[<Fact>]
let ``protected census release checks signed expiry inside reservation CAS`` () =
    let f = fixture ()
    let mutable stored: ProtectedIssueCensusReleaseRequest option = None
    let release =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
            member _.ReserveOnce request =
                // Protected clock has advanced after the source preflight.
                if request.SignedExpiresAtUtc = f.Seal.ExpiresAtUtc
                   && request.ClockResourceId = f.Pins.ClockResourceId then
                    ReleaseConflict
                else
                    stored <- Some request
                    ReleaseReserved
            member _.ReadReservation _ = stored }
    Assert.Equal(Error "protected-census-release-conflict",
                 reserve f (Some (store f.StoreHead))
                           (Some (journal f.ClaimRecord f.JournalHead)) (Some release))
    Assert.True(stored.IsNone)

[<Fact>]
let ``protected census release refuses unpinned or non-atomic expiry authority`` () =
    let f = fixture ()
    let invoke description =
        let release =
            { new IProtectedIssueCensusReleasePort with
                member _.Describe() = description
                member _.ReserveOnce _ = failwith "must refuse before reservation"
                member _.ReadReservation _ = None }
        reserve f (Some (store f.StoreHead))
                  (Some (journal f.ClaimRecord f.JournalHead)) (Some release)
    let expected = releaseDescription f.Pins.SignerPublicKeySha256
    Assert.Equal(Error "protected-census-release-installation",
                 invoke { expected with AtomicExpiryCompare=false })
    Assert.Equal(Error "protected-census-release-installation",
                 invoke { expected with ClockResourceId="candidate-clock" })
    Assert.Equal(Error "protected-census-release-installation",
                 invoke { expected with SignerPublicKeySha256=String.replicate 64 "0" })

[<Fact>]
let ``protected census release refuses stale heads and forged claim before reservation`` () =
    let f = fixture ()
    let mutable attempts = 0
    let release =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
            member _.ReserveOnce _ = attempts <- attempts + 1; ReleaseReserved
            member _.ReadReservation _ = None }
    let expectedJournal = Some (journal f.ClaimRecord f.JournalHead)
    Assert.Equal(Error "protected-census-release-head",
                 reserve f (Some (store { f.StoreHead with Generation=8L }))
                           expectedJournal (Some release))
    let forgedRequest =
        { f.ClaimRecord.Request with ExpectedStoreHeadSha256=String.replicate 64 "4" }
    let previous =
        { JournalResourceId=claimPins.JournalResourceId
          Generation=forgedRequest.ExpectedHeadGeneration
          SealSha256=forgedRequest.ExpectedHeadSha256 }
    let forgedSeal = MigrationProtectedIssueCensusClaim.expectedCommitHeadSha256
                         previous forgedRequest
    let wrongClaim =
        { f.ClaimRecord with Request=forgedRequest; CommitHeadSha256=forgedSeal }
    let forgedJournalHead = { f.JournalHead with SealSha256=forgedSeal }
    Assert.Equal(Error "protected-census-release-claim",
                 reserve f (Some (store f.StoreHead))
                           (Some (journal wrongClaim forgedJournalHead)) (Some release))
    Assert.Equal(0, attempts)

[<Fact>]
let ``protected census release refuses absent atomic authority and unknown reservation`` () =
    let f = fixture ()
    let storePort = Some (store f.StoreHead)
    let claimPort = Some (journal f.ClaimRecord f.JournalHead)
    Assert.Equal(Error "protected-census-release-unavailable",
                 reserve f storePort claimPort None)
    let noAtomic =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() =
                { releaseDescription f.Pins.SignerPublicKeySha256 with AtomicCompareAndConsume=false }
            member _.ReserveOnce _ = failwith "must refuse before reservation"
            member _.ReadReservation _ = None }
    Assert.Equal(Error "protected-census-release-installation",
                 reserve f storePort claimPort (Some noAtomic))
    let mutable attempts = 0
    let unknown =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
            member _.ReserveOnce _ = attempts <- attempts + 1; ReleaseUnknown
            member _.ReadReservation _ = failwith "must not recover by retry" }
    Assert.Equal(Error "protected-census-release-unknown",
                 reserve f storePort claimPort (Some unknown))
    Assert.Equal(1, attempts)

[<Fact>]
let ``protected census release refuses concurrent head conflict and lost readback`` () =
    let f = fixture ()
    let storePort = Some (store f.StoreHead)
    let claimPort = Some (journal f.ClaimRecord f.JournalHead)
    let conflict =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
            member _.ReserveOnce _ = ReleaseConflict
            member _.ReadReservation _ = failwith "conflict must not read success" }
    Assert.Equal(Error "protected-census-release-conflict",
                 reserve f storePort claimPort (Some conflict))
    let lost =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
            member _.ReserveOnce _ = ReleaseReserved
            member _.ReadReservation _ = None }
    Assert.Equal(Error "protected-census-release-unknown",
                 reserve f storePort claimPort (Some lost))
