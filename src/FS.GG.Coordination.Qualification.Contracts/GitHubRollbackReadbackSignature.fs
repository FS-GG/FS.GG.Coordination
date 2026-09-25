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

module GitHubRollbackReadbackSignature =
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

    let verifySigned pinnedSpkiSha256 (publicKeySpki: byte[]) (signature: byte[])
        (expected: GitHubRollbackExpectedReadbackBinding) (plan: GitHubRollbackPlan)
        (receipts: GitHubRollbackReceipt list) (claims: GitHubRollbackStepProvenanceClaim list)
        (terminal: GitHubRollbackEpochProvenanceClaim) =
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
            match payloadForSigning expected plan receipts claims terminal with
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
