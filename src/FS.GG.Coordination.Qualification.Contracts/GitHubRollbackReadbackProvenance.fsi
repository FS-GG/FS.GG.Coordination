namespace FS.GG.Coordination.Qualification.Contracts

type GitHubRollbackExpectedReadbackBinding =
    { PlanSeal: string
      RunNonce: string
      Challenge: string
      ObserverResourceId: string }

type GitHubRollbackStepProvenanceClaim =
    { Readback: GitHubRollbackReadbackClaim
      PlanSeal: string
      RunNonce: string
      Challenge: string
      ObserverResourceId: string
      AfterReceiptSha256: string
      NativeRevision: string }

type GitHubRollbackEpochProvenanceClaim =
    { Epoch: GitHubRollbackEpochClaim
      RunNonce: string
      Challenge: string
      ObserverResourceId: string
      AfterReceiptSha256: string
      NativeRevision: string }

type GitHubRollbackProvenanceFailure =
    | InvalidExpectedReadbackBinding
    | ReadbackClaimInvalid of GitHubRollbackReadbackFailure list
    | StepProvenanceMismatch of stepId:string
    | EpochProvenanceMismatch

module GitHubRollbackReadbackProvenance =
    val verifyProvenance:
        expected:GitHubRollbackExpectedReadbackBinding -> plan:GitHubRollbackPlan ->
        receipts:GitHubRollbackReceipt list -> claims:GitHubRollbackStepProvenanceClaim list ->
        terminal:GitHubRollbackEpochProvenanceClaim -> Result<unit, GitHubRollbackProvenanceFailure list>
