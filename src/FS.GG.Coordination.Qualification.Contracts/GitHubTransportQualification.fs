namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubTransportControl =
    | Truncation
    | UnsafeReplay
    | StaleRevision
    | RateExhaustion
    | IncompletePagination
    | RedactionLeakage
    | AmbiguousMapping

type GitHubTransportControlResult =
    { Control: GitHubTransportControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubTransportFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubTransportQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-transport-qualification/1"

    let requiredControls: GitHubTransportControl list =
        [ Truncation; UnsafeReplay; StaleRevision; RateExhaustion; IncompletePagination; RedactionLeakage; AmbiguousMapping ]

    let controlId (control: GitHubTransportControl) : string =
        match control with
        | Truncation -> "truncation"
        | UnsafeReplay -> "unsafe-replay"
        | StaleRevision -> "stale-revision"
        | RateExhaustion -> "rate-exhaustion"
        | IncompletePagination -> "incomplete-pagination"
        | RedactionLeakage -> "redaction-leakage"
        | AmbiguousMapping -> "ambiguous-mapping"

    let validate (generated: GitHubTransportControlResult list) (independent: GitHubTransportControlResult list) : Result<unit, GitHubTransportFinding list> =
        let expected = requiredControls |> List.map controlId
        let inventory (producer: string) (results: GitHubTransportControlResult list) : GitHubTransportFinding list =
            let observed = results |> List.map (fun value -> controlId value.Control)
            if observed = expected then []
            else
                let expectedText = String.concat "," expected
                let observedText = String.concat "," observed
                [ { Code = "GTQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" } ]
        let outcomes (producer: string) (results: GitHubTransportControlResult list) : GitHubTransportFinding list =
            [ for value in results do
                  let id = controlId value.Control
                  if not value.MutationRed then yield { Code = $"GTQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = id; Message = $"{producer} mutation did not turn red" }
                  if not value.BaselineGreen then yield { Code = $"GTQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = id; Message = $"{producer} unmutated control did not remain green" } ]
        let findings: GitHubTransportFinding list =
            [ yield! inventory "generated" generated
              yield! inventory "independent" independent
              yield! outcomes "generated" generated
              yield! outcomes "independent" independent ]
        if List.isEmpty findings then Ok () else Error findings
