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
    let verifyClaims expectedSeal (plan: GitHubRollbackPlan) (receipts: GitHubRollbackReceipt list)
        (readbacks: GitHubRollbackReadbackClaim list) (epoch: GitHubRollbackEpochClaim) =
        match GitHubRollbackPlanQualification.resumePinned expectedSeal plan receipts with
        | Error findings -> Error [ PlanOrReceiptInvalid findings ]
        | Ok(Some _) -> Error [ IncompleteReceiptPopulation ]
        | Ok None ->
            let failures =
                [ if readbacks.Length <> plan.Steps.Length then
                      ReadbackPopulationMismatch
                  else
                      for step, (receipt, observed) in List.zip plan.Steps (List.zip receipts readbacks) do
                          if not observed.Complete || not observed.Authorized
                             || observed.Order <> step.Order || observed.StepId <> step.StepId
                             || observed.Domain <> step.Domain || observed.TargetIdentity <> step.TargetIdentity
                             || observed.StateSha256 <> step.CapturedStateSha256
                             || receipt.ResultSha256 <> observed.StateSha256 then
                              ReadbackMismatch step.StepId
                  if not epoch.Complete || not epoch.Authorized
                     || epoch.Phase <> "OperatingV1" || epoch.PlanSeal <> expectedSeal then
                      TerminalEpochMismatch ]
            if failures.IsEmpty then Ok() else Error failures
