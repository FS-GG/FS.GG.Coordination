module FS.GG.Coordination.MigrationProtectedIssueCensusNativeAttestationTests

open System
open System.Security.Cryptography
open Xunit
open FS.GG.Coordination.Cli

let private digest (bytes: byte[]) =
    bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private clock resource artifact now =
    { new IProtectedIssueCensusClockPort with
        member _.Describe() =
            { ClockResourceId=resource; ClockArtifactSha256=artifact
              CandidateMayRead=false; CandidateMayWrite=false; MonotonicUtc=true }
        member _.ReadNow() = Some now }

let private handoffPins =
    { HandoffResourceId="protected-handoff:native-test"
      HandoffArtifactSha256=String.replicate 64 "8"
      VaultResourceId="protected-vault:native-test"
      VaultArtifactSha256=String.replicate 64 "9"
      NativeAttemptNamespaceId="protected-attempts:native-test"
      AppId=101L; InstallationId=202L; RepositoryId=42L
      PermissionSha256=String.replicate 64 "4" }

let private fixture () =
    use signer = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let publicBytes = signer.ExportSubjectPublicKeyInfo()
    let pins =
        { SignerPublicKeySpkiBase64=Convert.ToBase64String publicBytes
          SignerPublicKeySha256=digest publicBytes
          SignerArtifactSha256=String.replicate 64 "a"
          ClockResourceId="protected-clock:native-test"
          ClockArtifactSha256=String.replicate 64 "b"
          NativeAttemptResourceId="protected-native-attempts:native-test"
          NativeAttemptArtifactSha256=String.replicate 64 "c"
          MaximumAgeSeconds=60 }
    let issued = DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)
    let selection =
        { RunId=301L; RunAttempt=4; RunNonce="native-attestation-nonce"
          CandidateSha=String.replicate 40 "d"; WorkflowSha=String.replicate 40 "e"
          ApiOrigin="https://api.github.test"; Owner="FS-GG"; Repository="copy"
          RepositoryId=42L }
    let marker =
        { NativeAttemptId=MigrationProtectedIssueCensusHandoff.attemptId
                            (String.replicate 64 "2") handoffPins
          ReservationId=String.replicate 64 "2"
          ClaimId=String.replicate 64 "3"
          Selection=selection; AppId=handoffPins.AppId
          InstallationId=handoffPins.InstallationId
          RepositoryId=selection.RepositoryId
          PermissionSha256=handoffPins.PermissionSha256
          VaultResourceId=handoffPins.VaultResourceId
          ExpectedStoreHeadSha256=String.replicate 64 "5"
          ExpectedJournalHeadSha256=String.replicate 64 "6"
          ClockResourceId=pins.ClockResourceId
          ClockArtifactSha256=pins.ClockArtifactSha256
          SignedExpiresAtUtc=issued.AddSeconds 60 }
    let record =
        { Request=marker; ProviderAttemptId=marker.NativeAttemptId
          VaultResourceId=marker.VaultResourceId; Phase=TokenVaulted
          TokenFingerprintSha256=Some (String.replicate 64 "7")
          RevocationReceiptSha256=None }
    let unsignedSnapshot =
        { Head={ AttemptResourceId=pins.NativeAttemptResourceId
                 Generation=7L; SealSha256="" }
          AttemptId=marker.NativeAttemptId; Complete=true; Records=[record] }
    let snapshotSeal =
        MigrationProtectedIssueCensusAttemptRecovery.expectedSnapshotSealSha256
            unsignedSnapshot
    let snapshotHead = { unsignedSnapshot.Head with SealSha256=snapshotSeal }
    let snapshot = { unsignedSnapshot with Head=snapshotHead }
    let unsignedAttestation =
        { AttemptId=marker.NativeAttemptId
          NativeAttemptResourceId=pins.NativeAttemptResourceId
          HeadGeneration=snapshot.Head.Generation
          SnapshotSealSha256=snapshot.Head.SealSha256
          IssuedAtUtc=issued; ExpiresAtUtc=issued.AddSeconds 60
          SignatureBase64="" }
    let signature =
        signer.SignData(
            MigrationProtectedIssueCensusNativeAttestation.signingPayload
                pins unsignedAttestation,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    let signed =
        { unsignedAttestation with SignatureBase64=Convert.ToBase64String signature }
    pins, marker, snapshot, signed, issued.AddSeconds 10

let private verify pins marker snapshot attestation now =
    MigrationProtectedIssueCensusNativeAttestation.verify
        pins marker snapshot attestation
        (Some (clock pins.ClockResourceId pins.ClockArtifactSha256 now))

let private inspectSigned (pins: ProtectedIssueCensusNativeAttestationPins)
                          (marker: ProtectedIssueCensusHandoffRequest)
                          (snapshot: ProtectedIssueCensusNativeAttemptSnapshot)
                          (attestation: ProtectedIssueCensusNativeSnapshotAttestation option)
                          (now: DateTimeOffset) =
    let nativePins: ProtectedIssueCensusNativeAttemptPins =
        { AttemptResourceId=pins.NativeAttemptResourceId
          AttemptArtifactSha256=pins.NativeAttemptArtifactSha256 }
    let handoff =
        { new IProtectedIssueCensusHandoffPort with
            member _.Describe() =
                { HandoffResourceId=handoffPins.HandoffResourceId
                  HandoffArtifactSha256=handoffPins.HandoffArtifactSha256
                  VaultResourceId=handoffPins.VaultResourceId
                  VaultArtifactSha256=handoffPins.VaultArtifactSha256
                  NativeAttemptNamespaceId=handoffPins.NativeAttemptNamespaceId
                  ReleaseResourceId="protected-release:native-test"
                  ReleaseArtifactSha256=String.replicate 64 "a"
                  ClockResourceId=pins.ClockResourceId
                  ClockArtifactSha256=pins.ClockArtifactSha256
                  AppId=handoffPins.AppId
                  InstallationId=handoffPins.InstallationId
                  RepositoryId=handoffPins.RepositoryId
                  PermissionSha256=handoffPins.PermissionSha256
                  CandidateMayRead=false; CandidateMayWrite=false
                  AtomicReservationConsumeAndMark=true; AtomicExpiryCompare=true }
            member _.MarkOnce _ = failwith "signed recovery must not mark again"
            member _.ReadMarker _ = Some marker }
    let native =
        { new IProtectedIssueCensusNativeAttemptPort with
            member _.Describe() =
                { AttemptResourceId=nativePins.AttemptResourceId
                  AttemptArtifactSha256=nativePins.AttemptArtifactSha256
                  NativeAttemptNamespaceId=handoffPins.NativeAttemptNamespaceId
                  VaultResourceId=handoffPins.VaultResourceId
                  VaultArtifactSha256=handoffPins.VaultArtifactSha256
                  CandidateMayRead=false; CandidateMayWrite=false
                  AuthoritativeCompleteReadback=true }
            member _.ReadHead() = Some snapshot.Head
            member _.ReadAttempts _ = Some snapshot }
    MigrationProtectedIssueCensusSignedRecovery.inspectSigned
        pins attestation
        (Some (clock pins.ClockResourceId pins.ClockArtifactSha256 now))
        handoffPins nativePins marker (Some handoff) (Some native)

[<Fact>]
let ``native snapshot verifier accepts exact fake signed complete snapshot`` () =
    let pins, marker, snapshot, attestation, now = fixture ()
    Assert.Equal(Ok (), verify pins marker snapshot (Some attestation) now)

[<Fact>]
let ``native snapshot verifier refuses forged stale and foreign evidence`` () =
    let pins, marker, snapshot, attestation, now = fixture ()
    Assert.Equal(Error "protected-native-attestation-unavailable",
                 verify pins marker snapshot None now)
    Assert.Equal(Error "protected-native-attestation-binding",
                 verify pins marker snapshot
                     (Some { attestation with AttemptId=String.replicate 64 "9" }) now)
    Assert.Equal(Error "protected-native-attestation-stale",
                 verify pins marker snapshot (Some attestation) (now.AddSeconds 120))
    let forgedSignature = Convert.ToBase64String(Array.zeroCreate<byte> 64)
    let forged = { attestation with SignatureBase64=forgedSignature }
    Assert.Equal(Error "protected-native-attestation-signature",
                 verify pins marker snapshot (Some forged) now)
    Assert.Equal(Error "protected-native-attestation-signature",
                 verify { pins with SignerArtifactSha256=String.replicate 64 "0" }
                     marker snapshot (Some attestation) now)
    let tamperedRecord =
        { snapshot.Records.Head with TokenFingerprintSha256=Some (String.replicate 64 "8") }
    Assert.Equal(Error "protected-native-attestation-snapshot",
                 verify pins marker { snapshot with Records=[tamperedRecord] }
                     (Some attestation) now)
    Assert.Equal(Error "protected-native-attestation-snapshot",
                 verify pins marker { snapshot with Complete=false } (Some attestation) now)

[<Fact>]
let ``native snapshot verifier refuses candidate clock and missing native pin`` () =
    let pins, marker, snapshot, attestation, now = fixture ()
    let candidateClock =
        { new IProtectedIssueCensusClockPort with
            member _.Describe() =
                { ClockResourceId=pins.ClockResourceId
                  ClockArtifactSha256=pins.ClockArtifactSha256
                  CandidateMayRead=false; CandidateMayWrite=true; MonotonicUtc=true }
            member _.ReadNow() = Some now }
    Assert.Equal(Error "protected-native-clock-installation",
                 MigrationProtectedIssueCensusNativeAttestation.verify
                     pins marker snapshot (Some attestation) (Some candidateClock))
    Assert.Equal(Error "protected-native-attestation-pins",
                 verify { pins with NativeAttemptArtifactSha256="" }
                     marker snapshot (Some attestation) now)

[<Fact>]
let ``signed recovery qualifies exactly the snapshot it classifies`` () =
    let pins, marker, snapshot, attestation, now = fixture ()
    Assert.Equal(Ok NativeRevocationRequired,
                 inspectSigned pins marker snapshot (Some attestation) now)
    let forgedSignature = Convert.ToBase64String(Array.zeroCreate<byte> 64)
    let forged = { attestation with SignatureBase64=forgedSignature }
    Assert.Equal(Error "protected-native-attestation-signature",
                 inspectSigned pins marker snapshot (Some forged) now)
    let changedRecord =
        { snapshot.Records.Head with TokenFingerprintSha256=Some (String.replicate 64 "0") }
    let unsignedHead = { snapshot.Head with SealSha256="" }
    let unsignedChanged = { snapshot with Head=unsignedHead; Records=[changedRecord] }
    let changedSeal =
        MigrationProtectedIssueCensusAttemptRecovery.expectedSnapshotSealSha256
            unsignedChanged
    let changedHead = { unsignedChanged.Head with SealSha256=changedSeal }
    let changed = { unsignedChanged with Head=changedHead }
    Assert.Equal(Error "protected-native-attestation-binding",
                 inspectSigned pins marker changed (Some attestation) now)
