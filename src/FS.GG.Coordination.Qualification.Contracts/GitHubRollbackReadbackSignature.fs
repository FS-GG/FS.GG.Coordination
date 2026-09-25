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
    | InvalidRawEvidence
    | RawEvidenceMismatch
    | MissingProtectedNativePins
    | ProtectedNativePortUnavailable
    | ProtectedNativeInstallationMismatch
    | NativeCustodyReadUnavailable
    | NativeCustodyObjectMismatch

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

type GitHubRollbackStepNativeBytes =
    { StepId: string
      TargetIdentity: string
      NativeRevision: string
      NativeReadOrdinal: int64
      RawResponse: byte[]
      CanonicalState: byte[] }

type GitHubRollbackTerminalNativeBytes =
    { EpochResourceId: string
      NativeRevision: string
      NativeReadOrdinal: int64
      RawResponse: byte[]
      CanonicalEpoch: byte[] }

type GitHubRollbackNativeByteBatch =
    { Steps: GitHubRollbackStepNativeBytes list
      Terminal: GitHubRollbackTerminalNativeBytes }

type GitHubRollbackProtectedNativePins =
    { ReaderResourceId: string
      CanonicalizerSha256: string
      CustodyStoreResourceId: string }

type GitHubRollbackNativeInstallation =
    { ReaderResourceId: string
      CanonicalizerSha256: string
      CustodyStoreResourceId: string }

type GitHubRollbackCustodySubject =
    | RollbackStep of stepId:string
    | TerminalEpoch

type GitHubRollbackNativeCustodyLookup =
    { RunNonce: string
      PlanSeal: string
      SandboxResourceId: string
      WitnessGeneration: int64
      Subject: GitHubRollbackCustodySubject
      ReceiptSha256: string }

type GitHubRollbackRetainedNativeBytes =
    { Lookup: GitHubRollbackNativeCustodyLookup
      CustodyStoreResourceId: string
      CustodyObjectId: string
      Step: GitHubRollbackStepNativeBytes option
      Terminal: GitHubRollbackTerminalNativeBytes option }

type IProtectedNativeCustodyPort =
    abstract Describe: unit -> GitHubRollbackNativeInstallation
    abstract Read: GitHubRollbackNativeCustodyLookup -> GitHubRollbackRetainedNativeBytes option

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

    let private sha256Hex (bytes: byte[]) =
        (SHA256.HashData(bytes) |> Convert.ToHexString).ToLowerInvariant()

    let private canonicalEpochBytes (epoch: GitHubRollbackEpochClaim) =
        String.concat "\n"
            [ epoch.Phase; epoch.PlanSeal
              if epoch.Complete then "true" else "false"
              if epoch.Authorized then "true" else "false" ]
        |> Encoding.UTF8.GetBytes

    let payloadForCustodySigning (expectedOrder: GitHubRollbackExpectedOrderBinding)
        (witness: GitHubRollbackNativeOrderWitness) (native: GitHubRollbackNativeByteBatch)
        (expected: GitHubRollbackExpectedReadbackBinding) (plan: GitHubRollbackPlan)
        (receipts: GitHubRollbackReceipt list) (claims: GitHubRollbackStepProvenanceClaim list)
        (terminal: GitHubRollbackEpochProvenanceClaim) =
        match payloadForOrderedSigning expectedOrder witness expected plan receipts claims terminal with
        | Error failures -> Error failures
        | Ok orderedPayload ->
            if native.Steps.Length <> witness.Steps.Length then
                Error [ RawEvidenceMismatch ]
            elif native.Steps |> List.exists (fun step ->
                    isNull step.RawResponse || step.RawResponse.Length = 0
                    || isNull step.CanonicalState || step.CanonicalState.Length = 0)
                 || isNull native.Terminal.RawResponse || native.Terminal.RawResponse.Length = 0
                 || isNull native.Terminal.CanonicalEpoch || native.Terminal.CanonicalEpoch.Length = 0 then
                Error [ InvalidRawEvidence ]
            elif (List.zip3 witness.Steps claims native.Steps
                  |> List.exists (fun (order, claim, bytes) ->
                      bytes.StepId <> order.StepId
                      || bytes.TargetIdentity <> order.TargetIdentity
                      || bytes.NativeRevision <> claim.NativeRevision
                      || bytes.NativeReadOrdinal <> order.NativeReadOrdinal
                      || sha256Hex bytes.CanonicalState <> claim.Readback.StateSha256))
                 || native.Terminal.EpochResourceId <> witness.Terminal.EpochResourceId
                 || native.Terminal.NativeRevision <> terminal.NativeRevision
                 || native.Terminal.NativeReadOrdinal <> witness.Terminal.NativeReadOrdinal
                 || native.Terminal.CanonicalEpoch <> canonicalEpochBytes terminal.Epoch then
                Error [ RawEvidenceMismatch ]
            else
                use stream = new MemoryStream()
                use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                atom writer "fsgg.gs2-09.7.q6-native-byte-custody/v1"
                writer.Write(orderedPayload.Length)
                writer.Write(orderedPayload)
                integer writer native.Steps.Length
                for step in native.Steps do
                    atom writer step.StepId
                    atom writer step.TargetIdentity
                    atom writer step.NativeRevision
                    writer.Write(step.NativeReadOrdinal)
                    atom writer (sha256Hex step.RawResponse)
                    atom writer (sha256Hex step.CanonicalState)
                atom writer native.Terminal.EpochResourceId
                atom writer native.Terminal.NativeRevision
                writer.Write(native.Terminal.NativeReadOrdinal)
                atom writer (sha256Hex native.Terminal.RawResponse)
                atom writer (sha256Hex native.Terminal.CanonicalEpoch)
                writer.Flush()
                Ok(stream.ToArray())

    let verifySignedCustody pinnedSpkiSha256 (publicKeySpki: byte[]) (signature: byte[])
        (expectedOrder: GitHubRollbackExpectedOrderBinding) (witness: GitHubRollbackNativeOrderWitness)
        (native: GitHubRollbackNativeByteBatch) (expected: GitHubRollbackExpectedReadbackBinding)
        (plan: GitHubRollbackPlan) (receipts: GitHubRollbackReceipt list)
        (claims: GitHubRollbackStepProvenanceClaim list) (terminal: GitHubRollbackEpochProvenanceClaim) =
        verifyPinnedPayload pinnedSpkiSha256 publicKeySpki signature
            (fun () -> payloadForCustodySigning expectedOrder witness native expected plan receipts claims terminal)

    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private custodyLookup (expected: GitHubRollbackExpectedReadbackBinding)
        (expectedOrder: GitHubRollbackExpectedOrderBinding) subject receiptSha =
        { RunNonce=expected.RunNonce; PlanSeal=expected.PlanSeal
          SandboxResourceId=expectedOrder.SandboxResourceId
          WitnessGeneration=expectedOrder.WitnessGeneration
          Subject=subject; ReceiptSha256=receiptSha }

    let private writeLookup (writer: BinaryWriter) (lookup: GitHubRollbackNativeCustodyLookup) =
        atom writer lookup.RunNonce
        atom writer lookup.PlanSeal
        atom writer lookup.SandboxResourceId
        writer.Write(lookup.WitnessGeneration)
        match lookup.Subject with
        | RollbackStep stepId -> atom writer "step"; atom writer stepId
        | TerminalEpoch -> atom writer "terminal"
        atom writer lookup.ReceiptSha256

    let payloadForProtectedCustodySigning (pins: GitHubRollbackProtectedNativePins)
        (port: IProtectedNativeCustodyPort option) (expectedOrder: GitHubRollbackExpectedOrderBinding)
        (witness: GitHubRollbackNativeOrderWitness) (expected: GitHubRollbackExpectedReadbackBinding)
        (plan: GitHubRollbackPlan) (receipts: GitHubRollbackReceipt list)
        (claims: GitHubRollbackStepProvenanceClaim list) (terminal: GitHubRollbackEpochProvenanceClaim) =
        if not (exactAtom pins.ReaderResourceId && exactSha pins.CanonicalizerSha256
                && exactAtom pins.CustodyStoreResourceId) then
            Error [ MissingProtectedNativePins ]
        else
            match payloadForOrderedSigning expectedOrder witness expected plan receipts claims terminal with
            | Error failures -> Error failures
            | Ok _ ->
                match port with
                | None -> Error [ ProtectedNativePortUnavailable ]
                | Some protectedPort ->
                    try
                        let installed = protectedPort.Describe()
                        if installed.ReaderResourceId <> pins.ReaderResourceId
                           || installed.CanonicalizerSha256 <> pins.CanonicalizerSha256
                           || installed.CustodyStoreResourceId <> pins.CustodyStoreResourceId then
                            Error [ ProtectedNativeInstallationMismatch ]
                        else
                            let lookups =
                                (plan.Steps, receipts)
                                ||> List.map2 (fun step receipt ->
                                    custodyLookup expected expectedOrder (RollbackStep step.StepId) receipt.ReceiptSha256)
                                |> fun steps ->
                                    steps @ [ custodyLookup expected expectedOrder TerminalEpoch
                                                  (List.last receipts).ReceiptSha256 ]
                            let retained = lookups |> List.map protectedPort.Read
                            if retained |> List.exists Option.isNone then
                                Error [ NativeCustodyReadUnavailable ]
                            else
                                let records = retained |> List.choose id
                                let objects = records |> List.map _.CustodyObjectId
                                if (List.zip lookups records
                                    |> List.exists (fun (lookup, record) ->
                                        record.Lookup <> lookup
                                        || record.CustodyStoreResourceId <> pins.CustodyStoreResourceId
                                        || not (exactAtom record.CustodyObjectId)))
                                   || (objects |> List.distinct |> List.length) <> objects.Length then
                                    Error [ NativeCustodyObjectMismatch ]
                                else
                                    let stepRecords = records |> List.take plan.Steps.Length
                                    let lastRecord = records |> List.last
                                    if stepRecords |> List.exists (fun record -> record.Step.IsNone || record.Terminal.IsSome)
                                       || lastRecord.Step.IsSome || lastRecord.Terminal.IsNone then
                                        Error [ NativeCustodyObjectMismatch ]
                                    else
                                        let native =
                                            { Steps=stepRecords |> List.choose _.Step
                                              Terminal=lastRecord.Terminal.Value }
                                        match payloadForCustodySigning expectedOrder witness native expected plan receipts claims terminal with
                                        | Error failures -> Error failures
                                        | Ok custodyPayload ->
                                            use stream = new MemoryStream()
                                            use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                                            atom writer "fsgg.gs2-09.7.q6-protected-custody-port/v1"
                                            writer.Write(custodyPayload.Length)
                                            writer.Write(custodyPayload)
                                            atom writer pins.ReaderResourceId
                                            atom writer pins.CanonicalizerSha256
                                            atom writer pins.CustodyStoreResourceId
                                            integer writer records.Length
                                            for record in records do
                                                writeLookup writer record.Lookup
                                                atom writer record.CustodyStoreResourceId
                                                atom writer record.CustodyObjectId
                                            writer.Flush()
                                            Ok(stream.ToArray())
                    with _ -> Error [ NativeCustodyReadUnavailable ]

    let verifySignedProtectedCustody pinnedSpkiSha256 (publicKeySpki: byte[]) (signature: byte[])
        (pins: GitHubRollbackProtectedNativePins) (port: IProtectedNativeCustodyPort option)
        (expectedOrder: GitHubRollbackExpectedOrderBinding) (witness: GitHubRollbackNativeOrderWitness)
        (expected: GitHubRollbackExpectedReadbackBinding) (plan: GitHubRollbackPlan)
        (receipts: GitHubRollbackReceipt list) (claims: GitHubRollbackStepProvenanceClaim list)
        (terminal: GitHubRollbackEpochProvenanceClaim) =
        verifyPinnedPayload pinnedSpkiSha256 publicKeySpki signature
            (fun () -> payloadForProtectedCustodySigning pins port expectedOrder witness
                            expected plan receipts claims terminal)
