namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubSandboxClosureControl =
    | ProductionIdentity
    | ProductionTarget
    | ProductionCredential
    | Quota
    | StaleFence
    | ResponseUnknown
    | PartialCleanup
    | ReceiptSubstitution
    | WarmReuse
    | OmittedAdapter

type GitHubSandboxClosureControlResult =
    { Control: GitHubSandboxClosureControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubSandboxClosureFinding =
    { Code: string
      ControlId: string
      Message: string }

[<RequireQualifiedAccess>]
module GitHubSandboxClosureQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-sandbox-closure-qualification/1"

    let requiredControls =
        [ ProductionIdentity; ProductionTarget; ProductionCredential; Quota; StaleFence; ResponseUnknown; PartialCleanup; ReceiptSubstitution; WarmReuse; OmittedAdapter ]

    let controlId = function
        | ProductionIdentity -> "production-identity"
        | ProductionTarget -> "production-target"
        | ProductionCredential -> "production-credential"
        | Quota -> "quota"
        | StaleFence -> "stale-fence"
        | ResponseUnknown -> "response-unknown"
        | PartialCleanup -> "partial-cleanup"
        | ReceiptSubstitution -> "receipt-substitution"
        | WarmReuse -> "warm-reuse"
        | OmittedAdapter -> "omitted-adapter"

    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let findings producer results =
            let actual = results |> List.map (fun result -> controlId result.Control)
            let expectedText = String.concat "," expected
            let actualText = String.concat "," actual
            [ if actual <> expected then
                  { Code = "GSQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {actualText}" }
              for result in results do
                  if not result.MutationRed then { Code = $"GSQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = controlId result.Control; Message = "mutation did not turn red" }
                  if not result.BaselineGreen then { Code = $"GSQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = controlId result.Control; Message = "baseline did not remain green" } ]
        let all = findings "generated" generated @ findings "independent" independent
        if all.IsEmpty then Ok () else Error all
