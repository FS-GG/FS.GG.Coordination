namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubIssueFieldControl =
    | Pagination
    | DuplicateIdentity
    | TypeDrift
    | OptionDrift
    | StaleRevision
    | IncompleteObservation
    | NoOpMutation

type GitHubIssueFieldControlResult =
    { Control: GitHubIssueFieldControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubIssueFieldFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubIssueFieldQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-issue-field-qualification/1"

    let requiredControls =
        [ Pagination; DuplicateIdentity; TypeDrift; OptionDrift; StaleRevision; IncompleteObservation; NoOpMutation ]

    let controlId control =
        match control with
        | Pagination -> "pagination"
        | DuplicateIdentity -> "duplicate-identity"
        | TypeDrift -> "type-drift"
        | OptionDrift -> "option-drift"
        | StaleRevision -> "stale-revision"
        | IncompleteObservation -> "incomplete-observation"
        | NoOpMutation -> "no-op-mutation"

    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let inventory (producer: string) (results: GitHubIssueFieldControlResult list) =
            let observed = results |> List.map (fun value -> controlId value.Control)
            if observed = expected then []
            else
                let expectedText = String.concat "," expected
                let observedText = String.concat "," observed
                [ { Code = "GIFQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" } ]
        let outcomes (producer: string) (results: GitHubIssueFieldControlResult list) =
            [ for value in results do
                  let id = controlId value.Control
                  if not value.MutationRed then
                      yield { Code = $"GIFQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = id; Message = $"{producer} mutation did not turn red" }
                  if not value.BaselineGreen then
                      yield { Code = $"GIFQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = id; Message = $"{producer} baseline did not remain green" } ]
        let findings = inventory "generated" generated @ inventory "independent" independent @ outcomes "generated" generated @ outcomes "independent" independent
        if List.isEmpty findings then Ok () else Error findings
