namespace FS.GG.Coordination.Qualification.Contracts

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
    val payloadForSigning:
        expected:GitHubRollbackExpectedReadbackBinding -> plan:GitHubRollbackPlan ->
        receipts:GitHubRollbackReceipt list -> claims:GitHubRollbackStepProvenanceClaim list ->
        terminal:GitHubRollbackEpochProvenanceClaim -> Result<byte[], GitHubRollbackSignatureFailure list>
    val verifySigned:
        pinnedSpkiSha256:string -> publicKeySpki:byte[] -> signature:byte[] ->
        expected:GitHubRollbackExpectedReadbackBinding -> plan:GitHubRollbackPlan ->
        receipts:GitHubRollbackReceipt list -> claims:GitHubRollbackStepProvenanceClaim list ->
        terminal:GitHubRollbackEpochProvenanceClaim -> Result<unit, GitHubRollbackSignatureFailure list>
    val payloadForOrderedSigning:
        expectedOrder:GitHubRollbackExpectedOrderBinding -> witness:GitHubRollbackNativeOrderWitness ->
        expected:GitHubRollbackExpectedReadbackBinding -> plan:GitHubRollbackPlan ->
        receipts:GitHubRollbackReceipt list -> claims:GitHubRollbackStepProvenanceClaim list ->
        terminal:GitHubRollbackEpochProvenanceClaim -> Result<byte[], GitHubRollbackSignatureFailure list>
    val verifySignedOrdered:
        pinnedSpkiSha256:string -> publicKeySpki:byte[] -> signature:byte[] ->
        expectedOrder:GitHubRollbackExpectedOrderBinding -> witness:GitHubRollbackNativeOrderWitness ->
        expected:GitHubRollbackExpectedReadbackBinding -> plan:GitHubRollbackPlan ->
        receipts:GitHubRollbackReceipt list -> claims:GitHubRollbackStepProvenanceClaim list ->
        terminal:GitHubRollbackEpochProvenanceClaim -> Result<unit, GitHubRollbackSignatureFailure list>
    val payloadForCustodySigning:
        expectedOrder:GitHubRollbackExpectedOrderBinding -> witness:GitHubRollbackNativeOrderWitness ->
        native:GitHubRollbackNativeByteBatch -> expected:GitHubRollbackExpectedReadbackBinding ->
        plan:GitHubRollbackPlan -> receipts:GitHubRollbackReceipt list ->
        claims:GitHubRollbackStepProvenanceClaim list -> terminal:GitHubRollbackEpochProvenanceClaim ->
        Result<byte[], GitHubRollbackSignatureFailure list>
    val verifySignedCustody:
        pinnedSpkiSha256:string -> publicKeySpki:byte[] -> signature:byte[] ->
        expectedOrder:GitHubRollbackExpectedOrderBinding -> witness:GitHubRollbackNativeOrderWitness ->
        native:GitHubRollbackNativeByteBatch -> expected:GitHubRollbackExpectedReadbackBinding ->
        plan:GitHubRollbackPlan -> receipts:GitHubRollbackReceipt list ->
        claims:GitHubRollbackStepProvenanceClaim list -> terminal:GitHubRollbackEpochProvenanceClaim ->
        Result<unit, GitHubRollbackSignatureFailure list>
    val payloadForProtectedCustodySigning:
        pins:GitHubRollbackProtectedNativePins -> port:IProtectedNativeCustodyPort option ->
        expectedOrder:GitHubRollbackExpectedOrderBinding -> witness:GitHubRollbackNativeOrderWitness ->
        expected:GitHubRollbackExpectedReadbackBinding -> plan:GitHubRollbackPlan ->
        receipts:GitHubRollbackReceipt list -> claims:GitHubRollbackStepProvenanceClaim list ->
        terminal:GitHubRollbackEpochProvenanceClaim -> Result<byte[], GitHubRollbackSignatureFailure list>
    val verifySignedProtectedCustody:
        pinnedSpkiSha256:string -> publicKeySpki:byte[] -> signature:byte[] ->
        pins:GitHubRollbackProtectedNativePins -> port:IProtectedNativeCustodyPort option ->
        expectedOrder:GitHubRollbackExpectedOrderBinding -> witness:GitHubRollbackNativeOrderWitness ->
        expected:GitHubRollbackExpectedReadbackBinding -> plan:GitHubRollbackPlan ->
        receipts:GitHubRollbackReceipt list -> claims:GitHubRollbackStepProvenanceClaim list ->
        terminal:GitHubRollbackEpochProvenanceClaim -> Result<unit, GitHubRollbackSignatureFailure list>
