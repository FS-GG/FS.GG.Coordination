namespace FS.GG.Coordination.Qualification.Contracts

type GitHubRollbackSignatureFailure =
    | ProvenanceInvalid of GitHubRollbackProvenanceFailure list
    | MissingOrInvalidObserverPin
    | ObserverFingerprintMismatch
    | InvalidObserverSignature

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
