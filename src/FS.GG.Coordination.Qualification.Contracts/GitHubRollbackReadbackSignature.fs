namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type GitHubRollbackSignatureFailure =
    | ProvenanceInvalid of GitHubRollbackProvenanceFailure list
    | MissingOrInvalidObserverPin
    | ObserverFingerprintMismatch
    | InvalidObserverSignature
    | InvalidExpectedOrderBinding
    | OrderWitnessMismatch
    | InvalidObservationOrder

type GitHubRollbackExpectedOrderBinding =
    { SandboxResourceId: string
      EpochResourceId: string
      WitnessResourceId: string
      WitnessGeneration: int64 }

type GitHubRollbackStepOrderWitness =
    { StepId: string
      TargetIdentity: string
      ReceiptSha256: string
      ReceiptCommitOrdinal: int64
      NativeReadOrdinal: int64
      NativeStateSha256: string }

type GitHubRollbackTerminalOrderWitness =
    { EpochResourceId: string
      ReceiptSha256: string
      NativeReadOrdinal: int64
      Phase: string
      PlanSeal: string }

type GitHubRollbackNativeOrderWitness =
    { SandboxResourceId: string
      WitnessResourceId: string
      WitnessGeneration: int64
      Steps: GitHubRollbackStepOrderWitness list
      Terminal: GitHubRollbackTerminalOrderWitness }

module GitHubRollbackReadbackSignature =
    let private exactAtom (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && (value |> Seq.forall (fun ch -> not (Char.IsControl ch)))

    let private domainName = function
        | Settings -> "settings"
        | ReceiverPin -> "receiver-pin"
        | V1Projection -> "v1-projection"
        | Schedule -> "schedule"
        | AuthoritySnapshot -> "authority-snapshot"

    let private atom (writer: BinaryWriter) (value: string) =
        let bytes = Encoding.UTF8.GetBytes(value)
        writer.Write(bytes.Length)
        writer.Write(bytes)

    let private integer (writer: BinaryWriter) value = writer.Write(value: int)
    let private flag (writer: BinaryWriter) value = writer.Write(value: bool)

    let payloadForSigning (expected: GitHubRollbackExpectedReadbackBinding) (plan: GitHubRollbackPlan)
        (receipts: GitHubRollbackReceipt list) (claims: GitHubRollbackStepProvenanceClaim list)
        (terminal: GitHubRollbackEpochProvenanceClaim) =
        match GitHubRollbackReadbackProvenance.verifyProvenance expected plan receipts claims terminal with
        | Error failures -> Error [ ProvenanceInvalid failures ]
        | Ok() ->
            use stream = new MemoryStream()
            use writer = new BinaryWriter(stream, Encoding.UTF8, true)
            atom writer "fsgg.gs2-09.7.q6-observer-batch/v1"
            atom writer expected.PlanSeal
            atom writer expected.RunNonce
            atom writer expected.Challenge
            atom writer expected.ObserverResourceId
            atom writer plan.NormalizedDigest
            atom writer plan.Seal
            integer writer receipts.Length
            for receipt in receipts do
                integer writer receipt.Order
                atom writer receipt.StepId
                atom writer receipt.PlanSeal
                atom writer (receipt.PreviousReceiptSha256 |> Option.defaultValue "")
                atom writer receipt.ResultSha256
                atom writer receipt.ReceiptSha256
            integer writer claims.Length
            for claim in claims do
                let readback = claim.Readback
                integer writer readback.Order
                atom writer readback.StepId
                atom writer (domainName readback.Domain)
                atom writer readback.TargetIdentity
                atom writer readback.StateSha256
                flag writer readback.Complete
                flag writer readback.Authorized
                atom writer claim.PlanSeal
                atom writer claim.RunNonce
                atom writer claim.Challenge
                atom writer claim.ObserverResourceId
                atom writer claim.AfterReceiptSha256
                atom writer claim.NativeRevision
            atom writer terminal.Epoch.Phase
            atom writer terminal.Epoch.PlanSeal
            flag writer terminal.Epoch.Complete
            flag writer terminal.Epoch.Authorized
            atom writer terminal.RunNonce
            atom writer terminal.Challenge
            atom writer terminal.ObserverResourceId
            atom writer terminal.AfterReceiptSha256
            atom writer terminal.NativeRevision
            writer.Flush()
            Ok(stream.ToArray())

    let private verifyPinnedPayload pinnedSpkiSha256 (publicKeySpki: byte[]) (signature: byte[])
        (payloadFactory: unit -> Result<byte[], GitHubRollbackSignatureFailure list>) =
        if isNull pinnedSpkiSha256
           || not (Regex.IsMatch(pinnedSpkiSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)) then
            Error [ MissingOrInvalidObserverPin ]
        elif isNull publicKeySpki || publicKeySpki.Length = 0 then
            Error [ InvalidObserverSignature ]
        elif (SHA256.HashData(publicKeySpki) |> Convert.ToHexString).ToLowerInvariant() <> pinnedSpkiSha256 then
            Error [ ObserverFingerprintMismatch ]
        elif isNull signature || signature.Length = 0 then
            Error [ InvalidObserverSignature ]
        else
            match payloadFactory() with
            | Error failures -> Error failures
            | Ok payload ->
                try
                    use rsa = RSA.Create()
                    let mutable bytesRead = 0
                    rsa.ImportSubjectPublicKeyInfo(ReadOnlySpan<byte>(publicKeySpki), &bytesRead)
                    if bytesRead <> publicKeySpki.Length || rsa.KeySize < 3072
                       || not (rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) then
                        Error [ InvalidObserverSignature ]
                    else Ok()
                with
                | :? CryptographicException -> Error [ InvalidObserverSignature ]
                | :? ArgumentException -> Error [ InvalidObserverSignature ]

    let verifySigned pinnedSpkiSha256 (publicKeySpki: byte[]) (signature: byte[])
        (expected: GitHubRollbackExpectedReadbackBinding) (plan: GitHubRollbackPlan)
        (receipts: GitHubRollbackReceipt list) (claims: GitHubRollbackStepProvenanceClaim list)
        (terminal: GitHubRollbackEpochProvenanceClaim) =
        verifyPinnedPayload pinnedSpkiSha256 publicKeySpki signature
            (fun () -> payloadForSigning expected plan receipts claims terminal)

    let payloadForOrderedSigning (expectedOrder: GitHubRollbackExpectedOrderBinding)
        (witness: GitHubRollbackNativeOrderWitness) (expected: GitHubRollbackExpectedReadbackBinding)
        (plan: GitHubRollbackPlan) (receipts: GitHubRollbackReceipt list)
        (claims: GitHubRollbackStepProvenanceClaim list) (terminal: GitHubRollbackEpochProvenanceClaim) =
        match payloadForSigning expected plan receipts claims terminal with
        | Error failures -> Error failures
        | Ok basePayload ->
            if not (exactAtom expectedOrder.SandboxResourceId
                    && exactAtom expectedOrder.EpochResourceId
                    && exactAtom expectedOrder.WitnessResourceId)
               || expectedOrder.WitnessGeneration <= 0L then
                Error [ InvalidExpectedOrderBinding ]
            elif witness.SandboxResourceId <> expectedOrder.SandboxResourceId
                 || witness.WitnessResourceId <> expectedOrder.WitnessResourceId
                 || witness.WitnessGeneration <> expectedOrder.WitnessGeneration
                 || witness.Steps.Length <> plan.Steps.Length
                 || witness.Terminal.EpochResourceId <> expectedOrder.EpochResourceId
                 || witness.Terminal.ReceiptSha256 <> (List.last receipts).ReceiptSha256
                 || witness.Terminal.Phase <> terminal.Epoch.Phase
                 || witness.Terminal.PlanSeal <> terminal.Epoch.PlanSeal
                 || (List.zip3 plan.Steps receipts witness.Steps
                     |> List.exists (fun (step, receipt, observed) ->
                         observed.StepId <> step.StepId
                         || observed.TargetIdentity <> step.TargetIdentity
                         || observed.ReceiptSha256 <> receipt.ReceiptSha256
                         || observed.NativeStateSha256 <> step.CapturedStateSha256)) then
                Error [ OrderWitnessMismatch ]
            else
                let mutable priorNativeRead = 0L
                let mutable invalidOrder = false
                for step in witness.Steps do
                    if step.ReceiptCommitOrdinal <= priorNativeRead
                       || step.NativeReadOrdinal <= step.ReceiptCommitOrdinal then
                        invalidOrder <- true
                    priorNativeRead <- step.NativeReadOrdinal
                if invalidOrder || witness.Terminal.NativeReadOrdinal <= priorNativeRead then
                    Error [ InvalidObservationOrder ]
                else
                    use stream = new MemoryStream()
                    use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                    atom writer "fsgg.gs2-09.7.q6-ordered-observer-batch/v1"
                    writer.Write(basePayload.Length)
                    writer.Write(basePayload)
                    atom writer expectedOrder.SandboxResourceId
                    atom writer expectedOrder.EpochResourceId
                    atom writer expectedOrder.WitnessResourceId
                    writer.Write(expectedOrder.WitnessGeneration)
                    atom writer witness.SandboxResourceId
                    atom writer witness.WitnessResourceId
                    writer.Write(witness.WitnessGeneration)
                    integer writer witness.Steps.Length
                    for step in witness.Steps do
                        atom writer step.StepId
                        atom writer step.TargetIdentity
                        atom writer step.ReceiptSha256
                        writer.Write(step.ReceiptCommitOrdinal)
                        writer.Write(step.NativeReadOrdinal)
                        atom writer step.NativeStateSha256
                    atom writer witness.Terminal.EpochResourceId
                    atom writer witness.Terminal.ReceiptSha256
                    writer.Write(witness.Terminal.NativeReadOrdinal)
                    atom writer witness.Terminal.Phase
                    atom writer witness.Terminal.PlanSeal
                    writer.Flush()
                    Ok(stream.ToArray())

    let verifySignedOrdered pinnedSpkiSha256 (publicKeySpki: byte[]) (signature: byte[])
        (expectedOrder: GitHubRollbackExpectedOrderBinding) (witness: GitHubRollbackNativeOrderWitness)
        (expected: GitHubRollbackExpectedReadbackBinding) (plan: GitHubRollbackPlan)
        (receipts: GitHubRollbackReceipt list) (claims: GitHubRollbackStepProvenanceClaim list)
        (terminal: GitHubRollbackEpochProvenanceClaim) =
        verifyPinnedPayload pinnedSpkiSha256 publicKeySpki signature
            (fun () -> payloadForOrderedSigning expectedOrder witness expected plan receipts claims terminal)
