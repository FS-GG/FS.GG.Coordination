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
      ClockArtifactSha256=String.replicate 64 "9"
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
          ClockResourceId="protected-clock:release-test"
          ClockArtifactSha256=String.replicate 64 "9"; MaximumAgeSeconds=60 }
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
        member _.Describe() =
            { ClockResourceId=f.Pins.ClockResourceId
              ClockArtifactSha256=f.Pins.ClockArtifactSha256
              CandidateMayRead=false; CandidateMayWrite=false; MonotonicUtc=true }
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
    Assert.Equal(f.Pins.ClockArtifactSha256, stored.Value.ClockArtifactSha256)

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
                 invoke { expected with ClockArtifactSha256=String.replicate 64 "0" })
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

let private handoffPins =
    { HandoffResourceId="protected-handoff:release-test"
      HandoffArtifactSha256=String.replicate 64 "6"
      VaultResourceId="protected-vault:release-test"
      VaultArtifactSha256=String.replicate 64 "7"
      NativeAttemptNamespaceId="protected-attempts:release-test"
      AppId=101L; InstallationId=202L; RepositoryId=selection.RepositoryId
      PermissionSha256=String.replicate 64 "8" }

let private handoffDescription =
    { HandoffResourceId=handoffPins.HandoffResourceId
      HandoffArtifactSha256=handoffPins.HandoffArtifactSha256
      VaultResourceId=handoffPins.VaultResourceId
      VaultArtifactSha256=handoffPins.VaultArtifactSha256
      NativeAttemptNamespaceId=handoffPins.NativeAttemptNamespaceId
      ReleaseResourceId=releasePins.ReleaseResourceId
      ReleaseArtifactSha256=releasePins.ReleaseArtifactSha256
      ClockResourceId="protected-clock:release-test"
      ClockArtifactSha256=String.replicate 64 "9"
      AppId=handoffPins.AppId; InstallationId=handoffPins.InstallationId
      RepositoryId=handoffPins.RepositoryId
      PermissionSha256=handoffPins.PermissionSha256
      CandidateMayRead=false; CandidateMayWrite=false
      AtomicReservationConsumeAndMark=true; AtomicExpiryCompare=true }

let private capturedReservation (f: Fixture) =
    let mutable stored: ProtectedIssueCensusReleaseRequest option = None
    let release =
        { new IProtectedIssueCensusReleasePort with
            member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
            member _.ReserveOnce request = stored <- Some request; ReleaseReserved
            member _.ReadReservation _ = stored }
    Assert.Equal(Ok (), reserve f (Some (store f.StoreHead))
                                 (Some (journal f.ClaimRecord f.JournalHead)) (Some release))
    stored.Value

let private releaseReadback (f: Fixture) reservation =
    { new IProtectedIssueCensusReleasePort with
        member _.Describe() = releaseDescription f.Pins.SignerPublicKeySha256
        member _.ReserveOnce _ = failwith "handoff must not reserve again"
        member _.ReadReservation _ = reservation }

let private markHandoff (f: Fixture) pins reservation releasePort handoffPort =
    MigrationProtectedIssueCensusHandoff.mark
        f.Pins storePins claimPins releasePins pins selection f.Proof
        (Some f.Seal) (Some (clock f)) reservation releasePort handoffPort

[<Fact>]
let ``protected census token handoff marker is exact and one attempt`` () =
    let f = fixture ()
    let reservation = capturedReservation f
    let mutable marked: ProtectedIssueCensusHandoffRequest option = None
    let mutable attempts = 0
    let port =
        { new IProtectedIssueCensusHandoffPort with
            member _.Describe() = handoffDescription
            member _.MarkOnce request =
                attempts <- attempts + 1
                match marked with
                | Some _ -> HandoffDuplicate
                | None -> marked <- Some request; HandoffMarked
            member _.ReadMarker _ = marked }
    let invoke () =
        markHandoff f handoffPins reservation
            (Some (releaseReadback f (Some reservation))) (Some port)
    Assert.Equal(Ok (), invoke ())
    Assert.Equal(Error "protected-census-handoff-duplicate", invoke ())
    Assert.Equal(2, attempts)
    Assert.Equal(reservation.ReservationId, marked.Value.ReservationId)
    Assert.Equal(handoffPins.InstallationId, marked.Value.InstallationId)
    Assert.Equal(handoffPins.VaultResourceId, marked.Value.VaultResourceId)
    Assert.Equal(f.Pins.ClockArtifactSha256, marked.Value.ClockArtifactSha256)

[<Fact>]
let ``protected census token handoff refuses foreign target and candidate vault`` () =
    let f = fixture ()
    let reservation = capturedReservation f
    let release = Some (releaseReadback f (Some reservation))
    let mutable attempts = 0
    let port description =
        { new IProtectedIssueCensusHandoffPort with
            member _.Describe() = description
            member _.MarkOnce _ = attempts <- attempts + 1; HandoffUnknown
            member _.ReadMarker _ = None }
    Assert.Equal(Error "protected-census-handoff-pins",
                 markHandoff f { handoffPins with RepositoryId=selection.RepositoryId + 1L }
                     reservation release (Some (port handoffDescription)))
    Assert.Equal(Error "protected-census-handoff-installation",
                 markHandoff f handoffPins reservation release
                     (Some (port { handoffDescription with CandidateMayWrite=true })))
    Assert.Equal(Error "protected-census-handoff-installation",
                 markHandoff f handoffPins reservation release
                     (Some (port { handoffDescription with InstallationId=203L })))
    Assert.Equal(Error "protected-census-handoff-installation",
                 markHandoff f handoffPins reservation release
                     (Some (port { handoffDescription with AtomicExpiryCompare=false })))
    Assert.Equal(0, attempts)

[<Fact>]
let ``protected census token handoff refuses forged reservation and unknown marker`` () =
    let f = fixture ()
    let reservation = capturedReservation f
    let mutable attempts = 0
    let port =
        { new IProtectedIssueCensusHandoffPort with
            member _.Describe() = handoffDescription
            member _.MarkOnce _ = attempts <- attempts + 1; HandoffUnknown
            member _.ReadMarker _ = failwith "unknown must not be retried" }
    Assert.Equal(Error "protected-census-handoff-binding",
                 markHandoff f handoffPins
                     { reservation with ClaimId=String.replicate 64 "0" }
                     (Some (releaseReadback f (Some reservation))) (Some port))
    Assert.Equal(Error "protected-census-handoff-unknown",
                 markHandoff f handoffPins reservation
                     (Some (releaseReadback f (Some reservation))) (Some port))
    Assert.Equal(1, attempts)
    Assert.Equal(Error "protected-census-handoff-unknown",
                 markHandoff f handoffPins reservation
                     (Some (releaseReadback f None)) (Some port))
    Assert.Equal(1, attempts)
    let foreign = { reservation with ExpectedStoreHeadSha256=String.replicate 64 "0" }
    Assert.Equal(Error "protected-census-handoff-binding",
                 markHandoff f handoffPins reservation
                     (Some (releaseReadback f (Some foreign))) (Some port))
    Assert.Equal(1, attempts)
    let lostMarker =
        { new IProtectedIssueCensusHandoffPort with
            member _.Describe() = handoffDescription
            member _.MarkOnce _ = HandoffMarked
            member _.ReadMarker _ = None }
    Assert.Equal(Error "protected-census-handoff-unknown",
                 markHandoff f handoffPins reservation
                     (Some (releaseReadback f (Some reservation))) (Some lostMarker))

let private nativePins =
    { AttemptResourceId="protected-native-attempts:release-test"
      AttemptArtifactSha256=String.replicate 64 "a" }

let private nativeDescription =
    { AttemptResourceId=nativePins.AttemptResourceId
      AttemptArtifactSha256=nativePins.AttemptArtifactSha256
      NativeAttemptNamespaceId=handoffPins.NativeAttemptNamespaceId
      VaultResourceId=handoffPins.VaultResourceId
      VaultArtifactSha256=handoffPins.VaultArtifactSha256
      CandidateMayRead=false; CandidateMayWrite=false
      AuthoritativeCompleteReadback=true }

let private sealedSnapshot attemptId complete records =
    let unsigned =
        { Head={ AttemptResourceId=nativePins.AttemptResourceId
                 Generation=7L; SealSha256="" }
          AttemptId=attemptId; Complete=complete; Records=records }
    let seal = MigrationProtectedIssueCensusAttemptRecovery.expectedSnapshotSealSha256 unsigned
    { unsigned with Head={ unsigned.Head with SealSha256=seal } }

let private capturedMarker (f: Fixture) =
    let reservation = capturedReservation f
    let mutable marked: ProtectedIssueCensusHandoffRequest option = None
    let handoff =
        { new IProtectedIssueCensusHandoffPort with
            member _.Describe() = handoffDescription
            member _.MarkOnce request = marked <- Some request; HandoffMarked
            member _.ReadMarker _ = marked }
    Assert.Equal(Ok (), markHandoff f handoffPins reservation
                             (Some (releaseReadback f (Some reservation))) (Some handoff))
    marked.Value

let private markerPort marker =
    { new IProtectedIssueCensusHandoffPort with
        member _.Describe() = handoffDescription
        member _.MarkOnce _ = failwith "recovery must not mark again"
        member _.ReadMarker _ = marker }

let private nativePort description marker readback =
    let snapshot =
        sealedSnapshot marker.NativeAttemptId true (readback |> Option.defaultValue [])
    { new IProtectedIssueCensusNativeAttemptPort with
        member _.Describe() = description
        member _.ReadHead() = Some snapshot.Head
        member _.ReadAttempts _ = readback |> Option.map (fun _ -> snapshot) }

let private inspectAttempt marker handoff native =
    MigrationProtectedIssueCensusAttemptRecovery.inspect
        handoffPins nativePins marker handoff native

[<Fact>]
let ``protected native attempt recovery keeps every observed phase on hold`` () =
    let f = fixture ()
    let marker = capturedMarker f
    let unknown =
        { Request=marker; ProviderAttemptId=marker.NativeAttemptId
          VaultResourceId=handoffPins.VaultResourceId; Phase=InvocationUnknown
          TokenFingerprintSha256=None; RevocationReceiptSha256=None }
    let inspect record =
        inspectAttempt marker (Some (markerPort (Some marker)))
            (Some (nativePort nativeDescription marker (Some [record])))
    Assert.Equal(Ok NativeResultUnknown, inspect unknown)
    let vaulted =
        { unknown with Phase=TokenVaulted
                       TokenFingerprintSha256=Some (String.replicate 64 "b") }
    Assert.Equal(Ok NativeRevocationRequired, inspect vaulted)
    let revoked =
        { vaulted with Phase=NativeRevoked
                       RevocationReceiptSha256=Some (String.replicate 64 "c") }
    Assert.Equal(Ok ProtectedReceiptRequired, inspect revoked)

[<Fact>]
let ``protected native attempt recovery refuses omitted duplicate and foreign readback`` () =
    let f = fixture ()
    let marker = capturedMarker f
    let exact =
        { Request=marker; ProviderAttemptId=marker.NativeAttemptId
          VaultResourceId=handoffPins.VaultResourceId; Phase=InvocationUnknown
          TokenFingerprintSha256=None; RevocationReceiptSha256=None }
    let inspect readback =
        inspectAttempt marker (Some (markerPort (Some marker)))
            (Some (nativePort nativeDescription marker readback))
    Assert.Equal(Error "protected-census-attempt-unknown", inspect None)
    Assert.Equal(Error "protected-census-attempt-unknown", inspect (Some []))
    Assert.Equal(Error "protected-census-attempt-duplicate", inspect (Some [exact; exact]))
    Assert.Equal(Error "protected-census-attempt-binding",
                 inspect (Some [{ exact with ProviderAttemptId=String.replicate 64 "d" }]))
    Assert.Equal(Error "protected-census-attempt-phase",
                 inspect (Some [{ exact with Phase=TokenVaulted }]))

[<Fact>]
let ``protected native attempt recovery refuses candidate writable authority and missing marker`` () =
    let f = fixture ()
    let marker = capturedMarker f
    let mutable nativeReads = 0
    let empty = sealedSnapshot marker.NativeAttemptId true []
    let native description =
        { new IProtectedIssueCensusNativeAttemptPort with
            member _.Describe() = description
            member _.ReadHead() = Some empty.Head
            member _.ReadAttempts attemptId =
                nativeReads <- nativeReads + 1
                Some { empty with AttemptId=attemptId } }
    Assert.Equal(Error "protected-census-attempt-installation",
                 inspectAttempt marker (Some (markerPort (Some marker)))
                     (Some (native { nativeDescription with CandidateMayWrite=true })))
    Assert.Equal(0, nativeReads)
    Assert.Equal(Error "protected-census-attempt-unknown",
                 inspectAttempt marker (Some (markerPort None))
                     (Some (native nativeDescription)))
    Assert.Equal(0, nativeReads)

[<Fact>]
let ``protected native attempt recovery refuses incomplete or stale sealed snapshot`` () =
    let f = fixture ()
    let marker = capturedMarker f
    let record =
        { Request=marker; ProviderAttemptId=marker.NativeAttemptId
          VaultResourceId=handoffPins.VaultResourceId; Phase=InvocationUnknown
          TokenFingerprintSha256=None; RevocationReceiptSha256=None }
    let exact = sealedSnapshot marker.NativeAttemptId true [record]
    let inspect snapshot before after =
        let mutable reads = 0
        let native =
            { new IProtectedIssueCensusNativeAttemptPort with
                member _.Describe() = nativeDescription
                member _.ReadHead() =
                    reads <- reads + 1
                    if reads = 1 then before else after
                member _.ReadAttempts _ = Some snapshot }
        inspectAttempt marker (Some (markerPort (Some marker))) (Some native)
    Assert.Equal(Error "protected-census-attempt-incomplete",
                 inspect { exact with Complete=false } (Some exact.Head) (Some exact.Head))
    let stale = { exact.Head with Generation=6L }
    Assert.Equal(Error "protected-census-attempt-head",
                 inspect { exact with Head=stale } (Some exact.Head) (Some exact.Head))
    Assert.Equal(Error "protected-census-attempt-head",
                 inspect exact (Some exact.Head) (Some { exact.Head with Generation=8L }))
    Assert.Equal(Error "protected-census-attempt-unknown",
                 inspect exact None (Some exact.Head))
    let tamperedRecord =
        { record with Phase=TokenVaulted
                      TokenFingerprintSha256=Some (String.replicate 64 "b") }
    Assert.Equal(Error "protected-census-attempt-seal",
                 inspect { exact with Records=[tamperedRecord] }
                         (Some exact.Head) (Some exact.Head))

[<Fact>]
let ``protected native attempt recovery refuses marker lost or replaced during snapshot`` () =
    let f = fixture ()
    let marker = capturedMarker f
    let record =
        { Request=marker; ProviderAttemptId=marker.NativeAttemptId
          VaultResourceId=handoffPins.VaultResourceId; Phase=InvocationUnknown
          TokenFingerprintSha256=None; RevocationReceiptSha256=None }
    let inspect after =
        let snapshot = sealedSnapshot marker.NativeAttemptId true [record]
        let mutable currentMarker = Some marker
        let mutable markerReads = 0
        let handoff =
            { new IProtectedIssueCensusHandoffPort with
                member _.Describe() = handoffDescription
                member _.MarkOnce _ = failwith "recovery must not mark again"
                member _.ReadMarker _ =
                    markerReads <- markerReads + 1
                    currentMarker }
        let native =
            { new IProtectedIssueCensusNativeAttemptPort with
                member _.Describe() = nativeDescription
                member _.ReadHead() = Some snapshot.Head
                member _.ReadAttempts _ =
                    currentMarker <- after
                    Some snapshot }
        let result =
            inspectAttempt marker (Some handoff) (Some native)
        result, markerReads
    Assert.Equal((Error "protected-census-attempt-unknown", 2), inspect None)
    let foreign = { marker with ClaimId=String.replicate 64 "0" }
    Assert.Equal((Error "protected-census-attempt-binding", 2), inspect (Some foreign))

[<Fact>]
let ``protected native attempt recovery refuses claim substituted under one reservation`` () =
    let f = fixture ()
    let marker = capturedMarker f
    let foreign = { marker with ClaimId=String.replicate 64 "0" }
    let record =
        { Request=foreign; ProviderAttemptId=foreign.NativeAttemptId
          VaultResourceId=handoffPins.VaultResourceId; Phase=InvocationUnknown
          TokenFingerprintSha256=None; RevocationReceiptSha256=None }
    Assert.Equal(Error "protected-census-attempt-binding",
                 inspectAttempt foreign (Some (markerPort (Some foreign)))
                     (Some (nativePort nativeDescription foreign (Some [record]))))

[<Fact>]
let ``protected native attempt recovery refuses reservation substituted under one claim`` () =
    let f = fixture ()
    let marker = capturedMarker f
    let reservation = String.replicate 64 "0"
    let foreign =
        { marker with ReservationId=reservation
                      NativeAttemptId=MigrationProtectedIssueCensusHandoff.attemptId
                                          reservation handoffPins }
    let record =
        { Request=foreign; ProviderAttemptId=foreign.NativeAttemptId
          VaultResourceId=handoffPins.VaultResourceId; Phase=InvocationUnknown
          TokenFingerprintSha256=None; RevocationReceiptSha256=None }
    Assert.Equal(Error "protected-census-attempt-binding",
                 inspectAttempt foreign (Some (markerPort (Some foreign)))
                     (Some (nativePort nativeDescription foreign (Some [record]))))
