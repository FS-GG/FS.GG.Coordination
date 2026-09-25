namespace FS.GG.Coordination.Qualification.Contracts

type GitHubRollbackReadbackClaim =
    { Order: int
      StepId: string
      Domain: GitHubRollbackDomain
      TargetIdentity: string
      StateSha256: string
      Complete: bool
      Authorized: bool }

type GitHubRollbackEpochClaim =
    { Phase: string
      PlanSeal: string
      Complete: bool
      Authorized: bool }

type GitHubRollbackReadbackFailure =
    | PlanOrReceiptInvalid of GitHubRollbackPlanFinding list
    | IncompleteReceiptPopulation
    | ReadbackPopulationMismatch
    | ReadbackMismatch of stepId:string
    | TerminalEpochMismatch

module GitHubRollbackReadbackQualification =
    val verifyClaims:
        expectedSeal:string -> plan:GitHubRollbackPlan -> receipts:GitHubRollbackReceipt list ->
        readbacks:GitHubRollbackReadbackClaim list -> epoch:GitHubRollbackEpochClaim ->
            Result<unit, GitHubRollbackReadbackFailure list>
