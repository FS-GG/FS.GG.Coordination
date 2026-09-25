namespace FS.GG.Coordination.Qualification.Contracts

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
