module FS.GG.Coordination.GitHubRollbackReadbackTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubRollbackPlanQualification
open FS.GG.Coordination.Qualification.Contracts.GitHubRollbackReadbackQualification
open FS.GG.Coordination.Qualification.Contracts.GitHubRollbackReadbackProvenance
open FS.GG.Coordination.Qualification.Contracts.GitHubRollbackReadbackSignature

let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private digest character = String.replicate 64 character
let private revision character = String.replicate 40 character
let private get = function Ok value -> value | Error failures -> failwithf "unexpected refusal: %A" failures
let private refusal = function Error failures -> failures | Ok _ -> failwith "unqualified readback passed"

let private steps =
    [ 5, AuthoritySnapshot, "authority"; 4, Schedule, "schedules"
      3, V1Projection, "v1-projections"; 2, ReceiverPin, "receivers"; 1, Settings, "settings" ]
    |> List.map (fun (order, domain, name) ->
        { Order=order; StepId=$"restore-{name}"; Domain=domain; TargetIdentity=$"target:{name}"
          CapturedStateSha256=sha $"captured:{name}"; RestorePayloadSha256=sha $"payload:{name}" })

let private plan identity =
    qualify identity (revision "a") (digest "b") (digest "c") (digest "d")
        (digest "e") (digest "f") (digest "1") (digest "2") "VerifiedV2" steps
        (DateTimeOffset.Parse "2026-09-23T10:00:00Z") |> get

let private receipts (selected: GitHubRollbackPlan) wrongFirst =
    ([], selected.Steps)
    ||> List.fold (fun previous step ->
        let result = if step.Order = 5 && wrongFirst then sha "foreign-result" else step.CapturedStateSha256
        previous @ [ createReceipt selected (List.tryLast previous) step result ])

let private readbacks (selected: GitHubRollbackPlan) : GitHubRollbackReadbackClaim list =
    selected.Steps
    |> List.map (fun step ->
        { Order=step.Order; StepId=step.StepId; Domain=step.Domain
          TargetIdentity=step.TargetIdentity; StateSha256=step.CapturedStateSha256
          Complete=true; Authorized=true })

let private epoch seal : GitHubRollbackEpochClaim =
    { Phase="OperatingV1"; PlanSeal=seal; Complete=true; Authorized=true }

[<Fact>]
let ``complete five-domain native claims match pinned plan receipts and terminal epoch`` () =
    let selected = plan "accepted-rollback"
    Assert.Equal(Ok(), verifyClaims selected.Seal selected (receipts selected false)
                         (readbacks selected) (epoch selected.Seal))

[<Fact>]
let ``foreign plan and incomplete receipt prefix refuse before readback can qualify`` () =
    let selected = plan "accepted-rollback"
    let foreign = plan "foreign-rollback"
    Assert.Equal(Error [ PlanOrReceiptInvalid [ AlteredSeal ] ],
                 verifyClaims selected.Seal foreign (receipts foreign false)
                              (readbacks foreign) (epoch selected.Seal))
    Assert.Equal(Error [ IncompleteReceiptPopulation ],
                 verifyClaims selected.Seal selected ((receipts selected false) |> List.take 4)
                              (readbacks selected) (epoch selected.Seal))

[<Fact>]
let ``missing duplicate and foreign native subjects refuse`` () =
    let selected = plan "accepted-rollback"
    let proof = readbacks selected
    let completed = receipts selected false
    Assert.Equal(Error [ ReadbackPopulationMismatch ],
                 verifyClaims selected.Seal selected completed (proof |> List.take 4) (epoch selected.Seal))
    Assert.Contains(ReadbackMismatch selected.Steps[1].StepId,
                    verifyClaims selected.Seal selected completed (proof[0] :: proof[0] :: (proof |> List.skip 2))
                                 (epoch selected.Seal) |> refusal)
    let foreign = { proof[2] with TargetIdentity="target:foreign" }
    Assert.Contains(ReadbackMismatch selected.Steps[2].StepId,
                    verifyClaims selected.Seal selected completed (proof |> List.updateAt 2 foreign)
                                 (epoch selected.Seal) |> refusal)

[<Fact>]
let ``changed state forged receipt and untrusted or stale epoch refuse`` () =
    let selected = plan "accepted-rollback"
    let proof = readbacks selected
    let completed = receipts selected false
    let changed = { proof[3] with StateSha256=sha "changed-state" }
    Assert.Contains(ReadbackMismatch selected.Steps[3].StepId,
                    verifyClaims selected.Seal selected completed (proof |> List.updateAt 3 changed)
                                 (epoch selected.Seal) |> refusal)
    Assert.Contains(ReadbackMismatch selected.Steps[0].StepId,
                    verifyClaims selected.Seal selected (receipts selected true) proof
                                 (epoch selected.Seal) |> refusal)
    let unauthorized = { proof[4] with Authorized=false }
    Assert.Contains(ReadbackMismatch selected.Steps[4].StepId,
                    verifyClaims selected.Seal selected completed (proof |> List.updateAt 4 unauthorized)
                                 (epoch selected.Seal) |> refusal)
    let incomplete = { proof[0] with Complete=false }
    Assert.Contains(ReadbackMismatch selected.Steps[0].StepId,
                    verifyClaims selected.Seal selected completed (proof |> List.updateAt 0 incomplete)
                                 (epoch selected.Seal) |> refusal)
    Assert.Equal(Error [ TerminalEpochMismatch ],
                 verifyClaims selected.Seal selected completed proof { epoch selected.Seal with Phase="OpenV2" })
    Assert.Equal(Error [ TerminalEpochMismatch ],
                 verifyClaims selected.Seal selected completed proof { epoch selected.Seal with PlanSeal=sha "foreign-plan" })

let private binding seal : GitHubRollbackExpectedReadbackBinding =
    { PlanSeal=seal; RunNonce="run-812-attempt-1"; Challenge=sha "protected-read-challenge"
      ObserverResourceId="observer:registered-sandbox" }

let private provenance (selected: GitHubRollbackPlan) (completed: GitHubRollbackReceipt list) =
    List.map2 (fun (claim: GitHubRollbackReadbackClaim) (receipt: GitHubRollbackReceipt) ->
        { Readback=claim; PlanSeal=selected.Seal; RunNonce="run-812-attempt-1"
          Challenge=sha "protected-read-challenge"; ObserverResourceId="observer:registered-sandbox"
          AfterReceiptSha256=receipt.ReceiptSha256; NativeRevision=$"revision:{claim.Order}" })
        (readbacks selected) completed

let private terminal seal (completed: GitHubRollbackReceipt list) : GitHubRollbackEpochProvenanceClaim =
    { Epoch=epoch seal; RunNonce="run-812-attempt-1"; Challenge=sha "protected-read-challenge"
      ObserverResourceId="observer:registered-sandbox"
      AfterReceiptSha256=(List.last completed).ReceiptSha256; NativeRevision="epoch-revision:9" }

[<Fact>]
let ``Q6 provenance claims bind every restored target to its own receipt and terminal epoch`` () =
    let selected = plan "accepted-rollback"
    let completed = receipts selected false
    Assert.Equal(Ok(), verifyProvenance (binding selected.Seal) selected completed
                     (provenance selected completed) (terminal selected.Seal completed))

[<Fact>]
let ``stale or foreign receipt and observer claims refuse despite matching state digests`` () =
    let selected = plan "accepted-rollback"
    let completed = receipts selected false
    let claims = provenance selected completed
    let stale = { claims[2] with AfterReceiptSha256=completed[1].ReceiptSha256 }
    Assert.Contains(StepProvenanceMismatch selected.Steps[2].StepId,
                    verifyProvenance (binding selected.Seal) selected completed
                        (claims |> List.updateAt 2 stale) (terminal selected.Seal completed) |> refusal)
    let foreign = { claims[3] with ObserverResourceId="observer:foreign" }
    Assert.Contains(StepProvenanceMismatch selected.Steps[3].StepId,
                    verifyProvenance (binding selected.Seal) selected completed
                        (claims |> List.updateAt 3 foreign) (terminal selected.Seal completed) |> refusal)
    let wrongChallenge = { claims[0] with Challenge=sha "old-challenge" }
    Assert.Contains(StepProvenanceMismatch selected.Steps[0].StepId,
                    verifyProvenance (binding selected.Seal) selected completed
                        (claims |> List.updateAt 0 wrongChallenge) (terminal selected.Seal completed) |> refusal)
    let missingRevision = { claims[1] with NativeRevision="" }
    Assert.Contains(StepProvenanceMismatch selected.Steps[1].StepId,
                    verifyProvenance (binding selected.Seal) selected completed
                        (claims |> List.updateAt 1 missingRevision) (terminal selected.Seal completed) |> refusal)

[<Fact>]
let ``terminal epoch provenance and protected binding omissions refuse`` () =
    let selected = plan "accepted-rollback"
    let completed = receipts selected false
    let claims = provenance selected completed
    let staleEpoch = { terminal selected.Seal completed with AfterReceiptSha256=completed[3].ReceiptSha256 }
    Assert.Equal(Error [ EpochProvenanceMismatch ],
                 verifyProvenance (binding selected.Seal) selected completed claims staleEpoch)
    let foreignRun = { terminal selected.Seal completed with RunNonce="foreign-run" }
    Assert.Equal(Error [ EpochProvenanceMismatch ],
                 verifyProvenance (binding selected.Seal) selected completed claims foreignRun)
    let missingResource = { binding selected.Seal with ObserverResourceId="" }
    Assert.Equal(Error [ InvalidExpectedReadbackBinding ],
                 verifyProvenance missingResource selected completed claims (terminal selected.Seal completed))
    let missingEpochRevision = { terminal selected.Seal completed with NativeRevision="" }
    Assert.Equal(Error [ EpochProvenanceMismatch ],
                 verifyProvenance (binding selected.Seal) selected completed claims missingEpochRevision)

[<Fact>]
let ``signed Q6 readback batch requires the independently pinned observer key`` () =
    let selected = plan "accepted-rollback"
    let completed = receipts selected false
    let claims = provenance selected completed
    let finalEpoch = terminal selected.Seal completed
    let expected = binding selected.Seal
    use signer = RSA.Create()
    signer.KeySize <- 3072
    let publicKey = signer.ExportSubjectPublicKeyInfo()
    let pin = publicKey |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let payload = payloadForSigning expected selected completed claims finalEpoch |> get
    let signature = signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
    Assert.Equal(Ok(), verifySigned pin publicKey signature expected selected completed claims finalEpoch)
    Assert.Equal(Error [ MissingOrInvalidObserverPin ],
                 verifySigned "" publicKey signature expected selected completed claims finalEpoch)
    Assert.Equal(Error [ ObserverFingerprintMismatch ],
                 verifySigned (sha "foreign-key") publicKey signature expected selected completed claims finalEpoch)
    Assert.Equal(Error [ MissingOrInvalidObserverPin ],
                 verifySigned (pin.ToUpperInvariant()) publicKey signature expected selected completed claims finalEpoch)
    let replayedRun = { expected with RunNonce="run-812-attempt-2" }
    Assert.Equal(Error [ ProvenanceInvalid [ StepProvenanceMismatch selected.Steps[0].StepId
                                             StepProvenanceMismatch selected.Steps[1].StepId
                                             StepProvenanceMismatch selected.Steps[2].StepId
                                             StepProvenanceMismatch selected.Steps[3].StepId
                                             StepProvenanceMismatch selected.Steps[4].StepId
                                             EpochProvenanceMismatch ] ],
                 verifySigned pin publicKey signature replayedRun selected completed claims finalEpoch)

[<Fact>]
let ``signed Q6 readback refuses altered native revision and foreign signer`` () =
    let selected = plan "accepted-rollback"
    let completed = receipts selected false
    let claims = provenance selected completed
    let finalEpoch = terminal selected.Seal completed
    let expected = binding selected.Seal
    use signer = RSA.Create()
    signer.KeySize <- 3072
    let publicKey = signer.ExportSubjectPublicKeyInfo()
    let pin = publicKey |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let payload = payloadForSigning expected selected completed claims finalEpoch |> get
    let signature = signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
    let changed = { claims[2] with NativeRevision="revision:foreign" }
    Assert.Equal(Error [ InvalidObserverSignature ],
                 verifySigned pin publicKey signature expected selected completed
                     (claims |> List.updateAt 2 changed) finalEpoch)
    let changedEpoch = { finalEpoch with NativeRevision="epoch-revision:foreign" }
    Assert.Equal(Error [ InvalidObserverSignature ],
                 verifySigned pin publicKey signature expected selected completed claims changedEpoch)
    use foreign = RSA.Create()
    foreign.KeySize <- 3072
    let foreignPublicKey = foreign.ExportSubjectPublicKeyInfo()
    let foreignPin = foreignPublicKey |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    Assert.Equal(Error [ InvalidObserverSignature ],
                 verifySigned foreignPin foreignPublicKey signature expected selected completed claims finalEpoch)

let private orderBinding : GitHubRollbackExpectedOrderBinding =
    { SandboxResourceId="sandbox:qualified-812"; EpochResourceId="epoch:protected-v1"
      WitnessResourceId="witness:protected-journal"; WitnessGeneration=27L }

let private orderWitness (selected: GitHubRollbackPlan) (completed: GitHubRollbackReceipt list) : GitHubRollbackNativeOrderWitness =
    let events =
        (selected.Steps, completed)
        ||> List.map2 (fun step receipt ->
            let ordinal = int64 (6 - step.Order)
            { StepId=step.StepId; TargetIdentity=step.TargetIdentity
              ReceiptSha256=receipt.ReceiptSha256
              ReceiptCommitOrdinal=ordinal * 10L + 1L
              NativeReadOrdinal=ordinal * 10L + 2L
              NativeStateSha256=step.CapturedStateSha256 })
    { SandboxResourceId=orderBinding.SandboxResourceId
      WitnessResourceId=orderBinding.WitnessResourceId
      WitnessGeneration=orderBinding.WitnessGeneration
      Steps=events
      Terminal={ EpochResourceId=orderBinding.EpochResourceId
                 ReceiptSha256=(List.last completed).ReceiptSha256
                 NativeReadOrdinal=100L; Phase="OperatingV1"; PlanSeal=selected.Seal } }

[<Fact>]
let ``signed native order witness binds exact sandbox epoch and protected generation`` () =
    let selected = plan "accepted-rollback"
    let completed = receipts selected false
    let claims = provenance selected completed
    let finalEpoch = terminal selected.Seal completed
    let expected = binding selected.Seal
    let witness = orderWitness selected completed
    use signer = RSA.Create()
    signer.KeySize <- 3072
    let publicKey = signer.ExportSubjectPublicKeyInfo()
    let pin = publicKey |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let payload = payloadForOrderedSigning orderBinding witness expected selected completed claims finalEpoch |> get
    let signature = signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
    Assert.Equal(Ok(), verifySignedOrdered pin publicKey signature orderBinding witness expected selected completed claims finalEpoch)
    Assert.Equal(Error [ OrderWitnessMismatch ],
                 verifySignedOrdered pin publicKey signature
                     { orderBinding with SandboxResourceId="sandbox:foreign" }
                     witness expected selected completed claims finalEpoch)
    Assert.Equal(Error [ OrderWitnessMismatch ],
                 verifySignedOrdered pin publicKey signature
                     { orderBinding with WitnessGeneration=28L }
                     witness expected selected completed claims finalEpoch)
    Assert.Equal(Error [ OrderWitnessMismatch ],
                 verifySignedOrdered pin publicKey signature
                     { orderBinding with EpochResourceId="epoch:foreign" }
                     witness expected selected completed claims finalEpoch)
    Assert.Equal(Error [ InvalidExpectedOrderBinding ],
                 verifySignedOrdered pin publicKey signature
                     { orderBinding with WitnessResourceId="" }
                     witness expected selected completed claims finalEpoch)

[<Fact>]
let ``signed native order witness refuses crossed receipts duplicate and altered native reads`` () =
    let selected = plan "accepted-rollback"
    let completed = receipts selected false
    let claims = provenance selected completed
    let finalEpoch = terminal selected.Seal completed
    let expected = binding selected.Seal
    let witness = orderWitness selected completed
    Assert.Equal(Error [ OrderWitnessMismatch ],
                 payloadForOrderedSigning orderBinding
                     { witness with Steps=witness.Steps |> List.take 4 }
                     expected selected completed claims finalEpoch)
    Assert.Equal(Error [ OrderWitnessMismatch ],
                 payloadForOrderedSigning orderBinding
                     { witness with Steps=witness.Steps |> List.updateAt 4 witness.Steps[3] }
                     expected selected completed claims finalEpoch)
    let crossed = { witness.Steps[1] with ReceiptCommitOrdinal=witness.Steps[0].NativeReadOrdinal }
    Assert.Equal(Error [ InvalidObservationOrder ],
                 payloadForOrderedSigning orderBinding
                     { witness with Steps=witness.Steps |> List.updateAt 1 crossed }
                     expected selected completed claims finalEpoch)
    let beforeCommit = { witness.Steps[2] with NativeReadOrdinal=witness.Steps[2].ReceiptCommitOrdinal }
    Assert.Equal(Error [ InvalidObservationOrder ],
                 payloadForOrderedSigning orderBinding
                     { witness with Steps=witness.Steps |> List.updateAt 2 beforeCommit }
                     expected selected completed claims finalEpoch)
    Assert.Equal(Error [ InvalidObservationOrder ],
                 payloadForOrderedSigning orderBinding
                     { witness with Terminal={ witness.Terminal with
                                                  NativeReadOrdinal=witness.Steps[4].NativeReadOrdinal } }
                     expected selected completed claims finalEpoch)
    let foreignTarget = { witness.Steps[3] with TargetIdentity="target:foreign" }
    Assert.Equal(Error [ OrderWitnessMismatch ],
                 payloadForOrderedSigning orderBinding
                     { witness with Steps=witness.Steps |> List.updateAt 3 foreignTarget }
                     expected selected completed claims finalEpoch)
    use signer = RSA.Create()
    signer.KeySize <- 3072
    let publicKey = signer.ExportSubjectPublicKeyInfo()
    let pin = publicKey |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let payload = payloadForOrderedSigning orderBinding witness expected selected completed claims finalEpoch |> get
    let signature = signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
    let altered = { witness.Steps[0] with NativeReadOrdinal=13L }
    Assert.Equal(Error [ InvalidObserverSignature ],
                 verifySignedOrdered pin publicKey signature orderBinding
                     { witness with Steps=witness.Steps |> List.updateAt 0 altered }
                     expected selected completed claims finalEpoch)
    let relabeled = { witness.Steps[4] with NativeStateSha256=sha "altered-state" }
    Assert.Equal(Error [ OrderWitnessMismatch ],
                 verifySignedOrdered pin publicKey signature orderBinding
                     { witness with Steps=witness.Steps |> List.updateAt 4 relabeled }
                     expected selected completed claims finalEpoch)
