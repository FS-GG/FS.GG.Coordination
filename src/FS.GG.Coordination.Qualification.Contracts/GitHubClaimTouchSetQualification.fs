namespace FS.GG.Coordination.Qualification.Contracts

open System

[<RequireQualifiedAccess>]
type GitHubClaimTouchSetControl =
    | CanonicalIdentity
    | UnsafeTouch
    | SiblingCas
    | MonotonicGeneration
    | ActiveLease
    | ExpiredLease
    | SuccessorCas
    | ProjectionNotAuthority
    | TouchOverlap
    | RepositoryPartition
    | AcquisitionOrder
    | FullPlanPersistence
    | StaleFence
    | TerminalAuthority
    | ReverseCompensation
    | ExactReplay
    | BoundedCost
    | QuintAndPrerequisite

type GitHubClaimTouchSetControlResult =
    { Control: GitHubClaimTouchSetControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubClaimTouchSetFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubClaimTouchSetQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-claim-touch-set-qualification/1"

    let requiredControls =
        [ GitHubClaimTouchSetControl.CanonicalIdentity
          GitHubClaimTouchSetControl.UnsafeTouch
          GitHubClaimTouchSetControl.SiblingCas
          GitHubClaimTouchSetControl.MonotonicGeneration
          GitHubClaimTouchSetControl.ActiveLease
          GitHubClaimTouchSetControl.ExpiredLease
          GitHubClaimTouchSetControl.SuccessorCas
          GitHubClaimTouchSetControl.ProjectionNotAuthority
          GitHubClaimTouchSetControl.TouchOverlap
          GitHubClaimTouchSetControl.RepositoryPartition
          GitHubClaimTouchSetControl.AcquisitionOrder
          GitHubClaimTouchSetControl.FullPlanPersistence
          GitHubClaimTouchSetControl.StaleFence
          GitHubClaimTouchSetControl.TerminalAuthority
          GitHubClaimTouchSetControl.ReverseCompensation
          GitHubClaimTouchSetControl.ExactReplay
          GitHubClaimTouchSetControl.BoundedCost
          GitHubClaimTouchSetControl.QuintAndPrerequisite ]

    let controlId = function
        | GitHubClaimTouchSetControl.CanonicalIdentity -> "canonical-identity"
        | GitHubClaimTouchSetControl.UnsafeTouch -> "unsafe-touch"
        | GitHubClaimTouchSetControl.SiblingCas -> "sibling-cas"
        | GitHubClaimTouchSetControl.MonotonicGeneration -> "monotonic-generation"
        | GitHubClaimTouchSetControl.ActiveLease -> "active-lease"
        | GitHubClaimTouchSetControl.ExpiredLease -> "expired-lease"
        | GitHubClaimTouchSetControl.SuccessorCas -> "successor-cas"
        | GitHubClaimTouchSetControl.ProjectionNotAuthority -> "projection-not-authority"
        | GitHubClaimTouchSetControl.TouchOverlap -> "touch-overlap"
        | GitHubClaimTouchSetControl.RepositoryPartition -> "repository-partition"
        | GitHubClaimTouchSetControl.AcquisitionOrder -> "acquisition-order"
        | GitHubClaimTouchSetControl.FullPlanPersistence -> "full-plan-persistence"
        | GitHubClaimTouchSetControl.StaleFence -> "stale-fence"
        | GitHubClaimTouchSetControl.TerminalAuthority -> "terminal-authority"
        | GitHubClaimTouchSetControl.ReverseCompensation -> "reverse-compensation"
        | GitHubClaimTouchSetControl.ExactReplay -> "exact-replay"
        | GitHubClaimTouchSetControl.BoundedCost -> "bounded-cost"
        | GitHubClaimTouchSetControl.QuintAndPrerequisite -> "quint-and-prerequisite"

    let validate generated independent =
        let expected = requiredControls |> List.map controlId

        let inventory (producer: string) (results: GitHubClaimTouchSetControlResult list) =
            let observed = results |> List.map (fun result -> controlId result.Control)

            if observed = expected then
                []
            else
                let expectedText = String.concat "," expected
                let observedText = String.concat "," observed
                [ { Code = "GCTQ-INVENTORY"
                    ControlId = producer
                    Message = $"expected {expectedText}; observed {observedText}" } ]

        let outcomes (producer: string) (results: GitHubClaimTouchSetControlResult list) =
            [ for result in results do
                  if not result.MutationRed then
                      yield
                          { Code = $"GCTQ-{producer.ToUpperInvariant()}-NOT-RED"
                            ControlId = controlId result.Control
                            Message = "mutation did not turn red" }

                  if not result.BaselineGreen then
                      yield
                          { Code = $"GCTQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"
                            ControlId = controlId result.Control
                            Message = "baseline did not remain green" } ]

        let findings =
            inventory "generated" generated
            @ inventory "independent" independent
            @ outcomes "generated" generated
            @ outcomes "independent" independent

        if List.isEmpty findings then Ok() else Error findings
