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
