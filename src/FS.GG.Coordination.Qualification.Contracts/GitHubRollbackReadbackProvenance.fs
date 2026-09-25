namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Text.RegularExpressions

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
    let private exactSha value =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private exactAtom (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && (value |> Seq.forall (fun ch -> not (Char.IsControl ch)))

    let verifyProvenance (expected: GitHubRollbackExpectedReadbackBinding) (plan: GitHubRollbackPlan)
        (receipts: GitHubRollbackReceipt list) (claims: GitHubRollbackStepProvenanceClaim list)
        (terminal: GitHubRollbackEpochProvenanceClaim) =
        if not (exactSha expected.PlanSeal && exactSha expected.Challenge
                && exactAtom expected.RunNonce && exactAtom expected.ObserverResourceId) then
            Error [ InvalidExpectedReadbackBinding ]
        else
            let readbacks = claims |> List.map _.Readback
            match GitHubRollbackReadbackQualification.verifyClaims
                expected.PlanSeal plan receipts readbacks terminal.Epoch with
            | Error failures -> Error [ ReadbackClaimInvalid failures ]
            | Ok() ->
                let stepFailures =
                    (plan.Steps, List.zip receipts claims)
                    ||> List.map2 (fun step (receipt, claim) ->
                        if claim.PlanSeal <> expected.PlanSeal || claim.RunNonce <> expected.RunNonce
                           || claim.Challenge <> expected.Challenge
                           || claim.ObserverResourceId <> expected.ObserverResourceId
                           || claim.AfterReceiptSha256 <> receipt.ReceiptSha256
                           || not (exactAtom claim.NativeRevision) then
                            Some(StepProvenanceMismatch step.StepId)
                        else None)
                    |> List.choose id
                let lastReceipt = receipts |> List.last
                let epochFailure =
                    if terminal.RunNonce <> expected.RunNonce || terminal.Challenge <> expected.Challenge
                       || terminal.ObserverResourceId <> expected.ObserverResourceId
                       || terminal.AfterReceiptSha256 <> lastReceipt.ReceiptSha256
                       || not (exactAtom terminal.NativeRevision) then
                        [ EpochProvenanceMismatch ]
                    else []
                let failures = stepFailures @ epochFailure
                if failures.IsEmpty then Ok() else Error failures
